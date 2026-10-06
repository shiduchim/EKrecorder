using EKrecorder.App;
using EKrecorder.Diagnostics;
using EKrecorder.Platform;
using EKrecorder.Recording;
using EKrecorder.Shell;

namespace EKrecorder;

/// <summary>
/// EKrecorder starts here. Normally it lives in the notification area (tray); one copy runs per Windows session.
/// <list type="bullet">
/// <item>(no arguments): start, or show the Settings window of the copy that is already running.</item>
/// <item><c>--background</c>: start quietly in the tray (how Windows starts it at sign-in).</item>
/// <item><c>--exit</c>: ask the running copy to finish any recording and exit (the installer uses this).</item>
/// <item><c>--selftest [--output folder]</c> and <c>--selftest-record</c>, <c>--selftest-recover</c>,
/// <c>--screenshot</c>: the build machine's tests (everything under the output folder).</item>
/// </list>
/// </summary>
internal static class Program
{
    public const string InstanceName = "EKrecorder";

    [STAThread]
    private static int Main(string[] args)
    {
        // Must stay first: applies PerMonitorV2 DPI awareness (ApplicationHighDpiMode in the .csproj) before any
        // window exists. Every monitor and overlay coordinate after this is a physical pixel.
        ApplicationConfiguration.Initialize();
        // The Settings window follows Windows' light or dark mode.
        Application.SetColorMode(SystemColorMode.System);

        if (TestMode(args) is { } mode)
        {
            return RunSelfTest(mode, args);
        }

        if (Has(args, "--exit"))
        {
            return ExitRunningCopy();
        }

        bool background = Has(args, "--background");
        AppPaths paths = AppPaths.ForUser();
        using SingleInstance? instance = SingleInstance.TryAcquire(InstanceName);
        if (instance is null)
        {
            // Already running: a start from the Start menu shows its Settings; a second start at sign-in does nothing.
            if (!background)
            {
                SingleInstance.Signal(InstanceName, exit: false);
            }

            return 0;
        }

        Log.Start(paths.Logs);
        Log.Info($"===== EKrecorder starting{(background ? " in the background" : "")}");
        foreach (string line in EnvironmentInfo.Describe())
        {
            Log.Info(line);
        }

        CatchUnhandledExceptions();
        var store = new SettingsStore(paths.SettingsFile);
        (AppSettings settings, bool existed, string? problem) = store.Load();
        if (problem is not null)
        {
            Log.Warn(problem);
        }

        Log.Info($"Data in {paths.Root}; recordings go to {paths.RecordingsFolder(settings)}.");
        List<Leftover> leftovers = RecoveryService.FindLeftovers(paths);
        using (var app = new TrayApplication(paths, store, settings, firstRun: !existed, leftovers, instance, showSettings: !background || !existed))
        {
            instance.StartListening();
            Application.Run(app);
        }

        MediaFoundation.Shutdown();
        Log.Info("EKrecorder exited.");
        Log.Stop();
        return 0;
    }

    /// <summary>Asks the running EKrecorder to save any recording and exit, and waits for it (at most 3 minutes).</summary>
    private static int ExitRunningCopy()
    {
        if (!SingleInstance.Signal(InstanceName, exit: true))
        {
            return 0;
        }

        return SingleInstance.WaitUntilGone(InstanceName, TimeSpan.FromMinutes(3)) ? 0 : 1;
    }

    private static int RunSelfTest(string mode, string[] args)
    {
        string output = ArgumentValue(args, "--output")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "EKrecorder test results");
        Log.Start(Path.Combine(output, "logs"));
        Log.Info($"===== EKrecorder self-test ({mode}); results go to {output}");
        foreach (string line in EnvironmentInfo.Describe())
        {
            Log.Info(line);
        }

        CatchUnhandledExceptions();
        using var watchdog = new System.Threading.Timer(
            _ =>
            {
                Log.Error("Self-test watchdog: no result after 8 minutes; exiting.");
                Log.Stop();
                Environment.Exit(4);
            },
            null,
            mode == "record" ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(8),
            Timeout.InfiniteTimeSpan);

        int exitCode;
        using (var test = new SelfTest(mode, output))
        {
            Application.Run(test);
            exitCode = test.ExitCode;
        }

        MediaFoundation.Shutdown();
        Log.Info($"Exiting with code {exitCode}.");
        Log.Stop();
        return exitCode;
    }

    private static void CatchUnhandledExceptions()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled exception on the window thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static string? TestMode(string[] args) =>
        Has(args, "--selftest") ? "full"
        : Has(args, "--selftest-record") ? "record"
        : Has(args, "--selftest-recover") ? "recover"
        : Has(args, "--screenshot") ? "screenshot"
        : null;

    private static bool Has(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? ArgumentValue(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
