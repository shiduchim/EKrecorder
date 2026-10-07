using System.Diagnostics;
using EKrecorder.App;
using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Overlays;
using EKrecorder.Platform;
using EKrecorder.Recording;
using Microsoft.Win32;

namespace EKrecorder.Shell;

/// <summary>
/// EKrecorder as it normally runs: an icon in the notification area, the global start/stop shortcut, and the
/// Settings window on demand. Tray menu: Start/Stop recording, Settings, Open recordings folder, Open last recording,
/// Exit. Double-clicking the icon opens Settings; closing Settings never exits; Exit finishes a running recording
/// first. It also finishes recordings an earlier run left behind, and stops and saves a recording when Windows shuts
/// down or goes to sleep.
/// </summary>
internal sealed class TrayApplication : ApplicationContext
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _store;
    private readonly SingleInstance? _instance;
    private readonly MessageWindow _window;
    private readonly HotkeyManager _hotkeys;
    private readonly RecordingController _controller;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startStop;
    private readonly ToolStripMenuItem _openLast;
    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _identifyTimer = new() { Interval = 3000 };
    private AppSettings _settings;
    private SettingsForm? _settingsForm;
    private IdentifyOverlays? _identify;
    private Action? _balloonClick;
    private bool _exiting;

    public TrayApplication(AppPaths paths, SettingsStore store, AppSettings settings, bool firstRun, List<Leftover> leftovers, SingleInstance? instance, bool showSettings)
    {
        if (SynchronizationContext.Current is null)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }

        _ui = SynchronizationContext.Current!;
        _paths = paths;
        _store = store;
        _settings = settings;
        _instance = instance;
        _window = new MessageWindow();
        _hotkeys = new HotkeyManager(_window.Handle);
        _controller = new RecordingController(paths, () => _settings);

        _startStop = new ToolStripMenuItem("Start recording", null, (_, _) => _ = _controller.ToggleAsync("tray menu"));
        var settingsItem = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings());
        var openFolder = new ToolStripMenuItem("Open recordings folder", null, (_, _) => OpenRecordingsFolder());
        _openLast = new ToolStripMenuItem("Open last recording", null, (_, _) => OpenLastRecording());
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => _ = ExitAsync());
        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([_startStop, new ToolStripSeparator(), settingsItem, openFolder, _openLast, new ToolStripSeparator(), exit]);
        _menu.Opening += (_, _) => UpdateMenu(checkLastRecording: true);
        _tray = new NotifyIcon { Icon = AppIcons.Idle, ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();

        _controller.StateChanged += UpdateTray;
        _controller.Noticed += ShowNotice;
        _controller.Saved += RememberLastRecording;
        _window.HotkeyPressed += () =>
        {
            if (!_exiting)
            {
                _ = _controller.ToggleAsync("shortcut");
            }
        };
        _window.QueryEndSession = () => _controller.Busy;
        _window.CloseRequested += () => _ = ExitAsync();
        _window.EndingSession += OnEndingSession;
        _window.Suspending += OnSuspending;
        _window.Resumed += () => Log.Info("Windows woke up.");
        _window.DisplayChanged += _controller.OnDisplayChanged;
        _identifyTimer.Tick += (_, _) => HideIdentify();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        if (_instance is not null)
        {
            _instance.ShowRequested += () => _ui.Post(_ => ShowSettings(), null);
            _instance.ExitRequested += () => _ui.Post(_ => _ = ExitAsync(), null);
        }

        if (_hotkeys.Register(_settings.Hotkey) is not null)
        {
            ShowNotice(new Notice(
                "Shortcut not available",
                $"EKrecorder couldn't use the shortcut {_settings.Hotkey.ToDisplay()} because another program is already using it. Click here to choose another one.",
                NoticeKind.Warning,
                OpenSettings: true));
        }

        if (StartupRegistration.Apply(_settings.StartWithWindows) is { } startupProblem)
        {
            Log.Warn($"Start with Windows could not be set: {startupProblem}");
        }

        if (firstRun)
        {
            SaveSettings(_settings);
        }

        UpdateTray();
        if (leftovers.Count > 0)
        {
            _ = RecoverAsync(leftovers);
        }

        if (showSettings)
        {
            _ui.Post(_ => ShowSettings(), null);
        }

        Log.Info($"EKrecorder is ready in the tray. Shortcut: {_hotkeys.Current.ToDisplay()}.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            HideIdentify();
            _identifyTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _hotkeys.Dispose();
            _controller.Dispose();
            _window.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateTray()
    {
        string shortcut = _hotkeys.Current.IsEmpty ? "" : $" ({_hotkeys.Current.ToDisplay()})";
        string text;
        switch (_controller.State)
        {
            case RecorderState.Recording when _controller.NeedsAttention:
                _tray.Icon = AppIcons.Attention;
                text = $"EKrecorder – recording, needs attention: {string.Join("; ", _controller.Problems)}";
                break;
            case RecorderState.Recording:
            case RecorderState.Starting:
                _tray.Icon = AppIcons.Recording;
                text = $"EKrecorder – recording{shortcut}";
                break;
            case RecorderState.Stopping:
                _tray.Icon = AppIcons.Recording;
                text = "EKrecorder – saving the recording…";
                break;
            default:
                _tray.Icon = AppIcons.Idle;
                text = $"EKrecorder – ready{shortcut}";
                break;
        }

        // The notification area shows at most 127 characters.
        _tray.Text = text.Length > 127 ? text[..126] + "…" : text;
        _settingsForm?.SetRecording(_controller.IsRecording);
        UpdateMenu();
    }

    /// <param name="checkLastRecording">
    /// Only when the menu opens: whether the last recording still exists (a folder on an unreachable network drive
    /// can take long to answer, and this runs on the window thread).
    /// </param>
    private void UpdateMenu(bool checkLastRecording = false)
    {
        bool recording = _controller.IsRecording;
        _startStop.Text = recording ? "Stop recording" : "Start recording";
        _startStop.ShortcutKeyDisplayString = _hotkeys.Current.IsEmpty ? null : _hotkeys.Current.ToDisplay();
        _startStop.Enabled = _controller.State is RecorderState.Idle or RecorderState.Recording && !_exiting;
        _openLast.Enabled = _settings.LastRecording is { } last && (!checkLastRecording || File.Exists(last));
    }

    private void ShowSettings()
    {
        if (_exiting)
        {
            return;
        }

        if (_settingsForm is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized)
            {
                open.WindowState = FormWindowState.Normal;
            }

            open.Activate();
            return;
        }

        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetMonitors();
        var form = new SettingsForm(_settings, monitors, _hotkeys, ApplySettings, ShowIdentify);
        form.SetRecording(_controller.IsRecording);
        form.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            form.Dispose();
        };
        _settingsForm = form;
        form.Show();
        form.Activate();
        _ = form.LoadDevicesAsync();
    }

    /// <summary>
    /// Checks, saves and applies the Settings window's choices. Returns what went wrong, or null; when something
    /// goes wrong nothing is changed (what was already applied is undone).
    /// </summary>
    private string? ApplySettings(AppSettings updated)
    {
        // The window started from the settings of when it opened; what changed since without it (the last
        // recording) is kept.
        updated = updated with { LastRecording = _settings.LastRecording };
        string folder = _paths.RecordingsFolder(updated);
        if (!CanWriteTo(folder))
        {
            return "EKrecorder can't save recordings in that folder. Please choose another one.";
        }

        Hotkey previousHotkey = _hotkeys.Current;
        bool hotkeyChanged = updated.Hotkey != previousHotkey;
        if (hotkeyChanged && _hotkeys.Register(updated.Hotkey) is { } problem)
        {
            _hotkeys.Register(previousHotkey);
            return problem;
        }

        bool startupChanged = updated.StartWithWindows != _settings.StartWithWindows;
        if (startupChanged && StartupRegistration.Apply(updated.StartWithWindows) is not null)
        {
            UndoHotkey();
            return "Windows didn't let EKrecorder change the Start with Windows setting. The log has more information.";
        }

        if (!SaveSettings(updated))
        {
            UndoHotkey();
            if (startupChanged)
            {
                StartupRegistration.Apply(_settings.StartWithWindows);
            }

            return "EKrecorder couldn't save its settings. The log has more information.";
        }

        Log.Info($"Settings saved: monitor {updated.MonitorName ?? updated.MonitorId ?? "main"}, video {RecordingQuality.VideoLevelOf(updated.VideoQuality).Label}, audio {RecordingQuality.AudioLevelOf(updated.AudioQuality).Label}, "
            + $"microphone {updated.MicrophoneName ?? "Windows default"}, computer audio {updated.OutputName ?? "Windows default outputs"}, folder {folder}, shortcut {updated.Hotkey.ToDisplay()}, "
            + $"start with Windows {updated.StartWithWindows}, stop after {(updated.MaxRecordingHours == 0 ? "no limit" : $"{updated.MaxRecordingHours} h")}.");
        UpdateTray();
        return null;

        void UndoHotkey()
        {
            if (hotkeyChanged)
            {
                _hotkeys.Register(previousHotkey);
            }
        }
    }

    private bool SaveSettings(AppSettings settings)
    {
        try
        {
            _store.Save(settings);
            _settings = settings;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Error("Saving settings.json failed", ex);
            return false;
        }
    }

    private static bool CanWriteTo(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, $".ekrecorder-write-test-{Environment.ProcessId}.tmp");
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Warn($"Cannot write to {folder}: {ex.Message}");
            return false;
        }
    }

    private void ShowIdentify(IReadOnlyList<MonitorInfo> monitors)
    {
        HideIdentify();
        _identify = IdentifyOverlays.Show(MonitorEnumerator.GetMonitors());
        _identifyTimer.Start();
    }

    private void HideIdentify()
    {
        _identifyTimer.Stop();
        _identify?.Dispose();
        _identify = null;
    }

    private void ShowNotice(Notice notice)
    {
        if (_exiting && notice.Kind != NoticeKind.Error)
        {
            return;
        }

        _balloonClick = notice.OpenSettings ? ShowSettings
            : notice.Open is { } open ? () => Open(open)
            : null;
        _tray.ShowBalloonTip(
            8000,
            notice.Title,
            notice.Text,
            notice.Kind switch
            {
                NoticeKind.Error => ToolTipIcon.Error,
                NoticeKind.Warning => ToolTipIcon.Warning,
                _ => ToolTipIcon.Info,
            });
    }

    private void RememberLastRecording(string path)
    {
        SaveSettings(_settings with { LastRecording = path });
        UpdateMenu();
    }

    private async Task RecoverAsync(List<Leftover> leftovers)
    {
        foreach (Leftover leftover in leftovers)
        {
            AppSettings settings = _settings;
            FinishedFile finished;
            try
            {
                finished = await Task.Run(() => RecoveryService.Recover(leftover, _paths, settings));
            }
            catch (Exception ex)
            {
                Log.Error($"Recovering {leftover.Path} failed", ex);
                continue;
            }

            bool clean = leftover.Journal?.State == RecordingJournal.Stopped;
            if (finished.Playable && finished.Path is { } path && !path.StartsWith(_paths.InProgress, StringComparison.OrdinalIgnoreCase))
            {
                RememberLastRecording(path);
                ShowNotice(clean
                    ? new Notice("Recording saved", $"{Path.GetFileName(path)} (finished after Windows restarted).", NoticeKind.Info, path)
                    : new Notice("Recording recovered after a problem", Path.GetFileName(path), NoticeKind.Info, path));
            }
            else if (finished.Path is { } stuck && string.Equals(Path.GetDirectoryName(stuck), _paths.InProgress, StringComparison.OrdinalIgnoreCase))
            {
                // Still in InProgress (it could not be finished or moved yet): tried again at the next start.
                ShowNotice(new Notice("A recording is not saved yet", "EKrecorder will try again the next time it starts. The recording log contains more information.", NoticeKind.Warning, _paths.InProgress));
            }
            else if (!finished.Playable && leftover.Length > 1024 * 1024)
            {
                ShowNotice(new Notice("A recording could not be recovered", "EKrecorder kept the file. The recording log contains more information.", NoticeKind.Warning, _paths.Unrecoverable));
            }
        }
    }

    private void OnEndingSession()
    {
        Log.Info("Windows is ending the session (shutdown, restart or log off).");
        _controller.StopNow("Windows is shutting down", TimeSpan.FromSeconds(4), StopKind.Shutdown);
        Log.Info("Ready for Windows to end the session.");
    }

    private void OnSuspending()
    {
        // Windows gives about two seconds here; a file not finished by then is finished after waking up.
        Log.Info("Windows is going to sleep.");
        _controller.StopNow("the PC is going to sleep", TimeSpan.FromSeconds(2), StopKind.Sleep);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) =>
        Log.Info($"Windows session: {e.Reason}{(_controller.IsRecording ? " (recording continues)" : "")}.");

    private void OpenRecordingsFolder()
    {
        string folder = _paths.RecordingsFolder(_settings);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not create {folder}", ex);
        }

        Open(folder);
    }

    private void OpenLastRecording()
    {
        if (_settings.LastRecording is { } last && File.Exists(last))
        {
            Open(last);
        }
        else
        {
            OpenRecordingsFolder();
        }
    }

    private static void Open(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }

    /// <summary>True when nothing is being recorded or saved (Media Foundation can then be shut down).</summary>
    public bool Idle => !_controller.Busy;

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Log.Info("Exit requested.");
        _settingsForm?.Close();
        UpdateTray();
        if (_controller.IsRecording || _controller.Finishing)
        {
            _tray.Text = "EKrecorder – saving the recording…";
        }

        await _controller.ShutdownAsync(TimeSpan.FromMinutes(3));
        _tray.Visible = false;
        ExitThread();
    }
}
