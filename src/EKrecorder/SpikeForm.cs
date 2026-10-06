using System.Diagnostics;
using System.Globalization;
using EKrecorder.Capture;
using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Overlays;
using EKrecorder.Recording;
using Microsoft.Win32;
using Windows.Graphics.Capture;

namespace EKrecorder;

/// <summary>
/// The small test window: pick a monitor, Identify, Start/Stop recording, open the recordings and the last report,
/// and the Step 1 capture test. In self-test mode (the build machine) it runs the capture test and a 6-second
/// recording on Monitor 1, asks nothing, and closes.
/// </summary>
internal sealed class SpikeForm : Form
{
    private readonly bool _selfTest;
    private readonly string _outputRoot;
    private readonly RecordingFolders _folders;
    private readonly FlowLayoutPanel _monitorPanel;
    private readonly Button _identifyButton;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly Button _recordingsButton;
    private readonly Button _reportButton;
    private readonly Button _testButton;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _identifyTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _recordingTimer = new() { Interval = 500 };
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private string? _selectedId;
    private IdentifyOverlays? _identify;
    private RecordingSession? _recording;
    private RecordingIndicator? _indicator;
    private string? _lastReportPath;
    private bool _busy;
    private bool _stopping;
    private bool _closeAfterStop;

    public SpikeForm(bool selfTest, string outputRoot, RecordingFolders folders)
    {
        _selfTest = selfTest;
        _outputRoot = outputRoot;
        _folders = folders;

        Text = "EKrecorder – test build";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        layout.Controls.Add(new Label { Text = "Monitor to record:", AutoSize = true, Margin = new Padding(3, 0, 3, 2) });

        _monitorPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
        };
        layout.Controls.Add(_monitorPanel);

        _identifyButton = new Button { Text = "Identify", AutoSize = true };
        _startButton = new Button { Text = "Start recording", AutoSize = true };
        _stopButton = new Button { Text = "Stop recording", AutoSize = true, Enabled = false };
        layout.Controls.Add(ButtonRow(new Padding(0, 6, 0, 0), _identifyButton, _startButton, _stopButton));

        _recordingsButton = new Button { Text = "Open recordings", AutoSize = true };
        _reportButton = new Button { Text = "Open last report", AutoSize = true, Enabled = false };
        _testButton = new Button { Text = "Capture test", AutoSize = true };
        layout.Controls.Add(ButtonRow(new Padding(0), _recordingsButton, _reportButton, _testButton));

        _status = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            Margin = new Padding(3, 6, 3, 0),
            Text = $"Pick a monitor, then start recording. {RecordingPreset.Default.Describe()}.",
        };
        layout.Controls.Add(_status);
        Controls.Add(layout);

        _identifyButton.Click += (_, _) => ShowIdentify();
        _startButton.Click += async (_, _) => await StartRecordingAsync();
        _stopButton.Click += async (_, _) => await StopRecordingAsync("Stop button");
        _recordingsButton.Click += (_, _) => OpenRecordingsFolder();
        _reportButton.Click += (_, _) => OpenFile(_lastReportPath);
        _testButton.Click += async (_, _) => await RunTestAsync();
        _identifyTimer.Tick += (_, _) => HideIdentify();
        _recordingTimer.Tick += (_, _) => ShowRecordingStatus();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        RefreshMonitors("start");
    }

    /// <summary>0 = everything passed, 1 = a check or the recording failed, 3 = a test crashed.</summary>
    public int ExitCode { get; private set; }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_selfTest)
        {
            await RunTestAsync();
            await RunRecordingSelfTestAsync();
            Close();
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_recording is not null && !_closeAfterStop)
        {
            // Never lose a recording by closing the window: finish the file first, then close.
            e.Cancel = true;
            _closeAfterStop = true;
            await StopRecordingAsync("the window was closed");
            Close();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            HideIdentify();
            _indicator?.Dispose();
            _identifyTimer.Dispose();
            _recordingTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private static FlowLayoutPanel ButtonRow(Padding margin, params Control[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = margin };
        row.Controls.AddRange(buttons);
        return row;
    }

    private void RefreshMonitors(string reason)
    {
        Log.Info($"Finding monitors ({reason}).");
        _monitors = MonitorEnumerator.GetMonitors();
        // Keep the choice by the monitor's identity, not its number: numbers shift when monitors are added or moved.
        if (_selectedId is null || _monitors.All(m => m.StableId != _selectedId))
        {
            _selectedId = _monitors.FirstOrDefault()?.StableId;
        }

        Control[] old = _monitorPanel.Controls.Cast<Control>().ToArray();
        _monitorPanel.SuspendLayout();
        _monitorPanel.Controls.Clear();
        foreach (Control control in old)
        {
            control.Dispose();
        }

        foreach (MonitorInfo monitor in _monitors)
        {
            var choice = new RadioButton
            {
                AutoSize = true,
                Text = monitor.ChoiceLabel,
                Checked = monitor.StableId == _selectedId,
                Tag = monitor.StableId,
                Margin = new Padding(3, 1, 3, 1),
            };
            choice.CheckedChanged += (sender, _) =>
            {
                if (sender is RadioButton { Checked: true, Tag: string id })
                {
                    _selectedId = id;
                    Log.Info($"Selected {_monitors.FirstOrDefault(m => m.StableId == id)?.Name} ({id}).");
                }
            };
            _monitorPanel.Controls.Add(choice);
        }

        if (_monitors.Count == 0)
        {
            _monitorPanel.Controls.Add(new Label { Text = "No monitor found.", AutoSize = true });
        }

        _monitorPanel.ResumeLayout();
        UpdateButtons();
    }

    private MonitorInfo? SelectedMonitor() =>
        _monitors.FirstOrDefault(m => m.StableId == _selectedId) ?? _monitors.FirstOrDefault();

    private void ShowIdentify()
    {
        HideIdentify();
        if (_recording is null)
        {
            RefreshMonitors("Identify");
        }

        _identify = IdentifyOverlays.Show(_monitors);
        foreach (string line in _identify.Describe())
        {
            Log.Info($"Identify: {line}");
        }

        if (_recording is null)
        {
            SetStatus(_identify.AllExcluded
                ? $"Showing EKrecorder's numbers on {_identify.ShownCount} monitor(s). They are excluded from capture."
                : "Some numbers could not be excluded from capture, so they were not shown. See the log.");
        }

        _identifyTimer.Start();
    }

    private void HideIdentify()
    {
        _identifyTimer.Stop();
        _identify?.Dispose();
        _identify = null;
    }

    private async Task StartRecordingAsync()
    {
        if (_busy || _recording is not null)
        {
            return;
        }

        HideIdentify();
        // Fresh monitor handles and positions, in case anything changed since the window opened.
        RefreshMonitors("recording");
        MonitorInfo? monitor = SelectedMonitor();
        if (monitor is null)
        {
            SetStatus("No monitor found.");
            return;
        }

        _busy = true;
        UpdateButtons();
        SetStatus($"Starting to record {monitor.Name}…");
        try
        {
            CaptureAccessResult borderless = await CaptureSessionSetup.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            Directory.CreateDirectory(_folders.InProgress);
            string temporaryPath = Path.Combine(_folders.InProgress, $"EKrecording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            RecordingSession session = await RecordingSession.StartAsync(monitor, RecordingPreset.Default, temporaryPath, borderless.Text);
            _recording = session;

            _indicator = new RecordingIndicator();
            if (!_indicator.Show(monitor))
            {
                Log.Warn("Recording without the blue triangle: it could not be shown safely (see the log).");
            }

            _ = HandleSelfStopAsync(session);
            _recordingTimer.Start();
            ShowRecordingStatus();
        }
        catch (Exception ex)
        {
            Log.Error("The recording could not start", ex);
            SetStatus($"The recording could not start: {ex.Message}");
            _indicator?.Dispose();
            _indicator = null;
            _recording = null;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    /// <summary>Finishes the file when the recording stops on its own (monitor gone, encoder error).</summary>
    private async Task HandleSelfStopAsync(RecordingSession session)
    {
        await session.Completion;
        if (ReferenceEquals(_recording, session) && !_stopping)
        {
            await StopRecordingAsync("stopped by itself");
        }
    }

    private async Task<FinishedRecording?> StopRecordingAsync(string reason)
    {
        RecordingSession? session = _recording;
        if (session is null || _stopping)
        {
            return null;
        }

        _stopping = true;
        UpdateButtons();
        SetStatus("Finishing the recording…");
        try
        {
            await session.StopAsync(reason);
            _recordingTimer.Stop();
            _indicator?.Dispose();
            _indicator = null;
            FinishedRecording finished = await Task.Run(() => RecordingReport.Finish(session, _folders.Recordings, _folders.Reports));
            _lastReportPath = finished.ReportPath;
            SetStatus(finished.Saved
                ? $"{finished.Summary}. The report is open; please send its SUMMARY."
                : $"{finished.Summary}. See the report.");
            if (!_selfTest)
            {
                OpenFile(finished.ReportPath);
            }

            return finished;
        }
        catch (Exception ex)
        {
            Log.Error("Finishing the recording failed", ex);
            SetStatus($"Finishing the recording failed: {ex.Message}");
            return null;
        }
        finally
        {
            _recordingTimer.Stop();
            _indicator?.Dispose();
            _indicator = null;
            _recording = null;
            _stopping = false;
            UpdateButtons();
        }
    }

    private void ShowRecordingStatus()
    {
        if (_recording is not { } session || _stopping)
        {
            return;
        }

        // Another topmost window (the taskbar, for example) can be raised above the triangle; put it back.
        _indicator?.KeepOnTop();
        RecordingStats stats = session.Stats;
        TimeSpan length = TimeSpan.FromTicks(session.Preset.FrameTime(session.Slots));
        string dropped = stats.FramesDropped > 0 ? $", {stats.FramesDropped} dropped" : "";
        SetStatus(string.Create(CultureInfo.InvariantCulture,
            $"Recording {session.Monitor.Name}: {length:hh\\:mm\\:ss}, {session.OutputSize.Width}x{session.OutputSize.Height} at {session.Preset.FramesPerSecond} fps, {session.Encoder?.Summary ?? "encoder"}{dropped}."));
    }

    private async Task RunRecordingSelfTestAsync()
    {
        await StartRecordingAsync();
        if (_recording is null)
        {
            ExitCode = Math.Max(ExitCode, 1);
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(6));
        FinishedRecording? finished = await StopRecordingAsync("self-test finished");
        if (finished is null || !finished.Saved)
        {
            ExitCode = Math.Max(ExitCode, 1);
        }
    }

    private async Task RunTestAsync()
    {
        if (_busy || _recording is not null)
        {
            return;
        }

        HideIdentify();
        // Fresh monitor handles and positions, in case anything changed since the window opened.
        RefreshMonitors("test");
        MonitorInfo? monitor = SelectedMonitor();
        if (monitor is null)
        {
            SetStatus("No monitor found.");
            ExitCode = 1;
            return;
        }

        _busy = true;
        UpdateButtons();
        try
        {
            string folder = Path.Combine(_outputRoot, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {monitor.Name}");
            var options = new CaptureTestOptions(monitor, _monitors, folder, _selfTest ? null : AskYesNo);
            TestReport report = await new CaptureTest(options, new Progress<string>(SetStatus)).RunAsync();
            _lastReportPath = Path.Combine(folder, "report.txt");
            ExitCode = report.HasFailures ? 1 : 0;
            SetStatus(report.ShortSummary);
            if (!_selfTest)
            {
                OpenFolder(folder);
            }
        }
        catch (Exception ex)
        {
            Log.Error("The capture test stopped with an error", ex);
            SetStatus($"The test stopped with an error: {ex.Message}");
            ExitCode = 3;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private bool AskYesNo(string question) =>
        MessageBox.Show(this, question, "EKrecorder test", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnDisplaySettingsChanged(sender, e)));
            return;
        }

        if (_busy || _recording is not null)
        {
            Log.Warn("Display settings changed during a test or recording.");
            return;
        }

        HideIdentify();
        RefreshMonitors("display settings changed");
    }

    private void UpdateButtons()
    {
        bool recording = _recording is not null;
        bool idle = !_busy && !recording;
        _monitorPanel.Enabled = idle;
        _identifyButton.Enabled = !_busy && _monitors.Count > 0;
        _startButton.Enabled = idle && _monitors.Count > 0;
        _stopButton.Enabled = recording && !_stopping;
        _testButton.Enabled = idle && _monitors.Count > 0;
        _reportButton.Enabled = _lastReportPath is not null;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(_folders.Recordings);
        }
        catch (IOException ex)
        {
            Log.Error($"Could not create {_folders.Recordings}", ex);
        }

        OpenFolder(_folders.Recordings);
    }

    private static void OpenFolder(string? folder)
    {
        if (folder is not null && Directory.Exists(folder))
        {
            Open(folder);
        }
    }

    private static void OpenFile(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            Open(path);
        }
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }
}
