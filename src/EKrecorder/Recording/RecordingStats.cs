using System.Diagnostics;

namespace EKrecorder.Recording;

/// <summary>
/// Counters for one recording. Written by the capture and encoder threads, read by the window for live status.
/// <list type="bullet">
/// <item>Duplicated frame: an output frame for which the screen had not changed, so the previous picture is encoded
/// again. Normal whenever the screen is still.</item>
/// <item>Dropped frame: a 1/15 s output slot that got no frame because EKrecorder or the encoder fell behind.
/// Should stay 0.</item>
/// <item>Replaced capture frame: a captured frame that was replaced by a newer one before the next output slot.</item>
/// </list>
/// </summary>
internal sealed class RecordingStats
{
    private readonly object _gate = new();
    private readonly List<string> _errors = new();
    private long _captureFrames;
    private long _replacedCaptureFrames;

    public long CaptureFrames => Interlocked.Read(ref _captureFrames);

    public long ReplacedCaptureFrames => Interlocked.Read(ref _replacedCaptureFrames);

    // The fields below are written only by the encoder thread.
    public long FramesWritten { get; set; }

    public long FramesDuplicated { get; set; }

    public long FramesDroppedLate { get; set; }

    public long FramesDroppedEncoderBusy { get; set; }

    public long FramesDropped => FramesDroppedLate + FramesDroppedEncoderBusy;

    public long Ticks { get; set; }

    public double LatenessSumMs { get; set; }

    public double LatenessMaxMs { get; set; }

    public double WorkSumMs { get; set; }

    public double WorkMaxMs { get; set; }

    public double WriteMaxMs { get; set; }

    public ulong GpuMemoryPeakBytes { get; set; }

    public long WorkingSetPeakBytes { get; set; }

    public long PrivateBytesPeak { get; set; }

    public TimeSpan CpuTime { get; set; }

    public double FinalizeSeconds { get; set; }

    public long StartTimestamp { get; set; }

    public long EndTimestamp { get; set; }

    public DateTime StartedLocal { get; set; }

    /// <summary>Wall-clock time from the first frame until the recording stopped (or now, while recording).</summary>
    public TimeSpan WallClock => StartTimestamp == 0
        ? TimeSpan.Zero
        : Stopwatch.GetElapsedTime(StartTimestamp, EndTimestamp != 0 ? EndTimestamp : Stopwatch.GetTimestamp());

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_gate)
            {
                return _errors.ToArray();
            }
        }
    }

    public void CountCaptureFrame() => Interlocked.Increment(ref _captureFrames);

    public void CountReplacedCaptureFrame() => Interlocked.Increment(ref _replacedCaptureFrames);

    public void AddError(string message)
    {
        lock (_gate)
        {
            _errors.Add($"{DateTime.Now:HH:mm:ss} {message}");
        }
    }
}
