using System.Diagnostics;
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
        // A damaged settings file was kept aside; the defaults are saved in its place.
        bool idle;
        using (var app = new TrayApplication(paths, store, settings, firstRun: !existed || problem is not null, leftovers, instance, showSettings: !background))
        {
            instance.StartListening();
            Application.Run(app);
            idle = app.Idle;
        }

        if (idle)
        {
            MediaFoundation.Shutdown();
        }
        else
        {
            // A recording thread that did not finish in time may still use it; the file is recovered at the next start.
            Log.Warn("A recording was still being written at exit; Media Foundation is left to Windows.");
        }

        Log.Info("EKrecorder exited.");
        Log.Stop();
        return 0;
    }

    /// <summary>Asks the running EKrecorder to save any recording and exit, and waits for it (at most 3 minutes).</summary>
    private static int ExitRunningCopy()
    {
        // The running copies, taken before they are asked to exit: the wait ends when the processes are gone (and
        // their exe is free for an update), not merely when they let go of the single-instance lock.
        // (The installer runs this from a copy with another name; the running copy is always EKrecorder.exe, in this
        // Windows session.)
        int session = Process.GetCurrentProcess().SessionId;
        Process[] running = Process.GetProcessesByName("EKrecorder")
            .Where(p => p.Id != Environment.ProcessId && p.SessionId == session)
            .ToArray();
        try
        {
            if (!SingleInstance.Signal(InstanceName, exit: true))
            {
                return 0;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            if (!SingleInstance.WaitUntilGone(InstanceName, deadline - DateTime.UtcNow))
            {
                return 1;
            }

            foreach (Process process in running)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !process.WaitForExit(left))
                {
                    return 1;
                }
            }

            return 0;
        }
        finally
        {
            foreach (Process process in running)
            {
                process.Dispose();
            }
        }
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

        // --scale 2 lays the Settings window out as at 200 % (the build machine's monitor is at 100 %).
        float? scale = float.TryParse(ArgumentValue(args, "--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float forced)
            && forced is >= 1 and <= 4 ? forced : null;
        int exitCode;
        using (var test = new SelfTest(mode, output, scale))
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
