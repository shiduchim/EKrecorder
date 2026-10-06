using System.Globalization;
using EKrecorder.Capture;
using EKrecorder.Diagnostics;
using TerraFX.Interop.Windows;
using static EKrecorder.Recording.MediaFoundation;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Recording;

/// <summary>What the encoder in use says about itself and its settings (read back from the encoder, not assumed).</summary>
internal sealed record EncoderInfo(
    bool IsHardware,
    string Vendor,
    string Name,
    string IdentifiedBy,
    string Plan,
    string RateControl,
    string AverageBitrate,
    string PeakBitrate,
    string KeyframeInterval,
    string BFrames)
{
    public string Summary => IsHardware ? $"{Vendor} hardware H.264 encoder" : "Microsoft software H.264 encoder (fallback)";
}

/// <summary>
/// H.264 in MP4 through the Media Foundation sink writer. It prefers a hardware encoder; the software encoder is used
/// only when no hardware encoder accepts the settings. Rate control is tried in order: peak-constrained VBR
/// (2 Mbps average, 6 Mbps peak), unconstrained VBR, CBR, then the same on Main profile, then encoder defaults.
/// </summary>
internal sealed unsafe class H264Mp4Writer : IDisposable
{
    private const uint MFT_ENUM_FLAG_HARDWARE = 0x4;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    private readonly uint _stream;
    private IMFSinkWriter* _writer;

    private H264Mp4Writer(IMFSinkWriter* writer, uint stream)
    {
        _writer = writer;
        _stream = stream;
    }

    public EncoderInfo Encoder { get; private set; } = new(false, "unknown", "unknown", "", "", "", "", "", "", "");

    /// <summary>Hardware H.264 encoders Windows lists on this PC (for the report).</summary>
    public static IReadOnlyList<string> ListHardwareEncoders()
    {
        var found = new List<string>();
        MFT_REGISTER_TYPE_INFO input = new() { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat.MFVideoFormat_NV12 };
        MFT_REGISTER_TYPE_INFO output = new() { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat.MFVideoFormat_H264 };
        IMFActivate** activates = null;
        uint count = 0;
        HRESULT hr = MFTEnumEx(MFT.MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, &input, &output, &activates, &count);
        if (hr.FAILED)
        {
            Log.Api("MFTEnumEx(hardware H.264 encoders)", false, Describe(hr));
            return found;
        }

        for (uint i = 0; i < count; i++)
        {
            IMFAttributes* attributes = (IMFAttributes*)activates[i];
            string name = GetString(attributes, in MFT.MFT_FRIENDLY_NAME_Attribute) ?? "unnamed";
            string? vendorId = GetString(attributes, in MFT.MFT_ENUM_HARDWARE_VENDOR_ID_Attribute);
            found.Add($"{name} ({VendorName(vendorId) ?? vendorId ?? "vendor unknown"})");
            activates[i]->Release();
        }

        CoTaskMemFree(activates);
        Log.Info($"Hardware H.264 encoders on this PC: {(found.Count == 0 ? "none" : string.Join("; ", found))}");
        return found;
    }

    /// <summary>
    /// Opens <paramref name="path"/> for writing. <paramref name="manager"/> may be null (CPU frames).
    /// <paramref name="gpuInput"/>: frames will arrive as GPU textures rather than memory buffers.
    /// </summary>
    public static H264Mp4Writer Create(string path, Size size, RecordingPreset preset, IMFDXGIDeviceManager* manager, bool gpuInput, AdapterInfo adapter)
    {
        var failures = new List<string>();
        foreach (bool hardware in new[] { true, false })
        {
            if (!hardware)
            {
                Log.Decision("No encoder accepted the settings with hardware encoding allowed; trying software encoding only.");
            }

            foreach (Plan plan in Plans(preset))
            {
                H264Mp4Writer? writer = TryCreate(path, size, preset, manager, gpuInput, hardware, plan, out string failure);
                if (writer is not null)
                {
                    writer.Identify(plan, preset, adapter);
                    return writer;
                }

                failures.Add($"{(hardware ? "hardware allowed" : "software only")}, {plan.Name}: {failure}");
            }
        }

        throw new InvalidOperationException("No H.264 encoder could be set up: " + string.Join(" | ", failures));
    }

    /// <summary>Writes one frame. The sample's texture or buffer must not change until the encoder releases it.</summary>
    public void WriteSample(IMFSample* sample, long time, long duration)
    {
        Check(sample->SetSampleTime(time), "IMFSample::SetSampleTime");
        Check(sample->SetSampleDuration(duration), "IMFSample::SetSampleDuration");
        Check(_writer->WriteSample(_stream, sample), "IMFSinkWriter::WriteSample");
    }

    /// <summary>Writes one NV12 frame from memory (CPU path).</summary>
    public void WriteNv12(ReadOnlySpan<byte> frame, long time, long duration)
    {
        IMFMediaBuffer* buffer;
        Check(MFCreateMemoryBuffer((uint)frame.Length, &buffer), "MFCreateMemoryBuffer");
        IMFSample* sample = null;
        try
        {
            byte* data;
            uint maximum;
            uint current;
            Check(buffer->Lock(&data, &maximum, &current), "IMFMediaBuffer::Lock");
            frame.CopyTo(new Span<byte>(data, frame.Length));
            Check(buffer->Unlock(), "IMFMediaBuffer::Unlock");
            Check(buffer->SetCurrentLength((uint)frame.Length), "IMFMediaBuffer::SetCurrentLength");
            Check(MFCreateSample(&sample), "MFCreateSample");
            Check(sample->AddBuffer(buffer), "IMFSample::AddBuffer");
            WriteSample(sample, time, duration);
        }
        finally
        {
            buffer->Release();
            if (sample != null)
            {
                sample->Release();
            }
        }
    }

    /// <summary>The sink writer's own counters for the report.</summary>
    public string Statistics()
    {
        MF_SINK_WRITER_STATISTICS statistics = default;
        statistics.cb = (uint)sizeof(MF_SINK_WRITER_STATISTICS);
        HRESULT hr = _writer->GetStatistics(_stream, &statistics);
        return hr.FAILED
            ? $"not available ({Describe(hr)})"
            : $"{statistics.qwNumSamplesReceived} frames received, {statistics.qwNumSamplesEncoded} encoded, {statistics.qwNumSamplesProcessed} written to the file";
    }

    /// <summary>Drains the encoder and writes the MP4 index. Without this the file cannot be played.</summary>
    public void FinishFile() => Check(_writer->Finalize(), "IMFSinkWriter::Finalize");

    public void Dispose()
    {
        if (_writer != null)
        {
            _writer->Release();
            _writer = null;
        }
    }

    private static IEnumerable<Plan> Plans(RecordingPreset preset)
    {
        string average = (preset.AverageBitrate / 1e6).ToString("0.#", CultureInfo.InvariantCulture);
        string peak = (preset.PeakBitrate / 1e6).ToString("0.#", CultureInfo.InvariantCulture);
        yield return new Plan($"High profile, peak-constrained VBR {average}/{peak} Mbps", eAVEncH264VProfile.eAVEncH264VProfile_High, eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_PeakConstrainedVBR, true, true);
        yield return new Plan($"High profile, unconstrained VBR {average} Mbps", eAVEncH264VProfile.eAVEncH264VProfile_High, eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_UnconstrainedVBR, false, true);
        yield return new Plan($"High profile, CBR {average} Mbps", eAVEncH264VProfile.eAVEncH264VProfile_High, eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR, false, true);
        yield return new Plan($"Main profile, peak-constrained VBR {average}/{peak} Mbps", eAVEncH264VProfile.eAVEncH264VProfile_Main, eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_PeakConstrainedVBR, true, true);
        yield return new Plan($"Main profile, unconstrained VBR {average} Mbps", eAVEncH264VProfile.eAVEncH264VProfile_Main, eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_UnconstrainedVBR, false, true);
        yield return new Plan($"Main profile, encoder defaults at {average} Mbps", eAVEncH264VProfile.eAVEncH264VProfile_Main, null, false, false);
    }

    private static H264Mp4Writer? TryCreate(
        string path, Size size, RecordingPreset preset, IMFDXGIDeviceManager* manager, bool gpuInput, bool hardware, Plan plan, out string failure)
    {
        failure = "";
        TryDelete(path);
        IMFAttributes* attributes = null;
        IMFSinkWriter* writer = null;
        IMFMediaType* outputType = null;
        IMFMediaType* inputType = null;
        IMFAttributes* encoderSettings = null;
        try
        {
            Check(MFCreateAttributes(&attributes, 4), "MFCreateAttributes");
            if (manager != null)
            {
                // GPU textures go straight to the encoder; hardware encoders run on the same device.
                Check(attributes->SetUnknown(Ptr(in MF.MF_SINK_WRITER_D3D_MANAGER), (IUnknown*)manager), "set MF_SINK_WRITER_D3D_MANAGER");
            }

            Check(attributes->SetUINT32(Ptr(in MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS), hardware ? 1u : 0u), "set MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS");
            Check(attributes->SetGUID(Ptr(in MF.MF_TRANSCODE_CONTAINERTYPE), Ptr(in MFTranscodeContainerType.MFTranscodeContainerType_MPEG4)), "set MF_TRANSCODE_CONTAINERTYPE");
            fixed (char* file = path)
            {
                Check(MFCreateSinkWriterFromURL(file, null, attributes, &writer), "MFCreateSinkWriterFromURL");
            }

            outputType = CreateVideoType(in MFVideoFormat.MFVideoFormat_H264, size.Width, size.Height, preset.FramesPerSecond);
            Check(outputType->SetUINT32(Ptr(in MF.MF_MT_AVG_BITRATE), (uint)preset.AverageBitrate), "set MF_MT_AVG_BITRATE");
            Check(outputType->SetUINT32(Ptr(in MF.MF_MT_MPEG2_PROFILE), (uint)plan.Profile), "set MF_MT_MPEG2_PROFILE");
            Check(outputType->SetUINT32(Ptr(in MF.MF_MT_MAX_KEYFRAME_SPACING), (uint)preset.KeyframeIntervalFrames), "set MF_MT_MAX_KEYFRAME_SPACING");
            uint stream;
            Check(writer->AddStream(outputType, &stream), "IMFSinkWriter::AddStream");

            inputType = CreateVideoType(in MFVideoFormat.MFVideoFormat_NV12, size.Width, size.Height, preset.FramesPerSecond);
            if (!gpuInput)
            {
                // Memory frames: tightly packed NV12 rows.
                Check(inputType->SetUINT32(Ptr(in MF.MF_MT_DEFAULT_STRIDE), (uint)size.Width), "set MF_MT_DEFAULT_STRIDE");
                Check(inputType->SetUINT32(Ptr(in MF.MF_MT_SAMPLE_SIZE), (uint)(size.Width * size.Height * 3 / 2)), "set MF_MT_SAMPLE_SIZE");
                Check(inputType->SetUINT32(Ptr(in MF.MF_MT_FIXED_SIZE_SAMPLES), 1), "set MF_MT_FIXED_SIZE_SAMPLES");
                Check(inputType->SetUINT32(Ptr(in MF.MF_MT_ALL_SAMPLES_INDEPENDENT), 1), "set MF_MT_ALL_SAMPLES_INDEPENDENT");
            }

            if (plan.UseEncoderSettings)
            {
                encoderSettings = EncoderSettings(plan, preset);
            }

            // The sink writer creates the encoder here and hands it the settings through ICodecAPI.
            Check(writer->SetInputMediaType(stream, inputType, encoderSettings), "IMFSinkWriter::SetInputMediaType");
            Check(writer->BeginWriting(), "IMFSinkWriter::BeginWriting");
            Log.Info($"Sink writer ready: {plan.Name}, {(hardware ? "hardware encoders allowed" : "software only")}, {(gpuInput ? "GPU textures in" : "memory frames in")}");
            var result = new H264Mp4Writer(writer, stream);
            writer = null;
            return result;
        }
        catch (MediaFoundationException ex)
        {
            failure = ex.Message;
            Log.Warn($"Encoder set-up failed ({plan.Name}, {(hardware ? "hardware allowed" : "software only")}): {ex.Message}");
            return null;
        }
        finally
        {
            if (encoderSettings != null)
            {
                encoderSettings->Release();
            }

            if (inputType != null)
            {
                inputType->Release();
            }

            if (outputType != null)
            {
                outputType->Release();
            }

            if (writer != null)
            {
                writer->Release();
                TryDelete(path);
            }

            if (attributes != null)
            {
                attributes->Release();
            }
        }
    }

    private static IMFAttributes* EncoderSettings(Plan plan, RecordingPreset preset)
    {
        IMFAttributes* settings;
        Check(MFCreateAttributes(&settings, 4), "MFCreateAttributes");
        try
        {
            if (plan.RateControl is { } mode)
            {
                Check(settings->SetUINT32(__uuidof<CODECAPI_AVEncCommonRateControlMode>(), (uint)mode), "set CODECAPI_AVEncCommonRateControlMode");
            }

            Check(settings->SetUINT32(__uuidof<CODECAPI_AVEncCommonMeanBitRate>(), (uint)preset.AverageBitrate), "set CODECAPI_AVEncCommonMeanBitRate");
            if (plan.UsePeakBitrate)
            {
                Check(settings->SetUINT32(__uuidof<CODECAPI_AVEncCommonMaxBitRate>(), (uint)preset.PeakBitrate), "set CODECAPI_AVEncCommonMaxBitRate");
            }

            Check(settings->SetUINT32(__uuidof<CODECAPI_AVEncMPVGOPSize>(), (uint)preset.KeyframeIntervalFrames), "set CODECAPI_AVEncMPVGOPSize");
            return settings;
        }
        catch
        {
            settings->Release();
            throw;
        }
    }

    /// <summary>Finds out which encoder the sink writer loaded, and reads its settings back.</summary>
    private void Identify(Plan plan, RecordingPreset preset, AdapterInfo adapter)
    {
        Guid noService = Guid.Empty;
        bool isAsync = false;
        string? hardwareUrl = null;
        string? vendorId = null;
        string? friendlyName = null;
        IMFTransform* transform = null;
        HRESULT hr = _writer->GetServiceForStream(_stream, &noService, __uuidof<IMFTransform>(), (void**)&transform);
        if (hr.SUCCEEDED && transform != null)
        {
            IMFAttributes* attributes = null;
            if (transform->GetAttributes(&attributes).SUCCEEDED && attributes != null)
            {
                isAsync = GetUInt32(attributes, in MF.MF_TRANSFORM_ASYNC) == 1;
                hardwareUrl = GetString(attributes, in MFT.MFT_ENUM_HARDWARE_URL_Attribute);
                vendorId = GetString(attributes, in MFT.MFT_ENUM_HARDWARE_VENDOR_ID_Attribute);
                friendlyName = GetString(attributes, in MFT.MFT_FRIENDLY_NAME_Attribute);
                attributes->Release();
            }

            transform->Release();
        }
        else
        {
            Log.Api("IMFSinkWriter::GetServiceForStream(IMFTransform)", false, Describe(hr));
        }

        // Hardware encoders are asynchronous MFTs that publish a hardware URL; the Microsoft software encoder is neither.
        bool isHardware = hardwareUrl is not null || vendorId is not null || isAsync;
        string vendor = isHardware
            ? VendorName(vendorId) ?? VendorName(hardwareUrl) ?? adapter.VendorName
            : "Microsoft";
        string identifiedBy = vendorId is not null ? $"the encoder's vendor id {vendorId}"
            : hardwareUrl is not null ? $"the encoder's hardware id {hardwareUrl}"
            : isAsync ? "an asynchronous (hardware) encoder; vendor taken from the GPU it runs on"
            : "no hardware attributes: Microsoft software encoder";
        string name = friendlyName ?? (isHardware ? $"{vendor} H.264 encoder" : "Microsoft H.264 Video Encoder MFT");

        string rateControl = "not reported";
        string average = "not reported";
        string peak = "not reported";
        string gop = "not reported";
        string bFrames = "not reported";
        ICodecAPI* codec = null;
        hr = _writer->GetServiceForStream(_stream, &noService, __uuidof<ICodecAPI>(), (void**)&codec);
        if (hr.SUCCEEDED && codec != null)
        {
            if (ReadValue(codec, __uuidof<CODECAPI_AVEncCommonRateControlMode>()) is ulong mode)
            {
                rateControl = RateControlName(mode);
            }

            if (ReadValue(codec, __uuidof<CODECAPI_AVEncCommonMeanBitRate>()) is ulong mean)
            {
                average = $"{mean:N0} bps";
            }

            if (ReadValue(codec, __uuidof<CODECAPI_AVEncCommonMaxBitRate>()) is ulong max)
            {
                peak = $"{max:N0} bps";
            }

            if (ReadValue(codec, __uuidof<CODECAPI_AVEncMPVGOPSize>()) is ulong gopFrames)
            {
                gop = $"{gopFrames} frames ({gopFrames / (double)preset.FramesPerSecond:0.##} s)";
            }

            if (ReadValue(codec, __uuidof<CODECAPI_AVEncMPVDefaultBPictureCount>()) is ulong b)
            {
                bFrames = b.ToString(CultureInfo.InvariantCulture);
            }

            codec->Release();
        }
        else
        {
            Log.Api("IMFSinkWriter::GetServiceForStream(ICodecAPI)", false, Describe(hr));
        }

        Encoder = new EncoderInfo(isHardware, vendor, name, identifiedBy, plan.Name, rateControl, average, peak, gop, bFrames);
        Log.Info($"ENCODER: {Encoder.Summary}: \"{name}\" (identified by {identifiedBy})");
        Log.Info($"ENCODER settings read back: rate control {rateControl}; average {average}; peak {peak}; keyframe interval {gop}; B-frames {bFrames}; requested {plan.Name}");
        if (!isHardware)
        {
            Log.Decision("Software H.264 encoding is in use, because no hardware encoder was available for these settings.");
        }
    }

    private static ulong? ReadValue(ICodecAPI* codec, Guid* key)
    {
        VARIANT value = default;
        if (codec->GetValue(key, &value).FAILED)
        {
            return null;
        }

        try
        {
            // VARIANT: the type tag is the first 16 bits, the value starts at byte 8.
            ushort type = *(ushort*)&value;
            byte* data = (byte*)&value + 8;
            return type switch
            {
                3 or 22 => (ulong)*(int*)data,      // VT_I4, VT_INT
                11 => *(short*)data != 0 ? 1UL : 0, // VT_BOOL
                18 => *(ushort*)data,               // VT_UI2
                19 or 23 => *(uint*)data,           // VT_UI4, VT_UINT
                20 or 21 => *(ulong*)data,          // VT_I8, VT_UI8
                _ => null,
            };
        }
        finally
        {
            VariantClear(&value);
        }
    }

    private static string RateControlName(ulong mode) => mode switch
    {
        0 => "CBR (constant bitrate)",
        1 => "peak-constrained VBR",
        2 => "unconstrained VBR",
        3 => "quality-based VBR",
        4 => "low-delay VBR",
        5 => "global VBR",
        6 => "global low-delay VBR",
        _ => $"mode {mode}",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }

    private sealed record Plan(string Name, eAVEncH264VProfile Profile, eAVEncCommonRateControlMode? RateControl, bool UsePeakBitrate, bool UseEncoderSettings);
}
