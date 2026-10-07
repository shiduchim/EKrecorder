using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace EKrecorder.Shell;

/// <summary>A control the Settings window draws in its own colours and at its own scale.</summary>
internal interface IThemed
{
    void ApplyTheme(UiTheme theme, UiFonts fonts, float scale);
}

/// <summary>
/// One setting, as a Windows 11 Settings card: an icon, a title, an optional one-line description, and the
/// setting's controls (child controls, placed by the window). The description is shortened with "…" when it does
/// not fit; the whole text is then in its tooltip.
/// </summary>
internal sealed class Card : Panel, IThemed
{
    private readonly ToolTip _tip = new() { InitialDelay = 400 };
    private UiTheme? _theme;
    private UiFonts? _fonts;
    private float _scale = 1;
    private string _glyph = "";
    private Color? _glyphColor;
    private string _title = "";
    private string _subtitle = "";
    private Color? _subtitleColor;
    private bool _pathEllipsis;
    private int _textRight;
    private int _headerHeight;
    private string? _shownTip;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph
    {
        get => _glyph;
        set
        {
            if (_glyph != value)
            {
                _glyph = value;
                Invalidate();
            }
        }
    }

    /// <summary>A colour for the icon when something is wrong (null: the text colour).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? GlyphColor
    {
        get => _glyphColor;
        set
        {
            if (_glyphColor != value)
            {
                _glyphColor = value;
                Invalidate();
            }
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            AccessibleName = value;
            Invalidate();
        }
    }

    /// <summary>One line under the title (empty: the title is on its own).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (_subtitle == value)
            {
                return;
            }

            _subtitle = value;
            AccessibleDescription = value;
            Invalidate();
        }
    }

    /// <summary>A warning or error colour for the description (null: the usual grey).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? SubtitleColor
    {
        get => _subtitleColor;
        set
        {
            if (_subtitleColor != value)
            {
                _subtitleColor = value;
                Invalidate();
            }
        }
    }

    /// <summary>The description is a file path: shortened in the middle, not at the end.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool PathEllipsis
    {
        get => _pathEllipsis;
        set
        {
            _pathEllipsis = value;
            Invalidate();
        }
    }

    /// <summary>Where the title and description must end (the controls start after it).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TextRight
    {
        get => _textRight;
        set
        {
            _textRight = value;
            Invalidate();
        }
    }

    /// <summary>The height of the card's first row, where the icon, title and description are centred (0: the whole card).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int HeaderHeight
    {
        get => _headerHeight;
        set
        {
            _headerHeight = value;
            Invalidate();
        }
    }

    /// <summary>The left edge of the text column (after the icon).</summary>
    public int TextLeft => D(58);

    /// <summary>The height of the title and its description.</summary>
    public int TextHeight => _fonts is null ? 0 : _fonts.Body.Height + (_subtitle.Length > 0 ? D(2) + _fonts.Caption.Height : 0);

    public void ApplyTheme(UiTheme theme, UiFonts fonts, float scale)
    {
        _theme = theme;
        _fonts = fonts;
        _scale = scale;
        BackColor = theme.Card;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The corners outside the rounded card show the page.
        using var page = new SolidBrush(Parent?.BackColor ?? BackColor);
        e.Graphics.FillRectangle(page, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_theme is null || _fonts is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float border = Math.Max(1, (int)Math.Round(_scale));
        var bounds = new RectangleF(border / 2, border / 2, Width - border - 0.5f, Height - border - 0.5f);
        using (GraphicsPath shape = Glyphs.Rounded(bounds, (int)Math.Round(4 * _scale)))
        using (var fill = new SolidBrush(_theme.Card))
        using (var pen = new Pen(_theme.CardBorder, border))
        {
            g.FillPath(fill, shape);
            g.DrawPath(pen, shape);
        }

        g.SmoothingMode = SmoothingMode.None;
        int header = _headerHeight > 0 ? _headerHeight : Height;
        int top = (header - TextHeight) / 2;
        int right = _textRight > 0 ? _textRight : Width - D(16);
        Color text = Enabled ? _theme.Text : _theme.DisabledText;
        Glyphs.Draw(g, _glyph, new Rectangle(D(18), (header - D(20)) / 2, D(20), D(20)), _glyphColor ?? text, 20 * _scale, _theme.Card);
        var titleBox = new Rectangle(TextLeft, top, Math.Max(0, right - TextLeft), _fonts.Body.Height);
        TextRenderer.DrawText(g, _title, _fonts.Body, titleBox, text, _theme.Card, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        string? tip = null;
        if (_subtitle.Length > 0)
        {
            var subtitleBox = new Rectangle(TextLeft, titleBox.Bottom + D(2), titleBox.Width, _fonts.Caption.Height);
            TextFormatFlags flags = (_pathEllipsis ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis) | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, _subtitle, _fonts.Caption, subtitleBox, Enabled ? _subtitleColor ?? _theme.SecondaryText : _theme.DisabledText, _theme.Card, flags);
            bool cut = TextRenderer.MeasureText(g, _subtitle, _fonts.Caption, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width > subtitleBox.Width;
            tip = cut ? _subtitle : null;
        }

        if (tip != _shownTip)
        {
            _shownTip = tip;
            _tip.SetToolTip(this, tip);
        }
    }

    private int D(float dip) => (int)Math.Round(dip * _scale);
}

/// <summary>
/// The monitors as Windows' Display settings show them: to scale, in their real arrangement, numbered, with their
/// resolution. Click one (or use the arrow keys) to choose it; the chosen one is filled with the accent colour.
/// </summary>
internal sealed class MonitorPicker : Control, IThemed
{
    private readonly ToolTip _tip = new() { InitialDelay = 300 };
    private IReadOnlyList<Choice> _monitors = [];
    private string? _selectedId;
    private int _hover = -1;
    private UiTheme? _theme;
    private UiFonts? _fonts;
    private float _scale = 1;

    public MonitorPicker()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
    }

    public event EventHandler? SelectionChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<Choice> Monitors
    {
        get => _monitors;
        set
        {
            _monitors = value;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            if (_selectedId == value)
            {
                return;
            }

            _selectedId = value;
            AccessibleDescription = _monitors.FirstOrDefault(m => m.Id == value)?.Description;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyTheme(UiTheme theme, UiFonts fonts, float scale)
    {
        _theme = theme;
        _fonts = fonts;
        _scale = scale;
        BackColor = theme.Card;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int index = SelectedIndex();
        int next = e.KeyCode switch
        {
            Keys.Left or Keys.Up => index - 1,
            Keys.Right or Keys.Down => index + 1,
            Keys.Home => 0,
            Keys.End => _monitors.Count - 1,
            _ => -2,
        };
        if (next != -2 && _monitors.Count > 0)
        {
            SelectedId = _monitors[Math.Clamp(next, 0, _monitors.Count - 1)].Id;
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int hit = HitTest(e.Location);
        if (hit >= 0 && e.Button == MouseButtons.Left)
        {
            SelectedId = _monitors[hit].Id;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            _tip.SetToolTip(this, hit >= 0 ? _monitors[hit].Description : null);
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_theme is null || _fonts is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        List<RectangleF> boxes = Boxes();
        for (int i = 0; i < boxes.Count; i++)
        {
            Choice monitor = _monitors[i];
            bool selected = monitor.Id == _selectedId;
            RectangleF box = boxes[i];
            Color fill = selected ? _theme.Accent : i == _hover ? UiTheme.Blend(_theme.MonitorFill, _theme.Text, 0.92) : _theme.MonitorFill;
            using (GraphicsPath shape = Glyphs.Rounded(box, (int)Math.Round(4 * _scale)))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, shape);
                if (!selected)
                {
                    using var border = new Pen(_theme.MonitorBorder, Math.Max(1, (int)Math.Round(_scale)));
                    g.DrawPath(border, shape);
                }
            }

            if (selected && Focused && ShowFocusCues)
            {
                RectangleF ring = RectangleF.Inflate(box, 3 * _scale, 3 * _scale);
                using GraphicsPath focus = Glyphs.Rounded(ring, (int)Math.Round(7 * _scale));
                using var pen = new Pen(_theme.Text, Math.Max(1, 2 * _scale));
                g.DrawPath(pen, focus);
            }

            Color text = selected ? _theme.AccentText : _theme.Text;
            Rectangle area = Rectangle.Round(box);
            bool roomForResolution = box.Height >= _fonts.Strong.Height + _fonts.Caption.Height + (4 * _scale)
                && box.Width >= TextRenderer.MeasureText(monitor.Resolution, _fonts.Caption).Width;
            TextFormatFlags centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (roomForResolution)
            {
                int block = _fonts.Strong.Height + _fonts.Caption.Height;
                int top = area.Top + ((area.Height - block) / 2);
                TextRenderer.DrawText(g, monitor.Number, _fonts.Strong, new Rectangle(area.Left, top, area.Width, _fonts.Strong.Height), text, fill, centre);
                TextRenderer.DrawText(g, monitor.Resolution, _fonts.Caption, new Rectangle(area.Left, top + _fonts.Strong.Height, area.Width, _fonts.Caption.Height), text, fill, centre);
            }
            else
            {
                TextRenderer.DrawText(g, monitor.Number, _fonts.Strong, area, text, fill, centre | TextFormatFlags.VerticalCenter);
            }
        }
    }

    private int SelectedIndex()
    {
        for (int i = 0; i < _monitors.Count; i++)
        {
            if (_monitors[i].Id == _selectedId)
            {
                return i;
            }
        }

        return 0;
    }

    private int HitTest(Point point)
    {
        List<RectangleF> boxes = Boxes();
        for (int i = 0; i < boxes.Count; i++)
        {
            if (boxes[i].Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Every monitor's rectangle in the control: their arrangement, scaled to fit, centred.</summary>
    private List<RectangleF> Boxes()
    {
        var boxes = new List<RectangleF>();
        if (_monitors.Count == 0)
        {
            return boxes;
        }

        Rectangle all = _monitors.Select(m => m.Bounds).Aggregate(Rectangle.Union);
        float margin = 4 * _scale;
        float fit = Math.Min((Width - (2 * margin)) / all.Width, (Height - (2 * margin)) / all.Height);
        float offsetX = (Width - (all.Width * fit)) / 2;
        float offsetY = (Height - (all.Height * fit)) / 2;
        float gap = 2 * _scale;
        foreach (Choice monitor in _monitors)
        {
            Rectangle b = monitor.Bounds;
            boxes.Add(new RectangleF(
                MathF.Round(offsetX + ((b.X - all.X) * fit) + gap),
                MathF.Round(offsetY + ((b.Y - all.Y) * fit) + gap),
                MathF.Round(Math.Max(2, (b.Width * fit) - (2 * gap))),
                MathF.Round(Math.Max(2, (b.Height * fit) - (2 * gap)))));
        }

        return boxes;
    }

    /// <summary>One monitor: its id, its number, where it is on the desktop, its resolution class ("4K") and its name for the tooltip.</summary>
    internal sealed record Choice(string Id, string Number, Rectangle Bounds, string Resolution, string Description);
}

/// <summary>
/// A live level meter: a thin rounded bar that follows the loudest sample of each moment (−60 to 0 dBFS), rising
/// at once and falling back at 20 dB a second, with a mark that holds the peak for a second. Green; yellow from
/// −6 dBFS; red from −1 dBFS (about to clip).
/// </summary>
internal sealed class LevelMeter : Control, IThemed
{
    private const float FloorDb = -60f;
    private const float FallDbPerSecond = 20f;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private UiTheme? _theme;
    private float _scale = 1;
    private float _shownDb = FloorDb;
    private float _peakDb = FloorDb;
    private TimeSpan _peakAt;
    private TimeSpan _last;
    private bool _active;

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    /// <summary>False while no device is captured (the bar is empty).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
            {
                return;
            }

            _active = value;
            _shownDb = FloorDb;
            _peakDb = FloorDb;
            Invalidate();
        }
    }

    /// <summary>The loudest sample since the last call (0 to 1 of full scale).</summary>
    public void Push(float peak)
    {
        TimeSpan now = _clock.Elapsed;
        float seconds = (float)Math.Min(0.5, (now - _last).TotalSeconds);
        _last = now;
        float db = peak > 0 ? Math.Clamp(20f * MathF.Log10(peak), FloorDb, 0) : FloorDb;
        float shown = Math.Max(db, _shownDb - (FallDbPerSecond * seconds));
        if (db >= _peakDb || now - _peakAt > TimeSpan.FromSeconds(1))
        {
            _peakDb = db;
            _peakAt = now;
        }

        if (Math.Abs(shown - _shownDb) > 0.05f || _peakDb > FloorDb)
        {
            _shownDb = shown;
            Invalidate();
        }
    }

    public void ApplyTheme(UiTheme theme, UiFonts fonts, float scale)
    {
        _theme = theme;
        _scale = scale;
        BackColor = theme.Card;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_theme is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float trackHeight = Math.Max(2, 4 * _scale);
        float top = (Height - trackHeight) / 2;
        var track = new RectangleF(0, top, Width, trackHeight);
        using (GraphicsPath shape = Glyphs.Rounded(track, trackHeight / 2))
        using (var brush = new SolidBrush(_theme.Track))
        {
            g.FillPath(brush, shape);
        }

        if (!_active)
        {
            return;
        }

        float width = Width * Fraction(_shownDb);
        if (width >= 1)
        {
            Color color = _shownDb >= -1 ? _theme.Error : _shownDb >= -6 ? _theme.Warning : _theme.Success;
            using GraphicsPath level = Glyphs.Rounded(new RectangleF(0, top, Math.Max(width, trackHeight), trackHeight), trackHeight / 2);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, level);
        }

        if (_peakDb > FloorDb + 1)
        {
            float x = Math.Clamp(Width * Fraction(_peakDb), 1, Width - 1);
            using var mark = new SolidBrush(_theme.Text);
            g.FillRectangle(mark, x - _scale, 0, Math.Max(1, 2 * _scale), Height);
        }
    }

    private static float Fraction(float db) => Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);
}

/// <summary>A short status (a word or two, with an optional icon before it and a link after it), as on Windows 11 cards.</summary>
internal sealed class StatusLine : Control, IThemed
{
    private UiTheme? _theme;
    private UiFonts? _fonts;
    private float _scale = 1;
    private string _glyph = "";
    private Color? _color;

    public StatusLine()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        Link.Style = PillStyle.Link;
        Link.Visible = false;
        Controls.Add(Link);
    }

    /// <summary>The link after the text ("Unmute"); hidden while it has no text.</summary>
    public PillButton Link { get; } = new();

    /// <summary>Sets what the line says. <paramref name="color"/> null: the usual grey.</summary>
    public void Show(string text, Color? color = null, string glyph = "", string link = "")
    {
        if (Text == text && _color == color && _glyph == glyph && Link.Text == link)
        {
            return;
        }

        Text = text;
        AccessibleName = text;
        _color = color;
        _glyph = glyph;
        Link.Text = link;
        Link.Visible = link.Length > 0;
        PlaceLink();
        Invalidate();
    }

    public void ApplyTheme(UiTheme theme, UiFonts fonts, float scale)
    {
        _theme = theme;
        _fonts = fonts;
        _scale = scale;
        BackColor = theme.Card;
        Link.ApplyTheme(theme, fonts, scale);
        Height = Math.Max(Link.PreferredSize.Height, fonts.Caption.Height + D(4));
        PlaceLink();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PlaceLink();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_theme is null || _fonts is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        Color color = _color ?? _theme.SecondaryText;
        int x = 0;
        if (_glyph.Length > 0 && Glyphs.Family is not null)
        {
            Glyphs.Draw(g, _glyph, new Rectangle(0, 0, D(14), Height), color, 12 * _scale, _theme.Card);
            x = D(18);
        }

        TextRenderer.DrawText(g, Text, _fonts.Caption, new Rectangle(x, 0, Math.Max(0, TextEnd() - x), Height), color, _theme.Card,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private int TextEnd() => Link.Visible ? Link.Left - D(4) : Width;

    private void PlaceLink()
    {
        if (_fonts is null || !Link.Visible)
        {
            return;
        }

        int x = (_glyph.Length > 0 && Glyphs.Family is not null ? D(18) : 0)
            + TextRenderer.MeasureText(Text, _fonts.Caption, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + D(4);
        Link.Size = Link.PreferredSize;
        Link.Location = new Point(Math.Min(x, Math.Max(0, Width - Link.Width)), (Height - Link.Height) / 2);
    }

    private int D(float dip) => (int)Math.Round(dip * _scale);
}

/// <summary>
/// A drop-down list in the window's own colours (also in dark mode): a rounded frame, a chevron, long names shortened
/// by "…" and shown whole in the open list, and an accent line while it has the focus. Turning the mouse wheel over
/// it while it is closed does not change the choice.
/// </summary>
internal sealed class ThemedComboBox : ComboBox, IThemed
{
    private const int WmPaint = 0x000F;
    private const int WmPrintClient = 0x0318;
    private UiTheme? _theme;
    private UiFonts? _fonts;
    private float _scale = 1;

    public ThemedComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
    }

    public void ApplyTheme(UiTheme theme, UiFonts fonts, float scale)
    {
        _theme = theme;
        _fonts = fonts;
        _scale = scale;
        Font = fonts.Body;
        BackColor = theme.Control;
        ForeColor = theme.Text;
        ItemHeight = Math.Max(fonts.Body.Height + 2, (int)Math.Round(32 * scale) - 6);
        FitDropDown();
        Invalidate();
    }

    /// <summary>The open list is wide enough for the longest name.</summary>
    public void FitDropDown()
    {
        if (_fonts is null)
        {
            return;
        }

        int widest = Items.Cast<object>().Select(i => TextRenderer.MeasureText(GetItemText(i), _fonts.Body).Width).DefaultIfEmpty(0).Max();
        int scrollbar = SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        int limit = Screen.FromControl(this).WorkingArea.Width / 2;
        DropDownWidth = Math.Clamp(widest + scrollbar + (int)Math.Round(16 * _scale), Width, Math.Max(Width, limit));
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (_theme is null || !IsHandleCreated)
        {
            return;
        }

        if (m.Msg == WmPaint)
        {
            using Graphics g = Graphics.FromHwnd(Handle);
            PaintFrame(g);
        }
        else if (m.Msg == WmPrintClient && m.WParam != IntPtr.Zero)
        {
            using Graphics g = Graphics.FromHdc(m.WParam);
            PaintFrame(g);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!DroppedDown && e is HandledMouseEventArgs handled)
        {
            // Scrolling past the list must not switch the microphone.
            handled.Handled = true;
            return;
        }

        base.OnMouseWheel(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (_theme is null || _fonts is null || e.Index < 0)
        {
            base.OnDrawItem(e);
            return;
        }

        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool selected = !edit && (e.State & DrawItemState.Selected) != 0;
        Color back = selected ? UiTheme.Blend(_theme.Accent, _theme.Control, 0.18) : _theme.Control;
        using (var brush = new SolidBrush(back))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        Rectangle text = Rectangle.Inflate(e.Bounds, -D(6), 0);
        if (edit)
        {
            // Not under the chevron drawn over the native button.
            text.Width = Math.Max(0, Math.Min(text.Width, Width - ButtonWidth() - D(4) - text.Left));
        }

        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), _fonts.Body, text, Enabled ? _theme.Text : _theme.DisabledText, back,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private int ButtonWidth() => D(32);

    /// <summary>Over the native frame and button: a rounded frame, the chevron, and the focus line.</summary>
    private void PaintFrame(Graphics g)
    {
        UiTheme theme = _theme!;
        Rectangle r = ClientRectangle;
        int border = Math.Max(1, (int)Math.Round(_scale));
        int button = ButtonWidth();
        using (var fill = new SolidBrush(theme.Control))
        {
            g.FillRectangle(fill, r.Right - button - border, border, button, r.Height - (2 * border));
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(border / 2f, border / 2f, r.Width - border - 0.5f, r.Height - border - 0.5f);
        using (GraphicsPath shape = Glyphs.Rounded(bounds, D(4)))
        using (var outside = new Region(new Rectangle(Point.Empty, r.Size)))
        {
            outside.Exclude(shape);
            using (var corners = new SolidBrush(Parent?.BackColor ?? theme.Card))
            {
                g.FillRegion(corners, outside);
            }

            using var pen = new Pen(Enabled ? theme.ControlBorder : theme.DisabledText, border);
            g.DrawPath(pen, shape);
        }

        g.SmoothingMode = SmoothingMode.None;
        Glyphs.Draw(g, Glyphs.ChevronDown, new Rectangle(r.Right - button, 0, button, r.Height), Enabled ? theme.SecondaryText : theme.DisabledText, 12 * _scale);
        if (Focused || DroppedDown)
        {
            int line = Math.Max(2, (int)Math.Round(2 * _scale));
            using var accent = new SolidBrush(theme.Accent);
            g.FillRectangle(accent, D(3), r.Height - line - border, r.Width - D(6), line);
        }
    }

    private int D(float dip) => (int)Math.Round(dip * _scale);
}

/// <summary>A Windows 11 text field around a text box: rounded, in the control colour, with an accent line under it while it has the focus.</summary>
internal sealed class FieldHost : Panel, IThemed
{
    private readonly TextBox _box;
    private UiTheme? _theme;
    private float _scale = 1;

    public FieldHost(TextBox box)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _box = box;
        _box.BorderStyle = BorderStyle.None;
        Controls.Add(_box);
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        Click += (_, _) => _box.Focus();
    }

    public void ApplyTheme(UiTheme theme, UiFonts fonts, float scale)
    {
        _theme = theme;
        _scale = scale;
        BackColor = theme.Card;
        _box.Font = fonts.Body;
        _box.BackColor = theme.Control;
        _box.ForeColor = theme.Text;
        Height = Math.Max((int)Math.Round(32 * scale), _box.PreferredHeight + (int)Math.Round(10 * scale));
        PlaceBox();
        Invalidate();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        PlaceBox();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_theme is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float border = Math.Max(1, (int)Math.Round(_scale));
        var bounds = new RectangleF(border / 2, border / 2, Width - border - 0.5f, Height - border - 0.5f);
        using (GraphicsPath shape = Glyphs.Rounded(bounds, (int)Math.Round(4 * _scale)))
        using (var fill = new SolidBrush(_theme.Control))
        using (var pen = new Pen(_theme.ControlBorder, border))
        {
            g.FillPath(fill, shape);
            g.DrawPath(pen, shape);
        }

        if (_box.Focused)
        {
            float line = Math.Max(2, 2 * _scale);
            using var accent = new SolidBrush(_theme.Accent);
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(accent, (int)Math.Round(3 * _scale), Height - line - border, Width - (int)Math.Round(6 * _scale), line);
        }
    }

    private void PlaceBox()
    {
        int side = (int)Math.Round(10 * _scale);
        _box.Width = Math.Max(10, Width - (2 * side));
        _box.Location = new Point(side, (Height - _box.Height) / 2);
    }
}

/// <summary>A Windows 11 toggle switch with "On"/"Off" beside it. Click or Space switches it.</summary>
internal sealed class ToggleSwitch : Control, IThemed
{
    private UiTheme? _theme;
    private UiFonts? _fonts;
    private float _scale = 1;
    private bool _checked;
    private bool _hover;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        Cursor = Cursors.Hand;
    }

    public event EventHandler? CheckedChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            AccessibleDescription = value ? "On" : "Off";
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyTheme(UiTheme theme, UiFonts fonts, float scale)
    {
        _theme = theme;
        _fonts = fonts;
        _scale = scale;
        BackColor = theme.Card;
        Size = new Size(TextRenderer.MeasureText("Off", fonts.Body).Width + D(12) + D(44), Math.Max(D(28), fonts.Body.Height));
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Checked = !Checked;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_theme is null || _fonts is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        var track = new RectangleF(Width - D(42) + 0.5f, ((Height - D(20)) / 2f) + 0.5f, D(40) - 1, D(20) - 1);
        TextRenderer.DrawText(g, _checked ? "On" : "Off", _fonts.Body, new Rectangle(0, 0, (int)track.Left - D(12), Height), _theme.Text, _theme.Card,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath shape = Glyphs.Rounded(track, track.Height / 2);
        if (_checked)
        {
            using var fill = new SolidBrush(_hover ? UiTheme.Blend(_theme.Accent, _theme.Card, 0.9) : _theme.Accent);
            g.FillPath(fill, shape);
        }
        else
        {
            using var fill = new SolidBrush(_hover ? _theme.ControlHover : _theme.ControlPressed);
            using var pen = new Pen(_theme.Rail, Math.Max(1, (int)Math.Round(_scale)));
            g.FillPath(fill, shape);
            g.DrawPath(pen, shape);
        }

        float knob = (_hover ? 14 : 12) * _scale;
        float centerY = track.Top + (track.Height / 2);
        float centerX = _checked ? track.Right - (track.Height / 2) : track.Left + (track.Height / 2);
        using (var brush = new SolidBrush(_checked ? _theme.AccentText : _theme.SecondaryText))
        {
            g.FillEllipse(brush, centerX - (knob / 2), centerY - (knob / 2), knob, knob);
        }

        if (Focused && ShowFocusCues)
        {
            RectangleF ring = RectangleF.Inflate(track, 3 * _scale, 3 * _scale);
            using GraphicsPath focus = Glyphs.Rounded(ring, ring.Height / 2);
            using var pen = new Pen(_theme.Text, Math.Max(1, 2 * _scale));
            g.DrawPath(pen, focus);
        }
    }

    private int D(float dip) => (int)Math.Round(dip * _scale);
}

/// <summary>How a <see cref="PillButton"/> looks.</summary>
internal enum PillStyle
{
    /// <summary>A plain button (Cancel, Change…).</summary>
    Neutral,

    /// <summary>The main action (Save), filled with the accent colour.</summary>
    Accent,

    /// <summary>A text link in the link colour.</summary>
    Link,

    /// <summary>A square button with only an icon (the test sound).</summary>
    Icon,
}

/// <summary>A Windows 11 button: rounded, in the window's own colours, with hover, pressed, disabled and keyboard focus states.</summary>
internal sealed class PillButton : Control, IButtonControl, IThemed
{
    private UiTheme? _theme;
    private UiFonts? _fonts;
    private float _scale = 1;
    private bool _hover;
    private bool _pressed;

    public PillButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PillStyle Style { get; set; }

    /// <summary>An icon (a <see cref="Glyphs"/> character): before the text, or alone for <see cref="PillStyle.Icon"/>.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = "";

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DialogResult DialogResult { get; set; }

    /// <summary>Windows makes the focused button the default one; that is shown by the focus ring alone.</summary>
    public void NotifyDefault(bool value)
    {
    }

    public void PerformClick()
    {
        if (Enabled && Visible)
        {
            OnClick(EventArgs.Empty);
        }
    }

    public void ApplyTheme(UiTheme theme, UiFonts fonts, float scale)
    {
        _theme = theme;
        _fonts = fonts;
        _scale = scale;
        BackColor = theme.Card;
        Size = PreferredSize;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (_fonts is null)
        {
            return base.GetPreferredSize(proposedSize);
        }

        if (Style == PillStyle.Icon)
        {
            return new Size(D(32), D(32));
        }

        Size text = TextRenderer.MeasureText(Text, Style == PillStyle.Link ? _fonts.Caption : _fonts.Body, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (Style == PillStyle.Link)
        {
            return new Size(text.Width + D(8), Math.Max(D(20), text.Height + D(4)));
        }

        int glyph = Glyph.Length > 0 && Glyphs.Family is not null ? D(16) + D(8) : 0;
        return new Size(Math.Max(D(96), text.Width + glyph + D(24)), Math.Max(D(32), text.Height + D(12)));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName ??= Text;
        if (_fonts is not null)
        {
            Size = PreferredSize;
        }

        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        if (FindForm() is { } form && DialogResult != DialogResult.None && form.Modal)
        {
            form.DialogResult = DialogResult;
        }

        base.OnClick(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            PerformClick();
            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            PerformClick();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) == Keys.Enter || base.IsInputKey(keyData);

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && Enabled)
        {
            Focus();
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled && Style == PillStyle.Link ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var page = new SolidBrush(Parent?.BackColor ?? BackColor);
        e.Graphics.FillRectangle(page, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_theme is null || _fonts is null)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color back = Parent?.BackColor ?? _theme.Card;
        float border = Math.Max(1, (int)Math.Round(_scale));
        var bounds = new RectangleF(border / 2, border / 2, Width - border - 0.5f, Height - border - 0.5f);
        int radius = (int)Math.Round(4 * _scale);
        Color text;
        switch (Style)
        {
            case PillStyle.Link:
                text = !Enabled ? _theme.DisabledText : _pressed ? UiTheme.Blend(_theme.Link, back, 0.7) : _theme.Link;
                break;
            case PillStyle.Icon:
                if (_hover || _pressed)
                {
                    using GraphicsPath shape = Glyphs.Rounded(bounds, radius);
                    using var brush = new SolidBrush(_pressed ? _theme.ControlPressed : _theme.ControlHover);
                    g.FillPath(brush, shape);
                }

                text = Enabled ? _theme.Text : _theme.DisabledText;
                back = _hover || _pressed ? (_pressed ? _theme.ControlPressed : _theme.ControlHover) : back;
                break;
            default:
            {
                bool accent = Style == PillStyle.Accent;
                Color fill = accent
                    ? (!Enabled ? (_theme.Dark ? Color.FromArgb(0x43, 0x43, 0x43) : Color.FromArgb(0xBF, 0xBF, 0xBF))
                        : _pressed ? UiTheme.Blend(_theme.Accent, back, 0.8) : _hover ? UiTheme.Blend(_theme.Accent, back, 0.9) : _theme.Accent)
                    : (_pressed ? _theme.ControlPressed : _hover ? _theme.ControlHover : _theme.Control);
                using (GraphicsPath shape = Glyphs.Rounded(bounds, radius))
                using (var brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, shape);
                    if (!accent)
                    {
                        using var pen = new Pen(_theme.ControlBorder, border);
                        g.DrawPath(pen, shape);
                    }
                }

                text = accent ? (Enabled ? _theme.AccentText : (_theme.Dark ? Color.FromArgb(0xA0, 0xA0, 0xA0) : Color.White))
                    : !Enabled ? _theme.DisabledText : _pressed ? _theme.SecondaryText : _theme.Text;
                back = fill;
                break;
            }
        }

        Font font = Style == PillStyle.Link ? _fonts.Caption : _fonts.Body;
        if (Style == PillStyle.Icon)
        {
            Glyphs.Draw(g, Glyph, new Rectangle(0, 0, Width, Height), text, 16 * _scale, back);
        }
        else
        {
            int glyphWidth = Glyph.Length > 0 && Glyphs.Family is not null ? D(16) + D(8) : 0;
            Size measured = TextRenderer.MeasureText(g, Text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int x = Style == PillStyle.Link ? D(4) : (Width - glyphWidth - measured.Width) / 2;
            if (glyphWidth > 0)
            {
                Glyphs.Draw(g, Glyph, new Rectangle(x, 0, D(16), Height), text, 14 * _scale, back);
            }

            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (Style == PillStyle.Link && _hover && Enabled)
            {
                using var underline = new Font(font, FontStyle.Underline);
                TextRenderer.DrawText(g, Text, underline, new Rectangle(x + glyphWidth, 0, measured.Width + D(2), Height), text, back, flags);
            }
            else
            {
                TextRenderer.DrawText(g, Text, font, new Rectangle(x + glyphWidth, 0, measured.Width + D(2), Height), text, back, flags);
            }
        }

        if (Focused && ShowFocusCues)
        {
            using GraphicsPath focus = Glyphs.Rounded(new RectangleF(border / 2, border / 2, Width - border - 0.5f, Height - border - 0.5f), radius + D(2));
            using var pen = new Pen(_theme.Text, Math.Max(1, 2 * _scale));
            g.DrawPath(pen, focus);
        }
    }

    private int D(float dip) => (int)Math.Round(dip * _scale);
}
