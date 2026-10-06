using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using EKrecorder.App;

namespace EKrecorder.Shell;

/// <summary>EKrecorder's icons, drawn once: the app icon from the .exe, and the three tray icons.</summary>
internal static class AppIcons
{
    private static readonly Color Blue = Color.FromArgb(0x1E, 0x6B, 0xFF);
    private static readonly Color Orange = Color.FromArgb(0xFF, 0x8A, 0x00);
    private static readonly Color Red = Color.FromArgb(0xE5, 0x39, 0x35);
    private static Icon? _app;
    private static Icon? _idle;
    private static Icon? _recording;
    private static Icon? _attention;

    /// <summary>The icon in EKrecorder.exe (for windows).</summary>
    public static Icon App => _app ??= LoadAppIcon();

    /// <summary>Tray: ready (blue square, white dot).</summary>
    public static Icon Idle => _idle ??= Draw(Blue, recording: false);

    /// <summary>Tray: recording (blue square, white ring, red dot).</summary>
    public static Icon Recording => _recording ??= Draw(Blue, recording: true);

    /// <summary>Tray: recording, needs attention (orange square).</summary>
    public static Icon Attention => _attention ??= Draw(Orange, recording: true);

    private static Icon LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon)
            {
                return icon;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
        }

        return Draw(Blue, recording: true);
    }

    private static Icon Draw(Color background, bool recording)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bitmap = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float inset = size * 0.04f;
            float side = size - (2 * inset);
            using (GraphicsPath square = RoundedRectangle(new RectangleF(inset, inset, side, side), size * 0.22f))
            using (var fill = new SolidBrush(background))
            {
                g.FillPath(fill, square);
            }

            float center = size / 2f;
            if (recording)
            {
                float ring = size * 0.31f;
                using var white = new SolidBrush(Color.White);
                g.FillEllipse(white, center - ring, center - ring, 2 * ring, 2 * ring);
                float dot = size * 0.2f;
                using var red = new SolidBrush(Red);
                g.FillEllipse(red, center - dot, center - dot, 2 * dot, 2 * dot);
            }
            else
            {
                float dot = size * 0.24f;
                using var white = new SolidBrush(Color.White);
                g.FillEllipse(white, center - dot, center - dot, 2 * dot, 2 * dot);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // A copy that owns its own handle, so the one GetHicon made can be destroyed now.
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(2 * radius, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>
/// A slider with a few labelled steps (480p ... 4K, 96 ... 256 kbps). Drawn by itself so it looks the same in
/// light and dark mode. Mouse: click or drag. Keyboard: arrows, Home, End.
/// </summary>
internal sealed class StepSlider : Control
{
    private string[] _labels = [];
    private int _value = 1;
    private bool _dragging;

    public StepSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
    }

    public event EventHandler? ValueChanged;

    /// <summary>The text under each step; their number is the number of steps.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Labels
    {
        get => _labels;
        set
        {
            _labels = value;
            _value = Math.Clamp(_value, 1, Math.Max(1, _labels.Length));
            Invalidate();
        }
    }

    /// <summary>The chosen step, 1 to the number of labels.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, 1, Math.Max(1, _labels.Length));
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            AccessibleDescription = _labels.Length >= clamped ? _labels[clamped - 1] : null;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override Size DefaultSize => new(360, 48);

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Down:
                Value--;
                e.Handled = true;
                break;
            case Keys.Right:
            case Keys.Up:
                Value++;
                e.Handled = true;
                break;
            case Keys.Home:
                Value = 1;
                e.Handled = true;
                break;
            case Keys.End:
                Value = _labels.Length;
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _dragging = true;
            Value = StepAt(e.X);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Value = StepAt(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int steps = Math.Max(1, _labels.Length);
        float trackY = Px(12);
        float thumbRadius = Px(8);
        float trackHeight = Px(4);
        Color accent = Enabled ? SystemColors.Highlight : SystemColors.GrayText;
        Color rail = Blend(SystemColors.ControlText, BackColor, 0.25);

        float first = X(1);
        float last = X(steps);
        using (var railBrush = new SolidBrush(rail))
        using (GraphicsPath track = AppIcons.RoundedRectangle(new RectangleF(first, trackY - (trackHeight / 2), last - first, trackHeight), trackHeight / 2))
        {
            g.FillPath(railBrush, track);
        }

        float selected = X(_value);
        using (var accentBrush = new SolidBrush(accent))
        {
            if (selected > first)
            {
                using GraphicsPath done = AppIcons.RoundedRectangle(new RectangleF(first, trackY - (trackHeight / 2), selected - first, trackHeight), trackHeight / 2);
                g.FillPath(accentBrush, done);
            }

            float tick = Px(3);
            for (int step = 1; step <= steps; step++)
            {
                using var tickBrush = new SolidBrush(step <= _value ? accent : rail);
                g.FillEllipse(tickBrush, X(step) - tick, trackY - tick, 2 * tick, 2 * tick);
            }

            g.FillEllipse(accentBrush, selected - thumbRadius, trackY - thumbRadius, 2 * thumbRadius, 2 * thumbRadius);
        }

        using (var inner = new SolidBrush(BackColor))
        {
            float hole = thumbRadius * 0.45f;
            g.FillEllipse(inner, selected - hole, trackY - hole, 2 * hole, 2 * hole);
        }

        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(accent, Math.Max(1, Px(1.5f))) { DashStyle = DashStyle.Dot };
            float ring = thumbRadius + Px(3);
            g.DrawEllipse(pen, selected - ring, trackY - ring, 2 * ring, 2 * ring);
        }

        using var bold = new Font(Font, FontStyle.Bold);
        for (int step = 1; step <= steps; step++)
        {
            string label = _labels[step - 1];
            bool current = step == _value;
            Font font = current ? bold : Font;
            Size size = TextRenderer.MeasureText(g, label, font);
            int x = (int)Math.Round(X(step) - (size.Width / 2f));
            x = Math.Clamp(x, 0, Math.Max(0, Width - size.Width));
            Color color = !Enabled ? SystemColors.GrayText : current ? SystemColors.ControlText : Blend(SystemColors.ControlText, BackColor, 0.6);
            TextRenderer.DrawText(g, label, font, new Point(x, (int)(trackY + thumbRadius + Px(4))), color, TextFormatFlags.NoPadding);
        }
    }

    private float Px(float logical) => logical * DeviceDpi / 96f;

    /// <summary>The centre of a step; the end steps sit far enough in for their labels.</summary>
    private float X(int step)
    {
        int steps = Math.Max(1, _labels.Length);
        float margin = Px(22);
        return steps == 1 ? Width / 2f : margin + ((Width - (2 * margin)) * (step - 1) / (steps - 1));
    }

    private int StepAt(int x)
    {
        int best = 1;
        float bestDistance = float.MaxValue;
        for (int step = 1; step <= _labels.Length; step++)
        {
            float distance = Math.Abs(X(step) - x);
            if (distance < bestDistance)
            {
                best = step;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static Color Blend(Color a, Color b, double amountOfA) => Color.FromArgb(
        (int)((a.R * amountOfA) + (b.R * (1 - amountOfA))),
        (int)((a.G * amountOfA) + (b.G * (1 - amountOfA))),
        (int)((a.B * amountOfA) + (b.B * (1 - amountOfA))));
}

/// <summary>
/// The shortcut box: click it and press the keys. Modifiers alone show what is held so far; Backspace or Delete
/// clears the shortcut; Tab and Esc keep their usual meaning.
/// </summary>
internal sealed class HotkeyBox : TextBox
{
    private Hotkey _value;

    public HotkeyBox()
    {
        ReadOnly = true;
        TextAlign = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        ShortcutsEnabled = false;
        BackColor = SystemColors.Window;
    }

    /// <summary>A new combination was pressed (the owner checks it).</summary>
    public event EventHandler? ValueChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Hotkey Value
    {
        get => _value;
        set
        {
            _value = value;
            Text = value.ToDisplay();
        }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Text = _value.ToDisplay();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        Keys modifiers = keyData & Keys.Modifiers;
        bool windows = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
        if (modifiers == Keys.None && !windows && key is Keys.Tab or Keys.Escape)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        if (modifiers == Keys.Shift && !windows && key == Keys.Tab)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        HotkeyModifiers held = HotkeyModifiers.None;
        held |= (modifiers & Keys.Control) != 0 ? HotkeyModifiers.Control : 0;
        held |= (modifiers & Keys.Alt) != 0 ? HotkeyModifiers.Alt : 0;
        held |= (modifiers & Keys.Shift) != 0 ? HotkeyModifiers.Shift : 0;
        held |= windows ? HotkeyModifiers.Windows : 0;

        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            // Only modifiers so far: show them and wait for the key.
            string partial = new Hotkey(held, 0x41).ToDisplay();
            Text = held == HotkeyModifiers.None ? "" : partial[..^1] + "…";
            return true;
        }

        if (held == HotkeyModifiers.None && key is Keys.Back or Keys.Delete)
        {
            Value = default;
            ValueChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        Value = new Hotkey(held, (int)key);
        ValueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin && Text.EndsWith('…'))
        {
            // Let go without pressing a key: show the shortcut that is set.
            Text = _value.ToDisplay();
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
