using System.Diagnostics;
using System.Globalization;
using System.Text;
using EKrecorder.Capture;
using EKrecorder.Recording;
using TerraFX.Interop.Windows;
using static EKrecorder.Recording.MediaFoundation;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Diagnostics;

/// <summary>
/// Build-machine probe (<c>--probe folder</c>): which AAC settings Windows' encoder accepts, how the fragmented MP4
/// sink lays out a file while it is written and after it is finished, and where a beep and a white frame written at
/// the same moment come out after decoding.
/// </summary>
internal static unsafe class EncoderProbe
{
    private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    private const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    private const uint MF_SOURCE_READERF_ERROR = 0x1;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        report.AppendLine($"EKrecorder encoder probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, Windows {Environment.OSVersion.Version}");
        EnsureStarted();
        Section(report, "AAC matrix", () => AacMatrix(folder, report));
        foreach (bool fragmented in new[] { true, false })
        {
            string path = Path.Combine(folder, fragmented ? "probe-fragmented.mp4" : "probe-regular.mp4");
            Section(report, $"Write {Path.GetFileName(path)}", () => WriteSynthetic(path, fragmented, report));
            Section(report, $"Boxes of {Path.GetFileName(path)}", () => report.AppendLine(Mp4BoxDump.Dump(path, 3)));
            Section(report, $"Decode {Path.GetFileName(path)}", () => MeasureSync(path, report));
        }

        File.WriteAllText(Path.Combine(folder, "probe-report.txt"), report.ToString());
        Log.Info($"Probe report:{Environment.NewLine}{report}");
        return 0;
    }

    private static void Section(StringBuilder report, string name, Action action)
    {
        report.AppendLine();
        report.AppendLine($"===== {name}");
        try
        {
            action();
        }
        catch (Exception ex)
        {
            report.AppendLine($"FAILED: {ex}");
        }
    }

    private static void AacMatrix(string folder, StringBuilder report)
    {
        int[] byteRates = [4000, 6000, 8000, 10000, 12000, 14000, 16000, 18000, 20000, 24000, 28000, 32000, 40000];
        foreach (int rate in new[] { 48000, 44100 })
        {
            foreach (int channels in new[] { 1, 2 })
            {
                var accepted = new List<string>();
                var refused = new List<string>();
                foreach (int bytes in byteRates)
                {
                    string? error = TryAac(Path.Combine(folder, "aac-probe.mp4"), rate, channels, bytes);
                    (error is null ? accepted : refused).Add(Invariant($"{bytes * 8 / 1000}k"));
                    if (error is not null && bytes == 16000)
                    {
                        report.AppendLine($"  (128k refused because: {error})");
                    }
                }

                report.AppendLine(Invariant($"  {rate} Hz, {channels} ch: accepted [{string.Join(", ", accepted)}]; refused [{string.Join(", ", refused)}]"));
            }
        }
    }

    private static string? TryAac(string path, int rate, int channels, int bytesPerSecond)
    {
        IMFAttributes* attributes = null;
        IMFSinkWriter* writer = null;
        IMFMediaType* output = null;
        IMFMediaType* input = null;
        try
        {
            Check(MFCreateAttributes(&attributes, 1), "MFCreateAttributes");
            Check(attributes->SetGUID(Ptr(in MF.MF_TRANSCODE_CONTAINERTYPE), Ptr(in MFTranscodeContainerType.MFTranscodeContainerType_MPEG4)), "set container");
            fixed (char* file = path)
            {
                Check(MFCreateSinkWriterFromURL(file, null, attributes, &writer), "MFCreateSinkWriterFromURL");
            }

            output = AudioType(in MFAudioFormat.MFAudioFormat_AAC, rate, channels, bytesPerSecond);
            uint stream;
            Check(writer->AddStream(output, &stream), "AddStream(AAC)");
            input = AudioType(in MFAudioFormat.MFAudioFormat_PCM, rate, channels, rate * channels * 2);
            Check(writer->SetInputMediaType(stream, input, null), "SetInputMediaType(PCM)");
            Check(writer->BeginWriting(), "BeginWriting");
            return null;
        }
        catch (MediaFoundationException ex)
        {
            return ex.Message;
        }
        finally
        {
            if (input != null)
            {
                input->Release();
            }

            if (output != null)
            {
                output->Release();
            }

            if (writer != null)
            {
                writer->Release();
            }

            if (attributes != null)
            {
                attributes->Release();
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static IMFMediaType* AudioType(in Guid subtype, int rate, int channels, int bytesPerSecond)
    {
        IMFMediaType* type;
        Check(MFCreateMediaType(&type), "MFCreateMediaType");
        Check(type->SetGUID(Ptr(in MF.MF_MT_MAJOR_TYPE), Ptr(in MFMediaType_Audio)), "major");
        Check(type->SetGUID(Ptr(in MF.MF_MT_SUBTYPE), Ptr(in subtype)), "subtype");
        Check(type->SetUINT32(Ptr(in MF.MF_MT_AUDIO_BITS_PER_SAMPLE), 16), "bits");
        Check(type->SetUINT32(Ptr(in MF.MF_MT_AUDIO_SAMPLES_PER_SECOND), (uint)rate), "rate");
        Check(type->SetUINT32(Ptr(in MF.MF_MT_AUDIO_NUM_CHANNELS), (uint)channels), "channels");
        Check(type->SetUINT32(Ptr(in MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND), (uint)bytesPerSecond), "bytes");
        if (subtype == MFAudioFormat.MFAudioFormat_AAC)
        {
            Check(type->SetUINT32(Ptr(in MF.MF_MT_AAC_PAYLOAD_TYPE), 0), "payload");
            Check(type->SetUINT32(Ptr(in MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION), 0x29), "profile");
        }
        else
        {
            Check(type->SetUINT32(Ptr(in MF.MF_MT_AUDIO_BLOCK_ALIGNMENT), (uint)(channels * 2)), "align");
            Check(type->SetUINT32(Ptr(in MF.MF_MT_ALL_SAMPLES_INDEPENDENT), 1), "independent");
        }

        return type;
    }

    /// <summary>
    /// 6 s of 1280x720 30 fps video (black, with a white frame at 1, 2, 3, 4 and 5 s) and 48 kHz stereo audio
    /// (silence, with a 1 kHz beep starting at exactly 1, 2, 3, 4 and 5 s). Audio is written 0.6 s behind the video,
    /// as the mixer does. The fragmented file is written in real time and looked at every second.
    /// </summary>
    private static void WriteSynthetic(string path, bool fragmented, StringBuilder report)
    {
        var size = new Size(1280, 720);
        var preset = new RecordingPreset("probe", 1280, 720, 30, 4_000_000, 10_000_000, 2);
        using H264Mp4Writer writer = H264Mp4Writer.Create(path, size, preset, null, gpuInput: false, hardwareAllowed: false, new AdapterInfo("probe", 0, 0), withAudio: true, fragmented);
        report.AppendLine($"  writer: {writer.Encoder.Summary} ({writer.Encoder.Plan}); audio track {writer.HasAudio} {writer.AudioError}");
        byte[] black = Nv12(size, 16);
        byte[] white = Nv12(size, 235);
        var chunk = new short[960 * 2];
        long audioWritten = 0;
        const int Seconds = 6;
        const int Fps = 30;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < Seconds * Fps; i++)
        {
            bool flash = i > 0 && i % Fps == 0;
            writer.WriteNv12(flash ? white : black, preset.FrameTime(i), preset.FrameTime(i + 1) - preset.FrameTime(i));
            long due = ((long)(i + 1) * 48000 / Fps) - 28800;
            while (audioWritten + 960 <= due)
            {
                WriteBeepChunk(writer, chunk, audioWritten);
                audioWritten += 960;
            }

            if (fragmented)
            {
                long wait = (long)((i + 1) * 1000.0 / Fps) - clock.ElapsedMilliseconds;
                if (wait > 0)
                {
                    Thread.Sleep((int)wait);
                }

                if (i % Fps == Fps - 1)
                {
                    report.AppendLine(Invariant($"  after {(i + 1) / Fps} s of video (audio to {audioWritten / 48000.0:0.00} s): {TopLevel(path)}"));
                }
            }
        }

        while (audioWritten < Seconds * 48000)
        {
            WriteBeepChunk(writer, chunk, audioWritten);
            audioWritten += 960;
        }

        report.AppendLine($"  before Finalize: {TopLevel(path)}");
        var finalize = Stopwatch.StartNew();
        writer.FinishFile();
        report.AppendLine(Invariant($"  Finalize took {finalize.ElapsedMilliseconds} ms; after: {TopLevel(path)}"));
        report.AppendLine($"  video: {writer.Statistics()}; audio: {writer.AudioStatistics()}");
    }

    private static void WriteBeepChunk(H264Mp4Writer writer, short[] chunk, long start)
    {
        for (int j = 0; j < 960; j++)
        {
            long n = start + j;
            short value = n >= 48000 && n % 48000 < 4800 ? (short)(16000 * Math.Sin(2 * Math.PI * 1000 * n / 48000.0)) : (short)0;
            chunk[2 * j] = value;
            chunk[(2 * j) + 1] = value;
        }

        writer.WriteAudio(chunk, start * 10_000_000 / 48000, 960L * 10_000_000 / 48000);
    }

    private static byte[] Nv12(Size size, byte luma)
    {
        var frame = new byte[size.Width * size.Height * 3 / 2];
        Array.Fill(frame, luma, 0, size.Width * size.Height);
        Array.Fill(frame, (byte)128, size.Width * size.Height, frame.Length - (size.Width * size.Height));
        return frame;
    }

    private static string TopLevel(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var boxes = new List<string>();
            long position = 0;
            var head = new byte[16];
            while (position + 8 <= stream.Length && boxes.Count < 40)
            {
                stream.Position = position;
                stream.ReadExactly(head, 0, 8);
                long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head);
                string type = Encoding.ASCII.GetString(head, 4, 4);
                if (size == 1)
                {
                    stream.ReadExactly(head, 8, 8);
                    size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8));
                }
                else if (size == 0)
                {
                    size = stream.Length - position;
                }

                boxes.Add(position + size > stream.Length ? $"{type}({size}, cut at {stream.Length - position})" : $"{type}({size})");
                if (size < 8)
                {
                    break;
                }

                position += size;
            }

            return Invariant($"{stream.Length:N0} bytes: {string.Join(" ", boxes)}");
        }
        catch (Exception ex)
        {
            return $"cannot read while open: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Decodes the file with Media Foundation and reports where the white frames and the beeps come out.</summary>
    private static void MeasureSync(string path, StringBuilder report)
    {
        List<double> flashes = DecodeVideo(path, report);
        List<double> beeps = DecodeAudio(path, report);
        report.AppendLine($"  white frames at: {string.Join(", ", flashes.Select(t => Invariant($"{t:0.0000} s")))}");
        report.AppendLine($"  beeps start at: {string.Join(", ", beeps.Select(t => Invariant($"{t:0.0000} s")))}");
        foreach (double flash in flashes)
        {
            double? beep = beeps.Count == 0 ? null : beeps.MinBy(b => Math.Abs(b - flash));
            report.AppendLine(beep is { } b
                ? Invariant($"  flash {flash:0.0000} s -> beep {b:0.0000} s: audio is {(b - flash) * 1000:+0.00;-0.00} ms from the picture")
                : Invariant($"  flash {flash:0.0000} s -> no beep found"));
        }
    }

    private static List<double> DecodeVideo(string path, StringBuilder report)
    {
        var flashes = new List<double>();
        IMFSourceReader* reader = OpenReader(path);
        IMFMediaType* type = null;
        try
        {
            reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE);
            Check(reader->SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE), "select video");
            Check(MFCreateMediaType(&type), "MFCreateMediaType");
            Check(type->SetGUID(Ptr(in MF.MF_MT_MAJOR_TYPE), Ptr(in MFMediaType_Video)), "major");
            Check(type->SetGUID(Ptr(in MF.MF_MT_SUBTYPE), Ptr(in MFVideoFormat.MFVideoFormat_NV12)), "subtype");
            Check(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, null, type), "SetCurrentMediaType(NV12)");
            int frames = 0;
            long first = -1;
            long last = 0;
            bool wasWhite = false;
            while (true)
            {
                uint stream;
                uint flags;
                long time;
                IMFSample* sample = null;
                Check(reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &stream, &flags, &time, &sample), "ReadSample(video)");
                if (sample != null)
                {
                    frames++;
                    first = first < 0 ? time : first;
                    last = time;
                    double mean = MeanOfStart(sample, 1280 * 720);
                    bool white = mean > 128;
                    if (white && !wasWhite)
                    {
                        flashes.Add(time / 1e7);
                    }

                    wasWhite = white;
                    sample->Release();
                }

                if ((flags & (MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_ERROR)) != 0)
                {
                    break;
                }
            }

            report.AppendLine(Invariant($"  video decoded: {frames} frames, first at {first / 1e7:0.0000} s, last at {last / 1e7:0.0000} s"));
        }
        finally
        {
            if (type != null)
            {
                type->Release();
            }

            reader->Release();
        }

        return flashes;
    }

    private static List<double> DecodeAudio(string path, StringBuilder report)
    {
        var onsets = new List<double>();
        IMFSourceReader* reader = OpenReader(path);
        IMFMediaType* type = null;
        IMFMediaType* current = null;
        try
        {
            reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE);
            Check(reader->SetStreamSelection(MF_SOURCE_READER_FIRST_AUDIO_STREAM, TRUE), "select audio");
            Check(MFCreateMediaType(&type), "MFCreateMediaType");
            Check(type->SetGUID(Ptr(in MF.MF_MT_MAJOR_TYPE), Ptr(in MFMediaType_Audio)), "major");
            Check(type->SetGUID(Ptr(in MF.MF_MT_SUBTYPE), Ptr(in MFAudioFormat.MFAudioFormat_PCM)), "subtype");
            Check(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, null, type), "SetCurrentMediaType(PCM)");
            Check(reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, &current), "GetCurrentMediaType");
            int channels = (int)(GetUInt32((IMFAttributes*)current, in MF.MF_MT_AUDIO_NUM_CHANNELS) ?? 2);
            int rate = (int)(GetUInt32((IMFAttributes*)current, in MF.MF_MT_AUDIO_SAMPLES_PER_SECOND) ?? 48000);
            int bits = (int)(GetUInt32((IMFAttributes*)current, in MF.MF_MT_AUDIO_BITS_PER_SAMPLE) ?? 16);
            report.AppendLine(Invariant($"  audio decodes to {rate} Hz, {channels} ch, {bits} bit"));
            long quietRun = rate;
            long buffers = 0;
            long first = -1;
            long decodedFrames = 0;
            long lastEnd = 0;
            while (true)
            {
                uint stream;
                uint flags;
                long time;
                IMFSample* sample = null;
                Check(reader->ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &stream, &flags, &time, &sample), "ReadSample(audio)");
                if (sample != null)
                {
                    buffers++;
                    first = first < 0 ? time : first;
                    IMFMediaBuffer* buffer;
                    Check(sample->ConvertToContiguousBuffer(&buffer), "ConvertToContiguousBuffer");
                    byte* data;
                    uint max;
                    uint length;
                    Check(buffer->Lock(&data, &max, &length), "Lock");
                    int frames = (int)length / (2 * channels);
                    short* pcm = (short*)data;
                    for (int i = 0; i < frames; i++)
                    {
                        int value = Math.Abs((int)pcm[i * channels]);
                        if (value > 3000)
                        {
                            if (quietRun >= rate / 20)
                            {
                                onsets.Add((time / 1e7) + (i / (double)rate));
                            }

                            quietRun = 0;
                        }
                        else
                        {
                            quietRun++;
                        }
                    }

                    decodedFrames += frames;
                    lastEnd = time + (frames * 10_000_000L / rate);
                    buffer->Unlock();
                    buffer->Release();
                    sample->Release();
                }

                if ((flags & (MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_ERROR)) != 0)
                {
                    break;
                }
            }

            report.AppendLine(Invariant($"  audio decoded: {buffers} buffers, {decodedFrames} frames ({decodedFrames / (double)rate:0.0000} s), first at {first / 1e7:0.0000} s, ends at {lastEnd / 1e7:0.0000} s"));
        }
        finally
        {
            if (current != null)
            {
                current->Release();
            }

            if (type != null)
            {
                type->Release();
            }

            reader->Release();
        }

        return onsets;
    }

    private static IMFSourceReader* OpenReader(string path)
    {
        IMFSourceReader* reader;
        fixed (char* file = path)
        {
            Check(MFCreateSourceReaderFromURL(file, null, &reader), "MFCreateSourceReaderFromURL");
        }

        return reader;
    }

    private static double MeanOfStart(IMFSample* sample, int count)
    {
        IMFMediaBuffer* buffer;
        Check(sample->ConvertToContiguousBuffer(&buffer), "ConvertToContiguousBuffer");
        try
        {
            byte* data;
            uint max;
            uint length;
            Check(buffer->Lock(&data, &max, &length), "Lock");
            long sum = 0;
            int n = (int)Math.Min(count, length);
            for (int i = 0; i < n; i += 7)
            {
                sum += data[i];
            }

            buffer->Unlock();
            return n == 0 ? 0 : sum / (double)((n + 6) / 7);
        }
        finally
        {
            buffer->Release();
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
