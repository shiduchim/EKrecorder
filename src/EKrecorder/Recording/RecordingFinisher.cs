using System.Globalization;
using System.Runtime.InteropServices;
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
        try
        {
            return FinishOrThrow(temporaryPath, finalName, folders, unrecoverableFolder);
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the recording stays where it is (every finishing step can be repeated); the next
            // start tries again.
            Log.Error($"Finishing {temporaryPath} failed; it stays in place and is tried again at the next start", ex);
            return new FinishedFile(false, temporaryPath, $"the file could not be finished: {ex.Message}", null, null, null);
        }
    }

    private static FinishedFile FinishOrThrow(string temporaryPath, string finalName, IReadOnlyList<string> folders, string unrecoverableFolder)
    {
        Log.Info($"Finishing {temporaryPath} -> {finalName}");
        RepairResult repair = InUseRetried(() => Mp4Repair.Run(temporaryPath), $"Finishing {temporaryPath}");
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

    /// <summary>Runs <paramref name="action"/>, trying again for a few seconds while another program (an antivirus scan, a backup) holds the file.</summary>
    private static T InUseRetried<T>(Func<T> action, string what)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException ex) when (attempt < 4 && (ex.HResult & 0xFFFF) is 32 or 33)
            {
                // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
                Log.Warn($"{what}: the file is in use ({ex.Message}); trying again in {attempt} s");
                Thread.Sleep(TimeSpan.FromSeconds(attempt));
            }
        }
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
    /// checked, renamed (the rename itself written through to the disk), and only then is the original deleted: an
    /// interruption never leaves a half file under the real name, and never loses the original.
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
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                input.CopyTo(output, 1 << 20);
                output.Flush(flushToDisk: true);
            }

            if (new FileInfo(partial).Length != new FileInfo(source).Length)
            {
                throw new IOException("the copy is not complete");
            }

            if (!MoveFileEx(partial, target, MoveFileWriteThrough))
            {
                throw new IOException($"renaming the copy failed (error {Marshal.GetLastPInvokeError()})");
            }
        }
        catch
        {
            // A half copy (disk full, the network went away) is not left in the user's folder.
            TryDeleteQuietly(partial);
            throw;
        }

        try
        {
            File.Delete(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The recording is safely in its folder; the original is only in the way. It is renamed so the next start
            // does not take it for an unfinished recording, and removed then.
            Log.Warn($"The recording was copied to {target}, but the original could not be deleted ({ex.Message}).");
            try
            {
                File.Move(source, source + SavedCopySuffix);
            }
            catch (Exception rename) when (rename is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Renaming the original failed too ({rename.Message}); it may be saved again at the next start.");
            }
        }
    }

    /// <summary>The end of the name of an original that was copied to its folder but could not be deleted.</summary>
    internal const string SavedCopySuffix = ".saved-copy";

    private const uint MoveFileWriteThrough = 0x8;

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string target, uint flags);

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Deleting {path} failed: {ex.Message}");
        }
    }

    /// <summary>Keeps a file with nothing playable out of the way, but never deletes it.</summary>
    internal static string? SetAside(string path, string folder)
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

            // Orphaned journal temp files from a crash while one was written, and originals already copied to
            // their folder that could not be deleted then.
            foreach (string temporary in Directory.EnumerateFiles(paths.InProgress, "*.json.tmp")
                .Concat(Directory.EnumerateFiles(paths.InProgress, "*" + RecordingFinisher.SavedCopySuffix)))
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

        Log.Info($"Unfinished recording found: {leftover.Path} ({leftover.Length:N0} bytes, {(journal is null ? "no journal" : $"journal state {journal.State}, started {journal.StartedLocal:yyyy-MM-dd HH:mm:ss}, finishing tried {journal.FinishAttempts} time(s) before")}).");

        // A file whose finishing failed at several starts in a row (or took EKrecorder down) is set aside instead
        // of being tried at every start for ever.
        int attempts = (journal?.FinishAttempts ?? 0) + 1;
        if (attempts > MaxFinishAttempts)
        {
            string? kept = RecordingFinisher.SetAside(leftover.Path, paths.Unrecoverable);
            if (kept is not null && kept != leftover.Path)
            {
                RecordingJournal.TryDelete(leftover.Path);
            }

            return new FinishedFile(false, kept, $"finishing it failed {MaxFinishAttempts} times; it was set aside", null, null, null);
        }

        RecordingJournal counted = (journal ?? new RecordingJournal { FinalName = name, StartedLocal = File.GetLastWriteTime(leftover.Path) })
            with { FinishAttempts = attempts };
        counted.TryWrite(leftover.Path);
        string[] folders = [journal?.FinalFolder ?? paths.RecordingsFolder(settings), paths.DefaultRecordings];
        FinishedFile finished = RecordingFinisher.Finish(leftover.Path, name, folders, paths.Unrecoverable);
        if (finished.Repair is not null && File.Exists(leftover.Path))
        {
            // Finished but not moved (no folder could take it): that is not a failed attempt.
            (counted with { FinishAttempts = attempts - 1, State = RecordingJournal.Stopped, FinalName = name }).TryWrite(leftover.Path);
        }

        return finished;
    }

    private const int MaxFinishAttempts = 3;
}
