using System.Diagnostics;
using System.Globalization;
using EKrecorder.Audio;
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
/// and the Step 1 capture test. Audio: pick the microphone and computer-audio device (Windows defaults, or a specific
/// one), see each input's state and level while recording, or with "Show audio meters" (only then, or while
/// recording, are the devices open). In self-test mode (the build machine) it runs the capture test, a 6-second recording
/// and a 3-second recording whose first set-up is made to fail (to prove the fallback), asks nothing, and closes.
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
    private readonly ComboBox _micChoice;
    private readonly ComboBox _outputChoice;
    private readonly CheckBox _metersCheck;
    private readonly LevelMeter _micMeter;
    private readonly LevelMeter _outputMeter;
    private readonly Label _micState;
    private readonly Label _outputState;
    private readonly System.Windows.Forms.Timer _audioTimer = new() { Interval = 100 };
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
    private AudioCapture? _meterAudio;
    private bool _fillingDevices;
    private bool _refreshingDevices;

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

        var audio = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = new Padding(0, 8, 0, 0) };
        _micChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, Margin = new Padding(3, 2, 3, 2) };
        _outputChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, Margin = new Padding(3, 2, 3, 2) };
        _metersCheck = new CheckBox { Text = "Show audio meters (opens the devices)", AutoSize = true, Margin = new Padding(3, 4, 3, 2) };
        _micMeter = new LevelMeter();
        _outputMeter = new LevelMeter();
        _micState = new Label { AutoSize = true, MaximumSize = new Size(300, 0), Margin = new Padding(3, 2, 3, 2) };
        _outputState = new Label { AutoSize = true, MaximumSize = new Size(300, 0), Margin = new Padding(3, 2, 3, 2) };
        audio.Controls.Add(RowLabel("Microphone:"), 0, 0);
        audio.Controls.Add(_micChoice, 1, 0);
        audio.SetColumnSpan(_micChoice, 2);
        audio.Controls.Add(RowLabel("Computer audio:"), 0, 1);
        audio.Controls.Add(_outputChoice, 1, 1);
        audio.SetColumnSpan(_outputChoice, 2);
        audio.Controls.Add(_metersCheck, 0, 2);
        audio.SetColumnSpan(_metersCheck, 3);
        audio.Controls.Add(RowLabel("Mic"), 0, 3);
        audio.Controls.Add(_micMeter, 1, 3);
        audio.Controls.Add(_micState, 2, 3);
        audio.Controls.Add(RowLabel("Computer"), 0, 4);
        audio.Controls.Add(_outputMeter, 1, 4);
        audio.Controls.Add(_outputState, 2, 4);
        layout.Controls.Add(audio);
        _micChoice.Items.Add(new DeviceChoice(null, "Windows default communications microphone"));
        _outputChoice.Items.Add(new DeviceChoice(null, "Windows default outputs (playback + communications)"));
        _micChoice.SelectedIndex = 0;
        _outputChoice.SelectedIndex = 0;

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
        _startButton.Click += async (_, _) => await StartRecordingAsync(simulateFirstSetupFailure: false);
        _stopButton.Click += async (_, _) => await StopRecordingAsync("Stop button");
        _recordingsButton.Click += (_, _) => OpenRecordingsFolder();
        _reportButton.Click += (_, _) => OpenFile(_lastReportPath);
        _testButton.Click += async (_, _) => await RunTestAsync();
        _identifyTimer.Tick += (_, _) => HideIdentify();
        _recordingTimer.Tick += (_, _) => ShowRecordingStatus();
        _micChoice.DropDown += async (_, _) => await RefreshAudioDevicesAsync();
        _outputChoice.DropDown += async (_, _) => await RefreshAudioDevicesAsync();
        _micChoice.SelectedIndexChanged += (_, _) => OnAudioSelectionChanged();
        _outputChoice.SelectedIndexChanged += (_, _) => OnAudioSelectionChanged();
        _metersCheck.CheckedChanged += (_, _) => UpdateMeterSession();
        Resize += (_, _) => UpdateMeterSession();
        _audioTimer.Tick += (_, _) => ShowAudioStatus();
        _audioTimer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        RefreshMonitors("start");
    }

    /// <summary>0 = everything passed, 1 = a check or the recording failed, 3 = a test crashed.</summary>
    public int ExitCode { get; private set; }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshAudioDevicesAsync();
        if (_selfTest)
        {
            await RunTestAsync();
            await RunRecordingSelfTestAsync();
            Close();
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        StopMeterSession();
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
            _audioTimer.Dispose();
            StopMeterSession();
        }

        base.Dispose(disposing);
    }

    private static Label RowLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 2) };

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

    private async Task StartRecordingAsync(bool simulateFirstSetupFailure)
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
        // The recording opens the devices itself; the meters' own capture closes first.
        StopMeterSession();
        SetStatus($"Starting to record {monitor.Name}…");
        try
        {
            CaptureAccessResult borderless = await CaptureSessionSetup.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            Directory.CreateDirectory(_folders.InProgress);
            string temporaryPath = Path.Combine(_folders.InProgress, $"EKrecording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            AudioSelection audio = SelectedAudio();
            RecordingSession session = await RecordingSession.StartAsync(monitor, RecordingPreset.Default, temporaryPath, borderless.Text, audio, simulateFirstSetupFailure);
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
            UpdateMeterSession();
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
        string frames = session.UsesGpuPath ? "GPU frames" : "CPU frames";
        SetStatus(string.Create(CultureInfo.InvariantCulture,
            $"Recording {session.Monitor.Name}: {length:hh\\:mm\\:ss}, {session.OutputSize.Width}x{session.OutputSize.Height} at {session.Preset.FramesPerSecond} fps, {frames} -> {session.Encoder?.Summary ?? "encoder"}{dropped}."));
    }

    private async Task RunRecordingSelfTestAsync()
    {
        // A normal recording, then one whose first set-up is made to refuse frame 0: the next set-up must take over,
        // the file must be saved, and the report must show the failure.
        bool normal = await RecordForSelfTestAsync(TimeSpan.FromSeconds(6), simulateFirstSetupFailure: false);
        bool fallback = await RecordForSelfTestAsync(TimeSpan.FromSeconds(3), simulateFirstSetupFailure: true);
        if (!normal || !fallback)
        {
            ExitCode = Math.Max(ExitCode, 1);
        }
    }

    private async Task<bool> RecordForSelfTestAsync(TimeSpan length, bool simulateFirstSetupFailure)
    {
        string name = simulateFirstSetupFailure ? "Self-test recording with a simulated set-up failure" : "Self-test recording";
        await StartRecordingAsync(simulateFirstSetupFailure);
        RecordingSession? session = _recording;
        if (session is null)
        {
            Log.Error($"{name}: FAIL (it did not start)");
            return false;
        }

        await Task.Delay(length);
        FinishedRecording? finished = await StopRecordingAsync("self-test finished");
        bool fallbackShown = !simulateFirstSetupFailure || session.SetupFailures.Contains("simulated by the self-test", StringComparison.Ordinal);
        // The build machine has no sound devices: the audio track must still be there, silent, as long as the video.
        AudioTrackCheck? audioTrack = finished?.Check?.Audio;
        bool audioOk = audioTrack is { Present: true }
            && Math.Abs(audioTrack.Duration.TotalSeconds - finished!.Check!.VideoDuration.TotalSeconds) < 0.1;
        bool passed = finished is { Saved: true } && fallbackShown && audioOk;
        Log.Info($"{name}: {(passed ? "PASS" : "FAIL")} (saved: {finished?.Saved == true}; set-up failure reported: {fallbackShown}; audio track: {audioTrack?.Describe() ?? "none"}; video track {finished?.Check?.VideoDuration.TotalSeconds:0.000} s; {session.FramePath})");
        return passed;
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
        _micChoice.Enabled = idle;
        _outputChoice.Enabled = idle;
        _metersCheck.Enabled = idle;
        _monitorPanel.Enabled = idle;
        _identifyButton.Enabled = !_busy && _monitors.Count > 0;
        _startButton.Enabled = idle && _monitors.Count > 0;
        _stopButton.Enabled = recording && !_stopping;
        _testButton.Enabled = idle && _monitors.Count > 0;
        _reportButton.Enabled = _lastReportPath is not null;
    }

    private void SetStatus(string text) => _status.Text = text;

    private AudioSelection SelectedAudio()
    {
        var mic = _micChoice.SelectedItem as DeviceChoice;
        var output = _outputChoice.SelectedItem as DeviceChoice;
        return new AudioSelection(mic?.Id, mic?.Name, output?.Id, output?.Name);
    }

    /// <summary>Fills the device lists (on a background thread; listing does not open any device).</summary>
    private async Task RefreshAudioDevicesAsync()
    {
        if (_refreshingDevices)
        {
            return;
        }

        _refreshingDevices = true;
        try
        {
            (List<AudioDevice> mics, List<AudioDevice> outputs) = await Task.Run(() => (CoreAudio.ListMicrophones(), CoreAudio.ListOutputs()));
            Fill(_micChoice, mics);
            Fill(_outputChoice, outputs);
        }
        catch (Exception ex)
        {
            Log.Error("Listing audio devices failed", ex);
        }
        finally
        {
            _refreshingDevices = false;
        }
    }

    /// <summary>Default first, then the devices; a selected device that is not connected now stays listed.</summary>
    private void Fill(ComboBox box, List<AudioDevice> devices)
    {
        var selected = box.SelectedItem as DeviceChoice;
        var items = new List<DeviceChoice> { (DeviceChoice)box.Items[0]! };
        items.AddRange(devices.Select(d => new DeviceChoice(d.Id, d.Name)));
        if (selected?.Id is { } id && devices.All(d => d.Id != id))
        {
            items.Add(new DeviceChoice(id, selected.Name, Connected: false));
        }

        // Only while the list is rebuilt: the same choice is selected again, which is not a change.
        _fillingDevices = true;
        try
        {
            box.BeginUpdate();
            box.Items.Clear();
            box.Items.AddRange(items.ToArray<object>());
            box.SelectedItem = items.FirstOrDefault(i => i.Id == selected?.Id) ?? items[0];
            box.EndUpdate();
        }
        finally
        {
            _fillingDevices = false;
        }
    }

    private void OnAudioSelectionChanged()
    {
        if (_fillingDevices || _meterAudio is null)
        {
            return;
        }

        // The meters follow the new choice.
        StopMeterSession();
        UpdateMeterSession();
    }

    /// <summary>
    /// The meters' own capture runs only while "Show audio meters" is ticked, the window is not minimized and no
    /// recording runs (a recording shows its own levels). Otherwise no audio device is open.
    /// </summary>
    private void UpdateMeterSession()
    {
        bool wanted = _metersCheck.Checked && _recording is null && !_busy && WindowState != FormWindowState.Minimized && !IsDisposed;
        if (wanted && _meterAudio is null)
        {
            _meterAudio = new AudioCapture(SelectedAudio(), forRecording: false);
            _meterAudio.Start();
        }
        else if (!wanted)
        {
            StopMeterSession();
        }
    }

    private void StopMeterSession()
    {
        AudioCapture? meters = _meterAudio;
        _meterAudio = null;
        if (meters is not null)
        {
            // Closing waits for the capture threads; not on the window's thread.
            _ = Task.Run(meters.Dispose);
        }
    }

    private void ShowAudioStatus()
    {
        AudioCapture? audio = _recording?.Audio ?? _meterAudio;
        if (audio is null)
        {
            _micMeter.Clear();
            _outputMeter.Clear();
            string closed = _metersCheck.Checked && WindowState == FormWindowState.Minimized ? "closed while minimized" : "closed (opened while recording, or with the meters)";
            _micState.Text = closed;
            _outputState.Text = closed;
            return;
        }

        (AudioInputStatus mic, AudioInputStatus computer) = audio.Snapshot();
        _micMeter.Push(mic.Level);
        _outputMeter.Push(computer.Level);
        _micState.Text = Describe(mic);
        _outputState.Text = Describe(computer);
    }

    private static string Describe(AudioInputStatus status)
    {
        string state = status.State switch
        {
            InputState.Running => "Running",
            InputState.Fallback => "Fallback",
            InputState.Lost => "Lost (silence recorded)",
            InputState.Retrying => "Retrying",
            InputState.NoDevice => "No device",
            InputState.Closed => "Closed",
            _ => "Opening",
        };
        return status.Warning is null ? $"{state} · {status.Devices}" : $"{state} · {status.Devices}{Environment.NewLine}WARNING: {status.Warning}";
    }

    /// <summary>An entry in a device list: Windows' default (no id) or a specific device.</summary>
    private sealed record DeviceChoice(string? Id, string Name, bool Connected = true)
    {
        public override string ToString() => Connected ? Name : $"{Name} (not connected)";
    }

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
