using EKrecorder.Diagnostics;
using EKrecorder.Recording;

namespace EKrecorder;

/// <summary>
/// EKrecorder test build (Step 2: the recording engine). Run without arguments for the test window.
/// <c>--selftest [--output folder]</c> runs the capture test and a 6-second recording on Monitor 1 without questions
/// and exits with 0 (all passed), 1 (a check or the recording failed), 3 (a test crashed) or 4 (it hung).
/// </summary>
internal static class Program
{
    private static System.Threading.Timer? _watchdog;

    [STAThread]
    private static int Main(string[] args)
    {
        // Must stay first: applies PerMonitorV2 DPI awareness (ApplicationHighDpiMode in the .csproj) before any
        // window exists. Every monitor and overlay coordinate after this is a physical pixel.
        ApplicationConfiguration.Initialize();

        bool selfTest = args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase));
        string outputRoot = ArgumentValue(args, "--output")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "EKrecorder test results");

        Log.Start();
        RecordingFolders folders = selfTest ? RecordingFolders.Under(outputRoot) : RecordingFolders.Default;
        Log.Info($"EKrecorder starting{(selfTest ? " in self-test mode" : "")}; test results go to {outputRoot}");
        Log.Info($"Recordings: in progress {folders.InProgress}; finished {folders.Recordings}; reports {folders.Reports}");
        foreach (string line in EnvironmentInfo.Describe())
        {
            Log.Info(line);
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled exception on the UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        if (selfTest)
        {
            _watchdog = new System.Threading.Timer(
                _ =>
                {
                    Log.Error("Self-test watchdog: no result after 3 minutes; exiting.");
                    Log.Stop();
                    Environment.Exit(4);
                },
                null,
                TimeSpan.FromMinutes(3),
                Timeout.InfiniteTimeSpan);
        }

        int exitCode;
        using (var form = new SpikeForm(selfTest, outputRoot, folders))
        {
            Application.Run(form);
            exitCode = form.ExitCode;
        }

        _watchdog?.Dispose();
        MediaFoundation.Shutdown();
        Log.Info($"Exiting with code {exitCode}.");
        Log.Stop();
        return exitCode;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
