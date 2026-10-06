using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Native;

namespace EKrecorder.Overlays;

/// <summary>
/// The recording indicator: a tiny triangle tucked into the exact bottom-right corner of the recorded monitor, about
/// 12 logical pixels (scaled by that monitor's DPI). Blue while all is well, orange when the recording goes on but
/// needs attention. It is excluded from capture before it is ever shown, and it is never shown if that exclusion
/// cannot be verified. Click-through, never activated, not in Alt+Tab or the taskbar.
/// </summary>
internal sealed class RecordingIndicator : IDisposable
{
    public const int LogicalSize = 12;

    private OverlayWindow? _window;
    private MonitorInfo? _monitor;
    private bool _attention;

    public int SizePx { get; private set; }

    public Rectangle Bounds { get; private set; }

    public bool ExclusionVerified { get; private set; }

    public string ExclusionDetail { get; private set; } = "not attempted";

    public bool IsShown => _window?.IsShown == true;

    /// <summary>The monitor the triangle is on now.</summary>
    public MonitorInfo? Monitor => _monitor;

    public static int SizeFor(uint dpi) => Math.Max(6, (int)Math.Round(LogicalSize * dpi / 96.0));

    public static Rectangle BoundsFor(Rectangle monitorBounds, int size) =>
        new(monitorBounds.Right - size, monitorBounds.Bottom - size, size, size);

    private Color CurrentColor => _attention ? OverlayArt.IndicatorOrange : OverlayArt.IndicatorBlue;

    public bool Show(MonitorInfo monitor, bool attention = false)
    {
        Dispose();
        _monitor = monitor;
        _attention = attention;
        SizePx = SizeFor(monitor.Dpi);
        Bounds = BoundsFor(monitor.Bounds, SizePx);
        Log.Decision($"Indicator: {LogicalSize} logical px at {monitor.ScalePercent}% = {SizePx} physical px, at {Bounds} (bottom-right corner of {monitor.Name}).");

        _window = OverlayWindow.Create("recording indicator", Bounds, excludeFromCapture: true);
        ExclusionVerified = _window.ExclusionVerified;
        ExclusionDetail = _window.ExclusionDetail;
        if (!ExclusionVerified)
        {
            Log.Decision("The recording indicator is NOT displayed: it could not be excluded from capture.");
            Dispose();
            return false;
        }

        _window.DpiChanged += OnDpiChanged;
        if (!_window.SetContent(OverlayArt.CornerTriangle(SizePx, CurrentColor), Bounds.Location))
        {
            Dispose();
            return false;
        }

        return _window.ShowNoActivate();
    }

    /// <summary>Blue (false) or orange (true).</summary>
    public void SetAttention(bool attention)
    {
        if (attention == _attention)
        {
            return;
        }

        _attention = attention;
        _window?.SetContent(OverlayArt.CornerTriangle(SizePx, CurrentColor), Bounds.Location, logSuccess: false);
        Log.Info($"Indicator is now {(attention ? "orange (needs attention)" : "blue")}.");
    }

    /// <summary>Puts the triangle in the corner of <paramref name="monitor"/> (moved, resized, or another monitor).</summary>
    public void MoveTo(MonitorInfo monitor)
    {
        if (_window is null)
        {
            return;
        }

        int size = SizeFor(monitor.Dpi);
        Rectangle bounds = BoundsFor(monitor.Bounds, size);
        _monitor = monitor;
        if (bounds == Bounds)
        {
            return;
        }

        SizePx = size;
        Bounds = bounds;
        _window.SetContent(OverlayArt.CornerTriangle(SizePx, CurrentColor), Bounds.Location);
        KeepOnTop();
        Log.Info($"Indicator moved to {Bounds} ({monitor.Name}).");
    }

    public void KeepOnTop() => _window?.KeepOnTop();

    public void Dispose()
    {
        if (_window is not null)
        {
            _window.DpiChanged -= OnDpiChanged;
            _window.Dispose();
            _window = null;
        }
    }

    private void OnDpiChanged(object? sender, EventArgs e)
    {
        if (_window is null || _monitor is null)
        {
            return;
        }

        // The monitor's scale changed while the indicator was up: keep 12 logical pixels in the same corner.
        uint dpi = Win32.GetDpiForWindow(_window.Handle);
        SizePx = SizeFor(dpi);
        Bounds = BoundsFor(_monitor.Bounds, SizePx);
        _window.SetContent(OverlayArt.CornerTriangle(SizePx, CurrentColor), Bounds.Location);
        Log.Info($"Indicator re-drawn for {dpi} DPI: {SizePx} px at {Bounds}");
    }
}
