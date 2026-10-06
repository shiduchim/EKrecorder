namespace EKrecorder;

/// <summary>Where recordings are written while running, where finished ones go, and where reports go.</summary>
internal sealed record RecordingFolders(string InProgress, string Recordings, string Reports)
{
    /// <summary>
    /// %LocalAppData%\EKrecorder\InProgress while recording; Desktop\EKrecordings when finished;
    /// reports in %LocalAppData%\EKrecorder\Reports.
    /// </summary>
    public static RecordingFolders Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EKrecorder", "InProgress"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "EKrecordings"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EKrecorder", "Reports"));

    /// <summary>Everything under one folder (the build machine's self-test).</summary>
    public static RecordingFolders Under(string root) => new(
        Path.Combine(root, "InProgress"),
        Path.Combine(root, "EKrecordings"),
        root);
}
