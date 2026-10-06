using System.Diagnostics;

namespace EKrecorder.Audio;

/// <summary>
/// The recording's audio timeline: 48 kHz sample positions counted from timeline start, on the
/// QueryPerformanceCounter clock that also paces the video. Every audio feed is placed on it by the QPC time of its
/// packets, never by counting packets, so silence, gaps and lost devices keep their real length.
/// </summary>
internal sealed class TimelineClock
{
    public const int SampleRate = 48_000;
    private const double SamplesPer100ns = SampleRate / 10_000_000.0;

    /// <param name="startHns">QPC time of timeline position 0, in 100-ns units.</param>
    public TimelineClock(long startHns) => StartHns = startHns;

    /// <summary>QPC time of timeline position 0, in 100-ns units.</summary>
    public long StartHns { get; }

    /// <summary>The timeline position (fractional samples) of a QPC time given in 100-ns units.</summary>
    public double PositionOf(long hns) => (hns - StartHns) * SamplesPer100ns;

    /// <summary>QPC ticks (Stopwatch timestamps) to 100-ns units, without overflow.</summary>
    public static long ToHns(long qpcTicks, long frequency) => (long)((Int128)qpcTicks * 10_000_000 / frequency);

    /// <summary>Now, as QPC time in 100-ns units (the unit WASAPI uses for packet timestamps).</summary>
    public static long NowHns() => ToHns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    /// <summary>The time of a timeline position in 100-ns units (the MP4 time base), rounded down.</summary>
    public static long TimeOf(long position) => (long)((Int128)position * 10_000_000 / SampleRate);
}

/// <summary>
/// One capture stream's audio on the timeline: stereo samples stored by timeline position. The capture thread
/// writes; the mixer reads. Every slot remembers which position it holds, so a position nobody wrote (no packets, a
/// lost device) reads as silence, and nothing stale is ever read again when the ring wraps.
/// </summary>
internal sealed class TimelineRing
{
    /// <summary>5.46 s at 48 kHz: far more than the mixer's delay behind real time.</summary>
    public const int Capacity = 1 << 18;
    private const int Mask = Capacity - 1;

    private readonly float[] _left = new float[Capacity];
    private readonly float[] _right = new float[Capacity];
    private readonly long[] _positions = new long[Capacity];
    private long _readLimit;
    private long _lastWritten = -1;

    public TimelineRing() => Array.Fill(_positions, -1L);

    /// <summary>Samples that arrived after the mixer had already passed their position (dropped).</summary>
    public long LateSamples { get; private set; }

    /// <summary>Samples so far ahead of the mixer that they would overwrite unread ones (dropped).</summary>
    public long OverrunSamples { get; private set; }

    public long WrittenSamples { get; private set; }

    /// <summary>The highest position written, or -1.</summary>
    public long LastWritten => Volatile.Read(ref _lastWritten);

    /// <summary>The mixer has read, or is reading, every position before this one.</summary>
    public long ReadLimit => Volatile.Read(ref _readLimit);

    /// <summary>True when the mixer has read everything ever written here.</summary>
    public bool IsDrained => LastWritten < ReadLimit;

    /// <summary>
    /// Capture thread: stores one stereo sample at <paramref name="position"/>. Returns false (and counts it) when
    /// the mixer is already past that position, or when it is so far ahead that it would overwrite unread samples.
    /// </summary>
    public bool Write(long position, float left, float right)
    {
        long readLimit = Volatile.Read(ref _readLimit);
        if (position < readLimit)
        {
            LateSamples++;
            return false;
        }

        if (position >= readLimit + Capacity)
        {
            OverrunSamples++;
            return false;
        }

        int slot = (int)(position & Mask);
        _left[slot] = left;
        _right[slot] = right;
        // Published last: the mixer only uses a slot whose position matches, so it never sees half a write.
        Volatile.Write(ref _positions[slot], position);
        WrittenSamples++;
        if (position > _lastWritten)
        {
            Volatile.Write(ref _lastWritten, position);
        }

        return true;
    }

    /// <summary>
    /// Mixer: adds positions [<paramref name="start"/>, start + length) to the buffers. Positions nobody wrote add
    /// nothing (silence). Calls must move forward through the timeline.
    /// </summary>
    public void MixInto(long start, Span<float> left, Span<float> right)
    {
        // Announced before reading, so a late writer drops its samples instead of leaving them for later.
        Volatile.Write(ref _readLimit, start + left.Length);
        for (int k = 0; k < left.Length; k++)
        {
            long position = start + k;
            int slot = (int)(position & Mask);
            if (Volatile.Read(ref _positions[slot]) == position)
            {
                left[k] += _left[slot];
                right[k] += _right[slot];
            }
        }
    }
}
