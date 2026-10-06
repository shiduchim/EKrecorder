using System.Diagnostics;
using System.Text;

namespace EKrecorder.Diagnostics;

/// <summary>
/// EKrecorder's diagnostic log: %LocalAppData%\EKrecorder\Logs\EKrecorder.log, rolled over at 5 MB and kept as five
/// files at most (EKrecorder.log, EKrecorder.1.log ... EKrecorder.4.log), so it never grows without bound however
/// long the app runs. The last lines are also kept in memory for the self-test's reports. Thread-safe; logging
/// never throws and never shows anything.
/// </summary>
internal static class Log
{
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const int KeptFiles = 5;
    private const int MemoryLines = 4000;

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private static StreamWriter? _writer;
    private static string? _folder;

    public static string? FilePath { get; private set; }

    public static string? Folder => _folder;

    public static void Start(string folder)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                _folder = folder;
                FilePath = Path.Combine(folder, "EKrecorder.log");
                OpenWriter();
                foreach (string line in Recent)
                {
                    _writer!.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                _writer = null;
                FilePath = null;
                Debug.WriteLine($"EKrecorder: could not open the log file: {ex.Message}");
            }
        }

        PruneOldLogs(folder);
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    /// <summary>A choice the app made on its own (fallbacks, refusing to show a window, and so on).</summary>
    public static void Decision(string message) => Write("DECISION", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null
            ? message
            : $"{message}: {exception.GetType().Name} (HRESULT 0x{exception.HResult:X8}): {exception.Message}{Environment.NewLine}{exception}");

    /// <summary>One line per Windows API call: what was called and what came back.</summary>
    public static void Api(string call, bool succeeded, string result) =>
        Write(succeeded ? "API" : "API-FAIL", $"{call} -> {result}");

    /// <summary>The most recent lines (up to a few thousand).</summary>
    public static string Snapshot()
    {
        lock (Gate)
        {
            return string.Join(Environment.NewLine, Recent) + Environment.NewLine;
        }
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId:00}] {level,-8} {message}";
        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > MemoryLines)
            {
                Recent.Dequeue();
            }

            try
            {
                if (_writer is not null)
                {
                    _writer.WriteLine(line);
                    if (_writer.BaseStream.Length > MaxFileBytes)
                    {
                        Roll();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // The in-memory copy still has the line.
            }
        }

        Debug.WriteLine(line);
    }

    private static void OpenWriter()
    {
        var stream = new FileStream(FilePath!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>EKrecorder.log becomes EKrecorder.1.log, and so on; the oldest goes. Called with the lock held.</summary>
    private static void Roll()
    {
        _writer?.Dispose();
        _writer = null;
        string folder = _folder!;
        try
        {
            File.Delete(Path.Combine(folder, $"EKrecorder.{KeptFiles - 1}.log"));
            for (int i = KeptFiles - 2; i >= 1; i--)
            {
                string from = Path.Combine(folder, $"EKrecorder.{i}.log");
                if (File.Exists(from))
                {
                    File.Move(from, Path.Combine(folder, $"EKrecorder.{i + 1}.log"), overwrite: true);
                }
            }

            File.Move(FilePath!, Path.Combine(folder, "EKrecorder.1.log"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another program has a log file open: keep writing to the current file and try again later.
        }

        OpenWriter();
    }

    /// <summary>The test builds wrote one log per start; those older than a month go.</summary>
    private static void PruneOldLogs(string folder)
    {
        try
        {
            foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("EKrecorder-spike-*.log"))
            {
                if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))
                {
                    file.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
