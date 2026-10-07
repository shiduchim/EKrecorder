using System.Globalization;
using EKrecorder.App;
using EKrecorder.Audio;
using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Platform;
using EKrecorder.Recording;

namespace EKrecorder.Shell;

/// <summary>
/// EKrecorder's Settings window: Recording (monitor, video quality, folder), Audio (microphone, computer audio,
/// audio quality) and Controls (shortcut, maximum recording time, start with Windows). Save applies everything;
/// Cancel or closing the window keeps the settings as they were. Closing it never exits EKrecorder.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int LabelWidth = 150;
    private const int FieldWidth = 380;

    private readonly AppSettings _original;
    private readonly HotkeyManager _hotkeys;
    private readonly Func<AppSettings, string?> _apply;
    private readonly Action<IReadOnlyList<MonitorInfo>> _identify;
    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly FlowLayoutPanel _monitorList;
    private readonly StepSlider _videoQuality;
    private readonly Label _videoDetail;
    private readonly Label _videoSize;
    private readonly TextBox _folder;
    private readonly ComboBox _microphone;
    private readonly ComboBox _output;
    private readonly StepSlider _audioQuality;
    private readonly Label _audioDetail;
    private readonly HotkeyBox _shortcut;
    private readonly Label _shortcutMessage;
    private readonly ComboBox _maxTime;
    private readonly CheckBox _startWithWindows;
    private readonly Button _save;
    private readonly Label _recordingBanner;
    private readonly ToolTip _tips = new();
    private string? _monitorId;
    private string? _monitorName;

    public SettingsForm(AppSettings settings, IReadOnlyList<MonitorInfo> monitors, HotkeyManager hotkeys, Func<AppSettings, string?> apply, Action<IReadOnlyList<MonitorInfo>> identify)
    {
        _original = settings;
        _monitors = monitors;
        _hotkeys = hotkeys;
        _apply = apply;
        _identify = identify;
        _monitorId = settings.MonitorId;
        _monitorName = settings.MonitorName;

        Text = "EKrecorder Settings";
        Icon = AppIcons.App;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = false;

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(24, 14, 24, 8),
            Margin = new Padding(0),
        };

        _recordingBanner = new Label
        {
            AutoSize = true,
            Text = "● Recording now. Changes apply to the next recording.",
            ForeColor = Color.FromArgb(0xD1, 0x34, 0x38),
            Margin = new Padding(0, 0, 0, 6),
            Visible = false,
        };
        root.Controls.Add(_recordingBanner);

        // Recording
        root.Controls.Add(SectionHeader("Recording", first: true));
        TableLayoutPanel recording = Grid();
        _monitorList = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
        };
        var identifyButton = new Button { Text = "Identify", AutoSize = true, Margin = new Padding(12, 0, 0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _tips.SetToolTip(identifyButton, "Shows each monitor's number on that monitor for a few seconds.");
        identifyButton.Click += (_, _) => _identify(_monitors);
        var monitorRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0), Width = FieldWidth };
        monitorRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        monitorRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        monitorRow.Controls.Add(_monitorList, 0, 0);
        monitorRow.Controls.Add(identifyButton, 1, 0);
        AddRow(recording, "Monitor", monitorRow);

        _videoQuality = new StepSlider
        {
            Labels = RecordingQuality.Video.Select(v => v.Label).ToArray(),
            Width = FieldWidth,
            Height = 46,
            Margin = new Padding(0, 2, 0, 0),
            AccessibleName = "Video quality",
        };
        _videoDetail = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        _videoSize = Secondary("");
        AddRow(recording, "Video quality", Stack(_videoQuality, _videoDetail, _videoSize));

        var browse = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(0) };
        _folder = new TextBox { ReadOnly = true, Width = FieldWidth - browse.PreferredSize.Width - 6, Margin = new Padding(0, 1, 6, 0), TabStop = false };
        browse.Click += (_, _) => ChooseFolder();
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        folderRow.Controls.AddRange([_folder, browse]);
        AddRow(recording, "Save recordings to", folderRow);
        root.Controls.Add(recording);

        // Audio
        root.Controls.Add(SectionHeader("Audio"));
        TableLayoutPanel audio = Grid();
        _microphone = DeviceList("Windows default (communications)");
        _output = DeviceList("Windows default outputs");
        AddRow(audio, "Microphone", _microphone);
        AddRow(audio, "Computer audio", _output);
        _audioQuality = new StepSlider
        {
            Labels = RecordingQuality.Audio.Select(a => a.Label.Replace(" kbps", "", StringComparison.Ordinal)).ToArray(),
            Width = FieldWidth,
            Height = 46,
            Margin = new Padding(0, 2, 0, 0),
            AccessibleName = "Audio quality",
        };
        _audioDetail = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        AddRow(audio, "Audio quality", Stack(_audioQuality, _audioDetail));
        root.Controls.Add(audio);

        // Controls
        root.Controls.Add(SectionHeader("Controls"));
        TableLayoutPanel controls = Grid();
        _shortcut = new HotkeyBox { Width = 200, Margin = new Padding(0, 1, 0, 0), AccessibleName = "Start and stop shortcut" };
        _shortcutMessage = Secondary("Click the box, then press the keys you want.");
        _shortcutMessage.MaximumSize = new Size(FieldWidth, 0);
        AddRow(controls, "Start/stop shortcut", Stack(_shortcut, _shortcutMessage));
        _maxTime = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 1, 0, 0) };
        foreach (int hours in AppSettings.MaxHoursChoices)
        {
            _maxTime.Items.Add(hours == 0 ? "Off" : string.Create(CultureInfo.InvariantCulture, $"{hours} hours"));
        }

        AddRow(controls, "Stop recording after", _maxTime);
        _startWithWindows = new CheckBox { Text = "Start EKrecorder with Windows", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        AddRow(controls, "", _startWithWindows);
        root.Controls.Add(controls);

        // Buttons
        var bottom = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var version = Secondary($"EKrecorder {EnvironmentInfo.ShortVersion}");
        version.Anchor = AnchorStyles.Left;
        _save = new Button { Text = "Save", Width = 92, Height = 30, Margin = new Padding(0, 0, 8, 0) };
        var cancel = new Button { Text = "Cancel", Width = 92, Height = 30, Margin = new Padding(0), DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(version, 0, 0);
        bottom.Controls.Add(_save, 1, 0);
        bottom.Controls.Add(cancel, 2, 0);
        root.Controls.Add(bottom);
        Controls.Add(root);
        AcceptButton = _save;
        CancelButton = cancel;

        // The window is not modal, so DialogResult alone would not close it (Esc also comes here).
        cancel.Click += (_, _) => Close();

        // Values
        FillMonitors();
        _videoQuality.Value = settings.VideoQuality;
        _audioQuality.Value = settings.AudioQuality;
        _folder.Text = settings.RecordingFolder ?? AppPaths.DesktopRecordings;
        _shortcut.Value = settings.Hotkey;
        _maxTime.SelectedIndex = Math.Max(0, AppSettings.MaxHoursChoices.ToList().IndexOf(settings.MaxRecordingHours));
        _startWithWindows.Checked = settings.StartWithWindows;
        SetDevices(_microphone, [], settings.MicrophoneId, settings.MicrophoneName);
        SetDevices(_output, [], settings.OutputId, settings.OutputName);
        ShowVideoDetail();
        ShowAudioDetail();

        _videoQuality.ValueChanged += (_, _) => ShowVideoDetail();
        _audioQuality.ValueChanged += (_, _) => ShowAudioDetail();
        _shortcut.GotFocus += (_, _) => _hotkeys.Suspend();
        _shortcut.LostFocus += (_, _) => _hotkeys.Resume();
        _shortcut.ValueChanged += (_, _) => CheckShortcut();
        _save.Click += (_, _) => Save();
    }

    /// <summary>Shows or hides "Recording now. Changes apply to the next recording."</summary>
    public void SetRecording(bool recording) => _recordingBanner.Visible = recording;

    /// <summary>Fills the device lists (listing does not open any device).</summary>
    public async Task LoadDevicesAsync()
    {
        try
        {
            (List<AudioDevice> microphones, List<AudioDevice> outputs) = await Task.Run(() => (CoreAudio.ListMicrophones(), CoreAudio.ListOutputs()));
            if (IsDisposed)
            {
                return;
            }

            SetDevices(_microphone, microphones, (_microphone.SelectedItem as DeviceChoice)?.Id, (_microphone.SelectedItem as DeviceChoice)?.Name);
            SetDevices(_output, outputs, (_output.SelectedItem as DeviceChoice)?.Id, (_output.SelectedItem as DeviceChoice)?.Name);
        }
        catch (Exception ex)
        {
            Log.Error("Listing audio devices failed", ex);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _hotkeys.Resume();
        _tips.Dispose();
        base.OnFormClosed(e);
    }

    private static Label SectionHeader(string text, bool first = false) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 11F),
        Margin = new Padding(0, first ? 2 : 16, 0, 6),
    };

    private static Label Secondary(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 2, 0, 0),
    };

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control field)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 5, 8, 8) }, 0, row);
        field.Margin = new Padding(field.Margin.Left, field.Margin.Top + 2, field.Margin.Right, 8);
        grid.Controls.Add(field, 1, row);
    }

    private static FlowLayoutPanel Stack(params Control[] controls)
    {
        var stack = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
        };
        stack.Controls.AddRange(controls);
        return stack;
    }

    private static ComboBox DeviceList(string defaultName)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = FieldWidth, Margin = new Padding(0, 1, 0, 0) };
        box.Items.Add(new DeviceChoice(null, defaultName));
        return box;
    }

    /// <summary>Windows' default first, then the devices; a chosen device that is not connected stays listed.</summary>
    private static void SetDevices(ComboBox box, List<AudioDevice> devices, string? selectedId, string? selectedName)
    {
        var items = new List<DeviceChoice> { (DeviceChoice)box.Items[0]! };
        items.AddRange(devices.Select(d => new DeviceChoice(d.Id, d.Name)));
        if (selectedId is not null && items.All(i => i.Id != selectedId))
        {
            items.Add(new DeviceChoice(selectedId, selectedName ?? "Chosen device", Connected: devices.Count == 0 ? null : false));
        }

        box.BeginUpdate();
        box.Items.Clear();
        box.Items.AddRange(items.ToArray<object>());
        box.SelectedItem = items.FirstOrDefault(i => i.Id == selectedId) ?? items[0];
        box.EndUpdate();
    }

    private void FillMonitors()
    {
        _monitorList.Controls.Clear();
        MonitorInfo? main = _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();
        bool chosenMissing = _monitorId is not null && _monitors.All(m => m.StableId != _monitorId);
        foreach (MonitorInfo monitor in _monitors)
        {
            string detail = string.Join(" · ", new[]
            {
                monitor.FriendlyName,
                string.Create(CultureInfo.InvariantCulture, $"{monitor.Bounds.Width} × {monitor.Bounds.Height}"),
                monitor.IsPrimary ? "main" : "",
            }.Where(p => p.Length > 0));
            bool selected = _monitorId == monitor.StableId || (_monitorId is null && monitor == main);
            _monitorList.Controls.Add(MonitorChoice(monitor.Name, detail, selected, monitor.StableId, monitor.FriendlyName));
        }

        if (chosenMissing)
        {
            _monitorList.Controls.Add(MonitorChoice(_monitorName is { Length: > 0 } name ? name : "The monitor chosen before", "not connected now · the main monitor is recorded until it is back", true, _monitorId!, _monitorName));
        }
    }

    private Control MonitorChoice(string title, string detail, bool selected, string id, string? name)
    {
        var radio = new RadioButton { Text = title, AutoSize = true, Checked = selected, Margin = new Padding(0, 0, 0, 0), Tag = id };
        var line = Secondary(detail);
        line.Margin = new Padding(18, 0, 0, 6);
        line.Click += (_, _) => radio.Checked = true;
        radio.CheckedChanged += (_, _) =>
        {
            if (radio.Checked)
            {
                foreach (RadioButton other in _monitorList.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<RadioButton>()).Where(r => r != radio))
                {
                    other.Checked = false;
                }

                _monitorId = id;
                _monitorName = name;
                ShowVideoDetail();
            }
        };
        return Stack(radio, line);
    }

    private MonitorInfo? SelectedMonitor() =>
        _monitors.FirstOrDefault(m => m.StableId == _monitorId) ?? _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();

    private void ShowVideoDetail()
    {
        VideoLevel level = RecordingQuality.VideoLevelOf(_videoQuality.Value);
        string recommended = level.Level == RecordingQuality.DefaultVideoLevel ? " — recommended" : "";
        _videoDetail.Text = $"{level.Label}, {RecordingQuality.FramesPerSecond} fps{recommended}";
        string size = "";
        if (SelectedMonitor() is { } monitor)
        {
            Size output = RecordingQuality.Preset(level.Level, _audioQuality.Value).OutputSizeFor(monitor.Bounds.Size);
            size = string.Create(CultureInfo.InvariantCulture, $"{monitor.Name} is recorded at {output.Width} × {output.Height}") + (output == monitor.Bounds.Size ? " (its full size)" : "") + " · ";
        }

        _videoSize.Text = size + string.Create(CultureInfo.InvariantCulture, $"up to about {RecordingQuality.GigabytesPerHour(level.Level, _audioQuality.Value):0.0} GB per hour");
    }

    private void ShowAudioDetail()
    {
        AudioLevel level = RecordingQuality.AudioLevelOf(_audioQuality.Value);
        _audioDetail.Text = level.Level == RecordingQuality.DefaultAudioLevel ? $"{level.Label} — recommended for calls" : level.Label;
        ShowVideoDetail();
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where EKrecorder saves recordings",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath))
        {
            _folder.Text = dialog.SelectedPath;
        }
    }

    /// <summary>Says at once whether the pressed shortcut can be used.</summary>
    private bool CheckShortcut()
    {
        Hotkey hotkey = _shortcut.Value;
        string? problem = hotkey.Problem();
        if (problem is null && !_hotkeys.IsFree(Handle, hotkey))
        {
            problem = HotkeyManager.Taken;
        }

        _shortcutMessage.ForeColor = problem is null ? SystemColors.GrayText : Color.FromArgb(0xD1, 0x34, 0x38);
        _shortcutMessage.Text = problem ?? (hotkey.IsEmpty ? "No shortcut: use the tray icon to start and stop." : "Press this anywhere to start or stop recording.");
        return problem is null;
    }

    private void Save()
    {
        if (!CheckShortcut())
        {
            _shortcut.Focus();
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        string defaultFolder = AppPaths.DesktopRecordings;
        string folder = _folder.Text.Trim();
        var microphone = _microphone.SelectedItem as DeviceChoice;
        var output = _output.SelectedItem as DeviceChoice;
        AppSettings updated = _original with
        {
            MonitorId = _monitorId,
            MonitorName = _monitorName,
            VideoQuality = _videoQuality.Value,
            AudioQuality = _audioQuality.Value,
            RecordingFolder = string.Equals(folder, defaultFolder, StringComparison.OrdinalIgnoreCase) ? null : folder,
            MicrophoneId = microphone?.Id,
            MicrophoneName = microphone?.Id is null ? null : microphone.Name,
            OutputId = output?.Id,
            OutputName = output?.Id is null ? null : output.Name,
            Shortcut = _shortcut.Value.ToSetting(),
            MaxRecordingHours = AppSettings.MaxHoursChoices[Math.Max(0, _maxTime.SelectedIndex)],
            StartWithWindows = _startWithWindows.Checked,
        };

        string? problem = _apply(updated);
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "EKrecorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>An entry in a device list: Windows' default (no id) or a specific device.</summary>
    private sealed record DeviceChoice(string? Id, string Name, bool? Connected = true)
    {
        public override string ToString() => Connected == false ? $"{Name} (not connected)" : Name;
    }
}
