using System.Globalization;
using System.Text;
using EKrecorder.Diagnostics;

namespace EKrecorder.Recording;

/// <summary>A finished recording: where the video went, its report, and the read-back check.</summary>
internal sealed record FinishedRecording(bool Saved, string? VideoPath, string ReportPath, string Summary);

/// <summary>
/// After a recording stops: moves the MP4 from InProgress to the recordings folder, reads it back, and writes the
/// performance report (the part to send is at the top).
/// </summary>
internal static class RecordingReport
{
    public static FinishedRecording Finish(RecordingSession session, string recordingsFolder, string reportsFolder)
    {
        string? videoPath = null;
        string? moveProblem = null;
        if (session.FileFinalized)
        {
            try
            {
                Directory.CreateDirectory(recordingsFolder);
                videoPath = FreePath(Path.Combine(recordingsFolder, Path.GetFileName(session.TemporaryPath)));
                File.Move(session.TemporaryPath, videoPath);
                Log.Info($"Recording saved to {videoPath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                moveProblem = $"could not move it to {recordingsFolder}: {ex.Message}";
                videoPath = session.TemporaryPath;
                Log.Error($"Moving the recording to {recordingsFolder} failed; it stays in {session.TemporaryPath}", ex);
            }
        }

        Mp4Check? check = videoPath is not null ? Mp4Inspector.Inspect(videoPath) : null;
        Directory.CreateDirectory(reportsFolder);
        string reportPath = FreePath(Path.Combine(reportsFolder, Path.GetFileNameWithoutExtension(session.TemporaryPath) + " report.txt"));
        string summary = Write(session, videoPath, moveProblem, check, reportPath);
        return new FinishedRecording(session.FileFinalized && check?.Readable == true, videoPath, reportPath, summary);
    }

    private static string Write(RecordingSession session, string? videoPath, string? moveProblem, Mp4Check? check, string reportPath)
    {
        RecordingStats stats = session.Stats;
        EncoderInfo? encoder = session.Encoder;
        RecordingPreset preset = session.Preset;
        double recordedSeconds = preset.FrameTime(session.Slots) / 1e7;
        bool ok = session.FileFinalized && check?.Readable == true && stats.Errors.Count == 0;
        IReadOnlyList<string> errors = stats.Errors;
        string fileSize = check is not null ? Megabytes(check.FileBytes) : "no file";
        string summaryLine = ok
            ? $"Saved {Path.GetFileName(videoPath)} ({fileSize}, {Clock(TimeSpan.FromSeconds(recordedSeconds))})"
            : session.FileFinalized ? $"Saved with problems: {Path.GetFileName(videoPath)}" : "The recording was NOT saved";

        var text = new StringBuilder();
        text.AppendLine("EKrecorder recording report");
        text.AppendLine(Invariant($"Created {DateTime.Now:yyyy-MM-dd HH:mm:ss} by EKrecorder {EnvironmentInfo.AppVersion}"));
        text.AppendLine();
        text.AppendLine(ok ? "RESULT: OK - the recording was saved." : session.FileFinalized ? "RESULT: SAVED WITH PROBLEMS - see Errors." : "RESULT: FAILED - the recording was not saved. See Errors.");
        text.AppendLine($"Video file: {videoPath ?? "none"}{(moveProblem is null ? "" : $" ({moveProblem})")}");
        text.AppendLine();

        text.AppendLine("SUMMARY (please send this part)");
        Line(text, "Encoder used", encoder is null ? "none" : $"{encoder.Summary} - \"{encoder.Name}\"");
        Line(text, "Output resolution", check is { Readable: true }
            ? $"{check.Width}x{check.Height} (monitor {session.CaptureSize.Width}x{session.CaptureSize.Height})"
            : $"{session.OutputSize.Width}x{session.OutputSize.Height} planned (monitor {session.CaptureSize.Width}x{session.CaptureSize.Height})");
        Line(text, "Frame rate", check is { Readable: true }
            ? Invariant($"{check.FramesPerSecond:0.##} fps in the file (target {preset.FramesPerSecond} fps, constant)")
            : $"target {preset.FramesPerSecond} fps");
        Line(text, "Duration", Invariant($"{Clock(TimeSpan.FromSeconds(recordedSeconds))} ({session.Slots} frame slots; file says {(check is { Readable: true } ? Clock(check.Duration) : "-")})"));
        Line(text, "File size", fileSize);
        Line(text, "Average bitrate", check is { Readable: true } ? $"{Mbps(check.AverageBitsPerSecond)} measured from the file" : "-");
        Line(text, "Peak bitrate", check is { Readable: true } ? $"{Mbps(check.PeakBitsPerSecond)} in the busiest second" : "-");
        Line(text, "Bitrate mode", encoder is null ? "-" : $"{encoder.RateControl}; average {encoder.AverageBitrate}; peak {encoder.PeakBitrate} (read back from the encoder)");
        Line(text, "Keyframes", (encoder is null ? "" : $"set {encoder.KeyframeInterval}")
            + (check is { Readable: true } ? Invariant($"; measured {check.Keyframes} keyframes, every {check.AverageKeyframeSpacing:0.#} frames") : ""));
        Line(text, "H.264 profile", check is { Readable: true } ? check.Profile : "-");
        Line(text, "Frames written", stats.FramesWritten.ToString(CultureInfo.InvariantCulture) + (check is { Readable: true } ? $" (file has {check.Frames})" : ""));
        Line(text, "Dropped frames", $"{stats.FramesDropped} ({stats.FramesDroppedLate} because EKrecorder was late, {stats.FramesDroppedEncoderBusy} because the encoder was busy)");
        Line(text, "Duplicated frames", $"{stats.FramesDuplicated} (the screen had not changed, so the last picture was repeated; normal)");
        Line(text, "Errors", errors.Count == 0 ? "none" : $"{errors.Count}, listed below");
        Line(text, "CPU", stats.WallClock.TotalSeconds > 0
            ? Invariant($"{stats.CpuTime.TotalSeconds / stats.WallClock.TotalSeconds * 100:0.0}% of one core on average ({Environment.ProcessorCount} logical cores; whole app)")
            : "-");
        Line(text, "Memory", $"working set {Megabytes(stats.WorkingSetPeakBytes)} peak, private {Megabytes(stats.PrivateBytesPeak)}; GPU memory {Megabytes((long)stats.GpuMemoryPeakBytes)} peak");
        foreach (string error in errors)
        {
            text.AppendLine($"  ! {error}");
        }

        text.AppendLine();
        text.AppendLine("DETAILS");
        Line(text, "Monitor", session.Monitor.Summary);
        Line(text, "Preset", preset.Describe());
        Line(text, "Capture device", session.DeviceDescription);
        Line(text, "Capture settings", session.CaptureSettings);
        Line(text, "Scaling", session.ConverterDescription);
        Line(text, "Capture frames", $"{stats.CaptureFrames} received, {stats.ReplacedCaptureFrames} replaced by a newer one before the next output frame");
        Line(text, "Hardware encoders", session.HardwareEncoders.Count == 0 ? "none found" : string.Join("; ", session.HardwareEncoders));
        if (encoder is not null)
        {
            Line(text, "Encoder identified by", encoder.IdentifiedBy);
            Line(text, "Settings requested", encoder.Plan);
            Line(text, "B-frames", encoder.BFrames);
        }

        Line(text, "Sink writer", session.WriterStatistics);
        Line(text, "Pacing", stats.Ticks > 0
            ? Invariant($"{session.PacingTimer}; wake-up lateness {stats.LatenessSumMs / stats.Ticks:0.0} ms average, {stats.LatenessMaxMs:0.0} ms max; work per frame {stats.WorkSumMs / stats.Ticks:0.0} ms average, {stats.WorkMaxMs:0.0} ms max; slowest WriteSample {stats.WriteMaxMs:0.0} ms")
            : "-");
        Line(text, "Finishing the file", Invariant($"{stats.FinalizeSeconds:0.00} s"));
        Line(text, "Wall-clock time", Invariant($"{stats.WallClock.TotalSeconds:0.0} s from the first frame"));
        Line(text, "Started", stats.StartedLocal == default ? "-" : stats.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Line(text, "Stopped because", session.StopReason ?? "-");
        Line(text, "Temporary file", session.TemporaryPath);
        if (check is { Readable: false })
        {
            Line(text, "File check", $"could not read the file back: {check.Error}");
        }

        Line(text, "Log", Log.FilePath ?? "-");
        File.WriteAllText(reportPath, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Log.Info($"Recording report written: {reportPath}");
        return summaryLine;
    }

    private static void Line(StringBuilder text, string label, string value) => text.AppendLine($"  {label + ":",-22} {value}");

    private static string Clock(TimeSpan time) => time.ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static string Mbps(double bitsPerSecond) => (bitsPerSecond / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " Mbps";

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>The path itself, or "name (2).ext", "name (3).ext"... if it is taken.</summary>
    private static string FreePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string folder = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
