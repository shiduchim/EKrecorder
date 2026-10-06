using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using EKrecorder.App;
using EKrecorder.Capture;
using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Mp4;
using EKrecorder.Platform;
using EKrecorder.Recording;

namespace EKrecorder.Shell;

/// <summary>
/// The build machine's tests of the real app on a real Windows (no questions asked; everything under the output
/// folder, never in the user's own folders). Modes:
/// <list type="bullet">
/// <item><c>full</c>: the capture test, recordings through the same controller the tray uses (normal, a refused
/// first set-up, and a copy taken mid-recording as if the PC had died), the audio/video timing checks, and the
/// global shortcut.</item>
/// <item><c>record</c>: starts recording and never stops (the build machine kills the process).</item>
/// <item><c>recover</c>: what the next start does after that kill: finds and recovers the recording.</item>
/// <item><c>screenshot</c>: the Settings window as a picture (light or dark, as Windows is set).</item>
/// </list>
/// Results go to selftest-*-report.txt; the exit code is 0 when everything passed.
/// </summary>
internal sealed class SelfTest : ApplicationContext
{
    private readonly string _mode;
    private readonly string _output;
    private readonly AppPaths _paths;
    private readonly StringBuilder _report = new();
    private int _failures;

    public SelfTest(string mode, string output)
    {
        if (SynchronizationContext.Current is null)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }

        _mode = mode;
        _output = output;
        _paths = AppPaths.Under(output);
        var start = new System.Windows.Forms.Timer { Interval = 50 };
        start.Tick += async (_, _) =>
        {
            start.Stop();
            start.Dispose();
            await RunAsync();
            ExitThread();
        };
        start.Start();
    }

    public int ExitCode { get; private set; }

    private async Task RunAsync()
    {
        _report.AppendLine($"EKrecorder self-test ({_mode}), {DateTime.Now:yyyy-MM-dd HH:mm:ss}, EKrecorder {EnvironmentInfo.AppVersion}");
        try
        {
            switch (_mode)
            {
                case "full":
                    await FullAsync();
                    break;
                case "record":
                    await RecordUntilKilledAsync();
                    break;
                case "recover":
                    Recover();
                    break;
                case "screenshot":
                    await ScreenshotAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("The self-test crashed", ex);
            Result("Self-test", false, $"crashed: {ex.GetType().Name}: {ex.Message}");
            ExitCode = 3;
        }

        ExitCode = Math.Max(ExitCode, _failures > 0 ? 1 : 0);
        _report.AppendLine(_failures == 0 ? "OVERALL: PASS" : $"OVERALL: FAIL ({_failures} failed)");
        Directory.CreateDirectory(_output);
        File.WriteAllText(Path.Combine(_output, $"selftest-{_mode}-report.txt"), _report.ToString());
        Log.Info($"Self-test ({_mode}) finished:{Environment.NewLine}{_report}");
    }

    private async Task FullAsync()
    {
        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetMonitors();
        MonitorInfo monitor = monitors.First();

        string captureFolder = Path.Combine(_output, "capture-test");
        TestReport capture = await new CaptureTest(new CaptureTestOptions(monitor, monitors, captureFolder, null), new Progress<string>(s => Log.Info($"Capture test: {s}"))).RunAsync();
        Result("Capture test (frames, triangle and Identify numbers excluded from capture)", !capture.HasFailures, capture.ShortSummary);

        var settings = new AppSettings();
        string? saved = null;
        using var controller = new RecordingController(_paths, () => settings);
        controller.Saved += path => saved = path;

        // 1. A normal recording.
        saved = null;
        RecordingSession? normal = await RecordAsync(controller, TimeSpan.FromSeconds(6), simulateFailure: false);
        VerifyRecording("Recording (6 s, fragmented while recording, then a regular MP4)", saved, normal, minimumSeconds: 5);

        // 2. The first set-up refuses frame 0: the next one takes over.
        saved = null;
        RecordingSession? fallback = await RecordAsync(controller, TimeSpan.FromSeconds(3), simulateFailure: true);
        bool reported = fallback?.SetupFailures.Contains("simulated by the self-test", StringComparison.Ordinal) == true;
        VerifyRecording("Recording after a refused first set-up", saved, fallback, minimumSeconds: 2, extraCheck: reported, extraDetail: $"set-up failure reported: {reported}");

        // 3. The PC "dies" mid-recording: a copy of the file as it is at that moment must be recoverable.
        saved = null;
        string crashCopy = Path.Combine(_output, "crash", "copy taken while recording.mp4");
        RecordingSession? crashed = await RecordAsync(controller, TimeSpan.FromSeconds(8), simulateFailure: false, afterSeconds: 5, during: session =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(crashCopy)!);
            using var source = new FileStream(session.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new FileStream(crashCopy, FileMode.Create, FileAccess.Write);
            source.CopyTo(copy);
            Log.Info($"Self-test: copied the recording mid-way ({copy.Length:N0} bytes) to {crashCopy}.");
        });
        Log.Info($"Self-test: the crash copy before recovery:{Environment.NewLine}{Mp4BoxDump.Dump(crashCopy, 2)}");
        FinishedFile recovered = RecordingFinisher.Finish(crashCopy, "recovered after a simulated crash.mp4", [Path.Combine(_output, "crash", "recovered")], Path.Combine(_output, "crash", "unrecoverable"));
        TrackSummary? recoveredVideo = recovered.Summary?.Video;
        Mp4Check? recoveredCheck = recovered.Path is { } recoveredPath && File.Exists(recoveredPath) ? Mp4Inspector.Inspect(recoveredPath) : null;
        Result(
            "Recovery of a recording cut off mid-way (copy at 5 s)",
            recovered.Playable && recoveredVideo is { Seconds: >= 3 } && recovered.PlaybackCheck is null && recoveredCheck is { Readable: true },
            Invariant($"{recovered.Repair?.Outcome}: {recovered.Repair?.Detail}; video {recoveredVideo?.Seconds:0.00} s, audio {recovered.Summary?.Audio?.Seconds:0.00} s; Windows plays it: {recovered.PlaybackCheck ?? "yes"}; full read: {recoveredCheck?.Readable}"));
        Log.Info($"Self-test: the recovered file:{Environment.NewLine}{Mp4BoxDump.Dump(recovered.Path ?? crashCopy, 1)}");

        // 4. Audio/video timing through the real encoders and the crash-safe container.
        string syncFolder = Path.Combine(_output, "sync");
        Directory.CreateDirectory(syncFolder);
        SyncCheck("fragmented MP4, as recorded", () => SyncTest.Run(Path.Combine(syncFolder, "sync-fragmented.mp4"), "fragmented MP4, as recorded", new Size(1280, 720), fragmented: true, forceBFrames: null), required: true);
        SyncCheck("regular MP4 (fallback format)", () => SyncTest.Run(Path.Combine(syncFolder, "sync-regular.mp4"), "regular MP4 (fallback format)", new Size(1280, 720), fragmented: false, forceBFrames: null), required: true);
        SyncCheck("2 B-frames forced (edit list)", () => SyncTest.Run(Path.Combine(syncFolder, "sync-bframes.mp4"), "fragmented MP4 with 2 B-frames forced (edit list)", new Size(1280, 720), fragmented: true, forceBFrames: 2), required: false);
        SyncCheck("4K, software encoder", () => SyncTest.Run(Path.Combine(syncFolder, "sync-4k.mp4"), "4K (3840x2160), software encoder, 256 kbps audio", new Size(3840, 2160), fragmented: true, forceBFrames: null, audioBitrate: 256_000), required: true);

        // 5. The global shortcut: registered, and a second registration of the same keys is refused.
        using (var window = new MessageWindow())
        using (var other = new MessageWindow())
        using (var hotkeys = new HotkeyManager(window.Handle))
        using (var competitor = new HotkeyManager(other.Handle))
        {
            var test = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x7B);
            string? first = hotkeys.Register(test);
            string? second = competitor.Register(test);
            bool free = hotkeys.IsFree(other.Handle, test);
            Result("Global shortcut (registered; a conflict is reported)", first is null && second == HotkeyManager.Taken && free,
                $"first: {first ?? "registered"}; second program: {second ?? "registered (wrong)"}");
        }

        // 6. Settings survive a damaged file.
        var store = new SettingsStore(Path.Combine(_output, "settings-test", "settings.json"));
        store.Save(new AppSettings { VideoQuality = 5, Shortcut = "Shift+Up" });
        bool remembered = store.Load().Settings is { VideoQuality: 5 } loaded && loaded.Shortcut == "Shift+Up";
        File.WriteAllText(store.FilePath, "{ damaged");
        (AppSettings defaults, _, string? problem) = store.Load();
        Result("Settings remembered; a damaged settings file falls back to defaults", remembered && problem is not null && defaults == new AppSettings(), problem ?? "no problem reported");
    }

    private async Task<RecordingSession?> RecordAsync(
        RecordingController controller, TimeSpan length, bool simulateFailure, double afterSeconds = 0, Action<RecordingSession>? during = null)
    {
        controller.SimulateSetupFailure = simulateFailure;
        await controller.StartAsync("self-test");
        RecordingSession? session = controller.Session;
        if (session is null)
        {
            return null;
        }

        if (during is not null)
        {
            await Task.Delay(TimeSpan.FromSeconds(afterSeconds));
            during(session);
            await Task.Delay(length - TimeSpan.FromSeconds(afterSeconds));
        }
        else
        {
            await Task.Delay(length);
        }

        await controller.StopAsync("self-test finished");
        DateTime waitUntil = DateTime.UtcNow.AddMinutes(2);
        while (controller.Finishing && DateTime.UtcNow < waitUntil)
        {
            await Task.Delay(100);
        }

        return session;
    }

    private void VerifyRecording(string name, string? path, RecordingSession? session, double minimumSeconds, bool extraCheck = true, string? extraDetail = null)
    {
        if (session is null || path is null || !File.Exists(path))
        {
            Result(name, false, session is null ? "it did not start" : $"no saved file (path: {path ?? "none"})");
            return;
        }

        Mp4Summary summary = Mp4File.Summarize(path);
        Mp4Check check = Mp4Inspector.Inspect(path);
        TrackSummary? video = summary.Video;
        TrackSummary? audio = summary.Audio;
        double fps = video is { Seconds: > 0 } ? video.Samples / video.Seconds : 0;
        bool audioMatches = audio is not null && video is not null && Math.Abs(audio.Seconds - video.Seconds) < 0.1;
        bool inFolder = path.StartsWith(_paths.DefaultRecordings, StringComparison.OrdinalIgnoreCase);
        bool inProgressEmpty = !Directory.EnumerateFiles(_paths.InProgress, "*.mp4").Any();
        bool regular;
        using (FileStream stream = File.OpenRead(path))
        {
            regular = Mp4Scanner.Scan(stream) is { Kind: Mp4Kind.Regular } layout && layout.TopLevel.All(b => b.Type != "moof");
        }

        bool passed = summary.Ok && check.Readable && video is not null && video.Seconds >= minimumSeconds && Math.Abs(fps - 30) < 0.6
            && audioMatches && inFolder && inProgressEmpty && regular && session.IsFragmented && extraCheck;
        Result(name, passed, Invariant(
            $"{RecordingFinisher.Describe(summary)}; fps {fps:0.00}; Windows read {check.Frames} frames and {check.Audio.Describe()}; fragmented while recording {session.IsFragmented}; regular MP4 now {regular}; in EKrecordings {inFolder}; InProgress empty {inProgressEmpty}; {session.FramePath}{(extraDetail is null ? "" : "; " + extraDetail)}"));
    }

    private void SyncCheck(string name, Func<SyncResult> run, bool required)
    {
        SyncResult result;
        try
        {
            result = run();
        }
        catch (Exception ex)
        {
            Log.Error($"The A/V timing test ({name}) failed", ex);
            if (required)
            {
                Result($"A/V timing: {name}", false, $"{ex.GetType().Name}: {ex.Message}");
            }
            else
            {
                _report.AppendLine($"INFO A/V timing: {name}: could not run: {ex.Message}");
            }

            return;
        }

        bool passed = result.OffsetsMs.Count >= 4 && result.WorstMs <= 2.0 && Math.Abs(result.AudioSeconds - result.VideoSeconds) < 0.05;
        string offsets = string.Join(", ", result.OffsetsMs.Select(o => o.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)));
        string detail = Invariant($"beep minus picture: [{offsets}] ms; audio track {result.AudioSeconds:0.000} s, video {result.VideoSeconds:0.000} s; {result.Detail}");
        if (required)
        {
            Result($"A/V timing: {result.Name}", passed, detail);
        }
        else
        {
            _report.AppendLine($"INFO A/V timing: {result.Name}: {(passed ? "in sync" : "NOT in sync")}: {detail}");
        }
    }

    /// <summary>Records until the process is killed (the build machine's crash test).</summary>
    private async Task RecordUntilKilledAsync()
    {
        var settings = new AppSettings();
        var controller = new RecordingController(_paths, () => settings);
        await controller.StartAsync("self-test crash test");
        if (controller.Session is not { } session)
        {
            Result("Recording for the crash test", false, "it did not start");
            return;
        }

        File.WriteAllText(Path.Combine(_output, "recording-started.txt"), session.TemporaryPath);
        Log.Info("Self-test: recording until this process is killed.");
        await Task.Delay(Timeout.Infinite);
    }

    /// <summary>What the next start does after a crash: the recording left in InProgress is recovered.</summary>
    private void Recover()
    {
        List<Leftover> leftovers = RecoveryService.FindLeftovers(_paths);
        Result("An interrupted recording is found at the next start", leftovers.Count == 1, $"{leftovers.Count} found: {string.Join(", ", leftovers.Select(l => $"{Path.GetFileName(l.Path)} ({l.Length:N0} bytes, journal {l.Journal?.State ?? "none"})"))}");
        foreach (Leftover leftover in leftovers)
        {
            Log.Info($"Self-test: the killed recording before recovery:{Environment.NewLine}{Mp4BoxDump.Dump(leftover.Path, 2)}");
            FinishedFile finished = RecoveryService.Recover(leftover, _paths, new AppSettings());
            Mp4Check? check = finished.Path is { } path && File.Exists(path) ? Mp4Inspector.Inspect(path) : null;
            TrackSummary? video = finished.Summary?.Video;
            TrackSummary? audio = finished.Summary?.Audio;
            bool named = finished.Path?.Contains("(recovered)", StringComparison.Ordinal) == true;
            Result(
                "The killed recording is recovered and plays",
                finished.Playable && video is { Seconds: >= 6 } && audio is not null && check is { Readable: true } && finished.PlaybackCheck is null && named,
                Invariant($"{finished.Path}; {finished.Repair?.Outcome}: {finished.Repair?.Detail}; video {video?.Seconds:0.00} s, audio {audio?.Seconds:0.00} s; Windows read {check?.Frames} frames; plays: {finished.PlaybackCheck ?? "yes"}"));
        }
    }

    /// <summary>The Settings window as a picture, with this machine's monitors and devices.</summary>
    private async Task ScreenshotAsync()
    {
        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetMonitors();
        using var window = new MessageWindow();
        using var hotkeys = new HotkeyManager(window.Handle);
        var settings = new AppSettings { MonitorId = monitors.FirstOrDefault()?.StableId, MonitorName = monitors.FirstOrDefault()?.FriendlyName };
        using var form = new SettingsForm(settings, monitors, hotkeys, _ => null, _ => { });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(20, 20);
        form.Show();
        await form.LoadDevicesAsync();
        await Task.Delay(800);
        form.Activate();
        await Task.Delay(400);
        Rectangle bounds = form.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        Directory.CreateDirectory(_output);
        string png = Path.Combine(_output, "settings.png");
        bitmap.Save(png, ImageFormat.Png);
        File.WriteAllText(Path.Combine(_output, "settings.png.base64.txt"), Convert.ToBase64String(File.ReadAllBytes(png)));
        Result("Settings window shown", true, Invariant($"{bounds.Width}x{bounds.Height} at {form.DeviceDpi} DPI"));
        form.Close();
    }

    private void Result(string name, bool passed, string detail)
    {
        if (!passed)
        {
            _failures++;
        }

        string line = $"{(passed ? "PASS" : "FAIL")} {name}: {detail}";
        _report.AppendLine(line);
        Log.Info($"Self-test: {line}");
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
