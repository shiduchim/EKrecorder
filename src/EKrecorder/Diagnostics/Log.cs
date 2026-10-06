using System.Diagnostics;
using System.Text;

namespace EKrecorder.Diagnostics;

/// <summary>
/// The one log for an app session: a file in %LocalAppData%\EKrecorder\Logs, plus an in-memory copy that each
/// test saves next to its report. Thread-safe; logging never throws.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = new();
    private static StreamWriter? _writer;

    public static string? FilePath { get; private set; }

    public static void Start()
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EKrecorder", "Logs");
            Directory.CreateDirectory(folder);
            FilePath = Path.Combine(folder, $"EKrecorder-spike-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            lock (Gate)
            {
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                foreach (string line in Lines)
                {
                    _writer.WriteLine(line);
                }
            }
        }
        catch (Exception ex)
        {
            FilePath = null;
            Warn($"Could not open the log file; keeping the log in memory only. {ex.Message}");
        }
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

    /// <summary>Everything logged so far in this session.</summary>
    public static string Snapshot()
    {
        lock (Gate)
        {
            return string.Join(Environment.NewLine, Lines) + Environment.NewLine;
        }
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId:00}] {level,-8} {message}";
        lock (Gate)
        {
            Lines.Add(line);
            try
            {
                _writer?.WriteLine(line);
            }
            catch (IOException)
            {
                // The in-memory copy still has the line.
            }
        }

        Debug.WriteLine(line);
    }
}
