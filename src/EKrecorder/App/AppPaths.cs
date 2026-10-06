namespace EKrecorder.App;

/// <summary>
/// Where EKrecorder keeps its things. Everything of its own is under %LocalAppData%\EKrecorder: settings.json, the
/// logs, the reports, and InProgress, where a recording is written while it runs (never straight into the Desktop or
/// another folder a sync program might lock). Finished recordings go to the recordings folder, by default
/// Desktop\EKrecordings.
/// </summary>
internal sealed record AppPaths(string Root, string DefaultRecordings)
{
    public string InProgress => Path.Combine(Root, "InProgress");

    /// <summary>Files that held nothing playable after a crash are kept here, never deleted.</summary>
    public string Unrecoverable => Path.Combine(InProgress, "Unrecoverable");

    public string Logs => Path.Combine(Root, "Logs");

    public string Reports => Path.Combine(Root, "Reports");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public static AppPaths ForUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EKrecorder"),
        DesktopRecordings);

    /// <summary>Everything under one folder (the build machine's self-test).</summary>
    public static AppPaths Under(string root) => new(Path.Combine(root, "data"), Path.Combine(root, "EKrecordings"));

    /// <summary>Desktop\EKrecordings.</summary>
    public static string DesktopRecordings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "EKrecordings");

    /// <summary>The folder recordings go to with these settings.</summary>
    public string RecordingsFolder(AppSettings settings) => settings.RecordingFolder ?? DefaultRecordings;
}
