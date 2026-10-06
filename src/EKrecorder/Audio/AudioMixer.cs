using System.Globalization;
using EKrecorder.Diagnostics;

namespace EKrecorder.Audio;

/// <summary>Receives mixed 16-bit stereo audio with its time in the file (100-ns units).</summary>
internal delegate void AudioChunkSink(ReadOnlySpan<short> interleaved, long time, long duration);

/// <summary>
/// The mixer thread. Running 600 ms behind real time, it sums every capture stream's stretch of the timeline
/// (silence where a stream has nothing: quiet loopback, a lost device), limits the result to -1 dBFS, converts it
/// to 16-bit stereo and hands it to the AAC encoder in 20 ms chunks. Being that far behind gives every device
/// time to deliver, even after a long pause of a capture thread, so audio is only lost when a device loses it.
/// The output never stops or skips: one chunk follows the other for the whole recording, ending exactly with the
/// video.
/// </summary>
internal sealed class AudioMixer
{
    public const int ChunkFrames = 960;
    public const double DelaySeconds = 0.6;

    private readonly IReadOnlyList<TimelineRing> _rings;
    private readonly TimelineClock _clock;
    private readonly AudioChunkSink _sink;
    private readonly PeakLimiter _limiter = new();
    private readonly float[] _left = new float[ChunkFrames];
    private readonly float[] _right = new float[ChunkFrames];
    private readonly short[] _pcm = new short[ChunkFrames * 2];
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private long _end = long.MaxValue;
    private long _position;

    public AudioMixer(IReadOnlyList<TimelineRing> rings, TimelineClock clock, AudioChunkSink sink)
    {
        _rings = rings;
        _clock = clock;
        _sink = sink;
        _thread = new Thread(Run) { Name = "EKrecorder audio mixer", IsBackground = true, Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Timeline frames written to the file so far.</summary>
    public long WrittenFrames => Volatile.Read(ref _position);

    /// <summary>Why the audio track ended early, if it did.</summary>
    public string? Error { get; private set; }

    public PeakLimiter Limiter => _limiter;

    /// <summary>The furthest the mixer ever fell behind its schedule, in seconds (a stalled encoder would show here).</summary>
    public double MaxBacklogSeconds { get; private set; }

    public void Start() => _thread.Start();

    /// <summary>
    /// Writes the audio up to exactly <paramref name="endFrame"/> (the end of the video), waiting until the devices
    /// have delivered it, then stops. False if that did not happen within <paramref name="timeout"/>.
    /// </summary>
    public bool Finish(long endFrame, TimeSpan timeout)
    {
        Volatile.Write(ref _end, endFrame);
        _wake.Set();
        bool finished = _thread.Join(timeout);
        if (!finished)
        {
            Log.Warn($"The audio mixer did not finish within {timeout.TotalSeconds:0} s.");
        }

        return finished;
    }

    private void Run()
    {
        nint mmcss = CoreAudio.EnterMmcss();
        long delayFrames = (long)(DelaySeconds * TimelineClock.SampleRate);
        try
        {
            while (true)
            {
                long end = Volatile.Read(ref _end);
                long available = (long)Math.Floor(_clock.PositionOf(TimelineClock.NowHns())) - delayFrames;
                MaxBacklogSeconds = Math.Max(MaxBacklogSeconds, (available - _position) / (double)TimelineClock.SampleRate);
                while (true)
                {
                    long limit = Math.Min(available, end);
                    long count = Math.Min(ChunkFrames, limit - _position);
                    // Whole chunks while recording; the last, shorter one only once the end has been captured.
                    if (count <= 0 || (count < ChunkFrames && (end == long.MaxValue || available < end)))
                    {
                        break;
                    }

                    MixChunk((int)count);
                }

                if (_position >= end)
                {
                    break;
                }

                _wake.WaitOne(10);
            }
        }
        catch (Exception ex)
        {
            Error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error("The audio mixer stopped; the audio track ends here", ex);
        }
        finally
        {
            CoreAudio.LeaveMmcss(mmcss);
            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"Audio mixer finished: {_position / (double)TimelineClock.SampleRate:0.000} s written; limiter acted on {_limiter.LimitedSamples:N0} samples (deepest {_limiter.MaxReductionDb:0.0} dB); largest backlog {MaxBacklogSeconds:0.00} s"));
        }
    }

    private void MixChunk(int count)
    {
        Span<float> left = _left.AsSpan(0, count);
        Span<float> right = _right.AsSpan(0, count);
        AudioMix.Sum(_rings, _position, left, right);
        _limiter.Process(left, right);
        Span<short> pcm = _pcm.AsSpan(0, count * 2);
        AudioMix.ToPcm16(left, right, pcm);
        long time = TimelineClock.TimeOf(_position);
        _sink(pcm, time, TimelineClock.TimeOf(_position + count) - time);
        Volatile.Write(ref _position, _position + count);
    }
}
