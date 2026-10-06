using EKrecorder.Monitors;
using EKrecorder.Overlays;

namespace EKrecorder.Capture;

/// <summary>
/// Where each overlay lands inside a captured frame of one monitor. Frame pixel (0,0) is the monitor's top-left
/// pixel; monitor capture is 1:1 with physical pixels. Uses the same formulas the overlays use to place themselves.
/// </summary>
internal sealed class FrameGeometry
{
    public FrameGeometry(MonitorInfo monitor)
    {
        int size = RecordingIndicator.SizeFor(monitor.Dpi);
        Point origin = monitor.Bounds.Location;
        Triangle = ToFrame(RecordingIndicator.BoundsFor(monitor.Bounds, size), origin);
        Probe = ToFrame(CaptureProbe.BoundsFor(monitor.Bounds, size), origin);
        Identify = ToFrame(IdentifyOverlays.BoundsFor(monitor), origin);
        int corner = Math.Max(48, 4 * size);
        Corner = new Rectangle(monitor.Bounds.Width - corner, monitor.Bounds.Height - corner, corner, corner);

        // Pixels the triangle covers completely; its anti-aliased slanted edge is left out.
        var interior = new List<Point>();
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (x + y >= size)
                {
                    interior.Add(new Point(Triangle.X + x, Triangle.Y + y));
                }
            }
        }

        TriangleInterior = interior.ToArray();
    }

    public Rectangle Triangle { get; }

    public Rectangle Probe { get; }

    public Rectangle Identify { get; }

    /// <summary>The bottom-right square saved as an enlarged picture: holds the triangle and the control marker.</summary>
    public Rectangle Corner { get; }

    public Point[] TriangleInterior { get; }

    private static Rectangle ToFrame(Rectangle screen, Point origin) =>
        new(screen.X - origin.X, screen.Y - origin.Y, screen.Width, screen.Height);
}
