namespace EKrecorder.Audio;

/// <summary>
/// Places one capture stream on the timeline. Each packet comes with the QPC time of its first sample. The samples
/// are resampled (cubic interpolation) into consecutive timeline positions, and the resampling ratio is steered so
/// that the stream keeps landing where its timestamps say. That corrects the device clock's drift against the QPC
/// clock continuously, so a microphone and the computer audio stay together over hours instead of sliding apart.
/// <para>
/// When a packet lands more than 20 ms away from where the stream's own clock puts it (a gap, a glitch, a
/// loopback stream that sent nothing during silence), a new run starts at the right place and the skipped
/// positions stay silent.
/// </para>
/// </summary>
internal sealed class TimelineWriter
{
    /// <summary>A packet this far from its expected place starts a new run (20 ms).</summary>
    public const double ResyncThreshold = 0.020 * TimelineClock.SampleRate;

    /// <summary>The steering never changes the speed by more than 0.5 % (8.6 cents): far too little to hear.</summary>
    public const double MaxCorrection = 0.005;

    // PI controller on the position error (in samples). Settles in about 40 s; a device that is 150 ppm off
    // stays within about half a millisecond of its timestamps.
    private const double ProportionalGain = 4e-6;
    private const double IntegralGain = 4e-7;
    private const double ErrorSmoothingSeconds = 0.5;

    private readonly TimelineRing _ring;
    private readonly int _channels;
    private readonly double _inputRate;
    private readonly double _nominalStep;
    private double _step;
    private double _phase;
    private long _outPosition;
    private float _l0, _l1, _l2, _l3;
    private float _r0, _r1, _r2, _r3;
    private bool _running;
    private double _filteredError;
    private double _integral;
    private double _correction;

    /// <param name="ring">Where the samples go.</param>
    /// <param name="inputRate">Sample rate of the frames passed to <see cref="Write"/>.</param>
    /// <param name="channels">1 (written to both sides of the stereo mix, centred) or 2.</param>
    public TimelineWriter(TimelineRing ring, int inputRate, int channels)
    {
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "1 or 2 channels");
        }

        _ring = ring;
        _channels = channels;
        _inputRate = inputRate;
        _nominalStep = inputRate / (double)TimelineClock.SampleRate;
        _step = _nominalStep;
    }

    /// <summary>Runs started: the first packet, plus every <see cref="Restart"/>.</summary>
    public long Runs { get; private set; }

    /// <summary>Times a packet landed more than 20 ms from its place and the stream was moved there.</summary>
    public long Resyncs { get; private set; }

    /// <summary>The current speed correction in parts per million: about the device clock's drift.</summary>
    public double CorrectionPpm => _correction * 1e6;

    /// <summary>The smoothed distance between where packets land and where their timestamps say, in milliseconds.</summary>
    public double ErrorMilliseconds => _filteredError * 1000.0 / TimelineClock.SampleRate;

    /// <summary>The next packet starts a new run (after a stream restart or a known discontinuity).</summary>
    public void Restart() => _running = false;

    /// <param name="frames">Interleaved samples, one or two per frame.</param>
    /// <param name="frameCount">Frames in the packet.</param>
    /// <param name="position">Timeline position of the packet's first frame, from its QPC time.</param>
    public void Write(ReadOnlySpan<float> frames, int frameCount, double position)
    {
        if (!_running)
        {
            StartRun(position);
            Runs++;
        }
        else
        {
            // Where this packet's first frame lands if the stream just continues.
            double mapped = _outPosition + ((3 - _phase) / _step);
            double error = position - mapped;
            if (Math.Abs(error) > ResyncThreshold)
            {
                StartRun(position);
                Resyncs++;
            }
            else
            {
                Steer(error, frameCount / _inputRate);
            }
        }

        for (int i = 0; i < frameCount; i++)
        {
            _l0 = _l1;
            _l1 = _l2;
            _l2 = _l3;
            if (_channels == 2)
            {
                _l3 = frames[2 * i];
                _r0 = _r1;
                _r1 = _r2;
                _r2 = _r3;
                _r3 = frames[(2 * i) + 1];
            }
            else
            {
                _l3 = frames[i];
            }

            // _phase is where the next output falls, measured from the second-oldest of the four samples kept.
            _phase -= 1;
            while (_phase < 1)
            {
                float t = (float)_phase;
                float left = Cubic(_l0, _l1, _l2, _l3, t);
                float right = _channels == 2 ? Cubic(_r0, _r1, _r2, _r3, t) : left;
                if (_outPosition >= 0)
                {
                    _ring.Write(_outPosition, left, right);
                }

                _outPosition++;
                _phase += _step;
            }
        }
    }

    private void StartRun(double position)
    {
        _running = true;
        _outPosition = (long)Math.Round(position);
        // Three samples of history are needed before the first output; it then falls exactly on the first frame.
        _phase = 3;
        _l0 = _l1 = _l2 = _l3 = 0;
        _r0 = _r1 = _r2 = _r3 = 0;
        _filteredError = 0;
        // _integral is kept: it holds the clock drift learned so far, still right after a gap in the same stream.
    }

    private void Steer(double error, double seconds)
    {
        _filteredError += (error - _filteredError) * Math.Min(1.0, seconds / ErrorSmoothingSeconds);
        double integral = _integral + (_filteredError * seconds);
        // Landing too early (negative error) means the device runs fast: take input faster.
        double correction = -((ProportionalGain * _filteredError) + (IntegralGain * integral));
        if (Math.Abs(correction) <= MaxCorrection)
        {
            _integral = integral;
        }

        _correction = Math.Clamp(correction, -MaxCorrection, MaxCorrection);
        _step = _nominalStep * (1 + _correction);
    }

    /// <summary>Catmull-Rom interpolation between <paramref name="x0"/> and <paramref name="x1"/>.</summary>
    private static float Cubic(float xm1, float x0, float x1, float x2, float t)
    {
        float c1 = 0.5f * (x1 - xm1);
        float c2 = xm1 - (2.5f * x0) + (2f * x1) - (0.5f * x2);
        float c3 = (0.5f * (x2 - xm1)) + (1.5f * (x0 - x1));
        return (((((c3 * t) + c2) * t) + c1) * t) + x0;
    }
}
