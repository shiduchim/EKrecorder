using EKrecorder.Monitors;

namespace EKrecorder.Overlays;

/// <summary>
/// Test-only control marker. A small square on the bottom edge, just left of the indicator, that is deliberately NOT
/// excluded from capture and switches between two colours five times a second. It keeps frames coming even when the
/// screen is still, and when it shows up in the captured frames it proves that the capture does see topmost windows
/// in that corner, so a missing triangle there really means the exclusion worked.
/// </summary>
internal sealed class CaptureProbe : IDisposable
{
    public static readonly Color ColorA = Color.FromArgb(0xFF, 0x00, 0xFF); // magenta
    public static readonly Color ColorB = Color.FromArgb(0x00, 0xD0, 0x00); // green

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private OverlayWindow? _window;
    private OverlayBitmap? _colorA;
    private OverlayBitmap? _colorB;
    private bool _showingB;

    public CaptureProbe() => _timer.Tick += (_, _) => Toggle();

    public Rectangle Bounds { get; private set; }

    public bool IsShown => _window?.IsShown == true;

    /// <summary>Same size as the indicator, one indicator-width of gap to its left.</summary>
    public static Rectangle BoundsFor(Rectangle monitorBounds, int indicatorSize) =>
        new(monitorBounds.Right - (3 * indicatorSize), monitorBounds.Bottom - indicatorSize, indicatorSize, indicatorSize);

    public bool Show(MonitorInfo monitor)
    {
        int size = RecordingIndicator.SizeFor(monitor.Dpi);
        Bounds = BoundsFor(monitor.Bounds, size);
        _colorA = OverlayArt.Solid(size, size, ColorA);
        _colorB = OverlayArt.Solid(size, size, ColorB);
        _window = OverlayWindow.Create("capture probe (control, NOT excluded)", Bounds, excludeFromCapture: false);
        if (!_window.SetContent(_colorA, Bounds.Location) || !_window.ShowNoActivate())
        {
            return false;
        }

        _timer.Start();
        return true;
    }

    public void KeepOnTop() => _window?.KeepOnTop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _window?.Dispose();
        _window = null;
    }

    private void Toggle()
    {
        if (_window is null || _colorA is null || _colorB is null)
        {
            return;
        }

        _showingB = !_showingB;
        _window.SetContent(_showingB ? _colorB : _colorA, Bounds.Location, logSuccess: false);
    }
}
