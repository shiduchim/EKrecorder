using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace EKrecorder.Shell;

/// <summary>A control the Settings window draws in its own colours and at its own scale.</summary>
internal interface IThemed
{
    void ApplyTheme(UiTheme theme, UiFonts fonts, float scale);
}

/// <summary>
/// One setting, as a Windows 11 Settings card: an icon, a title, a one-line description, and the setting's
/// controls on the right (child controls, placed by the window). The description is shortened with "…" when it does
/// not fit; the whole text is then in its tooltip.
/// </summary>
internal sealed class Card : Panel, IThemed
{
    private readonly ToolTip _tip = new() { InitialDelay = 400 };
    private UiTheme? _theme;
    private UiFonts? _fonts;
    private float _scale = 1;
    private string _glyph = "";
    private string _title = "";
    private string _subtitle = "";
    private Color? _subtitleColor;
    private bool _pathEllipsis;
    private int _textRight;
    private int _textTop = -1;
    private string? _shownTip;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Invalidate();
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
            _subtitleColor = value;
            Invalidate();
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

    /// <summary>The title's top (-1: the title and description are centred in the card's first row).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TextTop
    {
        get => _textTop;
        set
        {
            _textTop = value;
            Invalidate();
        }
    }

    /// <summary>The left edge of the text column (after the icon).</summary>
    public int TextLeft => D(52);

    /// <summary>The height of the title and description together.</summary>
    public int TextHeight => _fonts is null ? 0 : _fonts.Body.Height + D(2) + _fonts.Caption.Height;

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
        float radius = 6 * _scale;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (GraphicsPath shape = Glyphs.Rounded(bounds, radius))
        using (var fill = new SolidBrush(_theme.Card))
        using (var border = new Pen(_theme.CardBorder, Math.Max(1, (int)_scale)))
        {
            g.FillPath(fill, shape);
            g.DrawPath(border, shape);
        }

        g.SmoothingMode = SmoothingMode.None;
        int top = _textTop >= 0 ? _textTop : Math.Max(D(8), (Math.Min(Height, D(68)) - TextHeight) / 2);
        int right = _textRight > 0 ? _textRight : Width - D(16);
        Color text = Enabled ? _theme.Text : _theme.DisabledText;
        Glyphs.Draw(g, _glyph, new Rectangle(D(16), top, D(20), _fonts.Body.Height), text, 18 * _scale);
        var titleBox = new Rectangle(TextLeft, top, Math.Max(0, right - TextLeft), _fonts.Body.Height);
        TextRenderer.DrawText(g, _title, _fonts.Body, titleBox, text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (_subtitle.Length > 0)
        {
            var subtitleBox = new Rectangle(TextLeft, top + _fonts.Body.Height + D(2), titleBox.Width, _fonts.Caption.Height);
            TextFormatFlags flags = (_pathEllipsis ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis) | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, _subtitle, _fonts.Caption, subtitleBox, Enabled ? _subtitleColor ?? _theme.SecondaryText : _theme.DisabledText, flags);
            bool cut = TextRenderer.MeasureText(g, _subtitle, _fonts.Caption, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width > subtitleBox.Width;
            string? tip = cut ? _subtitle : null;
            if (tip != _shownTip)
            {
                _shownTip = tip;
                _tip.SetToolTip(this, tip);
            }
        }
    }

    private int D(float dip) => (int)Math.Round(dip * _scale);
}

/// <summary>
/// The monitors as Windows' Display settings show them: to scale, in their real arrangement, numbered. Click one
/// (or use the arrow keys) to choose it; the chosen one is filled with the accent colour.
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
            Color fill = selected ? _theme.Accent : i == _hover ? _theme.ControlHover : _theme.Control;
            using (GraphicsPath shape = Glyphs.Rounded(box, 4 * _scale))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, shape);
                if (!selected)
                {
                    using var border = new Pen(_theme.ControlBorder, Math.Max(1, _scale));
                    g.DrawPath(border, shape);
                }
            }

            if (selected && Focused && ShowFocusCues)
            {
                RectangleF ring = RectangleF.Inflate(box, 3 * _scale, 3 * _scale);
                using GraphicsPath focus = Glyphs.Rounded(ring, 6 * _scale);
                using var pen = new Pen(_theme.Text, Math.Max(1, 2 * _scale));
                g.DrawPath(pen, focus);
            }

            Color text = selected ? _theme.AccentText : _theme.Text;
            TextRenderer.DrawText(g, monitor.Number, _fonts.Strong, Rectangle.Round(box), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
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
                offsetX + ((b.X - all.X) * fit) + gap,
                offsetY + ((b.Y - all.Y) * fit) + gap,
                Math.Max(2, (b.Width * fit) - (2 * gap)),
                Math.Max(2, (b.Height * fit) - (2 * gap))));
        }

        return boxes;
    }

    /// <summary>One monitor: its id, its number, where it is on the desktop, and its name and size for the tooltip.</summary>
    internal sealed record Choice(string Id, string Number, Rectangle Bounds, string Description);
}

/// <summary>
/// A live level meter: a thin rounded bar that follows the loudest sample of each moment (−60 to 0 dBFS),
/// rising at once and falling back smoothly. The last stretch before clipping is drawn in the warning colour.
/// </summary>
internal sealed class LevelMeter : Control, IThemed
{
    private const float FloorDb = -60f;
    private UiTheme? _theme;
    private float _scale = 1;
    private float _shown;
    private bool _active;

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    /// <summary>False while no device is captured (the bar is empty and dimmed).</summary>
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
            if (!value)
            {
                _shown = 0;
            }

            Invalidate();
        }
    }

    /// <summary>The loudest sample since the last call (0 to 1 of full scale); called about 30 times a second.</summary>
    public void Push(float peak)
    {
        float db = peak > 0 ? 20f * MathF.Log10(peak) : FloorDb;
        float fraction = Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);
        float shown = Math.Max(fraction, _shown - (1.5f / -FloorDb));
        if (Math.Abs(shown - _shown) > 0.001f)
        {
            _shown = shown;
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
        var track = new RectangleF(0, 0, Width, Height);
        float radius = Height / 2f;
        using (GraphicsPath shape = Glyphs.Rounded(track, radius))
        using (var brush = new SolidBrush(_active ? _theme.Track : UiTheme.Blend(_theme.Track, _theme.Card, 0.5)))
        {
            g.FillPath(brush, shape);
        }

        float width = Width * _shown;
        if (width >= 1)
        {
            float warnFrom = Width * ((-6f - FloorDb) / -FloorDb);
            using GraphicsPath level = Glyphs.Rounded(new RectangleF(0, 0, Math.Max(width, Height), Height), radius);
            using var accent = new SolidBrush(_theme.Accent);
            g.FillPath(accent, level);
            if (width > warnFrom)
            {
                g.SetClip(new RectangleF(warnFrom, 0, width - warnFrom, Height));
                using var warm = new SolidBrush(_theme.Dark ? Color.FromArgb(0xFF, 0xB9, 0x00) : Color.FromArgb(0xE0, 0x8A, 0x00));
                g.FillPath(warm, level);
                g.ResetClip();
            }
        }
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
        Size = new Size(TextRenderer.MeasureText("Off", fonts.Body).Width + D(12) + D(40), Math.Max(D(20), fonts.Body.Height));
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            e.Handled = true;
        }

        base.OnKeyDown(e);
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
        var track = new RectangleF(Width - D(40) + 0.5f, ((Height - D(20)) / 2f) + 0.5f, D(40) - 1, D(20) - 1);
        TextRenderer.DrawText(g, _checked ? "On" : "Off", _fonts.Body, new Rectangle(0, 0, (int)track.Left - D(12), Height), _theme.Text,
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
            using var fill = new SolidBrush(_hover ? _theme.ControlHover : _theme.Card);
            using var pen = new Pen(_theme.Rail, Math.Max(1, _scale));
            g.FillPath(fill, shape);
            g.DrawPath(pen, shape);
        }

        float knob = (_hover ? 14 : 12) * _scale;
        float centerY = track.Top + (track.Height / 2);
        float centerX = _checked ? track.Right - (track.Height / 2) : track.Left + (track.Height / 2);
        using (var brush = new SolidBrush(_checked ? _theme.AccentText : _theme.Rail))
        {
            g.FillEllipse(brush, centerX - (knob / 2), centerY - (knob / 2), knob, knob);
        }

        if (Focused && ShowFocusCues)
        {
            RectangleF ring = RectangleF.Inflate(track, 2.5f * _scale, 2.5f * _scale);
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
    /// <summary>A plain button (Cancel, Browse…).</summary>
    Neutral,

    /// <summary>The main action (Save), filled with the accent colour.</summary>
    Accent,

    /// <summary>A text link in the accent colour.</summary>
    Link,
}

/// <summary>A Windows 11 button: rounded, in the window's own colours, with hover, pressed and keyboard focus states.</summary>
internal sealed class PillButton : Control, IButtonControl, IThemed
{
    private UiTheme? _theme;
    private UiFonts? _fonts;
    private float _scale = 1;
    private bool _hover;
    private bool _pressed;
    private bool _isDefault;

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

    /// <summary>An icon before the text (a <see cref="Glyphs"/> character), or empty.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = "";

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DialogResult DialogResult { get; set; }

    public void NotifyDefault(bool value)
    {
        _isDefault = value;
        Invalidate();
    }

    public void PerformClick()
    {
        if (CanSelect || Visible)
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

        Size text = TextRenderer.MeasureText(Text, _fonts.Body, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int glyph = Glyph.Length > 0 && Glyphs.Family is not null ? D(16) + D(8) : 0;
        if (Style == PillStyle.Link)
        {
            return new Size(text.Width + glyph + D(8), Math.Max(D(24), text.Height + D(6)));
        }

        return new Size(Math.Max(D(96), text.Width + glyph + D(24)), Math.Max(D(32), text.Height + D(12)));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        if (_fonts is not null)
        {
            Size = PreferredSize;
        }

        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        if (FindForm() is { } form && DialogResult != DialogResult.None && form.Modal)
        {
            form.DialogResult = DialogResult;
        }

        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
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
        if (e.Button == MouseButtons.Left)
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
        var bounds = new RectangleF(1.5f * _scale, 1.5f * _scale, Width - (3 * _scale) - 1, Height - (3 * _scale) - 1);
        Color text;
        if (Style == PillStyle.Link)
        {
            text = !Enabled ? _theme.DisabledText : _pressed ? UiTheme.Blend(_theme.Accent, _theme.Card, 0.7) : _theme.Accent;
        }
        else
        {
            bool accent = Style == PillStyle.Accent && Enabled;
            Color fill = accent
                ? (_pressed ? UiTheme.Blend(_theme.Accent, _theme.Card, 0.8) : _hover ? UiTheme.Blend(_theme.Accent, _theme.Card, 0.9) : _theme.Accent)
                : (_pressed ? _theme.ControlPressed : _hover ? _theme.ControlHover : _theme.Control);
            using GraphicsPath shape = Glyphs.Rounded(bounds, 4 * _scale);
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, shape);
            }

            if (!accent)
            {
                using var pen = new Pen(_isDefault && Enabled ? _theme.Accent : _theme.ControlBorder, Math.Max(1, _scale));
                g.DrawPath(pen, shape);
            }

            text = !Enabled ? _theme.DisabledText : accent ? _theme.AccentText : _pressed ? _theme.SecondaryText : _theme.Text;
        }

        int glyphWidth = Glyph.Length > 0 && Glyphs.Family is not null ? D(16) + D(8) : 0;
        Size measured = TextRenderer.MeasureText(g, Text, _fonts.Body, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int contentWidth = glyphWidth + measured.Width;
        int x = Style == PillStyle.Link ? D(4) : (Width - contentWidth) / 2;
        if (glyphWidth > 0)
        {
            Glyphs.Draw(g, Glyph, new Rectangle(x, 0, D(16), Height), text, 14 * _scale);
        }

        TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (Style == PillStyle.Link && _hover)
        {
            using var underline = new Font(_fonts.Body, FontStyle.Underline);
            TextRenderer.DrawText(g, Text, underline, new Rectangle(x + glyphWidth, 0, measured.Width + D(2), Height), text, flags);
        }
        else
        {
            TextRenderer.DrawText(g, Text, _fonts.Body, new Rectangle(x + glyphWidth, 0, measured.Width + D(2), Height), text, flags);
        }

        if (Focused && ShowFocusCues)
        {
            using GraphicsPath focus = Glyphs.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6 * _scale);
            using var pen = new Pen(_theme.Text, Math.Max(1, 2 * _scale));
            g.DrawPath(pen, focus);
        }
    }

    private int D(float dip) => (int)Math.Round(dip * _scale);
}
