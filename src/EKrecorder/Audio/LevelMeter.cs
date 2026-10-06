namespace EKrecorder.Audio;

/// <summary>
/// A small horizontal level meter: -60 to 0 dBFS, green up to -12 dB, yellow to -3 dB, red above. It shows the peak
/// pushed in, falling back smoothly.
/// </summary>
internal sealed class LevelMeter : Control
{
    private const float FloorDb = -60f;
    private float _shown;

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(140, 12);
        Margin = new Padding(3, 4, 3, 3);
    }

    /// <summary>Shows a new peak (0 to 1 full scale); the bar falls by at most 3 dB per update.</summary>
    public void Push(float peak)
    {
        float db = peak > 0 ? 20f * MathF.Log10(peak) : FloorDb;
        float fraction = Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);
        _shown = Math.Max(fraction, _shown - (3f / -FloorDb));
        Invalidate();
    }

    public void Clear()
    {
        _shown = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle area = ClientRectangle;
        using (var background = new SolidBrush(Enabled ? SystemColors.ControlDark : SystemColors.ControlLight))
        {
            g.FillRectangle(background, area);
        }

        int width = (int)(area.Width * _shown);
        if (width > 0)
        {
            Color color = _shown > (57f / 60f) ? Color.FromArgb(0xD8, 0x3B, 0x2E) : _shown > (48f / 60f) ? Color.FromArgb(0xE0, 0xB0, 0x20) : Color.FromArgb(0x2E, 0xA0, 0x4F);
            using var bar = new SolidBrush(color);
            g.FillRectangle(bar, new Rectangle(area.X, area.Y, width, area.Height));
        }
    }
}
