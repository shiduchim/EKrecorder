using EKrecorder.Diagnostics;
using TerraFX.Interop.Windows;
using static EKrecorder.Recording.MediaFoundation;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Recording;

/// <summary>What a finished MP4 actually contains, read back from the file.</summary>
internal sealed record Mp4Check(
    bool Readable,
    string? Error,
    TimeSpan Duration,
    int Width,
    int Height,
    double FramesPerSecond,
    string Codec,
    string Profile,
    long Frames,
    long Keyframes,
    double AverageKeyframeSpacing,
    long FileBytes,
    double AverageBitsPerSecond,
    double PeakBitsPerSecond);

/// <summary>
/// Reads a finished recording back with the Media Foundation source reader, without decoding: duration, size,
/// frame rate, profile, every frame's size and keyframe flag. That gives the real bitrate, the busiest second and the
/// real keyframe spacing, and proves the file opens.
/// </summary>
internal static unsafe class Mp4Inspector
{
    private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    private const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;
    private const uint MF_SOURCE_READERF_ERROR = 0x1;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;

    public static Mp4Check Inspect(string path)
    {
        long fileBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        IMFSourceReader* reader = null;
        IMFMediaType* type = null;
        try
        {
            EnsureStarted();
            fixed (char* file = path)
            {
                Check(MFCreateSourceReaderFromURL(file, null, &reader), "MFCreateSourceReaderFromURL");
            }

            long duration = 0;
            PROPVARIANT value = default;
            if (reader->GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, Ptr(in MF.MF_PD_DURATION), &value).SUCCEEDED)
            {
                // PROPVARIANT: the type tag is the first 16 bits, the value starts at byte 8 (VT_UI8 here).
                duration = (long)*(ulong*)((byte*)&value + 8);
                PropVariantClear(&value);
            }

            Check(reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &type), "IMFSourceReader::GetNativeMediaType");
            ulong frameSize = 0;
            ulong frameRate = 0;
            type->GetUINT64(Ptr(in MF.MF_MT_FRAME_SIZE), &frameSize);
            type->GetUINT64(Ptr(in MF.MF_MT_FRAME_RATE), &frameRate);
            Guid subtype;
            string codec = type->GetGUID(Ptr(in MF.MF_MT_SUBTYPE), &subtype).SUCCEEDED && subtype == MFVideoFormat.MFVideoFormat_H264 ? "H.264" : "not H.264";
            string profile = GetUInt32((IMFAttributes*)type, in MF.MF_MT_MPEG2_PROFILE) switch
            {
                66 => "Baseline",
                77 => "Main",
                100 => "High",
                null => "not reported",
                uint other => $"profile {other}",
            };

            long frames = 0;
            long keyframes = 0;
            long bytes = 0;
            long lastKeyframe = -1;
            double spacingSum = 0;
            int spacings = 0;
            var bytesPerSecond = new Dictionary<long, long>();
            int emptyReads = 0;
            while (emptyReads < 1000)
            {
                uint streamIndex;
                uint flags;
                long timestamp;
                IMFSample* sample = null;
                Check(reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &streamIndex, &flags, &timestamp, &sample), "IMFSourceReader::ReadSample");
                emptyReads = sample == null ? emptyReads + 1 : 0;
                if (sample != null)
                {
                    uint length;
                    sample->GetTotalLength(&length);
                    bytes += length;
                    long second = timestamp / 10_000_000;
                    bytesPerSecond[second] = bytesPerSecond.GetValueOrDefault(second) + length;
                    uint cleanPoint;
                    if (sample->GetUINT32(Ptr(in MFSampleExtension_CleanPoint), &cleanPoint).SUCCEEDED && cleanPoint != 0)
                    {
                        if (lastKeyframe >= 0)
                        {
                            spacingSum += frames - lastKeyframe;
                            spacings++;
                        }

                        lastKeyframe = frames;
                        keyframes++;
                    }

                    frames++;
                    sample->Release();
                }

                if ((flags & (MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_ERROR)) != 0)
                {
                    break;
                }
            }

            double seconds = duration / 1e7;
            double average = seconds > 0 ? bytes * 8 / seconds : 0;
            // Busiest whole second (the last, partial second is left out unless it is the only one).
            long lastSecond = bytesPerSecond.Count > 0 ? bytesPerSecond.Keys.Max() : 0;
            double peak = bytesPerSecond.Count == 0 ? 0
                : bytesPerSecond.Where(p => p.Key < lastSecond || bytesPerSecond.Count == 1).Select(p => p.Value * 8.0).DefaultIfEmpty(0).Max();
            uint numerator = (uint)(frameRate >> 32);
            uint denominator = (uint)frameRate;
            var check = new Mp4Check(
                true,
                null,
                TimeSpan.FromTicks(duration),
                (int)(frameSize >> 32),
                (int)(uint)frameSize,
                denominator == 0 ? 0 : (double)numerator / denominator,
                codec,
                profile,
                frames,
                keyframes,
                spacings > 0 ? spacingSum / spacings : 0,
                fileBytes,
                average,
                peak);
            Log.Info($"File check: {check.Width}x{check.Height}, {check.FramesPerSecond:0.##} fps, {check.Codec} {check.Profile}, {check.Duration.TotalSeconds:0.0} s, {check.Frames} frames, {check.Keyframes} keyframes (every {check.AverageKeyframeSpacing:0.#} frames), {check.AverageBitsPerSecond / 1e6:0.00} Mbps average, {check.PeakBitsPerSecond / 1e6:0.00} Mbps busiest second");
            return check;
        }
        catch (Exception ex)
        {
            Log.Error($"Reading back {path} failed", ex);
            return new Mp4Check(false, ex.Message, TimeSpan.Zero, 0, 0, 0, "", "", 0, 0, 0, fileBytes, 0, 0);
        }
        finally
        {
            if (type != null)
            {
                type->Release();
            }

            if (reader != null)
            {
                reader->Release();
            }
        }
    }
}
