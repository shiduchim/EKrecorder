using System.Globalization;
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
    private const int WidthDip = 640;

    private readonly AppSettings _original;
    private readonly HotkeyManager _hotkeys;
    private readonly Func<AppSettings, string?> _apply;
    private readonly Action<IReadOnlyList<MonitorInfo>> _identify;
    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly Panel _content = new();
    private readonly Label _recordingHeading = Heading("Recording");
    private readonly Label _audioHeading = Heading("Audio");
    private readonly Label _controlsHeading = Heading("Controls");
    private readonly Panel _banner = new();
    private readonly Card _monitorCard = new() { Glyph = Glyphs.Monitor, Title = "Monitor" };
    private readonly Card _videoCard = new() { Glyph = Glyphs.Video, Title = "Video quality" };
    private readonly Card _folderCard = new() { Glyph = Glyphs.Folder, Title = "Save recordings to", PathEllipsis = true };
    private readonly Card _micCard = new() { Glyph = Glyphs.Microphone, Title = "Microphone" };
    private readonly Card _outputCard = new() { Glyph = Glyphs.Speaker, Title = "Computer audio" };
    private readonly Card _audioCard = new() { Glyph = Glyphs.Equalizer, Title = "Audio quality" };
    private readonly Card _shortcutCard = new() { Glyph = Glyphs.Keyboard, Title = "Start/stop shortcut" };
    private readonly Card _stopCard = new() { Glyph = Glyphs.Timer, Title = "Stop recording after" };
    private readonly Card _startupCard = new() { Glyph = Glyphs.Power, Title = "Start with Windows" };
    private readonly MonitorPicker _monitorPicker = new() { AccessibleName = "Monitor to record" };
    private readonly PillButton _identifyButton = new() { Text = "Identify" };
    private readonly StepSlider _videoQuality = new() { AccessibleName = "Video quality" };
    private readonly PillButton _browse = new() { Text = "Browse…" };
    private readonly ComboBox _microphone = DeviceList("Microphone");
    private readonly LevelMeter _micMeter = new() { AccessibleName = "Microphone level" };
    private readonly ComboBox _output = DeviceList("Computer audio");
    private readonly LevelMeter _outputMeter = new() { AccessibleName = "Computer audio level" };
    private readonly PillButton _testSound = new() { Text = "Play a test sound", Style = PillStyle.Link, Glyph = Glyphs.Play };
    private readonly StepSlider _audioQuality = new() { AccessibleName = "Audio quality" };
    private readonly HotkeyBox _shortcut = new() { AccessibleName = "Start and stop shortcut", BorderStyle = BorderStyle.FixedSingle };
    private readonly ComboBox _maxTime = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Stop recording after" };
    private readonly ToggleSwitch _startWithWindows = new() { AccessibleName = "Start EKrecorder with Windows" };
    private readonly Label _version = new() { AutoSize = true, Text = $"EKrecorder {EnvironmentInfo.ShortVersion}" };
    private readonly PillButton _save = new() { Text = "Save", Style = PillStyle.Accent };
    private readonly PillButton _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly AudioPreview _preview = new();
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 33 };
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
    private System.Media.SoundPlayer? _testPlayer;
    private MemoryStream? _testWave;

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

        Text = "EKrecorder Settings";
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

        _content.Controls.AddRange([_banner, _recordingHeading, _monitorCard, _videoCard, _folderCard, _audioHeading, _micCard, _outputCard, _audioCard,
            _controlsHeading, _shortcutCard, _stopCard, _startupCard, _version, _save, _cancel]);
        _monitorCard.Controls.AddRange([_identifyButton, _monitorPicker]);
        _videoCard.Controls.Add(_videoQuality);
        _folderCard.Controls.Add(_browse);
        _micCard.Controls.AddRange([_microphone, _micMeter]);
        _outputCard.Controls.AddRange([_output, _outputMeter, _testSound]);
        _audioCard.Controls.Add(_audioQuality);
        _shortcutCard.Controls.Add(_shortcut);
        _stopCard.Controls.Add(_maxTime);
        _startupCard.Controls.Add(_startWithWindows);
        Controls.Add(_content);
        _banner.Paint += PaintBanner;
        _banner.Visible = false;

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
            $"{m.Name}: {Describe(m)}")).ToList();
        _monitorPicker.SelectedId = SelectedMonitor()?.StableId is { } shown && (_monitorId is null || _monitorId == shown) ? shown : null;
        _shortcut.Value = settings.Hotkey;
        foreach (int hours in AppSettings.MaxHoursChoices)
        {
            _maxTime.Items.Add(hours == 0 ? "Off" : string.Create(CultureInfo.InvariantCulture, $"{hours} hours"));
        }

        _maxTime.SelectedIndex = Math.Max(0, AppSettings.MaxHoursChoices.ToList().IndexOf(settings.MaxRecordingHours));
        _startWithWindows.Checked = settings.StartWithWindows;
        SetDevices(_microphone, [], settings.MicrophoneId, settings.MicrophoneName);
        SetDevices(_output, [], settings.OutputId, settings.OutputName);

        _tips.SetToolTip(_identifyButton, "Shows each monitor's number on that monitor for a few seconds.");
        _tips.SetToolTip(_testSound, "Plays a short sound on Windows' default speakers or headset; the meter above should move.");
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
        _shortcut.GotFocus += (_, _) => _hotkeys.Suspend();
        _shortcut.LostFocus += (_, _) => _hotkeys.Resume();
        _shortcut.ValueChanged += (_, _) => CheckShortcut();
        _save.Click += (_, _) => Save();
        _cancel.Click += (_, _) => Close(); // not modal: DialogResult alone would not close it (Esc comes here too)
        _meterTimer.Tick += (_, _) => ShowLevels();

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
    /// Self-test only: what is wrong with the layout (controls outside their card or overlapping, text that cannot
    /// fit), or nothing.
    /// </summary>
    public IReadOnlyList<string> CheckLayout()
    {
        var problems = new List<string>();
        int margin = D(6);
        Card[] cards = [_monitorCard, _videoCard, _folderCard, _micCard, _outputCard, _audioCard, _shortcutCard, _stopCard, _startupCard];
        foreach (Card card in cards)
        {
            Rectangle inner = Rectangle.Inflate(new Rectangle(Point.Empty, card.Size), -margin, -margin);
            var children = card.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            foreach (Control child in children)
            {
                if (!inner.Contains(child.Bounds))
                {
                    problems.Add($"{card.Title}: {NameOf(child)} {child.Bounds} is not inside the card {card.Size}");
                }

                if (child.Left < card.TextRight && child != _testSound)
                {
                    problems.Add($"{card.Title}: {NameOf(child)} starts at {child.Left}, inside the text column (ends at {card.TextRight})");
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

        foreach (Control text in new Control[] { _microphone, _output, _maxTime, _shortcut })
        {
            int needed = TextRenderer.MeasureText(text.Text, text.Font).Width + (text is ComboBox ? D(24) : 0);
            if (needed > text.Width)
            {
                problems.Add($"{NameOf(text)}: \"{text.Text}\" needs {needed} px, the box is {text.Width}");
            }
        }

        return problems;

        static string NameOf(Control c) => c.AccessibleName ?? c.Text ?? c.GetType().Name;
    }

    /// <summary>Shows or hides "Recording now — changes apply to the next recording".</summary>
    public void SetRecording(bool recording)
    {
        if (_recording == recording)
        {
            return;
        }

        _recording = recording;
        LayoutAll();
    }

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
        StartPreview();
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopPreview();
        _preview.Dispose();
        _meterTimer.Dispose();
        _hotkeys.Resume();
        _tips.Dispose();
        _testPlayer?.Dispose();
        _testWave?.Dispose();
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

    private static ComboBox DeviceList(string name)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name };
        box.Items.Add(new DeviceChoice(null, "Windows default"));
        return box;
    }

    /// <summary>"LS32D80xU · 3840 × 2160 · main".</summary>
    private static string Describe(MonitorInfo monitor) => string.Join(" · ", new[]
    {
        monitor.FriendlyName,
        string.Create(CultureInfo.InvariantCulture, $"{monitor.Bounds.Width} × {monitor.Bounds.Height}"),
        monitor.IsPrimary ? "main" : "",
    }.Where(p => p.Length > 0));

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

    private static string DeviceText(ComboBox box, string defaultText) =>
        box.SelectedItem is DeviceChoice { Id: not null } choice ? choice.ToString() : defaultText;

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

    private int D(float dip) => (int)Math.Round(dip * _scale);

    private void SetScale(float scale)
    {
        _scale = scale;
        _fonts.Dispose();
        _fonts = new UiFonts(scale);
        ApplyTheme();
    }

    /// <summary>Colours and fonts for everything, then the layout.</summary>
    private void ApplyTheme()
    {
        SuspendLayout();
        BackColor = _theme.Page;
        _content.BackColor = _theme.Page;
        foreach (Label heading in new[] { _recordingHeading, _audioHeading, _controlsHeading })
        {
            heading.Font = _fonts.Strong;
            heading.ForeColor = _theme.Text;
            heading.BackColor = _theme.Page;
        }

        _version.Font = _fonts.Caption;
        _version.ForeColor = _theme.SecondaryText;
        _version.BackColor = _theme.Page;
        foreach (ComboBox box in new[] { _microphone, _output, _maxTime })
        {
            box.Font = _fonts.Body;
            box.BackColor = _theme.Control;
            box.ForeColor = _theme.Text;
        }

        _shortcut.Font = _fonts.Body;
        _shortcut.BackColor = _theme.Control;
        _shortcut.ForeColor = _theme.Text;
        foreach (IThemed control in new IThemed[]
        {
            _monitorCard, _videoCard, _folderCard, _micCard, _outputCard, _audioCard, _shortcutCard, _stopCard, _startupCard,
            _monitorPicker, _identifyButton, _videoQuality, _browse, _micMeter, _outputMeter, _testSound, _audioQuality, _startWithWindows, _save, _cancel,
        })
        {
            control.ApplyTheme(_theme, _fonts, _scale);
        }

        ShowDetails();
        LayoutAll();
        ResumeLayout(true);
    }

    /// <summary>Places every card and control; the window is as tall as its contents (and scrolls on a small screen).</summary>
    private void LayoutAll()
    {
        int width = D(WidthDip);
        int pad = D(24);
        int cardWidth = width - (2 * pad);
        int inside = D(16);
        int right = cardWidth - inside;
        int gap = D(4);
        int y = D(12);

        _banner.Visible = _recording;
        if (_recording)
        {
            _banner.Bounds = new Rectangle(pad, y, cardWidth, Math.Max(D(40), _fonts.Body.Height + D(20)));
            _banner.Invalidate();
            y = _banner.Bottom + D(8);
        }

        // Recording
        y = PlaceHeading(_recordingHeading, pad, y);
        _identifyButton.Size = _identifyButton.PreferredSize;
        _monitorPicker.Size = new Size(D(168), D(64));
        int monitorHeight = Math.Max(D(84), _monitorPicker.Height + D(20));
        _monitorCard.Bounds = new Rectangle(pad, y, cardWidth, monitorHeight);
        _monitorPicker.Location = new Point(right - _monitorPicker.Width, (monitorHeight - _monitorPicker.Height) / 2);
        _identifyButton.Location = new Point(_monitorPicker.Left - D(12) - _identifyButton.Width, (monitorHeight - _identifyButton.Height) / 2);
        _monitorCard.TextRight = _identifyButton.Left - D(12);
        y = _monitorCard.Bottom + gap;

        y = PlaceSliderCard(_videoCard, _videoQuality, pad, y, cardWidth, right) + gap;

        _browse.Size = _browse.PreferredSize;
        int folderHeight = Math.Max(D(68), _browse.Height + D(24));
        _folderCard.Bounds = new Rectangle(pad, y, cardWidth, folderHeight);
        _browse.Location = new Point(right - _browse.Width, (folderHeight - _browse.Height) / 2);
        _folderCard.TextRight = _browse.Left - D(16);
        y = _folderCard.Bottom;

        // Audio
        y = PlaceHeading(_audioHeading, pad, y + D(12));
        y = PlaceDeviceCard(_micCard, _microphone, _micMeter, null, pad, y, cardWidth, right) + gap;
        y = PlaceDeviceCard(_outputCard, _output, _outputMeter, _testSound, pad, y, cardWidth, right) + gap;
        y = PlaceSliderCard(_audioCard, _audioQuality, pad, y, cardWidth, right);

        // Controls
        y = PlaceHeading(_controlsHeading, pad, y + D(12));
        _shortcut.Width = Math.Max(D(200), TextRenderer.MeasureText("Ctrl + Alt + Shift + Page Down", _fonts.Body).Width + D(16));
        y = PlaceSimpleCard(_shortcutCard, _shortcut, pad, y, cardWidth, right) + gap;
        _maxTime.Width = Math.Max(D(140), TextRenderer.MeasureText("12 hours", _fonts.Body).Width + D(40));
        y = PlaceSimpleCard(_stopCard, _maxTime, pad, y, cardWidth, right) + gap;
        y = PlaceSimpleCard(_startupCard, _startWithWindows, pad, y, cardWidth, right);

        // Buttons
        y += D(20);
        _save.Size = _save.PreferredSize;
        _cancel.Size = _cancel.PreferredSize;
        _cancel.Location = new Point(pad + cardWidth - _cancel.Width, y);
        _save.Location = new Point(_cancel.Left - D(8) - _save.Width, y);
        _version.Location = new Point(pad + D(4), y + ((_save.Height - _version.Height) / 2));
        y = _save.Bottom + D(20);

        _content.Bounds = new Rectangle(0, 0, width, y);
        Size client = new(width, y);
        if (_forcedScale is null && IsHandleCreated)
        {
            // On a screen too short for the whole window, it scrolls.
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int chrome = Height - ClientSize.Height;
            if (client.Height + chrome > area.Height)
            {
                client = new Size(width + SystemInformation.VerticalScrollBarWidth, area.Height - chrome);
            }
        }

        ClientSize = client;
    }

    private int PlaceHeading(Label heading, int pad, int y)
    {
        heading.Location = new Point(pad + D(4), y + D(8));
        return heading.Bottom + D(8);
    }

    private int PlaceSliderCard(Card card, StepSlider slider, int pad, int y, int cardWidth, int right)
    {
        slider.Size = new Size(D(264), slider.NeededHeight);
        int height = Math.Max(D(68), slider.Height + D(20));
        card.Bounds = new Rectangle(pad, y, cardWidth, height);
        slider.Location = new Point(right - slider.Width + D(8), ((height - slider.Height) / 2) + D(2));
        card.TextRight = slider.Left - D(4);
        return card.Bottom;
    }

    private int PlaceSimpleCard(Card card, Control control, int pad, int y, int cardWidth, int right)
    {
        int height = Math.Max(D(64), control.Height + D(24));
        card.Bounds = new Rectangle(pad, y, cardWidth, height);
        control.Location = new Point(right - control.Width, (height - control.Height) / 2);
        card.TextRight = control.Left - D(16);
        return card.Bottom;
    }

    /// <summary>A device card: the list at the top right, its level meter under it, and (optionally) a link under the description.</summary>
    private int PlaceDeviceCard(Card card, ComboBox list, LevelMeter meter, PillButton? link, int pad, int y, int cardWidth, int right)
    {
        int top = D(14);
        list.Width = D(268);
        list.Location = new Point(right - list.Width, top);
        meter.Bounds = new Rectangle(list.Left, list.Bottom + D(10), list.Width, D(6));
        card.TextRight = list.Left - D(16);
        card.TextTop = top + Math.Max(0, (list.Height - _fonts.Body.Height) / 2);
        int bottom = meter.Bottom + D(14);
        if (link is not null)
        {
            link.Size = link.PreferredSize;
            link.Location = new Point(card.TextLeft - D(4), card.TextTop + card.TextHeight + D(4));
            bottom = Math.Max(bottom, link.Bottom + D(10));
        }

        card.Bounds = new Rectangle(pad, y, cardWidth, Math.Max(D(76), bottom));
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

        using (System.Drawing.Drawing2D.GraphicsPath shape = Glyphs.Rounded(new RectangleF(0.5f, 0.5f, _banner.Width - 1.5f, _banner.Height - 1.5f), 6 * _scale))
        using (var fill = new SolidBrush(_theme.InfoBar))
        {
            g.FillPath(fill, shape);
        }

        float dot = 10 * _scale;
        using (var red = new SolidBrush(Color.FromArgb(0xE5, 0x39, 0x35)))
        {
            g.FillEllipse(red, D(16), (_banner.Height - dot) / 2, dot, dot);
        }

        TextRenderer.DrawText(g, "Recording now — changes apply to the next recording", _fonts.Body,
            new Rectangle(D(36), 0, _banner.Width - D(44), _banner.Height), _theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private MonitorInfo? SelectedMonitor() =>
        _monitors.FirstOrDefault(m => m.StableId == _monitorId) ?? _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();

    /// <summary>The one-line descriptions: what each setting means right now.</summary>
    private void ShowDetails()
    {
        // Monitor
        MonitorInfo? monitor = SelectedMonitor();
        bool chosenMissing = _monitorId is not null && _monitors.All(m => m.StableId != _monitorId);
        if (chosenMissing)
        {
            string name = _monitorName is { Length: > 0 } n ? n : "The chosen monitor";
            _monitorCard.Subtitle = $"{name} is not connected · the main monitor is recorded";
            _monitorCard.SubtitleColor = _theme.Warning;
        }
        else
        {
            _monitorCard.Subtitle = monitor is null ? "No monitor found" : $"{monitor.Name} · {Describe(monitor)}";
            _monitorCard.SubtitleColor = null;
        }

        // Video
        VideoLevel level = RecordingQuality.VideoLevelOf(_videoQuality.Value);
        Size screen = monitor?.Bounds.Size ?? new Size(level.MaxWidth, level.MaxHeight);
        RecordingPreset preset = RecordingQuality.Preset(level.Level, _audioQuality.Value, screen);
        Size output = preset.OutputSizeFor(screen);
        _videoCard.Subtitle = string.Create(CultureInfo.InvariantCulture,
            $"{output.Width} × {output.Height} · {RecordingQuality.FramesPerSecond} fps · up to {RecordingQuality.GigabytesPerHour(preset):0.0} GB per hour");

        // Folder
        _folderCard.Subtitle = _folder;

        // Audio quality
        AudioLevel audio = RecordingQuality.AudioLevelOf(_audioQuality.Value);
        _audioCard.Subtitle = audio.Level == RecordingQuality.DefaultAudioLevel ? $"{audio.Label} · recommended for calls" : audio.Label;

        // Controls
        if (_shortcutProblem is { } problem)
        {
            _shortcutCard.Subtitle = problem;
            _shortcutCard.SubtitleColor = _theme.Error;
        }
        else
        {
            _shortcutCard.Subtitle = _shortcut.Value.IsEmpty ? "None: use the tray icon to start and stop" : "Press it anywhere to start or stop";
            _shortcutCard.SubtitleColor = null;
        }

        int hours = AppSettings.MaxHoursChoices[Math.Max(0, _maxTime.SelectedIndex)];
        _stopCard.Subtitle = hours == 0 ? "Records until you stop it" : string.Create(CultureInfo.InvariantCulture, $"Saved and stopped after {hours} hours");
        _startupCard.Subtitle = _startWithWindows.Checked ? "Waits in the tray, no window" : "Start it from the Start menu";
        if (!_meterTimer.Enabled)
        {
            ShowDeviceNames();
        }
    }

    /// <summary>Without the preview: the chosen devices' names.</summary>
    private void ShowDeviceNames()
    {
        _micCard.Subtitle = DeviceText(_microphone, "Windows' default microphone");
        _micCard.SubtitleColor = null;
        _outputCard.Subtitle = DeviceText(_output, "What Windows plays on its default speakers or headset");
        _outputCard.SubtitleColor = null;
    }

    private AudioSelection Selection()
    {
        var microphone = _microphone.SelectedItem as DeviceChoice;
        var output = _output.SelectedItem as DeviceChoice;
        return new AudioSelection(microphone?.Id, microphone?.Id is null ? null : microphone.Name, output?.Id, output?.Id is null ? null : output.Name);
    }

    private void DevicesChanged()
    {
        if (_meterTimer.Enabled)
        {
            _preview.Show(Selection());
        }
        else
        {
            ShowDeviceNames();
        }
    }

    private void StartPreview()
    {
        if (!Visible || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        _preview.Show(Selection());
        _micMeter.Active = true;
        _outputMeter.Active = true;
        _meterTimer.Start();
    }

    private void StopPreview()
    {
        _meterTimer.Stop();
        _preview.Close();
        _micMeter.Active = false;
        _outputMeter.Active = false;
        ShowDeviceNames();
    }

    /// <summary>About 30 times a second: the meters, and which device is in use (or why none is).</summary>
    private void ShowLevels()
    {
        if (_preview.Snapshot() is not { } levels)
        {
            return;
        }

        ShowInput(_micCard, _micMeter, levels.Microphone, isMicrophone: true);
        ShowInput(_outputCard, _outputMeter, levels.Computer, isMicrophone: false);
    }

    private void ShowInput(Card card, LevelMeter meter, AudioInputStatus status, bool isMicrophone)
    {
        meter.Active = status.State is InputState.Running or InputState.Fallback;
        meter.Push(status.Level);
        string devices = status.Devices;
        (string text, Color? color) = status.State switch
        {
            InputState.Running when isMicrophone && status.Warning is not null => ("No sound at all · muted, or blocked in Windows privacy settings?", _theme.Warning),
            InputState.Running => (devices, null),
            InputState.Fallback => ($"Not connected · using {devices}", _theme.Warning),
            InputState.NoDevice => (isMicrophone ? "No microphone found" : "No speakers or headset found", _theme.Warning),
            InputState.Lost or InputState.Retrying => ("Not connected · trying again", _theme.Warning),
            _ => ("Connecting…", (Color?)null),
        };
        card.Subtitle = text;
        card.SubtitleColor = color;
    }

    /// <summary>A short two-note chime on Windows' default output, so the computer-audio meter can be seen moving.</summary>
    private void PlayTestSound()
    {
        try
        {
            if (_testPlayer is null)
            {
                _testWave = TestChime();
                _testPlayer = new System.Media.SoundPlayer(_testWave);
                _testPlayer.Load();
            }

            _testWave!.Position = 0;
            _testPlayer.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            Log.Warn($"Playing the test sound failed: {ex.Message}");
        }
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where EKrecorder saves recordings",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(_folder) ? _folder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath))
        {
            _folder = dialog.SelectedPath;
            ShowDetails();
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

    private void Save()
    {
        if (!CheckShortcut())
        {
            _shortcut.Focus();
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        string folder = _folder.Trim();
        var microphone = _microphone.SelectedItem as DeviceChoice;
        var output = _output.SelectedItem as DeviceChoice;
        AppSettings updated = _original with
        {
            MonitorId = _monitorId,
            MonitorName = _monitorName,
            VideoQuality = _videoQuality.Value,
            AudioQuality = _audioQuality.Value,
            RecordingFolder = string.Equals(folder, AppPaths.DesktopRecordings, StringComparison.OrdinalIgnoreCase) ? null : folder,
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

        Close();
    }

    /// <summary>An entry in a device list: Windows' default (no id) or a specific device.</summary>
    private sealed record DeviceChoice(string? Id, string Name, bool? Connected = true)
    {
        public override string ToString() => Connected == false ? $"{Name} (not connected)" : Name;
    }
}
