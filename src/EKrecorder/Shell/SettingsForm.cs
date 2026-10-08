using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms.Automation;
using EKrecorder.App;
using EKrecorder.Audio;
using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Platform;
using EKrecorder.Recording;

namespace EKrecorder.Shell;

/// <summary>
/// EKrecorder's Settings window, laid out like Windows 11's own Settings: one card per setting, each with an icon,
/// a short title, one line that says what is in effect, and its control on the right. Recording: monitor (picked
/// from a picture of the monitors), video quality, folder. Audio: microphone and computer audio, each with a live
/// level meter, and audio quality. Controls: shortcut, maximum recording time, start with Windows. Save applies
/// everything; Cancel, Esc or closing keeps the settings as they were. Closing it never exits EKrecorder.
/// <para>
/// Every size comes from one scale (the window's DPI ÷ 96), so it looks the same at 100 % and 200 % and when it is
/// moved between monitors with different scales. The microphone and computer audio are only opened while the window
/// is on the screen.
/// </para>
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int WidthDip = 680;
    private const int ControlWidthDip = 248;
    private const float HeardLevel = 0.01f; // -40 dBFS: a voice, not room noise
    private const float PlayingLevel = 0.003f; // -50 dBFS

    private readonly AppSettings _original;
    private readonly HotkeyManager _hotkeys;
    private readonly Func<AppSettings, string?> _apply;
    private readonly Action<IReadOnlyList<MonitorInfo>> _identify;
    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly Panel _content = new();
    private readonly Label _recordingHeading = Heading("Recording");
    private readonly Label _audioHeading = Heading("Audio");
    private readonly Label _controlsHeading = Heading("Start and stop");
    private readonly Panel _banner = new();
    private readonly Card _monitorCard = new() { Glyph = Glyphs.Monitor, Title = "Monitor" };
    private readonly Card _videoCard = new() { Glyph = Glyphs.Video, Title = "Video quality" };
    private readonly Card _folderCard = new() { Glyph = Glyphs.Folder, Title = "Save recordings to" };
    private readonly Card _micCard = new() { Glyph = Glyphs.Microphone, Title = "Microphone" };
    private readonly Card _outputCard = new() { Glyph = Glyphs.Speaker, Title = "Computer audio" };
    private readonly Card _audioCard = new() { Glyph = Glyphs.Equalizer, Title = "Audio quality" };
    private readonly Card _shortcutCard = new() { Glyph = Glyphs.Keyboard, Title = "Shortcut" };
    private readonly Card _stopCard = new() { Glyph = Glyphs.Timer, Title = "Stop recording after" };
    private readonly Card _startupCard = new() { Glyph = Glyphs.Power, Title = "Start with Windows" };
    private readonly MonitorPicker _monitorPicker = new() { AccessibleName = "Monitor to record" };
    private readonly PillButton _identifyButton = new() { Text = "Identify" };
    private readonly StepSlider _videoQuality = new() { AccessibleName = "Video quality" };
    private readonly PillButton _browse = new() { Text = "Change…", AccessibleName = "Change the recordings folder" };
    private readonly ThemedComboBox _microphone = DeviceList("Microphone");
    private readonly LevelMeter _micMeter = new() { AccessibleName = "Microphone level", Tag = Line2 };
    private readonly StatusLine _micStatus = new() { Tag = Line2 };
    private readonly ThemedComboBox _output = DeviceList("Computer audio");
    private readonly LevelMeter _outputMeter = new() { AccessibleName = "Computer audio level", Tag = Line2 };
    private readonly StatusLine _outputStatus = new() { Tag = Line2 };
    private readonly PillButton _testSound = new() { Style = PillStyle.Icon, Glyph = Glyphs.Play, AccessibleName = "Play a test sound" };
    private readonly StepSlider _audioQuality = new() { AccessibleName = "Audio quality" };
    private readonly HotkeyBox _shortcut = new() { AccessibleName = "Start and stop shortcut" };
    private readonly FieldHost _shortcutField;
    private readonly ThemedComboBox _maxTime = new() { AccessibleName = "Stop recording after" };
    private readonly ToggleSwitch _startWithWindows = new() { AccessibleName = "Start EKrecorder with Windows" };
    private readonly Panel _footer = new();
    private readonly PillButton _save = new() { Text = "Save", Style = PillStyle.Accent };
    private readonly PillButton _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly AudioPreview _preview = new();
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _restartTimer = new() { Interval = 300 };
    private readonly ToolTip _tips = new();
    private readonly UiTheme _theme;
    private UiFonts _fonts;
    private float _scale;
    private float? _forcedScale;
    private bool _recording;
    private string? _monitorId;
    private string? _monitorName;
    private string _folder;
    private string? _shortcutProblem;
    private FolderCheck? _folderCheck;
    private bool _closed;
    private bool _micHeard;
    private bool? _micMuted;
    private bool _muteCheckRunning;
    private long _nextMuteCheck;
    private long _outputHeardAt;
    private string _micDefaultName = "";
    private string? _micCapturedId;
    private readonly bool _showPicker;
    private string _outputDefaultName = "";

    /// <summary>The test chime, in memory that never moves (Windows reads it while it plays, after PlaySound returns).</summary>
    private static byte[]? _chime;

    /// <summary>Marks the controls on a device card's second line (they sit under the title, by design).</summary>
    private const string Line2 = "line 2";

    public SettingsForm(AppSettings settings, IReadOnlyList<MonitorInfo> monitors, HotkeyManager hotkeys, Func<AppSettings, string?> apply, Action<IReadOnlyList<MonitorInfo>> identify)
    {
        _original = settings;
        _monitors = monitors;
        _hotkeys = hotkeys;
        _apply = apply;
        _identify = identify;
        _monitorId = settings.MonitorId;
        _monitorName = settings.MonitorName;
        _folder = settings.RecordingFolder ?? AppPaths.DesktopRecordings;

        // The monitor picture: with several monitors, or when the saved one is gone (to choose the one that is here).
        _showPicker = monitors.Count > 1 || (monitors.Count > 0 && settings.MonitorId is { } saved && monitors.All(m => m.StableId != saved));

        Text = "EKrecorder settings";
        Icon = AppIcons.App;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScroll = true;
        _scale = DeviceDpi / 96f;
        _theme = UiTheme.Current();
        _fonts = new UiFonts(_scale);

        _shortcutField = new FieldHost(_shortcut) { Tag = "field" };
        _content.Controls.AddRange([_banner, _recordingHeading, _monitorCard, _videoCard, _folderCard, _audioHeading, _micCard, _outputCard, _audioCard,
            _controlsHeading, _shortcutCard, _stopCard, _startupCard, _footer]);
        _footer.Controls.AddRange([_save, _cancel]);
        _footer.Paint += PaintFooter;
        _monitorCard.Controls.AddRange([_identifyButton, _monitorPicker]);
        _videoCard.Controls.Add(_videoQuality);
        _folderCard.Controls.Add(_browse);
        _micCard.Controls.AddRange([_microphone, _micMeter, _micStatus]);
        _outputCard.Controls.AddRange([_testSound, _output, _outputMeter, _outputStatus]);
        _audioCard.Controls.Add(_audioQuality);
        _shortcutCard.Controls.Add(_shortcutField);
        _stopCard.Controls.Add(_maxTime);
        _startupCard.Controls.Add(_startWithWindows);
        Controls.Add(_content);
        _banner.Paint += PaintBanner;
        _banner.Visible = false;
        _banner.AccessibleRole = AccessibleRole.StaticText;
        _banner.AccessibleName = "Recording now. Changes apply to the next recording.";
        _micStatus.Link.AccessibleName = "Unmute microphone";

        AcceptButton = _save;
        CancelButton = _cancel;

        // Values
        _videoQuality.Labels = RecordingQuality.Video.Select(v => v.Label).ToArray();
        _audioQuality.Labels = RecordingQuality.Audio.Select(a => a.Label.Replace(" kbps", "", StringComparison.Ordinal)).ToArray();
        _videoQuality.Value = settings.VideoQuality;
        _audioQuality.Value = settings.AudioQuality;
        _monitorPicker.Monitors = monitors.Select(m => new MonitorPicker.Choice(
            m.StableId,
            m.Number.ToString(CultureInfo.InvariantCulture),
            m.Bounds,
            ResolutionName(m.Bounds.Size),
            $"{m.Name}: {Describe(m)}")).ToList();
        _monitorPicker.SelectedId = SelectedMonitor()?.StableId is { } shown && (_monitorId is null || _monitorId == shown) ? shown : null;
        _shortcut.Value = settings.Hotkey;
        foreach (int hours in AppSettings.MaxHoursChoices)
        {
            _maxTime.Items.Add(hours == 0 ? "Off" : string.Create(CultureInfo.InvariantCulture, $"{hours} hours"));
        }

        _maxTime.SelectedIndex = Math.Max(0, AppSettings.MaxHoursChoices.ToList().IndexOf(settings.MaxRecordingHours));
        _startWithWindows.Checked = settings.StartWithWindows;
        SetDevices(_microphone, DefaultLabel(""), [], settings.MicrophoneId, settings.MicrophoneName);
        SetDevices(_output, DefaultLabel(""), [], settings.OutputId, settings.OutputName);

        _tips.SetToolTip(_identifyButton, "Shows each monitor's number on that monitor for a few seconds");
        _tips.SetToolTip(_testSound, "Play a test sound (on Windows' default speakers or headset)");
        _monitorPicker.SelectionChanged += (_, _) =>
        {
            if (_monitorPicker.SelectedId is { } id && _monitors.FirstOrDefault(m => m.StableId == id) is { } monitor)
            {
                _monitorId = monitor.StableId;
                _monitorName = monitor.FriendlyName;
            }

            ShowDetails();
        };
        _identifyButton.Click += (_, _) => _identify(_monitors);
        _browse.Click += (_, _) => ChooseFolder();
        _videoQuality.ValueChanged += (_, _) => ShowDetails();
        _audioQuality.ValueChanged += (_, _) => ShowDetails();
        _maxTime.SelectedIndexChanged += (_, _) => ShowDetails();
        _startWithWindows.CheckedChanged += (_, _) => ShowDetails();
        _microphone.SelectedIndexChanged += (_, _) => DevicesChanged();
        _output.SelectedIndexChanged += (_, _) => DevicesChanged();
        _testSound.Click += (_, _) => PlayTestSound();
        _micStatus.Link.Click += (_, _) => Unmute();
        _shortcut.GotFocus += (_, _) => _hotkeys.Suspend();
        _shortcut.LostFocus += (_, _) => _hotkeys.Resume();
        _shortcut.ValueChanged += (_, _) => CheckShortcut();
        _save.Click += (_, _) => Save();
        _cancel.Click += (_, _) => Close(); // not modal: DialogResult alone would not close it (Esc comes here too)
        _meterTimer.Tick += (_, _) => ShowLevels();
        _restartTimer.Tick += (_, _) => RestartPreview();

        ApplyTheme();
    }

    /// <summary>Self-test only: lays the window out at this scale whatever the monitor's DPI (to check 200 % on a 100 % machine).</summary>
    public void ForceScale(float scale)
    {
        _forcedScale = scale;
        SetScale(scale);
    }

    /// <summary>Self-test only: the window's contents as a picture (works even when they are larger than the screen).</summary>
    public Bitmap RenderContent()
    {
        var bitmap = new Bitmap(_content.Width, _content.Height);
        _content.DrawToBitmap(bitmap, new Rectangle(Point.Empty, _content.Size));
        return bitmap;
    }

    /// <summary>
    /// Self-test only: what is wrong with the layout (controls outside their card or overlapping, a control over the
    /// titles, text that cannot fit), or nothing.
    /// </summary>
    public IReadOnlyList<string> CheckLayout()
    {
        var problems = new List<string>();
        int margin = D(6);
        foreach (Card card in Cards())
        {
            Rectangle inner = Rectangle.Inflate(new Rectangle(Point.Empty, card.Size), -margin, -margin);
            var children = card.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            foreach (Control child in children)
            {
                if (!inner.Contains(child.Bounds))
                {
                    problems.Add($"{card.Title}: {NameOf(child)} {child.Bounds} is not inside the card {card.Size}");
                }

                bool underTitle = Equals(child.Tag, Line2) && child.Top >= (card.HeaderHeight > 0 ? card.HeaderHeight : card.Height) - D(8);
                if (child.Left < card.TextRight && !underTitle)
                {
                    problems.Add($"{card.Title}: {NameOf(child)} starts at {child.Left}, over the title (it ends at {card.TextRight})");
                }
            }

            for (int i = 0; i < children.Count; i++)
            {
                for (int j = i + 1; j < children.Count; j++)
                {
                    if (children[i].Bounds.IntersectsWith(children[j].Bounds))
                    {
                        problems.Add($"{card.Title}: {NameOf(children[i])} and {NameOf(children[j])} overlap");
                    }
                }
            }

            int titleWidth = TextRenderer.MeasureText(card.Title, _fonts.Body, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (titleWidth > card.TextRight - card.TextLeft)
            {
                problems.Add($"{card.Title}: the title needs {titleWidth} px, the text column has {card.TextRight - card.TextLeft}");
            }
        }

        foreach (Control control in _content.Controls.Cast<Control>().Where(c => c.Visible))
        {
            if (!new Rectangle(Point.Empty, _content.Size).Contains(control.Bounds))
            {
                problems.Add($"{NameOf(control)} {control.Bounds} is outside the window {_content.Size}");
            }
        }

        foreach (PillButton button in new[] { _save, _cancel, _browse, _identifyButton })
        {
            int needed = TextRenderer.MeasureText(button.Text, _fonts.Body, Size.Empty, TextFormatFlags.NoPadding).Width + D(16);
            if (button.Visible && needed > button.Width)
            {
                problems.Add($"\"{button.Text}\" needs {needed} px, the button is {button.Width}");
            }
        }

        if (TextRenderer.MeasureText(_shortcut.Text, _shortcut.Font).Width > _shortcut.Width)
        {
            problems.Add($"the shortcut \"{_shortcut.Text}\" does not fit its box");
        }

        return problems;

        static string NameOf(Control c) => c.AccessibleName ?? (c.Text.Length > 0 ? c.Text : c.GetType().Name);
    }

    /// <summary>
    /// Self-test only, for a window opened as the tray opens it: it lies on its screen (a short screen scrolls the
    /// page), and the fonts WinForms could rescale are at the window's scale.
    /// </summary>
    public IReadOnlyList<string> CheckPlacement()
    {
        var problems = new List<string>();
        Rectangle area = Screen.FromControl(this).WorkingArea;
        if (!area.Contains(Bounds))
        {
            problems.Add($"the window {Bounds} is not inside the screen's working area {area}");
        }

        if (_content.Height > ClientSize.Height && !VerticalScroll.Visible)
        {
            problems.Add($"the page ({_content.Height} px) is taller than the window ({ClientSize.Height} px) but does not scroll");
        }

        if (HorizontalScroll.Visible)
        {
            problems.Add("the window scrolls sideways");
        }

        float expected = 14 * DeviceDpi / 96f;
        foreach (Control control in new Control[] { _recordingHeading, _shortcut, _microphone })
        {
            if (Math.Abs(control.Font.Size - expected) > 0.5f)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{control.AccessibleName ?? control.Text}: font {control.Font.Size:0.#} px, expected {expected:0.#} px"));
            }
        }

        return problems;
    }

    /// <summary>Shows or hides "Recording now · Changes apply to the next recording".</summary>
    public void SetRecording(bool recording)
    {
        if (_recording == recording)
        {
            return;
        }

        _recording = recording;
        LayoutAll();
        if (recording && Visible && ContainsFocus)
        {
            AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, _banner.AccessibleName!);
        }
    }

    /// <summary>Fills the device lists and names Windows' defaults (listing does not open any device).</summary>
    public async Task LoadDevicesAsync()
    {
        try
        {
            (List<AudioDevice> microphones, List<AudioDevice> outputs, (string? Microphone, IReadOnlyList<string> Outputs) defaults) =
                await Task.Run(() => (CoreAudio.ListMicrophones(), CoreAudio.ListOutputs(), CoreAudio.DefaultNames()));
            if (IsDisposed)
            {
                return;
            }

            _micDefaultName = defaults.Microphone is { } microphone ? ShortName(microphone) : "";
            _outputDefaultName = string.Join(" + ", defaults.Outputs.Select(ShortName));
            SetDevices(_microphone, DefaultLabel(_micDefaultName), microphones, (_microphone.SelectedItem as DeviceChoice)?.Id, (_microphone.SelectedItem as DeviceChoice)?.Name);
            SetDevices(_output, DefaultLabel(_outputDefaultName), outputs, (_output.SelectedItem as DeviceChoice)?.Id, (_output.SelectedItem as DeviceChoice)?.Name);
            DevicesChanged();
        }
        catch (Exception ex)
        {
            Log.Error("Listing audio devices failed", ex);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_forcedScale is null && Math.Abs((DeviceDpi / 96f) - _scale) > 0.01f)
        {
            SetScale(DeviceDpi / 96f);
        }

        // The title bar in the page colour (Windows 11), dark in dark mode.
        int dark = _theme.Dark ? 1 : 0;
        AppNative.DwmSetWindowAttribute(Handle, AppNative.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (!_theme.HighContrast)
        {
            int caption = ColorTranslator.ToWin32(_theme.Page);
            AppNative.DwmSetWindowAttribute(Handle, AppNative.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        // Every control has its window now. Fonts and sizes once more at this monitor's scale: WinForms rescales a font
        // that was set before a control's window existed when that window opens at another DPI (a heading at half or
        // double size), and only now can the window's height be fitted to the screen.
        float scale = _forcedScale ?? DeviceDpi / 96f;
        if (Math.Abs(scale - _scale) > 0.001f)
        {
            SetScale(scale);
        }
        else
        {
            ApplyTheme();
        }

        float before = _scale;
        base.OnLoad(e); // picks the monitor under the pointer; it may have another scale (the window is laid out again)
        if (StartPosition == FormStartPosition.CenterScreen || Math.Abs(_scale - before) > 0.001f)
        {
            // Centred at its final size (fitted to a short screen, or at that monitor's scale).
            CenterToScreen();
        }
    }

    protected override bool OnGetDpiScaledSize(int deviceDpiOld, int deviceDpiNew, ref Size desiredSize)
    {
        // Windows scales the window by the DPI ratio, which is what the layout makes of it within a pixel or two
        // (WinForms' own answer, without automatic scaling, would keep the old size, and the window would then
        // jump in size around its corner and could flip between the two monitors' scales while being dragged).
        base.OnGetDpiScaledSize(deviceDpiOld, deviceDpiNew, ref desiredSize);
        return false;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_forcedScale is null)
        {
            SetScale(e.DeviceDpiNew / 96f);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        BeginInvoke(StartPreview);
        CheckFolder();
        if (ShortcutNotWorking())
        {
            CheckShortcut(); // says whether another program still holds it
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Whatever stopped the meters while the window stayed open (a cancelled shutdown), they come back (after the
        // window is painted, as when it opens).
        if (!_meterTimer.Enabled && IsHandleCreated && !_closed)
        {
            BeginInvoke(StartPreview);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            // Nobody looks at the meters: the devices are let go.
            StopPreview();
        }
        else if (Visible && IsHandleCreated && !_meterTimer.Enabled)
        {
            StartPreview();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            StopPreview();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Not in FormClosing: Windows asks that at a shutdown that can still be cancelled, and the window stays.
        _closed = true;
        StopPreview();
        _preview.Dispose();
        _meterTimer.Dispose();
        _restartTimer.Dispose();
        _hotkeys.Resume();
        _tips.Dispose();
        AppNative.PlaySound(IntPtr.Zero, IntPtr.Zero, 0); // stops the test chime
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Label Heading(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false };

    private static ThemedComboBox DeviceList(string name)
    {
        var box = new ThemedComboBox { AccessibleName = name };
        box.Items.Add(new DeviceChoice(null, DefaultLabel("")));
        return box;
    }

    private static string DefaultLabel(string deviceName) => deviceName.Length > 0 ? $"Windows default ({deviceName})" : "Windows default";

    /// <summary>"Headset Microphone (JLAB TALK GO)" → "JLAB TALK GO": the device, without what Windows calls its connector.</summary>
    private static string ShortName(string name)
    {
        int open = name.IndexOf('(', StringComparison.Ordinal);
        return open > 0 && name.EndsWith(')') && name.Length - open > 3 ? name[(open + 1)..^1] : name;
    }

    /// <summary>"Monitor 2 · Main · 4K".</summary>
    private static string Summary(MonitorInfo monitor) =>
        string.Join(" · ", new[] { monitor.Name, monitor.IsPrimary ? "Main" : "", ResolutionName(monitor.Bounds.Size) }.Where(p => p.Length > 0));

    /// <summary>"PHL 243V7 · 1920 × 1080 · main" (the tooltips).</summary>
    private static string Describe(MonitorInfo monitor) => string.Join(" · ", new[]
    {
        monitor.FriendlyName,
        string.Create(CultureInfo.InvariantCulture, $"{monitor.Bounds.Width} × {monitor.Bounds.Height}"),
        monitor.IsPrimary ? "main" : "",
    }.Where(p => p.Length > 0));

    /// <summary>"4K", "1440p", "1080p"… by the shorter side.</summary>
    private static string ResolutionName(Size size)
    {
        int lines = Math.Min(size.Width, size.Height);
        return lines >= 2160 ? "4K" : lines >= 1440 ? "1440p" : lines >= 1080 ? "1080p" : lines >= 720 ? "720p" : string.Create(CultureInfo.InvariantCulture, $"{lines}p");
    }

    /// <summary>Windows' default first, then the devices; a chosen device that is not connected stays listed.</summary>
    private static void SetDevices(ThemedComboBox box, string defaultLabel, List<AudioDevice> devices, string? selectedId, string? selectedName)
    {
        var items = new List<DeviceChoice> { new(null, defaultLabel) };
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
        box.FitDropDown();
    }

    /// <summary>"Desktop › EKrecordings" for a folder in the user's own folders, else the whole path.</summary>
    private static string FriendlyPath(string folder)
    {
        foreach ((Environment.SpecialFolder special, string name) in new[]
        {
            (Environment.SpecialFolder.DesktopDirectory, "Desktop"),
            (Environment.SpecialFolder.MyVideos, "Videos"),
            (Environment.SpecialFolder.MyDocuments, "Documents"),
            (Environment.SpecialFolder.UserProfile, "Home"),
        })
        {
            string root = Environment.GetFolderPath(special);
            if (root.Length > 0 && (string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                string rest = folder[root.Length..].Trim('\\');
                return rest.Length == 0 ? name : $"{name} › {rest.Replace("\\", " › ", StringComparison.Ordinal)}";
            }
        }

        return folder;
    }

    /// <summary>A WAV file: two soft notes, 0.7 s.</summary>
    private static MemoryStream TestChime()
    {
        const int rate = 48000;
        const double seconds = 0.7;
        int samples = (int)(rate * seconds);
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + (samples * 2));
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / rate;
                double frequency = t < seconds / 2 ? 660 : 880;
                double local = t % (seconds / 2);
                double envelope = Math.Min(1, local / 0.01) * Math.Exp(-local * 6);
                writer.Write((short)(Math.Sin(2 * Math.PI * frequency * t) * envelope * 0.3 * short.MaxValue));
            }
        }

        stream.Position = 0;
        return stream;
    }

    private Card[] Cards() => [_monitorCard, _videoCard, _folderCard, _micCard, _outputCard, _audioCard, _shortcutCard, _stopCard, _startupCard];

    private int D(float dip) => (int)Math.Round(dip * _scale);

    private void SetScale(float scale)
    {
        if (Math.Abs(scale - _scale) < 0.001f)
        {
            // Already laid out at this scale. New fonts would not reach the controls either: a control keeps a font
            // equal to the one it has, and that one would then be disposed.
            return;
        }

        _scale = scale;
        UiFonts old = _fonts;
        _fonts = new UiFonts(scale);
        ApplyTheme();
        old.Dispose();
    }

    /// <summary>Colours and fonts for everything, then the layout.</summary>
    private void ApplyTheme()
    {
        SuspendLayout();
        BackColor = _theme.Page;
        _content.BackColor = _theme.Page;
        _footer.BackColor = _theme.Page;
        foreach (Label heading in new[] { _recordingHeading, _audioHeading, _controlsHeading })
        {
            heading.Font = _fonts.Strong;
            heading.ForeColor = _theme.Text;
            heading.BackColor = _theme.Page;
        }

        foreach (IThemed control in new IThemed[]
        {
            _monitorCard, _videoCard, _folderCard, _micCard, _outputCard, _audioCard, _shortcutCard, _stopCard, _startupCard,
            _monitorPicker, _identifyButton, _videoQuality, _browse, _microphone, _micMeter, _micStatus, _output, _outputMeter, _outputStatus,
            _testSound, _audioQuality, _shortcutField, _maxTime, _startWithWindows, _save, _cancel,
        })
        {
            control.ApplyTheme(_theme, _fonts, _scale);
        }

        // The footer buttons sit on the page, not on a card.
        _save.BackColor = _theme.Page;
        _cancel.BackColor = _theme.Page;
        ShowDetails();
        LayoutAll();
        ResumeLayout(true);
    }

    /// <summary>Places every card and control; the window is as tall as its contents (and scrolls on a short screen).</summary>
    private void LayoutAll()
    {
        int width = D(WidthDip);
        int pad = D(24);
        int cardWidth = width - (2 * pad);
        int right = cardWidth - D(16);
        int controlWidth = D(ControlWidthDip);
        int gap = D(4);
        int y = D(16);

        _banner.Visible = _recording;
        if (_recording)
        {
            _banner.Bounds = new Rectangle(pad, y, cardWidth, Math.Max(D(48), _fonts.Body.Height + D(24)));
            _banner.Invalidate();
            y = _banner.Bottom + D(4);
        }

        // Recording
        y = PlaceHeading(_recordingHeading, pad, y, first: true);
        bool severalMonitors = _showPicker;
        _monitorPicker.Visible = severalMonitors;
        _identifyButton.Visible = severalMonitors;
        _identifyButton.Size = _identifyButton.PreferredSize;
        _monitorPicker.Size = new Size(D(176), D(68));
        int monitorHeight = severalMonitors ? _monitorPicker.Height + D(20) : D(68);
        _monitorCard.Bounds = new Rectangle(pad, y, cardWidth, monitorHeight);
        _monitorPicker.Location = new Point(right - _monitorPicker.Width, (monitorHeight - _monitorPicker.Height) / 2);
        _identifyButton.Location = new Point(_monitorPicker.Left - D(12) - _identifyButton.Width, (monitorHeight - _identifyButton.Height) / 2);
        _monitorCard.TextRight = (severalMonitors ? _identifyButton.Left : right) - D(24);
        y = _monitorCard.Bottom + gap;

        y = PlaceSliderCard(_videoCard, _videoQuality, pad, y, cardWidth, right, controlWidth) + gap;

        _browse.Size = _browse.PreferredSize;
        y = PlaceSimpleCard(_folderCard, _browse, pad, y, cardWidth, right);

        // Audio
        y = PlaceHeading(_audioHeading, pad, y, first: false);
        y = PlaceDeviceCard(_micCard, _microphone, null, _micMeter, _micStatus, pad, y, cardWidth, right, controlWidth) + gap;
        y = PlaceDeviceCard(_outputCard, _output, _testSound, _outputMeter, _outputStatus, pad, y, cardWidth, right, controlWidth) + gap;
        y = PlaceSliderCard(_audioCard, _audioQuality, pad, y, cardWidth, right, controlWidth);

        // Start and stop
        y = PlaceHeading(_controlsHeading, pad, y, first: false);
        _shortcutField.Width = controlWidth;
        y = PlaceSimpleCard(_shortcutCard, _shortcutField, pad, y, cardWidth, right) + gap;
        _maxTime.Width = controlWidth;
        y = PlaceSimpleCard(_stopCard, _maxTime, pad, y, cardWidth, right) + gap;
        y = PlaceSimpleCard(_startupCard, _startWithWindows, pad, y, cardWidth, right);
        _startWithWindows.Left += _startWithWindows.RingRoom; // the switch lines up with the lists; its focus ring may reach past them

        // Footer: a line, then Save and Cancel on the right.
        y += D(24);
        _save.Size = _save.PreferredSize;
        _cancel.Size = _cancel.PreferredSize;
        int buttonWidth = Math.Max(_save.Width, _cancel.Width);
        _save.Width = buttonWidth;
        _cancel.Width = buttonWidth;
        _footer.Bounds = new Rectangle(0, y, width, D(16) + _save.Height + D(16));
        _cancel.Location = new Point(width - pad - buttonWidth, D(16));
        _save.Location = new Point(_cancel.Left - D(8) - buttonWidth, D(16));
        _footer.Invalidate();
        y = _footer.Bottom;

        _content.Bounds = new Rectangle(AutoScrollPosition.X, AutoScrollPosition.Y, width, y); // where the page is scrolled to
        Size client = new(width, y);
        if (_forcedScale is null && IsHandleCreated)
        {
            // On a screen too short for the whole window, it scrolls.
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int chrome = Height - ClientSize.Height;
            if (client.Height + chrome > area.Height * 0.95)
            {
                client = new Size(width + SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi), (int)(area.Height * 0.95) - chrome);
            }
        }

        ClientSize = client;
    }

    private int PlaceHeading(Label heading, int pad, int y, bool first)
    {
        heading.Location = new Point(pad + D(1), y + (first ? 0 : D(26)));
        return heading.Bottom + D(8);
    }

    private int PlaceSliderCard(Card card, StepSlider slider, int pad, int y, int cardWidth, int right, int controlWidth)
    {
        slider.Size = new Size(controlWidth + D(16), slider.NeededHeight);
        int height = Math.Max(D(68), slider.Height + D(16));
        card.Bounds = new Rectangle(pad, y, cardWidth, height);
        card.HeaderHeight = 0;
        slider.Location = new Point(right - slider.Width + D(8), ((height - slider.Height) / 2) + D(2));
        card.TextRight = slider.Left - D(8);
        return card.Bottom;
    }

    private int PlaceSimpleCard(Card card, Control control, int pad, int y, int cardWidth, int right)
    {
        int height = Math.Max(D(card.Subtitle.Length > 0 ? 68 : 60), control.Height + D(24));
        card.Bounds = new Rectangle(pad, y, cardWidth, height);
        card.HeaderHeight = 0;
        control.Location = new Point(right - control.Width, (height - control.Height) / 2);
        card.TextRight = control.Left - D(24);
        return card.Bottom;
    }

    /// <summary>
    /// A device card: the title and the list (with an optional icon button before it) on the first line; the level
    /// meter and a short status under the title.
    /// </summary>
    private int PlaceDeviceCard(Card card, ComboBox list, PillButton? button, LevelMeter meter, StatusLine status, int pad, int y, int cardWidth, int right, int controlWidth)
    {
        int top = D(14);
        list.Width = controlWidth;
        list.Location = new Point(right - list.Width, top);
        int header = (2 * top) + list.Height;
        if (button is not null)
        {
            button.Size = button.PreferredSize;
            button.Location = new Point(list.Left - D(8) - button.Width, top + ((list.Height - button.Height) / 2));
        }

        card.HeaderHeight = header;
        card.TextRight = (button?.Left ?? list.Left) - D(24);
        int line = header - D(4);
        int meterWidth = D(140);
        meter.Bounds = new Rectangle(card.TextLeft, line + ((status.Height - D(10)) / 2), meterWidth, D(10));
        status.Location = new Point(meter.Shown ? meter.Right + D(12) : card.TextLeft, line);
        status.Width = right - status.Left;
        card.Bounds = new Rectangle(pad, y, cardWidth, line + status.Height + D(14));
        return card.Bottom;
    }

    private void PaintBanner(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var page = new SolidBrush(_theme.Page))
        {
            g.FillRectangle(page, _banner.ClientRectangle);
        }

        float border = Math.Max(1, (int)Math.Round(_scale));
        using (System.Drawing.Drawing2D.GraphicsPath shape = Glyphs.Rounded(new RectangleF(border / 2, border / 2, _banner.Width - border - 0.5f, _banner.Height - border - 0.5f), D(4)))
        using (var fill = new SolidBrush(_theme.InfoBar))
        using (var pen = new Pen(_theme.CardBorder, border))
        {
            g.FillPath(fill, shape);
            g.DrawPath(pen, shape);
        }

        float dot = 10 * _scale;
        using (var red = new SolidBrush(Color.FromArgb(0xE5, 0x39, 0x35)))
        {
            g.FillEllipse(red, D(19), (_banner.Height - dot) / 2, dot, dot);
        }

        int x = D(40);
        string strong = "Recording now";
        int strongWidth = TextRenderer.MeasureText(g, strong, _fonts.Strong, Size.Empty, TextFormatFlags.NoPadding).Width;
        TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, strong, _fonts.Strong, new Rectangle(x, 0, strongWidth + D(2), _banner.Height), _theme.Text, _theme.InfoBar, flags);
        TextRenderer.DrawText(g, "Changes apply to the next recording", _fonts.Body,
            new Rectangle(x + strongWidth + D(12), 0, Math.Max(0, _banner.Width - x - strongWidth - D(28)), _banner.Height), _theme.Text, _theme.InfoBar, flags);
    }

    private void PaintFooter(object? sender, PaintEventArgs e)
    {
        using var line = new SolidBrush(_theme.Divider);
        e.Graphics.FillRectangle(line, 0, 0, _footer.Width, Math.Max(1, (int)Math.Round(_scale)));
    }

    private MonitorInfo? SelectedMonitor() =>
        _monitors.FirstOrDefault(m => m.StableId == _monitorId) ?? _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();

    /// <summary>The one-line descriptions: what each setting means right now; and Save only when something changed.</summary>
    private void ShowDetails()
    {
        // Monitor
        MonitorInfo? monitor = SelectedMonitor();
        bool chosenMissing = _monitorId is not null && _monitors.All(m => m.StableId != _monitorId);
        if (chosenMissing)
        {
            _monitorCard.Subtitle = monitor is null ? "Saved monitor not connected" : $"Saved monitor not connected · recording {monitor.Name}";
            _monitorCard.SubtitleColor = _theme.Warning;
            _monitorCard.GlyphColor = _theme.Warning;
        }
        else
        {
            _monitorCard.Subtitle = monitor is null ? "No monitor found" : Summary(monitor);
            _monitorCard.SubtitleColor = null;
            _monitorCard.GlyphColor = null;
        }

        // Video
        VideoLevel level = RecordingQuality.VideoLevelOf(_videoQuality.Value);
        Size screen = monitor?.Bounds.Size ?? new Size(level.MaxWidth, level.MaxHeight);
        RecordingPreset preset = RecordingQuality.Preset(level.Level, _audioQuality.Value, screen);
        double gigabytes = RecordingQuality.GigabytesPerHour(preset);
        bool capped = monitor is not null && (monitor.Bounds.Width < level.MaxWidth && monitor.Bounds.Height < level.MaxHeight);
        _videoCard.Subtitle = capped
            ? string.Create(CultureInfo.InvariantCulture, $"{monitor!.Name} is {ResolutionName(monitor.Bounds.Size)} · up to {gigabytes:0.0} GB per hour")
            : string.Create(CultureInfo.InvariantCulture, $"Up to {gigabytes:0.0} GB per hour");

        // Folder
        ShowFolder(gigabytes);

        // Audio quality
        AudioLevel audio = RecordingQuality.AudioLevelOf(_audioQuality.Value);
        _audioCard.Subtitle = audio.Level == RecordingQuality.DefaultAudioLevel ? "Recommended for calls" : "";

        // Start and stop
        if (_shortcutProblem is { } problem)
        {
            _shortcutCard.Subtitle = problem;
            _shortcutCard.SubtitleColor = _theme.Error;
        }
        else if (ShortcutNotWorking())
        {
            _shortcutCard.Subtitle = "Not working now · Save to try again";
            _shortcutCard.SubtitleColor = _theme.Warning;
        }
        else
        {
            _shortcutCard.Subtitle = _shortcut.Value.IsEmpty ? "None · use the tray icon to start and stop" : "Start or stop from any app";
            _shortcutCard.SubtitleColor = null;
        }

        _shortcut.AccessibleDescription = _shortcutCard.Subtitle; // what the card says, for Narrator in the box

        if (!_meterTimer.Enabled)
        {
            ShowDevicesIdle();
        }

        // Device and monitor names only go with their ids (Windows may rename a device): they are not a change.
        static AppSettings Comparable(AppSettings s) => s with { MonitorName = null, MicrophoneName = null, OutputName = null };
        _save.Enabled = Comparable(BuildSettings()) != Comparable(_original) || ShortcutNotWorking();
    }

    /// <summary>The saved shortcut is shown but does not work (Windows refused it): Save tries it again.</summary>
    private bool ShortcutNotWorking() => _shortcut.Value == _original.Hotkey && !_shortcut.Value.IsEmpty && !_hotkeys.Holds(_shortcut.Value);

    /// <summary>
    /// The folder (as "Desktop › EKrecordings"), or why it would not do: missing, or nearly full. Uses the last
    /// <see cref="CheckFolder"/>: a folder on an unreachable network drive must not freeze the window (and the tray).
    /// </summary>
    private void ShowFolder(double gigabytesPerHour)
    {
        _folderCard.Subtitle = FriendlyPath(_folder);
        _folderCard.SubtitleColor = null;
        _tips.SetToolTip(_folderCard, _folder);
        _tips.SetToolTip(_browse, _folder); // also shown when the button gets the keyboard focus
        _browse.AccessibleDescription = _folder;
        if (_folderCheck is not { } check || !string.Equals(check.Folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool isDefault = string.Equals(_folder, AppPaths.DesktopRecordings, StringComparison.OrdinalIgnoreCase);
        if (!isDefault && !check.Exists)
        {
            _folderCard.Subtitle = "Folder not found · choose another one";
            _folderCard.SubtitleColor = _theme.Error;
            return;
        }

        if (check.FreeBytes is { } bytes && gigabytesPerHour > 0)
        {
            double free = bytes / 1e9;
            double hours = free / gigabytesPerHour;
            if (hours < 3)
            {
                _folderCard.Subtitle = string.Create(CultureInfo.InvariantCulture, $"Only {free:0} GB free · about {Math.Max(0, hours):0.#} hours at this quality");
                _folderCard.SubtitleColor = _theme.Warning;
            }
        }
    }

    /// <summary>Looks at the folder and its drive off the window thread, then shows what was found.</summary>
    private void CheckFolder()
    {
        string folder = _folder;
        _ = Task.Run(() => FolderCheck.Of(folder)).ContinueWith(
            t =>
            {
                if (!IsDisposed && t.IsCompletedSuccessfully && string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
                {
                    _folderCheck = t.Result;
                    ShowDetails();
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Without the preview: the meters are empty and say nothing yet.</summary>
    private void ShowDevicesIdle()
    {
        _micMeter.Active = false;
        _outputMeter.Active = false;
        _micStatus.Show("");
        _outputStatus.Show("");
        _micCard.GlyphColor = null;
    }

    private AudioSettingsChoice Choice()
    {
        var microphone = _microphone.SelectedItem as DeviceChoice;
        var output = _output.SelectedItem as DeviceChoice;
        return new AudioSettingsChoice(microphone?.Id, microphone?.Id is null ? null : microphone.Name, output?.Id, output?.Id is null ? null : output.Name);
    }

    private AudioSelection Selection()
    {
        AudioSettingsChoice choice = Choice();
        return new AudioSelection(choice.MicrophoneId, choice.MicrophoneName, choice.OutputId, choice.OutputName);
    }

    private void DevicesChanged()
    {
        _micHeard = false;
        _micMuted = null;
        _nextMuteCheck = 0;
        if (_meterTimer.Enabled && !_preview.IsShowing(Selection()))
        {
            // Stepping through a list with the arrow keys picks every device on the way: only the one it stops on is
            // opened.
            _preview.Close();
            ShowDevicesIdle();
            _restartTimer.Stop();
            _restartTimer.Start();
        }

        ShowDetails();
    }

    private void RestartPreview()
    {
        _restartTimer.Stop();
        if (_meterTimer.Enabled)
        {
            _preview.Show(Selection());
        }
    }

    private void StartPreview()
    {
        if (_closed || IsDisposed || !Visible || WindowState == FormWindowState.Minimized || _meterTimer.Enabled)
        {
            return;
        }

        _micHeard = false;
        _preview.Show(Selection());
        _meterTimer.Start();
    }

    private void StopPreview()
    {
        if (!_meterTimer.Enabled)
        {
            return;
        }

        _meterTimer.Stop();
        _restartTimer.Stop();
        _preview.Close();
        ShowDevicesIdle();
    }

    /// <summary>About 30 times a second: the meters, and what each input is doing (or why it is not).</summary>
    private void ShowLevels()
    {
        if (_preview.Snapshot() is not { } levels)
        {
            return;
        }

        CheckMuteSoon();
        ShowMicrophone(levels.Microphone);
        ShowOutput(levels.Computer);
    }

    private void ShowMicrophone(AudioInputStatus status)
    {
        bool capturing = status.State is InputState.Running or InputState.Fallback;
        _micCapturedId = capturing ? status.DeviceId : null;
        if (status.Warning is not null)
        {
            // Only exact zeros for a while (a hardware mute switch): what was heard before no longer counts.
            _micHeard = false;
        }

        _micHeard |= capturing && status.Level >= HeardLevel;
        (string text, Color? color, string glyph, string link) = status.State switch
        {
            InputState.Running or InputState.Fallback when _micMuted == true => ("Muted in Windows", _theme.Error, "", "Unmute"),
            InputState.Running or InputState.Fallback when status.Warning is not null && !_micHeard => ("No sound · check the mic's mute switch", _theme.Warning, "", ""),
            InputState.Fallback when Choice().MicrophoneId is null => ("Default mic not responding · using another one", _theme.Warning, "", ""),
            InputState.Fallback => ("Not available · Windows default is used", _theme.Warning, "", ""),
            InputState.Running when _micHeard => ("Working", _theme.Success, Glyphs.CheckMark, ""),
            InputState.Running => ("Speak to test", (Color?)null, "", ""),
            InputState.NoDevice => ("No microphone found", _theme.Warning, "", ""),
            InputState.Lost or InputState.Retrying => ("Not connected · trying again", _theme.Warning, "", ""),
            _ => ("Connecting…", (Color?)null, "", ""),
        };
        if (link.Length == 0 && _micStatus.Link.ContainsFocus)
        {
            // The Unmute link is about to go (it worked): the focus stays on this card instead of jumping on.
            _microphone.Focus();
        }

        ShowInput(_micCard, _micMeter, _micStatus, status, capturing && _micMuted != true, text, color, glyph, link);
        _micCard.GlyphColor = _micMuted == true || status.State is InputState.NoDevice or InputState.Lost or InputState.Retrying ? color : null;
        _micCard.Glyph = _micMuted == true ? Glyphs.MicrophoneOff : Glyphs.Microphone;
    }

    private void ShowOutput(AudioInputStatus status)
    {
        bool capturing = status.State is InputState.Running or InputState.Fallback;
        long now = Environment.TickCount64;
        if (capturing && status.Level >= PlayingLevel)
        {
            _outputHeardAt = now;
        }

        bool playing = _outputHeardAt > 0 && now - _outputHeardAt < 1500;
        (string text, Color? color, string glyph) = status.State switch
        {
            InputState.Fallback => ("Not available · Windows default is used", _theme.Warning, ""),
            InputState.Running when playing => ("Working", _theme.Success, Glyphs.CheckMark),
            InputState.Running => ("Nothing playing", (Color?)null, ""),
            InputState.NoDevice => ("No speakers or headset found", _theme.Warning, ""),
            InputState.Lost or InputState.Retrying => ("Not connected · trying again", _theme.Warning, ""),
            _ => ("Connecting…", (Color?)null, ""),
        };
        ShowInput(_outputCard, _outputMeter, _outputStatus, status, capturing, text, color, glyph, "");
    }

    /// <summary>The meter and status of one input; without a working device the status takes the whole line.</summary>
    private void ShowInput(Card card, LevelMeter meter, StatusLine status, AudioInputStatus input, bool showMeter, string text, Color? color, string glyph, string link)
    {
        if (meter.Shown != showMeter)
        {
            meter.Shown = showMeter;
            status.Left = showMeter ? meter.Right + D(12) : card.TextLeft;
            status.Width = card.Width - D(16) - status.Left;
        }

        meter.Active = showMeter;
        meter.Push(input.Level);
        status.Show(text, color, glyph, link);
    }

    /// <summary>Once a second, off the window thread: is the microphone muted in Windows?</summary>
    private void CheckMuteSoon()
    {
        long now = Environment.TickCount64;
        if (_muteCheckRunning || now < _nextMuteCheck)
        {
            return;
        }

        _muteCheckRunning = true;
        _nextMuteCheck = now + 1000;
        string? id = MicrophoneInUse();
        _ = Task.Run(() => CoreAudio.IsMicrophoneMuted(id)).ContinueWith(
            t =>
            {
                _muteCheckRunning = false;
                if (!IsDisposed && MicrophoneInUse() == id)
                {
                    _micMuted = t.IsCompletedSuccessfully ? t.Result : null;
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The microphone the meter listens to (Windows' default when a chosen one is not available), else the chosen one.</summary>
    private string? MicrophoneInUse() => _micCapturedId ?? Choice().MicrophoneId;

    private void Unmute()
    {
        string? id = MicrophoneInUse();
        _ = Task.Run(() => CoreAudio.UnmuteMicrophone(id)).ContinueWith(
            t =>
            {
                if (!IsDisposed)
                {
                    _micMuted = t.IsCompletedSuccessfully && t.Result ? false : _micMuted;
                    _nextMuteCheck = 0;
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>A short two-note chime on Windows' default output, so the computer-audio meter can be seen moving.</summary>
    private static void PlayTestSound()
    {
        if (_chime is null)
        {
            using MemoryStream wave = TestChime();
            byte[] pinned = GC.AllocateUninitializedArray<byte>((int)wave.Length, pinned: true);
            wave.ToArray().CopyTo(pinned, 0);
            _chime = pinned;
        }

        if (!AppNative.PlaySound(Marshal.UnsafeAddrOfPinnedArrayElement(_chime, 0), IntPtr.Zero, AppNative.SND_MEMORY | AppNative.SND_ASYNC | AppNative.SND_NODEFAULT))
        {
            Log.Warn("Playing the test sound failed (no sound device?).");
        }
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where EKrecorder saves recordings",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = _folderCheck is { Exists: true } check && check.Folder == _folder ? _folder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath))
        {
            _folder = dialog.SelectedPath;
            ShowDetails();
            CheckFolder();
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

        _shortcutProblem = problem;
        ShowDetails();
        return problem is null;
    }

    /// <summary>The settings as the window shows them now.</summary>
    private AppSettings BuildSettings()
    {
        string folder = _folder.Trim();
        AudioSettingsChoice audio = Choice();
        return _original with
        {
            MonitorId = _monitorId,
            MonitorName = _monitorName,
            VideoQuality = _videoQuality.Value,
            AudioQuality = _audioQuality.Value,
            RecordingFolder = string.Equals(folder, AppPaths.DesktopRecordings, StringComparison.OrdinalIgnoreCase) ? null : folder,
            MicrophoneId = audio.MicrophoneId,
            MicrophoneName = audio.MicrophoneName,
            OutputId = audio.OutputId,
            OutputName = audio.OutputName,
            Shortcut = _shortcut.Value.ToSetting(),
            MaxRecordingHours = AppSettings.MaxHoursChoices[Math.Max(0, _maxTime.SelectedIndex)],
            StartWithWindows = _startWithWindows.Checked,
        };
    }

    private void Save()
    {
        if (!CheckShortcut())
        {
            _shortcut.Focus();
            System.Media.SystemSounds.Beep.Play();
            _shortcut.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, _shortcutProblem ?? "");
            return;
        }

        string? problem = _apply(BuildSettings());
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "EKrecorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Close();
    }

    /// <summary>The devices chosen in the window.</summary>
    private sealed record AudioSettingsChoice(string? MicrophoneId, string? MicrophoneName, string? OutputId, string? OutputName);

    /// <summary>What was found about the recordings folder: whether it exists, and its drive's free space (if readable).</summary>
    private sealed record FolderCheck(string Folder, bool Exists, long? FreeBytes)
    {
        public static FolderCheck Of(string folder)
        {
            bool exists = false;
            long? free = null;
            try
            {
                exists = Directory.Exists(folder);
                if (Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } root && new DriveInfo(root) is { IsReady: true } drive)
                {
                    free = drive.AvailableFreeSpace;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                // The folder's drive cannot be read now; the folder is shown as it is.
            }

            return new FolderCheck(folder, exists, free);
        }
    }

    /// <summary>An entry in a device list: Windows' default (no id) or a specific device.</summary>
    private sealed record DeviceChoice(string? Id, string Name, bool? Connected = true)
    {
        public override string ToString() => Connected == false ? $"{Name} (not connected)" : Name;
    }
}
