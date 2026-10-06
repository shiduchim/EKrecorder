using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Native;

namespace EKrecorder.Overlays;

/// <summary>
/// The recording indicator: a tiny blue triangle tucked into the exact bottom-right corner of the recorded monitor,
/// about 12 logical pixels (scaled by that monitor's DPI). It is excluded from capture before it is ever shown, and
/// it is never shown if that exclusion cannot be verified.
/// </summary>
internal sealed class RecordingIndicator : IDisposable
{
    public const int LogicalSize = 12;

    private OverlayWindow? _window;
    private MonitorInfo? _monitor;

    public int SizePx { get; private set; }

    public Rectangle Bounds { get; private set; }

    public bool ExclusionVerified { get; private set; }

    public string ExclusionDetail { get; private set; } = "not attempted";

    public bool IsShown => _window?.IsShown == true;

    public static int SizeFor(uint dpi) => Math.Max(6, (int)Math.Round(LogicalSize * dpi / 96.0));

    public static Rectangle BoundsFor(Rectangle monitorBounds, int size) =>
        new(monitorBounds.Right - size, monitorBounds.Bottom - size, size, size);

    public bool Show(MonitorInfo monitor)
    {
        Dispose();
        _monitor = monitor;
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
        if (!_window.SetContent(OverlayArt.CornerTriangle(SizePx, OverlayArt.IndicatorBlue), Bounds.Location))
        {
            Dispose();
            return false;
        }

        return _window.ShowNoActivate();
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
        _window.SetContent(OverlayArt.CornerTriangle(SizePx, OverlayArt.IndicatorBlue), Bounds.Location);
        Log.Info($"Indicator re-drawn for {dpi} DPI: {SizePx} px at {Bounds}");
    }
}
