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
    double PeakBitsPerSecond,
    AudioTrackCheck Audio,
    TimeSpan VideoDuration);

/// <summary>The file's audio track, read back.</summary>
internal sealed record AudioTrackCheck(bool Present, string Codec, int SampleRate, int Channels, double KilobitsPerSecond, TimeSpan Duration, long Packets, string? Error)
{
    public static AudioTrackCheck None(string? error) => new(false, "", 0, 0, 0, TimeSpan.Zero, 0, error);

    public string Describe() => Present
        ? FormattableString.Invariant($"{Codec}, {SampleRate} Hz, {Channels} channel(s), {KilobitsPerSecond:0} kbps, {Duration.TotalSeconds:0.000} s ({Packets:N0} packets)")
        : $"no audio track{(Error is null ? "" : $" ({Error})")}";
}

/// <summary>
/// Reads a finished recording back with the Media Foundation source reader, without decoding: duration, size,
/// frame rate, profile, every frame's size and keyframe flag. That gives the real bitrate, the busiest second and the
/// real keyframe spacing, and proves the file opens.
/// </summary>
internal static unsafe class Mp4Inspector
{
    private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    private const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
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

            // Video only in this pass; otherwise the reader would queue every audio packet while we read video.
            reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE);
            Check(reader->SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE), "IMFSourceReader::SetStreamSelection(video)");
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
            long videoEnd = 0;
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
                    long frameDuration;
                    videoEnd = Math.Max(videoEnd, timestamp + (sample->GetSampleDuration(&frameDuration).SUCCEEDED ? frameDuration : 0));
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
                peak,
                InspectAudio(path),
                TimeSpan.FromTicks(videoEnd));
            Log.Info($"File check: audio {check.Audio.Describe()}");
            Log.Info($"File check: {check.Width}x{check.Height}, {check.FramesPerSecond:0.##} fps, {check.Codec} {check.Profile}, {check.Duration.TotalSeconds:0.0} s, {check.Frames} frames, {check.Keyframes} keyframes (every {check.AverageKeyframeSpacing:0.#} frames), {check.AverageBitsPerSecond / 1e6:0.00} Mbps average, {check.PeakBitsPerSecond / 1e6:0.00} Mbps busiest second");
            return check;
        }
        catch (Exception ex)
        {
            Log.Error($"Reading back {path} failed", ex);
            return new Mp4Check(false, ex.Message, TimeSpan.Zero, 0, 0, 0, "", "", 0, 0, 0, fileBytes, 0, 0, AudioTrackCheck.None(null), TimeSpan.Zero);
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

    /// <summary>
    /// Opens the file the way Windows' own players do, reads the first picture and the first sound, then jumps near
    /// the end and reads a picture there. Fast for any length. Null when all of that works, else what failed.
    /// Video and sound are read with separate readers (switching streams on one reader mid-way is unreliable).
    /// </summary>
    public static string? QuickCheck(string path)
    {
        try
        {
            EnsureStarted();
            string? video = CheckStream(path, MF_SOURCE_READER_FIRST_VIDEO_STREAM, seekNearEnd: true);
            if (video is not null)
            {
                return video;
            }

            return CheckStream(path, MF_SOURCE_READER_FIRST_AUDIO_STREAM, seekNearEnd: false);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? CheckStream(string path, uint stream, bool seekNearEnd)
    {
        bool video = stream == MF_SOURCE_READER_FIRST_VIDEO_STREAM;
        IMFSourceReader* reader = null;
        try
        {
            fixed (char* file = path)
            {
                Check(MFCreateSourceReaderFromURL(file, null, &reader), "MFCreateSourceReaderFromURL");
            }

            reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE);
            if (reader->SetStreamSelection(stream, TRUE).FAILED)
            {
                // A file without a sound track is reported by the full check, not here.
                return video ? "the file has no video track" : null;
            }

            if (!ReadOne(reader, stream))
            {
                return video ? "no picture could be read" : "no sound could be read";
            }

            long duration = 0;
            PROPVARIANT value = default;
            if (seekNearEnd && reader->GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, Ptr(in MF.MF_PD_DURATION), &value).SUCCEEDED)
            {
                duration = (long)*(ulong*)((byte*)&value + 8);
                PropVariantClear(&value);
            }

            if (duration > 30_000_000)
            {
                PROPVARIANT position = default;
                *(ushort*)&position = 20; // VT_I8
                *(long*)((byte*)&position + 8) = duration - 20_000_000;
                Guid timeFormat = Guid.Empty;
                Check(reader->SetCurrentPosition(&timeFormat, &position), "IMFSourceReader::SetCurrentPosition");
                if (!ReadOne(reader, stream))
                {
                    return "the end of the file could not be read";
                }
            }

            return null;
        }
        finally
        {
            if (reader != null)
            {
                reader->Release();
            }
        }
    }

    private static bool ReadOne(IMFSourceReader* reader, uint stream)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            uint index;
            uint flags;
            long time;
            IMFSample* sample = null;
            Check(reader->ReadSample(stream, 0, &index, &flags, &time, &sample), "IMFSourceReader::ReadSample");
            if (sample != null)
            {
                sample->Release();
                return true;
            }

            if ((flags & (MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_ERROR)) != 0)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Reads the audio track on its own: codec, rate, channels, and every packet's length and time.</summary>
    private static AudioTrackCheck InspectAudio(string path)
    {
        IMFSourceReader* reader = null;
        IMFMediaType* type = null;
        try
        {
            fixed (char* file = path)
            {
                Check(MFCreateSourceReaderFromURL(file, null, &reader), "MFCreateSourceReaderFromURL");
            }

            reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE);
            if (reader->SetStreamSelection(MF_SOURCE_READER_FIRST_AUDIO_STREAM, TRUE).FAILED
                || reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &type).FAILED)
            {
                return AudioTrackCheck.None(null);
            }

            Guid subtype;
            string codec = type->GetGUID(Ptr(in MF.MF_MT_SUBTYPE), &subtype).SUCCEEDED && subtype == MFAudioFormat.MFAudioFormat_AAC ? "AAC" : "not AAC";
            int rate = (int)(GetUInt32((IMFAttributes*)type, in MF.MF_MT_AUDIO_SAMPLES_PER_SECOND) ?? 0);
            int channels = (int)(GetUInt32((IMFAttributes*)type, in MF.MF_MT_AUDIO_NUM_CHANNELS) ?? 0);
            long packets = 0;
            long bytes = 0;
            long end = 0;
            int emptyReads = 0;
            while (emptyReads < 1000)
            {
                uint streamIndex;
                uint flags;
                long timestamp;
                IMFSample* sample = null;
                Check(reader->ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &streamIndex, &flags, &timestamp, &sample), "IMFSourceReader::ReadSample(audio)");
                emptyReads = sample == null ? emptyReads + 1 : 0;
                if (sample != null)
                {
                    uint length;
                    long sampleDuration;
                    sample->GetTotalLength(&length);
                    bytes += length;
                    packets++;
                    end = Math.Max(end, timestamp + (sample->GetSampleDuration(&sampleDuration).SUCCEEDED ? sampleDuration : 0));
                    sample->Release();
                }

                if ((flags & (MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_ERROR)) != 0)
                {
                    break;
                }
            }

            double seconds = end / 1e7;
            return new AudioTrackCheck(true, codec, rate, channels, seconds > 0 ? bytes * 8 / seconds / 1000 : 0, TimeSpan.FromTicks(end), packets, null);
        }
        catch (Exception ex)
        {
            Log.Error($"Reading the audio track of {path} back failed", ex);
            return AudioTrackCheck.None(ex.Message);
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
