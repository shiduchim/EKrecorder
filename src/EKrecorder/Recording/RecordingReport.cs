using System.Globalization;
using System.Text;
using EKrecorder.Audio;
using EKrecorder.Diagnostics;

namespace EKrecorder.Recording;

/// <summary>A finished recording: where the video went, its report, and the read-back check.</summary>
internal sealed record FinishedRecording(bool Saved, string? VideoPath, string ReportPath, string Summary, Mp4Check? Check);

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
        return new FinishedRecording(session.FileFinalized && check?.Readable == true, videoPath, reportPath, summary, check);
    }

    private static string Write(RecordingSession session, string? videoPath, string? moveProblem, Mp4Check? check, string reportPath)
    {
        RecordingStats stats = session.Stats;
        EncoderInfo? encoder = session.Encoder;
        RecordingPreset preset = session.Preset;
        double recordedSeconds = preset.FrameTime(session.Slots) / 1e7;
        bool ok = session.FileFinalized && check?.Readable == true && stats.Errors.Count == 0;
        IReadOnlyList<string> errors = stats.Errors;
        IReadOnlyList<string> fallbacks = session.Fallbacks;
        string setupFailures = session.SetupFailures;
        string fileSize = check is not null ? Megabytes(check.FileBytes) : "no file";
        string saved = $"Saved {Path.GetFileName(videoPath)} ({fileSize}, {Clock(TimeSpan.FromSeconds(recordedSeconds))})";
        string summaryLine = !session.FileFinalized ? "The recording was NOT saved"
            : !ok ? $"Saved with problems: {Path.GetFileName(videoPath)}"
            : session.UsesGpuPath ? saved
            : $"{saved}, but not on the GPU path";
        string result = !session.FileFinalized ? "RESULT: FAILED - the recording was not saved. See Errors."
            : !ok ? "RESULT: SAVED WITH PROBLEMS - see Errors."
            : session.UsesGpuPath ? "RESULT: OK - the recording was saved. Frames stayed on the GPU, from capture to encoder."
            : setupFailures.Length > 0 ? "RESULT: SAVED, BUT NOT ON THE GPU PATH - a set-up was refused (see FAILURE DETAILS). Please send the whole report."
            : "RESULT: OK - the recording was saved, but not on the GPU path (see Fallbacks).";

        List<string> audioProblems = AudioProblems(session, check);
        string audioResult = session.Audio is null ? "AUDIO: none (recorded without audio)."
            : audioProblems.Count == 0 ? "AUDIO: OK - microphone and computer audio recorded, no device was lost."
            : $"AUDIO: {audioProblems.Count} PROBLEM(S) - see Audio health and AUDIO EVENTS.";

        var text = new StringBuilder();
        text.AppendLine("EKrecorder recording report");
        text.AppendLine(Invariant($"Created {DateTime.Now:yyyy-MM-dd HH:mm:ss} by EKrecorder {EnvironmentInfo.AppVersion}"));
        text.AppendLine();
        text.AppendLine(result);
        text.AppendLine(audioResult);
        text.AppendLine($"Video file: {videoPath ?? "none"}{(moveProblem is null ? "" : $" ({moveProblem})")}");
        text.AppendLine();

        text.AppendLine("SUMMARY (please send this part)");
        Line(text, "Encoder used", encoder is null ? "none" : $"{encoder.Summary} - \"{encoder.Name}\"");
        Line(text, "Frame path", session.FramePath.Length > 0 ? session.FramePath : "none");
        Line(text, "Fallbacks", fallbacks.Count == 0 ? "none" : $"{fallbacks.Count}, listed below");
        if (session.UsesGpuPath)
        {
            Line(text, "GPU samples", session.GpuSamples);
        }

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
        if (session.Audio is { } capture)
        {
            Line(text, "Audio track", check is { Readable: true } ? check.Audio.Describe() : "-");
            Line(text, "Microphone", capture.Describe(capture.Microphone));
            Line(text, "Computer audio", capture.Describe(capture.Computer));
            Line(text, "Audio health", audioProblems.Count == 0 ? "OK" : string.Join("; ", audioProblems));
        }

        Line(text, "Errors", errors.Count == 0 ? "none" : $"{errors.Count}, listed below");
        Line(text, "CPU", stats.WallClock.TotalSeconds > 0
            ? Invariant($"{stats.CpuTime.TotalSeconds / stats.WallClock.TotalSeconds * 100:0.0}% of one core on average ({Environment.ProcessorCount} logical cores; whole app)")
            : "-");
        Line(text, "Memory", $"working set {Megabytes(stats.WorkingSetPeakBytes)} peak, private {Megabytes(stats.PrivateBytesPeak)}; GPU memory {Megabytes((long)stats.GpuMemoryPeakBytes)} peak");
        foreach (string error in errors)
        {
            text.AppendLine($"  ! {error}");
        }

        foreach (string fallback in fallbacks)
        {
            text.AppendLine($"  > {fallback}");
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

        Line(text, "Input media type", session.EncoderInputType.Length > 0 ? session.EncoderInputType : "-");
        Line(text, "Encoder input stream", session.EncoderInputStream.Length > 0 ? session.EncoderInputStream : "-");
        Line(text, "Sink writer", session.WriterStatistics);
        if (session.Audio is { } audio)
        {
            foreach (string stream in audio.StreamDetails())
            {
                Line(text, "Audio stream", stream);
            }

            Line(text, "Audio mix", session.Mixer is { } mixer
                ? Invariant($"48 kHz float, mixed {AudioMixer.DelaySeconds:0.0} s behind real time; {mixer.WrittenFrames / (double)TimelineClock.SampleRate:0.000} s written; limiter (-1 dBFS) acted on {mixer.Limiter.LimitedSamples:N0} samples, deepest {mixer.Limiter.MaxReductionDb:0.0} dB; largest backlog {mixer.MaxBacklogSeconds:0.00} s; encoder: {session.AudioStatistics}{(mixer.Error is null ? "" : $"; ENDED EARLY: {mixer.Error}")}")
                : $"no audio track ({session.AudioError ?? "not started"})");
            Line(text, "Audio offset", Invariant($"audio placed {session.AudioOffset.TotalMilliseconds:0} ms after its capture time (half a video frame: the average age of the picture in a frame)"));
        }

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
        if (session.Audio is { } events)
        {
            text.AppendLine();
            text.AppendLine("AUDIO EVENTS (recording time; before the recording starts, clock time)");
            foreach (string line in events.Events)
            {
                text.AppendLine($"  {line}");
            }
        }

        if (setupFailures.Length > 0 || session.RecordingFailure.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("FAILURE DETAILS (please send this part too)");
            text.Append(setupFailures);
            text.Append(session.RecordingFailure);
        }

        File.WriteAllText(reportPath, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Log.Info($"Recording report written: {reportPath}");
        return summaryLine;
    }

    /// <summary>What went wrong with the audio, in a few words each (empty when nothing did).</summary>
    private static List<string> AudioProblems(RecordingSession session, Mp4Check? check)
    {
        var problems = new List<string>();
        if (session.Audio is not { } audio)
        {
            return problems;
        }

        if (session.AudioError is { } error)
        {
            problems.Add($"NO AUDIO TRACK: {error}");
        }
        else if (check is { Readable: true })
        {
            if (!check.Audio.Present)
            {
                problems.Add("the file has no audio track");
            }
            else if (Math.Abs(check.Audio.Duration.TotalSeconds - check.Duration.TotalSeconds) > 0.1)
            {
                problems.Add(Invariant($"audio track is {check.Audio.Duration.TotalSeconds:0.00} s, video {check.Duration.TotalSeconds:0.00} s"));
            }
        }

        foreach (AudioCapture.Input input in new[] { audio.Microphone, audio.Computer })
        {
            if (input.MissingSeconds >= 0.05)
            {
                problems.Add(Invariant($"{input.Name.ToLowerInvariant()} had no device for {input.MissingSeconds:0.0} s (silence recorded there)"));
            }
        }

        if (audio.Microphone.DigitalSilenceEpisodes > 0)
        {
            problems.Add($"microphone sent only digital silence ({audio.Microphone.DigitalSilenceEpisodes} warning(s): muted or blocked?)");
        }

        long late = audio.Rings.Sum(r => r.LateSamples);
        if (late > 0)
        {
            problems.Add(Invariant($"{late * 1000.0 / TimelineClock.SampleRate:0} ms of audio arrived too late for the mix and was dropped"));
        }

        if (session.Mixer?.Error is { } mixerError)
        {
            problems.Add($"the audio track ended early: {mixerError}");
        }

        return problems;
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
