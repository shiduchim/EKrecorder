namespace EKrecorder.Audio;

/// <summary>
/// A stereo-linked peak limiter: no sample leaves it above -1 dBFS, so the mix of microphone and computer audio never
/// clips before AAC encoding. The gain drops at once on a peak and recovers over about 150 ms; ordinary levels pass
/// unchanged.
/// </summary>
internal sealed class PeakLimiter
{
    /// <summary>-1 dBFS.</summary>
    public const float Ceiling = 0.8912509f;

    private readonly float _release;
    private float _gain = 1f;

    public PeakLimiter(int sampleRate = TimelineClock.SampleRate, double releaseSeconds = 0.15)
    {
        _release = (float)(1 - Math.Exp(-1.0 / (releaseSeconds * sampleRate)));
    }

    /// <summary>Samples the limiter turned down.</summary>
    public long LimitedSamples { get; private set; }

    /// <summary>The deepest gain reduction so far, in dB (0 when it never acted).</summary>
    public double MaxReductionDb { get; private set; }

    public void Process(Span<float> left, Span<float> right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            float peak = Math.Max(Math.Abs(left[i]), Math.Abs(right[i]));
            float target = peak > Ceiling ? Ceiling / peak : 1f;
            _gain = target < _gain ? target : _gain + ((target - _gain) * _release);
            if (_gain < 1f)
            {
                LimitedSamples++;
                MaxReductionDb = Math.Max(MaxReductionDb, -20 * Math.Log10(_gain));
            }

            // The clamp only catches what float rounding might leave a hair above the ceiling.
            left[i] = Math.Clamp(left[i] * _gain, -Ceiling, Ceiling);
            right[i] = Math.Clamp(right[i] * _gain, -Ceiling, Ceiling);
        }
    }
}

/// <summary>The mixer's steps that do not touch Windows: summing the feeds and making 16-bit PCM.</summary>
internal static class AudioMix
{
    /// <summary>
    /// Sums every ring's positions [<paramref name="start"/>, start + length) into the buffers (cleared first).
    /// A feed with nothing there (silence, no packets, a lost device) adds nothing.
    /// </summary>
    public static void Sum(IReadOnlyList<TimelineRing> rings, long start, Span<float> left, Span<float> right)
    {
        left.Clear();
        right.Clear();
        for (int i = 0; i < rings.Count; i++)
        {
            rings[i].MixInto(start, left, right);
        }
    }

    /// <summary>Interleaves two float channels into 16-bit PCM, rounding and saturating.</summary>
    public static void ToPcm16(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<short> interleaved)
    {
        for (int i = 0; i < left.Length; i++)
        {
            interleaved[2 * i] = ToInt16(left[i]);
            interleaved[(2 * i) + 1] = ToInt16(right[i]);
        }
    }

    private static short ToInt16(float sample) => (short)Math.Clamp((int)MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);
}
