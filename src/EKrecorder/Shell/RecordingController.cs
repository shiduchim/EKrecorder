using EKrecorder.App;
using EKrecorder.Audio;
using EKrecorder.Capture;
using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Overlays;
using EKrecorder.Platform;
using EKrecorder.Recording;
using Windows.Graphics.Capture;

namespace EKrecorder.Shell;

internal enum RecorderState
{
    Idle,
    Starting,
    Recording,
    Stopping,
}

/// <summary>Why a recording stopped (decides the notification).</summary>
internal enum StopKind
{
    User,
    MaxTime,
    DiskFull,
    Failure,
    Exit,
    Shutdown,
    Sleep,
}

internal enum NoticeKind
{
    Info,
    Warning,
    Error,
}

/// <summary>A small Windows notification. <c>Open</c>: what a click opens (a file or folder); <c>OpenSettings</c>: a click opens Settings.</summary>
internal sealed record Notice(string Title, string Text, NoticeKind Kind, string? Open = null, bool OpenSettings = false);

/// <summary>
/// Runs recordings for the tray app: starts one with the current settings, watches it once a second (the
/// triangle's colour, free disk space, the maximum recording time, the monitor coming and going), stops it, and
/// has it finished and saved in the background. A recording that stops by itself because of an error is saved and
/// a new one is started at once (at most three times in a row). Runs on the window thread.
/// </summary>
internal sealed class RecordingController : IDisposable
{
    private static readonly TimeSpan DiskCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(2);
    private const int MaxFailuresInARow = 3;

    private readonly AppPaths _paths;
    private readonly Func<AppSettings> _settings;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly AttentionTracker _attention = new();
    private readonly List<Task> _finishing = new();
    private readonly TaskScheduler _ui;
    private RecordingSession? _session;
    private RecordingIndicator? _indicator;
    private KeepAwake? _keepAwake;
    private RecordingJournal? _journal;
    private string? _substituteMonitor;
    private MonitorInfo? _lastSessionMonitor;
    private MonitorInfo? _placement;
    private string? _pendingStop;
    private DateTime _lastDiskCheck;
    private long _lastLength;
    private DateTime _lastLengthTime;
    private double _bytesPerSecond;
    private DiskState _disk;
    private int _failuresInARow;
    private DateTime _lastFailure;
    private bool _avoidFragmented;
    private bool _stopping;

    public RecordingController(AppPaths paths, Func<AppSettings> settings)
    {
        _paths = paths;
        _settings = settings;
        _ui = TaskScheduler.FromCurrentSynchronizationContext();
        _timer.Tick += (_, _) => OnTick();
    }

    /// <summary>Raised on the window thread when recording starts or stops, or the triangle changes colour.</summary>
    public event Action? StateChanged;

    public event Action<Notice>? Noticed;

    /// <summary>A recording was saved (its path).</summary>
    public event Action<string>? Saved;

    public RecorderState State { get; private set; }

    public bool IsRecording => State is RecorderState.Recording or RecorderState.Starting;

    public bool NeedsAttention => _attention.NeedsAttention;

    public IReadOnlyList<string> Problems => _attention.Active;

    /// <summary>The recording running now (null when none is). For the self-test.</summary>
    public RecordingSession? Session => _session;

    /// <summary>True while a stopped recording is still being finished and saved.</summary>
    public bool Finishing => _finishing.Any(t => !t.IsCompleted);

    /// <summary>Self-test only: the next recording's first set-up is made to fail.</summary>
    public bool SimulateSetupFailure { get; set; }

    public Task ToggleAsync(string trigger) => State switch
    {
        RecorderState.Idle => StartAsync(trigger),
        RecorderState.Recording or RecorderState.Starting => StopAsync($"stopped ({trigger})"),
        _ => Task.CompletedTask,
    };

    public async Task StartAsync(string trigger)
    {
        if (State != RecorderState.Idle)
        {
            return;
        }

        SetState(RecorderState.Starting);
        Log.Info($"===== Start recording ({trigger})");
        string? temporary = null;
        try
        {
            AppSettings settings = _settings();
            RecordingPreset preset = RecordingQuality.Preset(settings.VideoQuality, settings.AudioQuality);
            (MonitorInfo? monitor, string? substitute) = ChooseMonitor(settings);
            if (monitor is null)
            {
                Notify(new Notice("EKrecorder couldn't start recording", "No monitor was found.", NoticeKind.Error));
                SetState(RecorderState.Idle);
                return;
            }

            Directory.CreateDirectory(_paths.InProgress);
            long free = FreeSpace(_paths.InProgress);
            if (free >= 0 && free < DiskSpacePolicy.MinimumToStart)
            {
                Log.Decision($"Not recording: only {free / 1048576.0:0} MB free on the disk with {_paths.InProgress}.");
                Notify(new Notice("EKrecorder couldn't start recording", "The disk is almost full. Free up some space and try again.", NoticeKind.Error));
                SetState(RecorderState.Idle);
                return;
            }

            DateTime start = DateTime.Now;
            string name = RecordingNames.For(start);
            temporary = RecordingNames.FreePath(_paths.InProgress, name);
            _journal = new RecordingJournal
            {
                FinalName = name,
                FinalFolder = _paths.RecordingsFolder(settings),
                StartedLocal = start,
                ProcessId = Environment.ProcessId,
                Monitor = monitor.Summary,
                Quality = preset.Describe(),
            };
            _journal.TryWrite(temporary);

            CaptureAccessResult borderless = await CaptureSessionSetup.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            var audio = new AudioSelection(settings.MicrophoneId, settings.MicrophoneName, settings.OutputId, settings.OutputName);
            bool simulate = SimulateSetupFailure;
            SimulateSetupFailure = false;
            RecordingSession session = await RecordingSession.StartAsync(monitor, preset, temporary, borderless.Text, audio, simulate, allowFragmented: !_avoidFragmented);

            _session = session;
            _lastSessionMonitor = session.CurrentMonitor;
            _placement = session.CurrentMonitor;
            _substituteMonitor = substitute;
            _attention.Update(DateTime.UtcNow, []);
            _disk = DiskState.Ok;
            _lastDiskCheck = DateTime.MinValue;
            _lastLength = 0;
            _lastLengthTime = DateTime.UtcNow;
            _bytesPerSecond = (preset.PeakBitrate + preset.AudioBitrate) / 8.0;
            _indicator = new RecordingIndicator();
            if (!_indicator.Show(session.CurrentMonitor))
            {
                Log.Warn("Recording without the triangle: it could not be shown safely (see above).");
            }

            _keepAwake = KeepAwake.Start("EKrecorder is recording");
            _timer.Start();
            _ = WatchAsync(session);
            SetState(RecorderState.Recording);
            if (substitute is not null)
            {
                Notify(new Notice("Recording a different monitor", substitute, NoticeKind.Warning, OpenSettings: true));
            }

            if (_pendingStop is { } reason)
            {
                _pendingStop = null;
                await StopAsync(reason);
            }
        }
        catch (Exception ex)
        {
            Log.Error("The recording could not start", ex);
            if (temporary is not null && !File.Exists(temporary))
            {
                RecordingJournal.TryDelete(temporary);
            }

            _pendingStop = null;
            SetState(RecorderState.Idle);
            Notify(new Notice("EKrecorder couldn't start recording", "The recording log contains more information.", NoticeKind.Error, _paths.Logs));
        }
    }

    public async Task StopAsync(string reason, StopKind kind = StopKind.User)
    {
        if (State == RecorderState.Starting)
        {
            _pendingStop = reason;
            return;
        }

        if (State != RecorderState.Recording || _session is not { } session || _stopping)
        {
            return;
        }

        _stopping = true;
        SetState(RecorderState.Stopping);
        _timer.Stop();
        Log.Info($"Stopping the recording: {reason}");
        try
        {
            await session.StopAsync(reason);
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the recording failed", ex);
        }

        EndRecording(session, finalized: true);
        _ = FinishAsync(session, kind);
        _stopping = false;
        SetState(RecorderState.Idle);
        if (kind == StopKind.Failure)
        {
            await RestartAfterFailureAsync(session);
        }
    }

    /// <summary>
    /// Windows is shutting down or going to sleep: stops the recording right now and waits (at most
    /// <paramref name="budget"/>) for the file to be finished. Returns true when it was.
    /// </summary>
    public bool StopNow(string reason, TimeSpan budget, StopKind kind)
    {
        if (_session is not { } session)
        {
            return true;
        }

        _stopping = true;
        _pendingStop = null;
        _timer.Stop();
        Log.Info($"Stopping the recording now: {reason}");
        bool finished = session.StopAsync(reason).Wait(budget);
        Log.Info(finished ? "The recording file is finished." : "The recording did not finish in time; it is recovered at the next start.");
        EndRecording(session, finalized: finished);
        if (kind == StopKind.Sleep)
        {
            // Saving goes on in the background (and after waking up, if Windows goes to sleep first).
            _ = FinishAsync(session, kind);
        }

        _stopping = false;
        SetState(RecorderState.Idle);
        return finished;
    }

    /// <summary>Stops a running recording and waits until every recording is saved (at most <paramref name="timeout"/>).</summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        if (State == RecorderState.Starting)
        {
            _pendingStop = "EKrecorder was closed";
            var waited = DateTime.UtcNow;
            while (State == RecorderState.Starting && DateTime.UtcNow - waited < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(100);
            }
        }

        if (State == RecorderState.Recording)
        {
            await StopAsync("EKrecorder was closed", StopKind.Exit);
        }

        Task all = Task.WhenAll(_finishing.ToArray());
        if (await Task.WhenAny(all, Task.Delay(timeout)) != all)
        {
            Log.Warn("Saving the last recording did not finish in time; it is finished at the next start.");
        }
    }

    /// <summary>The displays changed: the triangle follows the recorded monitor if it moved.</summary>
    public void OnDisplayChanged()
    {
        if (_session is { CaptureLost: false } session && _indicator is not null)
        {
            MonitorInfo? moved = MonitorEnumerator.GetMonitors(log: false).FirstOrDefault(m => m.StableId == session.Monitor.StableId);
            if (moved is not null)
            {
                _placement = moved;
                _indicator.MoveTo(moved);
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _indicator?.Dispose();
        _keepAwake?.Dispose();
    }

    /// <summary>The monitor from the settings; the main monitor (with a warning) when that one is not connected.</summary>
    private static (MonitorInfo? Monitor, string? Substitute) ChooseMonitor(AppSettings settings)
    {
        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetMonitors();
        MonitorInfo? main = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (settings.MonitorId is null)
        {
            return (main, null);
        }

        MonitorInfo? chosen = monitors.FirstOrDefault(m => m.StableId == settings.MonitorId);
        if (chosen is not null || main is null)
        {
            return (chosen, null);
        }

        string missing = string.IsNullOrEmpty(settings.MonitorName) ? "The monitor chosen in Settings" : $"The monitor chosen in Settings ({settings.MonitorName})";
        string text = $"{missing} is not connected, so EKrecorder is recording the main monitor.";
        Log.Decision(text);
        return (main, text);
    }

    private static long FreeSpace(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            return root is null ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private async Task WatchAsync(RecordingSession session)
    {
        await session.Completion;
        if (ReferenceEquals(_session, session) && State == RecorderState.Recording && !_stopping)
        {
            Log.Warn($"The recording stopped by itself: {session.StopReason ?? "an error"}.");
            await StopAsync($"it stopped by itself ({session.StopReason ?? "an error"})", StopKind.Failure);
        }
    }

    private void EndRecording(RecordingSession session, bool finalized)
    {
        _indicator?.Dispose();
        _indicator = null;
        _keepAwake?.Dispose();
        _keepAwake = null;
        if (_journal is { } journal && finalized && File.Exists(session.TemporaryPath))
        {
            _journal = journal with { State = RecordingJournal.Stopped };
            _journal.TryWrite(session.TemporaryPath);
        }

        if (ReferenceEquals(_session, session))
        {
            _session = null;
        }

        _attention.Update(DateTime.UtcNow, []);
    }

    private Task FinishAsync(RecordingSession session, StopKind kind)
    {
        RecordingJournal journal = _journal ?? new RecordingJournal { FinalName = Path.GetFileName(session.TemporaryPath) };
        string[] folders = [journal.FinalFolder ?? _paths.RecordingsFolder(_settings()), _paths.DefaultRecordings];
        string reports = _paths.Reports;
        string unrecoverable = _paths.Unrecoverable;
        Task<FinishedFile> work = Task.Run(() =>
        {
            FinishedFile finished = File.Exists(session.TemporaryPath)
                ? RecordingFinisher.Finish(session.TemporaryPath, journal.FinalName, folders, unrecoverable)
                : new FinishedFile(false, null, "nothing was recorded", null, null, null);
            if (!File.Exists(session.TemporaryPath))
            {
                RecordingJournal.TryDelete(session.TemporaryPath);
            }

            RecordingReport.Write(session, finished, reports);
            return finished;
        });
        Task done = work.ContinueWith(t => OnFinished(t, kind), CancellationToken.None, TaskContinuationOptions.None, _ui);
        _finishing.RemoveAll(t => t.IsCompleted);
        _finishing.Add(done);
        return done;
    }

    private void OnFinished(Task<FinishedFile> task, StopKind kind)
    {
        if (task.IsFaulted || task.Result is not { } finished)
        {
            Log.Error("Saving the recording failed", task.Exception?.GetBaseException());
            Notify(new Notice("Recording could not be saved", "EKrecorder had a problem saving the recording. The recording log contains more information.", NoticeKind.Error, _paths.Logs));
            return;
        }

        if (!finished.Playable || finished.Path is null)
        {
            if (finished.Problem != "nothing was recorded")
            {
                Notify(new Notice("Recording could not be saved", "EKrecorder had a problem saving the recording. The recording log contains more information.", NoticeKind.Error, _paths.Logs));
            }

            return;
        }

        Saved?.Invoke(finished.Path);
        string file = Path.GetFileName(finished.Path);
        string where = finished.Problem is null ? file : $"{file} ({finished.Problem})";
        Notice? notice = kind switch
        {
            StopKind.Exit or StopKind.Shutdown => null,
            StopKind.MaxTime => new Notice("Recording stopped and saved", $"The maximum recording time was reached. {where}", NoticeKind.Info, finished.Path),
            StopKind.DiskFull => new Notice("Recording stopped and saved", $"The disk is almost full. {where}", NoticeKind.Warning, finished.Path),
            StopKind.Failure => new Notice("Recording saved after a problem", $"{where}. A new recording was started.", NoticeKind.Warning, finished.Path),
            StopKind.Sleep => new Notice("Recording saved", $"The PC went to sleep. {where}", NoticeKind.Info, finished.Path),
            _ => new Notice("Recording saved", where, finished.Problem is null ? NoticeKind.Info : NoticeKind.Warning, finished.Path),
        };
        if (notice is not null)
        {
            Notify(notice);
        }
    }

    private async Task RestartAfterFailureAsync(RecordingSession failed)
    {
        DateTime now = DateTime.UtcNow;
        _failuresInARow = now - _lastFailure > FailureWindow ? 1 : _failuresInARow + 1;
        _lastFailure = now;
        if (failed.IsFragmented && failed.Elapsed < TimeSpan.FromSeconds(30))
        {
            // The crash-safe file format may be what failed so early; the next recordings use a regular MP4.
            _avoidFragmented = true;
            Log.Decision("The next recordings use a regular MP4: a fragmented one failed early.");
        }

        if (_failuresInARow > MaxFailuresInARow)
        {
            Log.Error($"The recording failed {_failuresInARow} times in a row; not starting again.");
            Notify(new Notice("Recording stopped", "EKrecorder had a problem recording and stopped. The recording log contains more information.", NoticeKind.Error, _paths.Logs));
            return;
        }

        await Task.Delay(1000);
        if (State == RecorderState.Idle)
        {
            await StartAsync("restart after a problem");
        }
    }

    private void OnTick()
    {
        if (_session is not { } session || State != RecorderState.Recording)
        {
            return;
        }

        PlaceIndicator(session);
        var problems = new List<(string Problem, TimeSpan Delay)>();
        if (session.CaptureLost)
        {
            problems.Add(("the recorded monitor is not connected", TimeSpan.Zero));
        }

        if (_substituteMonitor is not null)
        {
            problems.Add(("the monitor chosen in Settings is not connected", TimeSpan.Zero));
        }

        if (session.Fallbacks.Count > 0)
        {
            problems.Add(("the best way to record could not be used (the log says why)", TimeSpan.Zero));
        }

        if (session.AudioError is not null)
        {
            problems.Add(("the file has no sound track", TimeSpan.Zero));
        }

        if (session.Audio is { } audio)
        {
            (AudioInputStatus microphone, AudioInputStatus computer) = audio.Snapshot();
            AddAudioProblems(problems, microphone, isMicrophone: true);
            AddAudioProblems(problems, computer, isMicrophone: false);
        }

        CheckDisk(session, problems);
        if (_attention.Update(DateTime.UtcNow, problems))
        {
            Log.Info(_attention.NeedsAttention ? $"Needs attention: {string.Join("; ", _attention.Active)}." : "All fine again.");
            _indicator?.SetAttention(_attention.NeedsAttention);
            StateChanged?.Invoke();
        }

        _indicator?.KeepOnTop();
        int hours = _settings().MaxRecordingHours;
        if (hours > 0 && session.Elapsed >= TimeSpan.FromHours(hours))
        {
            _ = StopAsync($"the maximum recording time ({hours} h) was reached", StopKind.MaxTime);
        }
        else if (_disk == DiskState.Critical)
        {
            _ = StopAsync("the disk is almost full", StopKind.DiskFull);
        }
    }

    private static void AddAudioProblems(List<(string, TimeSpan)> problems, AudioInputStatus status, bool isMicrophone)
    {
        string what = isMicrophone ? "the microphone" : "the computer audio device";
        switch (status.State)
        {
            case InputState.Lost or InputState.Retrying or InputState.NoDevice:
                problems.Add(($"{what} is not available", TimeSpan.FromSeconds(isMicrophone ? 2 : 3)));
                break;
            case InputState.Fallback:
                problems.Add(($"{what} chosen in Settings is not connected (Windows' default is used)", TimeSpan.FromSeconds(2)));
                break;
            case InputState.Resolving:
                problems.Add(($"{what} is not ready", TimeSpan.FromSeconds(8)));
                break;
        }

        if (isMicrophone && status.Warning is not null)
        {
            problems.Add(("the microphone sends only silence (muted?)", TimeSpan.Zero));
        }
    }

    private void CheckDisk(RecordingSession session, List<(string, TimeSpan)> problems)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastDiskCheck >= DiskCheckInterval)
        {
            _lastDiskCheck = now;
            try
            {
                long length = File.Exists(session.TemporaryPath) ? new FileInfo(session.TemporaryPath).Length : 0;
                double seconds = (now - _lastLengthTime).TotalSeconds;
                if (_lastLength > 0 && seconds > 0 && length >= _lastLength)
                {
                    // Smoothed, and never below a quarter of the planned rate (a still screen writes little).
                    double measured = (length - _lastLength) / seconds;
                    _bytesPerSecond = Math.Max(_bytesPerSecond * 0.25, (0.7 * _bytesPerSecond) + (0.3 * measured));
                }

                _lastLength = length;
                _lastLengthTime = now;
            }
            catch (IOException)
            {
            }

            long free = FreeSpace(_paths.InProgress);
            DiskState disk = free < 0 ? DiskState.Ok : DiskSpacePolicy.Evaluate(free, _bytesPerSecond);
            if (disk != _disk)
            {
                Log.Warn($"Disk space: {disk} ({free / 1048576.0:0} MB free, writing about {_bytesPerSecond / 1024:0} KB/s).");
                _disk = disk;
            }
        }

        if (_disk != DiskState.Ok)
        {
            problems.Add(("the disk is almost full", TimeSpan.Zero));
        }
    }

    private void PlaceIndicator(RecordingSession session)
    {
        if (_indicator is null)
        {
            return;
        }

        if (session.CaptureLost)
        {
            // The recorded monitor is gone; show the (orange) triangle on the main monitor meanwhile.
            MonitorInfo? main = MonitorEnumerator.GetMonitors(log: false).FirstOrDefault(m => m.IsPrimary);
            if (main is not null && _indicator.Monitor?.StableId != main.StableId)
            {
                _indicator.MoveTo(main);
            }

            return;
        }

        // Where the recorded monitor is now: as the recording found it again, or as a display change moved it.
        if (!ReferenceEquals(session.CurrentMonitor, _lastSessionMonitor))
        {
            _lastSessionMonitor = session.CurrentMonitor;
            _placement = session.CurrentMonitor;
        }

        if (_placement is not null)
        {
            _indicator.MoveTo(_placement);
        }
    }

    private void SetState(RecorderState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    private void Notify(Notice notice)
    {
        Log.Info($"Notification: {notice.Title}: {notice.Text}");
        Noticed?.Invoke(notice);
    }
}
