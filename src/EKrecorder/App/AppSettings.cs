using System.Text.Json;
using System.Text.Json.Serialization;
using EKrecorder.Recording;

namespace EKrecorder.App;

/// <summary>
/// Everything EKrecorder remembers, saved as %LocalAppData%\EKrecorder\settings.json. Devices and the monitor are
/// remembered by their stable identity, with the name for when they are not connected.
/// </summary>
internal sealed record AppSettings
{
    public const int CurrentVersion = 1;

    /// <summary>The choices for the maximum recording time, in hours (0 = off).</summary>
    public static IReadOnlyList<int> MaxHoursChoices { get; } = [0, 2, 4, 8, 12];

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The monitor's stable identity (its display-configuration device path); null = the main monitor.</summary>
    public string? MonitorId { get; init; }

    public string? MonitorName { get; init; }

    public int VideoQuality { get; init; } = RecordingQuality.DefaultVideoLevel;

    public int AudioQuality { get; init; } = RecordingQuality.DefaultAudioLevel;

    /// <summary>A pinned microphone; null = Windows' default communications microphone.</summary>
    public string? MicrophoneId { get; init; }

    public string? MicrophoneName { get; init; }

    /// <summary>A pinned output device; null = Windows' default outputs.</summary>
    public string? OutputId { get; init; }

    public string? OutputName { get; init; }

    /// <summary>Where finished recordings go; null = Desktop\EKrecordings.</summary>
    public string? RecordingFolder { get; init; }

    /// <summary>The start/stop shortcut as text ("Ctrl+Alt+R"); empty = none.</summary>
    public string Shortcut { get; init; } = Hotkey.Default.ToSetting();

    public bool StartWithWindows { get; init; } = true;

    /// <summary>0 = off; otherwise a recording stops and is saved after this many hours.</summary>
    public int MaxRecordingHours { get; init; }

    /// <summary>The last recording that was saved (for "Open last recording").</summary>
    public string? LastRecording { get; init; }

    [JsonIgnore]
    public Hotkey Hotkey => Hotkey.TryParse(Shortcut, out Hotkey hotkey) ? hotkey : Hotkey.Default;

    /// <summary>The same settings with every value inside its allowed range.</summary>
    public AppSettings Normalized() => this with
    {
        Version = CurrentVersion,
        VideoQuality = Math.Clamp(VideoQuality, 1, RecordingQuality.Video.Count),
        AudioQuality = Math.Clamp(AudioQuality, 1, RecordingQuality.Audio.Count),
        MaxRecordingHours = MaxHoursChoices.Contains(MaxRecordingHours) ? MaxRecordingHours : 0,
        Shortcut = Hotkey.TryParse(Shortcut, out Hotkey hotkey) ? hotkey.ToSetting() : Hotkey.Default.ToSetting(),
        RecordingFolder = string.IsNullOrWhiteSpace(RecordingFolder) ? null : RecordingFolder,
        MonitorId = string.IsNullOrWhiteSpace(MonitorId) ? null : MonitorId,
        MicrophoneId = string.IsNullOrWhiteSpace(MicrophoneId) ? null : MicrophoneId,
        OutputId = string.IsNullOrWhiteSpace(OutputId) ? null : OutputId,
    };
}

/// <summary>Reads and writes settings.json. A damaged file is kept aside and the defaults are used.</summary>
internal sealed class SettingsStore
{
    public SettingsStore(string path)
    {
        FilePath = path;
    }

    public string FilePath { get; }

    /// <summary>The settings; <c>Existed</c> is false on the first run; <c>Problem</c> says what was wrong with a damaged file.</summary>
    public (AppSettings Settings, bool Existed, string? Problem) Load()
    {
        if (!File.Exists(FilePath))
        {
            return (new AppSettings(), false, null);
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            AppSettings settings = JsonSerializer.Deserialize(json, AppJson.Default.AppSettings)
                ?? throw new JsonException("the file is empty");
            return (settings.Normalized(), true, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string? keptAs = KeepDamagedCopy();
            return (new AppSettings(), true,
                $"settings.json could not be read ({ex.Message}); the defaults are used" + (keptAs is null ? "." : $" and the damaged file was kept as {Path.GetFileName(keptAs)}."));
        }
    }

    /// <summary>Writes the settings (to a temporary file first, so a crash never leaves half a file).</summary>
    public void Save(AppSettings settings)
    {
        string folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);
        string temporary = FilePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings.Normalized(), AppJson.Default.AppSettings);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }

    private string? KeepDamagedCopy()
    {
        try
        {
            string copy = $"{FilePath}.damaged-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(FilePath, copy, overwrite: true);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>JSON for settings.json and the recording journals (compiled, no reflection).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(RecordingJournal))]
internal sealed partial class AppJson : JsonSerializerContext
{
}
