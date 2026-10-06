using System.Globalization;
using EKrecorder.App;
using EKrecorder.Diagnostics;
using EKrecorder.Mp4;

namespace EKrecorder.Recording;

/// <summary>What happened to a recording after it stopped (or after a crash).</summary>
internal sealed record FinishedFile(bool Saved, string? Path, string? Problem, RepairResult? Repair, Mp4Summary? Summary, string? PlaybackCheck)
{
    public bool Playable => Saved && Repair?.Playable == true;
}

/// <summary>
/// Turns a recording in InProgress into a finished file in the recordings folder: the crash-safe fragmented MP4
/// becomes a regular MP4 in place (or a crash-cut one is recovered), the result is read back and checked, and only
/// then is it moved. If the chosen folder cannot take it, Desktop\EKrecordings does; if that fails too, it stays
/// where it is. A file with nothing playable is set aside in InProgress\Unrecoverable, never deleted.
/// </summary>
internal static class RecordingFinisher
{
    public static FinishedFile Finish(string temporaryPath, string finalName, IReadOnlyList<string> folders, string unrecoverableFolder)
    {
        Log.Info($"Finishing {temporaryPath} -> {finalName}");
        RepairResult repair;
        try
        {
            repair = Mp4Repair.Run(temporaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OverflowException)
        {
            Log.Error($"Finishing {temporaryPath} failed; it stays in place and is tried again at the next start", ex);
            return new FinishedFile(false, temporaryPath, $"the file could not be finished: {ex.Message}", null, null, null);
        }

        Log.Info($"MP4: {repair.Outcome}: {repair.Detail}");
        foreach (string note in repair.Notes)
        {
            Log.Info($"MP4 note: {note}");
        }

        if (!repair.Playable)
        {
            string? kept = SetAside(temporaryPath, unrecoverableFolder);
            RecordingJournal.TryDelete(temporaryPath);
            return new FinishedFile(false, kept, $"nothing playable was in it ({repair.Detail})", repair, null, null);
        }

        Mp4Summary summary = Mp4File.Summarize(temporaryPath);
        if (summary.Ok)
        {
            Log.Info($"File check: {Describe(summary)}");
        }
        else
        {
            Log.Warn($"File check found a problem: {summary.Problem}");
        }

        string? playback = Mp4Inspector.QuickCheck(temporaryPath);
        Log.Info(playback is null ? "Windows opens and plays the file." : $"Windows could not play the file: {playback}");

        (string? path, string? moveProblem) = MoveToFolder(temporaryPath, finalName, folders);
        if (path is null)
        {
            // It stays in InProgress with its journal; the next start tries again.
            return new FinishedFile(true, temporaryPath, moveProblem, repair, summary, playback);
        }

        RecordingJournal.TryDelete(temporaryPath);
        Log.Info($"Recording saved: {path}");
        return new FinishedFile(true, path, moveProblem, repair, summary, playback);
    }

    public static string Describe(Mp4Summary summary)
    {
        var parts = new List<string>();
        if (summary.Video is { } video)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"video {video.Width}x{video.Height} {video.Codec} {video.Profile}, {video.Samples} frames ({video.Samples / Math.Max(video.Seconds, 0.001):0.##} fps), {video.SyncSamples} keyframes, {video.Seconds:0.000} s, {video.AverageBitsPerSecond / 1e6:0.00} Mbps average, {video.PeakBitsPerSecond / 1e6:0.00} Mbps busiest second"));
        }

        if (summary.Audio is { } audio)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"audio {audio.Codec} {audio.SampleRate} Hz {audio.Channels} ch, {audio.Seconds:0.000} s, {audio.AverageBitsPerSecond / 1000:0} kbps"));
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"{summary.FileLength / 1048576.0:0.0} MB"));
        return string.Join("; ", parts);
    }

    private static (string? Path, string? Problem) MoveToFolder(string source, string name, IReadOnlyList<string> folders)
    {
        var problems = new List<string>();
        foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(folder);
                string target = RecordingNames.FreePath(folder, name);
                MoveFile(source, target);
                string? problem = problems.Count == 0 ? null : $"it was saved in {folder} instead ({string.Join("; ", problems)})";
                return (target, problem);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                Log.Error($"Moving the recording to {folder} failed", ex);
                problems.Add($"{folder}: {ex.Message}");
            }
        }

        return (null, $"it could not be moved out of {Path.GetDirectoryName(source)} ({string.Join("; ", problems)})");
    }

    /// <summary>
    /// A rename on the same drive. To another drive (or a network folder) it is copied as "name.partial", flushed,
    /// checked, renamed, and only then is the original deleted: an interruption never leaves a half file under the
    /// real name, and never loses the original.
    /// </summary>
    private static void MoveFile(string source, string target)
    {
        string sourceRoot = Path.GetPathRoot(Path.GetFullPath(source)) ?? "";
        string targetRoot = Path.GetPathRoot(Path.GetFullPath(target)) ?? "";
        if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(source, target);
            return;
        }

        string partial = target + ".partial";
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            input.CopyTo(output, 1 << 20);
            output.Flush(flushToDisk: true);
        }

        if (new FileInfo(partial).Length != new FileInfo(source).Length)
        {
            File.Delete(partial);
            throw new IOException("the copy is not complete");
        }

        File.Move(partial, target);
        File.Delete(source);
    }

    /// <summary>Keeps a file with nothing playable out of the way, but never deletes it.</summary>
    private static string? SetAside(string path, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string target = RecordingNames.FreePath(folder, Path.GetFileName(path));
            File.Move(path, target);
            Log.Warn($"Nothing playable in {path}; kept as {target}.");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Setting {path} aside failed", ex);
            return path;
        }
    }
}

/// <summary>A recording an earlier run of EKrecorder left in InProgress (it crashed, Windows shut down, the power went).</summary>
internal sealed record Leftover(string Path, RecordingJournal? Journal, long Length);

/// <summary>Finishes or recovers the recordings an earlier run left behind.</summary>
internal static class RecoveryService
{
    /// <summary>The recordings in InProgress, taken at start before this run records anything.</summary>
    public static List<Leftover> FindLeftovers(AppPaths paths)
    {
        var found = new List<Leftover>();
        try
        {
            if (!Directory.Exists(paths.InProgress))
            {
                return found;
            }

            foreach (string file in Directory.EnumerateFiles(paths.InProgress, "*.mp4"))
            {
                found.Add(new Leftover(file, RecordingJournal.TryRead(file), new FileInfo(file).Length));
            }

            // Orphaned journal temp files from a crash while one was written.
            foreach (string temporary in Directory.EnumerateFiles(paths.InProgress, "*.json.tmp"))
            {
                File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Looking for unfinished recordings failed", ex);
        }

        return found;
    }

    /// <summary>
    /// Finishes one leftover. A recording that was stopped cleanly (Windows shut down while it was being saved)
    /// keeps its name; one that was cut off gets "(recovered)" in its name.
    /// </summary>
    public static FinishedFile Recover(Leftover leftover, AppPaths paths, AppSettings settings)
    {
        RecordingJournal? journal = leftover.Journal;
        bool clean = journal?.State == RecordingJournal.Stopped;
        string name = journal?.FinalName is { Length: > 0 } finalName ? finalName : Path.GetFileName(leftover.Path);
        if (!clean)
        {
            name = RecordingNames.Recovered(name);
        }

        Log.Info($"Unfinished recording found: {leftover.Path} ({leftover.Length:N0} bytes, {(journal is null ? "no journal" : $"journal state {journal.State}, started {journal.StartedLocal:yyyy-MM-dd HH:mm:ss}")}).");
        string[] folders = [journal?.FinalFolder ?? paths.RecordingsFolder(settings), paths.DefaultRecordings];
        return RecordingFinisher.Finish(leftover.Path, name, folders, paths.Unrecoverable);
    }
}
