using System.Globalization;
using System.Text.Json;

namespace EKrecorder.App;

/// <summary>Names of recording files.</summary>
internal static class RecordingNames
{
    /// <summary>"EKrecording 2026-10-06 20-45-30.mp4" (no characters Windows forbids in file names).</summary>
    public static string For(DateTime start) =>
        string.Create(CultureInfo.InvariantCulture, $"EKrecording {start:yyyy-MM-dd HH-mm-ss}.mp4");

    /// <summary>"EKrecording 2026-10-06 20-45-30 (recovered).mp4".</summary>
    public static string Recovered(string fileName) =>
        $"{Path.GetFileNameWithoutExtension(fileName)} (recovered){Path.GetExtension(fileName)}";

    /// <summary>The path itself, or "name (2).ext", "name (3).ext"... if it is taken.</summary>
    public static string FreePath(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        string name = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name} ({i}){extension}"));
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}

internal enum DiskState
{
    Ok,

    /// <summary>Getting low: the triangle turns orange.</summary>
    Low,

    /// <summary>The recording must stop and be saved now, before the disk is full.</summary>
    Critical,
}

/// <summary>
/// How much free space a recording needs. It warns with plenty of room left (5 GB, or half an hour at the current
/// rate) and stops while there is still 1 GB (or 3 minutes) free, so the file can always be finished and the PC
/// keeps working.
/// </summary>
internal static class DiskSpacePolicy
{
    public const long MinimumToStart = 1536L * 1024 * 1024;

    public static long LowBelow(double bytesPerSecond) => Math.Max(5L * 1024 * 1024 * 1024, (long)(bytesPerSecond * 1800));

    public static long CriticalBelow(double bytesPerSecond) => Math.Max(1L * 1024 * 1024 * 1024, (long)(bytesPerSecond * 180));

    public static DiskState Evaluate(long freeBytes, double bytesPerSecond) =>
        freeBytes < CriticalBelow(bytesPerSecond) ? DiskState.Critical
        : freeBytes < LowBelow(bytesPerSecond) ? DiskState.Low
        : DiskState.Ok;
}

/// <summary>
/// Decides when the recording triangle turns orange. A problem counts once it has lasted its own delay (a device
/// switch takes a moment and is not worth a warning), and stops counting as soon as it is gone.
/// </summary>
internal sealed class AttentionTracker
{
    private readonly Dictionary<string, DateTime> _firstSeen = new();

    /// <summary>The problems that count now, in a stable order.</summary>
    public IReadOnlyList<string> Active { get; private set; } = [];

    public bool NeedsAttention => Active.Count > 0;

    /// <summary>Feeds what is wrong right now. Returns true when the list of problems that count changed.</summary>
    public bool Update(DateTime now, IEnumerable<(string Problem, TimeSpan Delay)> seen)
    {
        var current = seen.GroupBy(p => p.Problem).ToDictionary(g => g.Key, g => g.Min(p => p.Delay));
        foreach (string gone in _firstSeen.Keys.Where(k => !current.ContainsKey(k)).ToList())
        {
            _firstSeen.Remove(gone);
        }

        foreach (string problem in current.Keys)
        {
            _firstSeen.TryAdd(problem, now);
        }

        var active = current.Where(p => now - _firstSeen[p.Key] >= p.Value).Select(p => p.Key).OrderBy(p => p, StringComparer.Ordinal).ToList();
        bool changed = !active.SequenceEqual(Active);
        Active = active;
        return changed;
    }
}

/// <summary>
/// The small file next to a recording in %LocalAppData%\EKrecorder\InProgress (same name plus ".json"). It says
/// where the recording should go and how far it got, so the next start can finish or recover it.
/// </summary>
internal sealed record RecordingJournal
{
    public const string Recording = "recording";
    public const string Stopped = "stopped";

    public int Version { get; init; } = 1;

    /// <summary>The name the finished file gets.</summary>
    public string FinalName { get; init; } = "";

    /// <summary>The folder it goes to (null: the recordings folder in the settings at that time).</summary>
    public string? FinalFolder { get; init; }

    public DateTime StartedLocal { get; init; }

    /// <summary><see cref="Recording"/> while it runs; <see cref="Stopped"/> once the file was finished cleanly.</summary>
    public string State { get; init; } = Recording;

    public int ProcessId { get; init; }

    public string? Monitor { get; init; }

    public string? Quality { get; init; }

    public static string PathFor(string recordingPath) => recordingPath + ".json";

    public static RecordingJournal? TryRead(string recordingPath)
    {
        try
        {
            string path = PathFor(recordingPath);
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), AppJson.Default.RecordingJournal) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the journal (to a temporary file first). Never throws: a journal is a help, not a must.</summary>
    public bool TryWrite(string recordingPath)
    {
        try
        {
            string path = PathFor(recordingPath);
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, AppJson.Default.RecordingJournal);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static void TryDelete(string recordingPath)
    {
        try
        {
            File.Delete(PathFor(recordingPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
