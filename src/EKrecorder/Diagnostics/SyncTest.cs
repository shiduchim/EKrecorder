using System.Globalization;
using System.Text;
using EKrecorder.Capture;
using EKrecorder.Mp4;
using EKrecorder.Recording;
using TerraFX.Interop.Windows;
using static EKrecorder.Recording.MediaFoundation;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Diagnostics;

/// <summary>The outcome of one audio/video timing check.</summary>
internal sealed record SyncResult(string Name, string File, IReadOnlyList<double> OffsetsMs, double AudioSeconds, double VideoSeconds, string Detail)
{
    /// <summary>The largest distance between a beep and its white frame, in ms (infinity when nothing matched).</summary>
    public double WorstMs => OffsetsMs.Count == 0 ? double.PositiveInfinity : OffsetsMs.Max(Math.Abs);
}

/// <summary>
/// End-to-end audio/video timing through the real writer, the real encoders, the crash-safe container and its
/// conversion: 6 seconds of black video with a white frame at 1, 2, 3, 4 and 5 s, and silence with a 1 kHz beep
/// starting at exactly the same moments (written the way the mixer writes, 0.6 s behind the video). The finished
/// file is decoded with Windows' own decoders and each beep is compared with its white frame. A constant AAC
/// encoder delay, a container timing error or a picture reordering delay would all show up here.
/// </summary>
internal static unsafe class SyncTest
{
    private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    private const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    private const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    private const uint MF_SOURCE_READERF_ERROR = 0x1;
    private const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
    private const int Seconds = 6;

    public static SyncResult Run(string path, string name, Size size, bool fragmented, int? forceBFrames, int audioBitrate = 128_000)
    {
        EnsureStarted();
        var detail = new StringBuilder();
        var preset = new RecordingPreset("sync test", size.Width, size.Height, RecordingQuality.FramesPerSecond, 4_000_000, 10_000_000, 2, audioBitrate);
        using (H264Mp4Writer writer = H264Mp4Writer.Create(path, size, preset, null, gpuInput: false, hardwareAllowed: false, new AdapterInfo("sync test", 0, 0), withAudio: true, fragmented, forceBFrames))
        {
            detail.Append(CultureInfo.InvariantCulture, $"{writer.Encoder.Summary} ({writer.Encoder.Plan}, B-frames {writer.Encoder.BFrames}); {(writer.IsFragmented ? "fragmented" : "regular")} MP4; ");
            Write(writer, size, preset);
            writer.FinishFile();
        }

        RepairResult repair = Mp4Repair.Run(path);
        detail.Append($"conversion: {repair.Outcome}; ");
        Mp4Summary summary = Mp4File.Summarize(path);
        if (summary.Video is { } video && summary.Audio is { } audio)
        {
            detail.Append(CultureInfo.InvariantCulture, $"index: video {video.Seconds:0.000} s (starts {video.StartSeconds * 1000:0.0} ms), audio {audio.Seconds:0.000} s; ");
        }

        List<double> flashes = DecodeVideo(path, size, detail);
        List<double> beeps = DecodeAudio(path, detail);
        var offsets = new List<double>();
        foreach (double flash in flashes)
        {
            if (beeps.Count > 0)
            {
                double beep = beeps.MinBy(b => Math.Abs(b - flash));
                if (Math.Abs(beep - flash) < 0.5)
                {
                    offsets.Add((beep - flash) * 1000);
                }
            }
        }

        detail.Append(CultureInfo.InvariantCulture, $"white frames at {string.Join(", ", flashes.Select(f => f.ToString("0.0000", CultureInfo.InvariantCulture)))} s; beeps at {string.Join(", ", beeps.Select(b => b.ToString("0.0000", CultureInfo.InvariantCulture)))} s");
        return new SyncResult(name, path, offsets, summary.Audio?.Seconds ?? 0, summary.Video?.Seconds ?? 0, detail.ToString());
    }

    private static void Write(H264Mp4Writer writer, Size size, RecordingPreset preset)
    {
        byte[] black = Nv12(size, 16);
        byte[] white = Nv12(size, 235);
        var chunk = new short[960 * 2];
        long audioWritten = 0;
        int fps = preset.FramesPerSecond;
        for (int i = 0; i < Seconds * fps; i++)
        {
            bool flash = i > 0 && i % fps == 0;
            writer.WriteNv12(flash ? white : black, preset.FrameTime(i), preset.FrameTime(i + 1) - preset.FrameTime(i));
            long due = ((long)(i + 1) * 48000 / fps) - 28800;
            while (audioWritten + 960 <= due)
            {
                WriteBeepChunk(writer, chunk, audioWritten);
                audioWritten += 960;
            }
        }

        while (audioWritten < Seconds * 48000)
        {
            WriteBeepChunk(writer, chunk, audioWritten);
            audioWritten += 960;
        }
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

    private static List<double> DecodeVideo(string path, Size size, StringBuilder detail)
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
                    bool white = MeanOfStart(sample, size.Width * size.Height) > 128;
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

            detail.Append(CultureInfo.InvariantCulture, $"decoded {frames} frames, the first at {first / 1e4:0.0} ms; ");
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

    private static List<double> DecodeAudio(string path, StringBuilder detail)
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
            long quietRun = rate;
            long first = -1;
            long decoded = 0;
            while (true)
            {
                uint stream;
                uint flags;
                long time;
                IMFSample* sample = null;
                Check(reader->ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &stream, &flags, &time, &sample), "ReadSample(audio)");
                if (sample != null)
                {
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
                        if (Math.Abs((int)pcm[i * channels]) > 3000)
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

                    decoded += frames;
                    buffer->Unlock();
                    buffer->Release();
                    sample->Release();
                }

                if ((flags & (MF_SOURCE_READERF_ENDOFSTREAM | MF_SOURCE_READERF_ERROR)) != 0)
                {
                    break;
                }
            }

            detail.Append(CultureInfo.InvariantCulture, $"decoded {decoded / (double)rate:0.000} s of sound from {first / 1e4:0.0} ms; ");
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
            int samples = 0;
            for (int i = 0; i < n; i += 7)
            {
                sum += data[i];
                samples++;
            }

            buffer->Unlock();
            return samples == 0 ? 0 : sum / (double)samples;
        }
        finally
        {
            buffer->Release();
        }
    }
}
