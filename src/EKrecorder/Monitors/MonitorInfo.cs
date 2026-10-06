namespace EKrecorder.Monitors;

/// <summary>
/// One connected monitor. <see cref="Bounds"/> and <see cref="WorkArea"/> are physical pixels in virtual-screen
/// coordinates (the process is PerMonitorV2). <see cref="Number"/> is EKrecorder's own numbering: 1, 2, ... from
/// left to right, which can differ from the numbers in Windows Settings.
/// </summary>
internal sealed record MonitorInfo(
    int Number,
    IntPtr Handle,
    string GdiDeviceName,
    Rectangle Bounds,
    Rectangle WorkArea,
    bool IsPrimary,
    uint Dpi,
    string FriendlyName,
    string StableId)
{
    public int ScalePercent => (int)Math.Round(Dpi * 100.0 / 96.0);

    public string Name => $"Monitor {Number}";

    /// <summary>The text on the monitor's radio button.</summary>
    public string ChoiceLabel =>
        $"{Name}    {Bounds.Width} × {Bounds.Height}, {ScalePercent}%"
        + (IsPrimary ? ", main" : "")
        + (FriendlyName.Length > 0 ? $"    ({FriendlyName})" : "");

    public string Summary =>
        $"{Name}: {Bounds.Width}x{Bounds.Height} at ({Bounds.X},{Bounds.Y}), scale {ScalePercent}% ({Dpi} DPI)"
        + (IsPrimary ? ", main display" : "")
        + (FriendlyName.Length > 0 ? $", \"{FriendlyName}\"" : "");
}
