using EKrecorder.Diagnostics;
using EKrecorder.Monitors;

namespace EKrecorder.Overlays;

/// <summary>
/// Shows EKrecorder's number on every monitor for a moment. Each badge is excluded from capture before it is shown;
/// a badge whose exclusion cannot be verified is not shown.
/// </summary>
internal sealed class IdentifyOverlays : IDisposable
{
    public const int LogicalSize = 200;

    private readonly List<(MonitorInfo Monitor, OverlayWindow Window)> _badges = new();

    private IdentifyOverlays()
    {
    }

    public bool AllExcluded => _badges.Count > 0 && _badges.All(b => b.Window.ExclusionVerified);

    public int ShownCount => _badges.Count(b => b.Window.IsShown);

    public static Rectangle BoundsFor(MonitorInfo monitor)
    {
        int size = (int)Math.Round(LogicalSize * monitor.Dpi / 96.0);
        return new Rectangle(
            monitor.Bounds.X + ((monitor.Bounds.Width - size) / 2),
            monitor.Bounds.Y + ((monitor.Bounds.Height - size) / 2),
            size,
            size);
    }

    public static IdentifyOverlays Show(IReadOnlyList<MonitorInfo> monitors)
    {
        var overlays = new IdentifyOverlays();
        foreach (MonitorInfo monitor in monitors)
        {
            Rectangle bounds = BoundsFor(monitor);
            OverlayWindow window = OverlayWindow.Create($"identify {monitor.Name}", bounds, excludeFromCapture: true);
            overlays._badges.Add((monitor, window));
            if (window.ExclusionVerified
                && window.SetContent(OverlayArt.IdentifyBadge(monitor.Number, bounds.Width, monitor.Dpi / 96.0), bounds.Location))
            {
                window.ShowNoActivate();
            }
        }

        Log.Info($"Identify: {overlays.ShownCount} of {overlays._badges.Count} badge(s) shown; all excluded from capture: {overlays.AllExcluded}");
        return overlays;
    }

    public bool IsShownOn(MonitorInfo monitor) =>
        _badges.Any(b => b.Monitor.StableId == monitor.StableId && b.Window.IsShown);

    /// <summary>One line per badge, for the report. Read it before Dispose.</summary>
    public IReadOnlyList<string> Describe() =>
        _badges.Select(b => $"{b.Monitor.Name}: {(b.Window.IsShown ? "shown" : "NOT shown")}; {b.Window.ExclusionDetail}").ToList();

    public void Dispose()
    {
        foreach ((_, OverlayWindow window) in _badges)
        {
            window.Dispose();
        }

        _badges.Clear();
    }
}
