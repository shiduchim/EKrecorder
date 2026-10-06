using System.Globalization;
using System.Text;
using EKrecorder.Audio;
using EKrecorder.Diagnostics;
using EKrecorder.Mp4;

namespace EKrecorder.Recording;

/// <summary>
/// The technical report of one recording, written quietly to %LocalAppData%\EKrecorder\Reports (never opened by
/// itself). It holds what is needed to diagnose a problem: what was recorded, how, every fallback and audio event,
/// and the file read back. The newest 100 reports are kept.
/// </summary>
internal static class RecordingReport
{
    private const int KeptReports = 100;

    /// <summary>Writes the report; returns its path, or null if it could not be written (never throws).</summary>
    public static string? Write(RecordingSession session, FinishedFile finished, string reportsFolder)
    {
        try
        {
            Directory.CreateDirectory(reportsFolder);
            string name = Path.GetFileNameWithoutExtension(finished.Path ?? session.TemporaryPath) + " report.txt";
            string path = Path.Combine(reportsFolder, name);
            File.WriteAllText(path, Build(session, finished), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Log.Info($"Recording report written: {path}");
            Prune(reportsFolder);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Writing the recording report failed", ex);
            return null;
        }
    }

    /// <summary>What went wrong with the audio, in a few words each (empty when nothing did).</summary>
    public static List<string> AudioProblems(RecordingSession session, Mp4Summary? summary)
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
        else if (summary is { Ok: true })
        {
            if (summary.Audio is not { } track)
            {
                problems.Add("the file has no audio track");
            }
            else if (summary.Video is { } video && Math.Abs(track.Seconds - video.Seconds) > 0.1)
            {
                problems.Add(Invariant($"audio track is {track.Seconds:0.00} s, video {video.Seconds:0.00} s"));
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

    private static string Build(RecordingSession session, FinishedFile finished)
    {
        RecordingStats stats = session.Stats;
        EncoderInfo? encoder = session.Encoder;
        RecordingPreset preset = session.Preset;
        Mp4Summary? summary = finished.Summary;
        TrackSummary? video = summary?.Video;
        TrackSummary? audioTrack = summary?.Audio;
        double recordedSeconds = preset.FrameTime(session.Slots) / 1e7;
        IReadOnlyList<string> errors = stats.Errors;
        IReadOnlyList<string> fallbacks = session.Fallbacks;
        string setupFailures = session.SetupFailures;
        bool ok = finished.Playable && summary is { Ok: true } && finished.PlaybackCheck is null && errors.Count == 0;

        string result = !finished.Saved ? $"RESULT: NOT SAVED - {finished.Problem}"
            : !finished.Playable ? $"RESULT: NOT PLAYABLE - {finished.Problem}"
            : !ok ? "RESULT: SAVED WITH PROBLEMS - see Errors."
            : session.UsesGpuPath ? "RESULT: OK - saved; frames stayed on the GPU from capture to encoder."
            : "RESULT: OK - saved, but not on the GPU path (see Fallbacks).";
        List<string> audioProblems = AudioProblems(session, summary);
        string audioResult = session.Audio is null ? "AUDIO: none (recorded without audio)."
            : audioProblems.Count == 0 ? "AUDIO: OK - microphone and computer audio recorded, no device was lost."
            : $"AUDIO: {audioProblems.Count} PROBLEM(S) - see Audio health and AUDIO EVENTS.";

        var text = new StringBuilder();
        text.AppendLine("EKrecorder recording report");
        text.AppendLine(Invariant($"Created {DateTime.Now:yyyy-MM-dd HH:mm:ss} by EKrecorder {EnvironmentInfo.AppVersion}"));
        text.AppendLine();
        text.AppendLine(result);
        text.AppendLine(audioResult);
        text.AppendLine($"Video file: {finished.Path ?? "none"}{(finished.Problem is null || !finished.Saved ? "" : $" ({finished.Problem})")}");
        text.AppendLine();

        text.AppendLine("SUMMARY");
        Line(text, "Encoder used", encoder is null ? "none" : $"{encoder.Summary} - \"{encoder.Name}\"");
        Line(text, "Frame path", session.FramePath.Length > 0 ? session.FramePath : "none");
        Line(text, "File format", session.IsFragmented ? "fragmented MP4 while recording (crash-safe), then regular MP4" : "regular MP4 (the crash-safe format was not available; see Fallbacks)");
        Line(text, "Fallbacks", fallbacks.Count == 0 ? "none" : $"{fallbacks.Count}, listed below");
        if (session.UsesGpuPath)
        {
            Line(text, "GPU samples", session.GpuSamples);
        }

        Line(text, "Output resolution", video is not null
            ? $"{video.Width}x{video.Height} (monitor {session.CaptureSize.Width}x{session.CaptureSize.Height})"
            : $"{session.OutputSize.Width}x{session.OutputSize.Height} planned (monitor {session.CaptureSize.Width}x{session.CaptureSize.Height})");
        Line(text, "Frame rate", video is { Seconds: > 0 }
            ? Invariant($"{video.Samples / video.Seconds:0.##} fps in the file (target {preset.FramesPerSecond} fps, constant)")
            : $"target {preset.FramesPerSecond} fps");
        Line(text, "Duration", Invariant($"{Clock(TimeSpan.FromSeconds(recordedSeconds))} ({session.Slots} frame slots; file {(video is null ? "-" : Clock(TimeSpan.FromSeconds(video.Seconds)))})"));
        Line(text, "File size", summary is null ? "-" : Megabytes(summary.FileLength));
        Line(text, "Average bitrate", video is null ? "-" : $"{Mbps(video.AverageBitsPerSecond)} video, measured from the file");
        Line(text, "Peak bitrate", video is null ? "-" : $"{Mbps(video.PeakBitsPerSecond)} in the busiest second");
        Line(text, "Bitrate mode", encoder is null ? "-" : $"{encoder.RateControl}; average {encoder.AverageBitrate}; peak {encoder.PeakBitrate} (read back from the encoder)");
        Line(text, "Keyframes", (encoder is null ? "" : $"set {encoder.KeyframeInterval}")
            + (video is { SyncSamples: > 0 } ? Invariant($"; measured {video.SyncSamples} keyframes, every {video.Samples / (double)video.SyncSamples:0.#} frames") : ""));
        Line(text, "H.264 profile", video?.Profile is { Length: > 0 } profile ? profile : "-");
        Line(text, "Frames written", stats.FramesWritten.ToString(CultureInfo.InvariantCulture) + (video is null ? "" : $" (file has {video.Samples})"));
        Line(text, "Dropped frames", $"{stats.FramesDropped} ({stats.FramesDroppedLate} because EKrecorder was late, {stats.FramesDroppedEncoderBusy} because the encoder was busy)");
        Line(text, "Duplicated frames", $"{stats.FramesDuplicated} (the screen had not changed, so the last picture was repeated; normal)");
        if (session.CaptureLosses > 0)
        {
            Line(text, "Monitor lost", $"{session.CaptureLosses} time(s); the last picture held until it was back");
        }

        if (session.Audio is { } capture)
        {
            Line(text, "Audio track", audioTrack is null ? "none" : Invariant($"{audioTrack.Codec}, {audioTrack.SampleRate} Hz, {audioTrack.Channels} channel(s), {audioTrack.AverageBitsPerSecond / 1000:0} kbps, {audioTrack.Seconds:0.000} s"));
            Line(text, "Microphone", capture.Describe(capture.Microphone));
            Line(text, "Computer audio", capture.Describe(capture.Computer));
            Line(text, "Audio health", audioProblems.Count == 0 ? "OK" : string.Join("; ", audioProblems));
        }

        Line(text, "File check", summary is null ? "-" : summary.Ok ? "index and media data agree" : $"PROBLEM: {summary.Problem}");
        Line(text, "Playback check", finished.PlaybackCheck is null ? "Windows opens and plays the file" : $"PROBLEM: {finished.PlaybackCheck}");
        if (finished.Repair is { } repair)
        {
            Line(text, "MP4 conversion", $"{repair.Outcome}: {repair.Detail}");
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
        if (session.Audio is { } details)
        {
            foreach (string stream in details.StreamDetails())
            {
                Line(text, "Audio stream", stream);
            }

            Line(text, "Audio mix", session.Mixer is { } mixer
                ? Invariant($"48 kHz float, mixed {AudioMixer.DelaySeconds:0.0} s behind real time; {mixer.WrittenFrames / (double)TimelineClock.SampleRate:0.000} s written; limiter (-1 dBFS) acted on {mixer.Limiter.LimitedSamples:N0} samples, deepest {mixer.Limiter.MaxReductionDb:0.0} dB; largest backlog {mixer.MaxBacklogSeconds:0.00} s; encoder: {session.AudioStatistics}{(mixer.Error is null ? "" : $"; ENDED EARLY: {mixer.Error}")}")
                : $"no audio track ({session.AudioError ?? "not started"})");
            Line(text, "Audio offset", Invariant($"audio placed {session.AudioOffset.TotalMilliseconds:0.0} ms after its capture time (half a video frame: the average age of the picture in a frame)"));
        }

        Line(text, "Pacing", stats.Ticks > 0
            ? Invariant($"{session.PacingTimer}; wake-up lateness {stats.LatenessSumMs / stats.Ticks:0.0} ms average, {stats.LatenessMaxMs:0.0} ms max; work per frame {stats.WorkSumMs / stats.Ticks:0.0} ms average, {stats.WorkMaxMs:0.0} ms max; slowest WriteSample {stats.WriteMaxMs:0.0} ms")
            : "-");
        Line(text, "Finishing the file", Invariant($"{stats.FinalizeSeconds:0.00} s"));
        Line(text, "Wall-clock time", Invariant($"{stats.WallClock.TotalSeconds:0.0} s from the first frame"));
        Line(text, "Started", stats.StartedLocal == default ? "-" : stats.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Line(text, "Stopped because", session.StopReason ?? "-");
        Line(text, "Temporary file", session.TemporaryPath);
        Line(text, "Log", Log.FilePath ?? "-");
        if (finished.Repair is { Notes.Count: > 0 } notes)
        {
            text.AppendLine();
            text.AppendLine("MP4 NOTES");
            foreach (string note in notes.Notes)
            {
                text.AppendLine($"  {note}");
            }
        }

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
            text.AppendLine("FAILURE DETAILS");
            text.Append(setupFailures);
            text.Append(session.RecordingFailure);
        }

        return text.ToString();
    }

    private static void Prune(string folder)
    {
        try
        {
            foreach (FileInfo old in new DirectoryInfo(folder).EnumerateFiles("* report.txt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeptReports))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Line(StringBuilder text, string label, string value) => text.AppendLine($"  {label + ":",-22} {value}");

    private static string Clock(TimeSpan time) => time.ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static string Mbps(double bitsPerSecond) => (bitsPerSecond / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " Mbps";

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
