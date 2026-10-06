using EKrecorder.Audio;

namespace EKrecorder.Tests;

/// <summary>
/// The signal the simulated devices capture: a sawtooth that rises from 0 to 0.5 every 100 ms of true time. Read at
/// a timeline position it tells, to within nanoseconds, which moment of true time ended up there.
/// </summary>
internal static class TrueTimeSignal
{
    public const double Period = 0.1;
    public const double Amplitude = 0.5;

    public static double Phase(double seconds) => (seconds / Period) - Math.Floor(seconds / Period);

    public static float At(double seconds) => (float)(Amplitude * Phase(seconds));
}

/// <summary>
/// A capture device with its own clock: it takes <c>48 kHz × (1 + drift)</c> samples per true second and reports
/// each packet's start time with random jitter, like WASAPI's QPC timestamps.
/// </summary>
internal sealed class SimulatedDevice
{
    private readonly double _framesPerSecond;
    private readonly double _jitterSeconds;
    private readonly Random _random;
    private readonly int _packetFrames;
    private readonly float[] _packet;
    private double _runStart;
    private long _frame;

    public SimulatedDevice(double driftPpm, double jitterMs, int channels, int seed, int packetFrames = 480, int nominalRate = TimelineClock.SampleRate)
    {
        _framesPerSecond = nominalRate * (1 + (driftPpm / 1e6));
        _jitterSeconds = jitterMs / 1000.0;
        _random = new Random(seed);
        _packetFrames = packetFrames;
        Channels = channels;
        _packet = new float[packetFrames * channels];
    }

    public int Channels { get; }

    /// <summary>True time of the next sample to be captured.</summary>
    public double Now => _runStart + (_frame / _framesPerSecond);

    /// <summary>(Re)starts capturing at true time <paramref name="seconds"/>.</summary>
    public void StartAt(double seconds)
    {
        _runStart = seconds;
        _frame = 0;
    }

    /// <summary>Captures one packet into the writer; returns the true time at its end (when it is delivered).</summary>
    public double CapturePacket(TimelineWriter writer, double timestampOffsetSeconds = 0)
    {
        double start = Now;
        for (int i = 0; i < _packetFrames; i++)
        {
            float value = TrueTimeSignal.At(_runStart + ((_frame + i) / _framesPerSecond));
            if (Channels == 1)
            {
                _packet[i] = value;
            }
            else
            {
                _packet[2 * i] = value;
                _packet[(2 * i) + 1] = -value;
            }
        }

        _frame += _packetFrames;
        double jitter = (_random.NextDouble() - 0.5) * 2 * _jitterSeconds;
        double timestamp = start + jitter + timestampOffsetSeconds;
        writer.Write(_packet, _packetFrames, timestamp * TimelineClock.SampleRate);
        return Now;
    }
}

/// <summary>What a stretch of the timeline should hold.</summary>
internal enum Expect
{
    Signal,
    Silence,
    Anything,
}

/// <summary>
/// Plays the mixer: reads the ring 600 ms behind "now", like the real mixer, and checks every sample against the
/// true-time signal (or against silence).
/// </summary>
internal sealed class TimelineCheck
{
    private const int Chunk = 960;
    private const double ReaderDelaySeconds = 0.6;
    private readonly TimelineRing[] _rings;
    private readonly float[] _left = new float[Chunk];
    private readonly float[] _right = new float[Chunk];
    private readonly int _channels;
    private readonly int _every;

    public TimelineCheck(int channels, int checkEvery = 1, params TimelineRing[] rings)
    {
        _channels = channels;
        _every = checkEvery;
        _rings = rings;
    }

    public long ReadPosition { get; private set; }

    /// <summary>Largest timing error of a checked signal sample, in milliseconds.</summary>
    public double MaxErrorMs { get; private set; }

    public double MeanErrorMs => Checked == 0 ? 0 : _errorSumMs / Checked;

    public long Checked { get; private set; }

    /// <summary>Samples that should have been silent but were not.</summary>
    public long NoiseInSilence { get; private set; }

    /// <summary>Signal samples that read as exact silence (a hole where audio should be).</summary>
    public long Holes { get; private set; }

    private double _errorSumMs;

    public void Reset()
    {
        MaxErrorMs = 0;
        _errorSumMs = 0;
        Checked = 0;
        NoiseInSilence = 0;
        Holes = 0;
    }

    /// <summary>Reads everything the mixer would have read by true time <paramref name="now"/>.</summary>
    public void ReadUntil(double now, Func<long, Expect> expect)
    {
        long limit = (long)Math.Floor((now - ReaderDelaySeconds) * TimelineClock.SampleRate);
        while (ReadPosition + Chunk <= limit)
        {
            AudioMix.Sum(_rings, ReadPosition, _left, _right);
            for (int k = 0; k < Chunk; k += _every)
            {
                long position = ReadPosition + k;
                switch (expect(position))
                {
                    case Expect.Silence:
                        if (_left[k] != 0 || _right[k] != 0)
                        {
                            NoiseInSilence++;
                        }

                        break;
                    case Expect.Signal:
                        Measure(position, _left[k], _right[k]);
                        break;
                }
            }

            ReadPosition += Chunk;
        }
    }

    private void Measure(long position, float left, float right)
    {
        double seconds = position / (double)TimelineClock.SampleRate;
        double phase = TrueTimeSignal.Phase(seconds);
        // Next to the sawtooth's drop the interpolation rings; the measurement is only meaningful away from it.
        if (phase < 0.02 || phase > 0.98)
        {
            return;
        }

        if (left == 0 && right == 0)
        {
            Holes++;
            return;
        }

        double expected = TrueTimeSignal.Amplitude * phase;
        double difference = left - expected;
        if (Math.Abs(difference) > TrueTimeSignal.Amplitude / 2)
        {
            // Off by more than half a period: count it as a hole (the measurement wraps).
            Holes++;
            return;
        }

        double errorMs = Math.Abs(difference) / TrueTimeSignal.Amplitude * TrueTimeSignal.Period * 1000;
        // Stereo devices capture the signal inverted on the right; mono goes to both sides unchanged.
        Assert.True(_channels == 2 ? Math.Abs(left + right) < 1e-5 : left == right, $"channels mixed up at {position}: {left} / {right}");

        MaxErrorMs = Math.Max(MaxErrorMs, errorMs);
        _errorSumMs += errorMs;
        Checked++;
    }
}
