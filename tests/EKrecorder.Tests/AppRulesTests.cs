using EKrecorder.App;
using EKrecorder.Recording;

namespace EKrecorder.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+R", "Ctrl + Alt + R")]
    [InlineData("Shift+Up", "Shift + Up Arrow")]
    [InlineData("shift + up arrow", "Shift + Up Arrow")]
    [InlineData("F9", "F9")]
    [InlineData("Win+Shift+F12", "Shift + Win + F12")]
    [InlineData("Control+Num5", "Ctrl + Num 5")]
    [InlineData("Alt+PageDown", "Alt + Page Down")]
    [InlineData("Ctrl+7", "Ctrl + 7")]
    public void ShortcutsAreReadAndShown(string text, string display)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey hotkey));
        Assert.Equal(display, hotkey.ToDisplay());
        Assert.True(Hotkey.TryParse(hotkey.ToSetting(), out Hotkey again));
        Assert.Equal(hotkey, again);
    }

    [Theory]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+R+T")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Banana")]
    public void NonsenseIsRefused(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void EmptyMeansNoShortcut()
    {
        Assert.True(Hotkey.TryParse("", out Hotkey hotkey));
        Assert.True(hotkey.IsEmpty);
        Assert.Equal("None", hotkey.ToDisplay());
        Assert.Null(hotkey.Problem());
    }

    [Theory]
    [InlineData("Shift+Up", true)]
    [InlineData("F9", true)]
    [InlineData("Ctrl+Alt+R", true)]
    [InlineData("Win+Shift+S", true)]
    [InlineData("R", false)]
    [InlineData("Shift+R", false)]
    [InlineData("Up", false)]
    [InlineData("Space", false)]
    [InlineData("Alt+Space", true)]
    [InlineData("Pause", true)]
    public void OnlySensibleShortcutsAreAccepted(string text, bool sensible)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey hotkey));
        Assert.Equal(sensible, hotkey.Problem() is null);
    }

    [Fact]
    public void TheDefaultIsCtrlAltR() => Assert.Equal("Ctrl+Alt+R", Hotkey.Default.ToSetting());
}

public sealed class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ekrecorder-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheFirstRunGetsTheRecommendedDefaults()
    {
        var store = new SettingsStore(Path.Combine(_folder, "settings.json"));
        (AppSettings settings, bool existed, string? problem) = store.Load();
        Assert.False(existed);
        Assert.Null(problem);
        Assert.Equal(5, settings.VideoQuality);
        Assert.Equal(2, settings.AudioQuality);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(0, settings.MaxRecordingHours);
        Assert.Equal(Hotkey.Default, settings.Hotkey);
        Assert.Null(settings.RecordingFolder);
        Assert.Null(settings.MicrophoneId);
        Assert.Null(settings.OutputId);
    }

    [Fact]
    public void EverythingIsRemembered()
    {
        var store = new SettingsStore(Path.Combine(_folder, "settings.json"));
        var saved = new AppSettings
        {
            MonitorId = @"\\?\DISPLAY#TEST0000#1&2&3#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            MonitorName = "Test monitor",
            VideoQuality = 5,
            AudioQuality = 4,
            MicrophoneId = "{0.0.1.00000000}.{11111111-2222-3333-4444-555555555555}",
            MicrophoneName = "Test microphone",
            OutputId = "{0.0.0.00000000}.{66666666-7777-8888-9999-000000000000}",
            OutputName = "Test speakers",
            RecordingFolder = Path.Combine(_folder, "videos"),
            Shortcut = "Shift+Up",
            StartWithWindows = false,
            MaxRecordingHours = 8,
            LastRecording = Path.Combine(_folder, "videos", "EKrecording 2026-10-06 20-45-30.mp4"),
        };
        store.Save(saved);

        (AppSettings loaded, bool existed, string? problem) = store.Load();
        Assert.True(existed);
        Assert.Null(problem);
        Assert.Equal(saved, loaded);
        Assert.Equal(new Hotkey(HotkeyModifiers.Shift, 0x26), loaded.Hotkey);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"videoQuality\": \"high\"}")]
    [InlineData("[1,2,3]")]
    public void ADamagedFileFallsBackToDefaultsAndIsKept(string content)
    {
        string path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, content);
        (AppSettings settings, bool existed, string? problem) = new SettingsStore(path).Load();
        Assert.True(existed);
        Assert.NotNull(problem);
        Assert.Equal(new AppSettings(), settings);
        Assert.Single(Directory.GetFiles(_folder, "settings.json.damaged-*"));
    }

    [Fact]
    public void OutOfRangeValuesAreCorrected()
    {
        string path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, "{\"videoQuality\": 9, \"audioQuality\": -3, \"maxRecordingHours\": 5, \"shortcut\": \"Ctrl+Nothing\", \"unknownSetting\": true}");
        (AppSettings settings, _, string? problem) = new SettingsStore(path).Load();
        Assert.Null(problem);
        Assert.Equal(5, settings.VideoQuality);
        Assert.Equal(1, settings.AudioQuality);
        Assert.Equal(0, settings.MaxRecordingHours);
        Assert.Equal("Ctrl+Alt+R", settings.Shortcut);
    }

    [Fact]
    public void JournalsRoundTrip()
    {
        string recording = Path.Combine(_folder, "EKrecording 2026-10-06 20-45-30.mp4");
        var journal = new RecordingJournal { FinalName = "EKrecording 2026-10-06 20-45-30.mp4", FinalFolder = _folder, StartedLocal = new DateTime(2026, 10, 6, 20, 45, 30), ProcessId = 1234 };
        Assert.True(journal.TryWrite(recording));
        Assert.Equal(journal, RecordingJournal.TryRead(recording));
        RecordingJournal.TryDelete(recording);
        Assert.Null(RecordingJournal.TryRead(recording));
    }
}

public sealed class RecordingRulesTests
{
    [Fact]
    public void FileNamesHaveTheStartTime()
    {
        Assert.Equal("EKrecording 2026-10-06 20-45-30.mp4", RecordingNames.For(new DateTime(2026, 10, 6, 20, 45, 30)));
        Assert.Equal("EKrecording 2026-10-06 20-45-30 (recovered).mp4", RecordingNames.Recovered("EKrecording 2026-10-06 20-45-30.mp4"));
        Assert.DoesNotContain(':', RecordingNames.For(DateTime.Now));
    }

    [Fact]
    public void ATakenNameGetsANumber()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ekrecorder-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal(Path.Combine(folder, "a.mp4"), RecordingNames.FreePath(folder, "a.mp4"));
            File.WriteAllText(Path.Combine(folder, "a.mp4"), "");
            File.WriteAllText(Path.Combine(folder, "a (2).mp4"), "");
            Assert.Equal(Path.Combine(folder, "a (3).mp4"), RecordingNames.FreePath(folder, "a.mp4"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(20L * 1024 * 1024 * 1024, 1_000_000, "Ok")]
    [InlineData(4L * 1024 * 1024 * 1024, 1_000_000, "Low")]
    [InlineData(900L * 1024 * 1024, 1_000_000, "Critical")]
    [InlineData(2L * 1024 * 1024 * 1024, 20_000_000, "Critical")]
    public void DiskSpaceIsJudgedBeforeItRunsOut(long free, double bytesPerSecond, string expected) =>
        Assert.Equal(expected, DiskSpacePolicy.Evaluate(free, bytesPerSecond).ToString());

    [Fact]
    public void AProblemTurnsOrangeOnlyAfterItsDelayAndClearsAtOnce()
    {
        var tracker = new AttentionTracker();
        var start = new DateTime(2026, 1, 1, 12, 0, 0);
        (string, TimeSpan)[] lost = [("microphone lost", TimeSpan.FromSeconds(2))];
        Assert.False(tracker.Update(start, lost));
        Assert.False(tracker.NeedsAttention);
        Assert.False(tracker.Update(start.AddSeconds(1.5), lost));
        Assert.True(tracker.Update(start.AddSeconds(2), lost));
        Assert.Equal(["microphone lost"], tracker.Active);
        Assert.True(tracker.Update(start.AddSeconds(3), []));
        Assert.False(tracker.NeedsAttention);

        // A short drop that comes back before its delay never shows.
        Assert.False(tracker.Update(start.AddSeconds(10), lost));
        Assert.False(tracker.Update(start.AddSeconds(11), []));
        Assert.False(tracker.Update(start.AddSeconds(12), lost));
        Assert.False(tracker.NeedsAttention);

        Assert.True(tracker.Update(start.AddSeconds(12), [("disk space low", TimeSpan.Zero)]));
        Assert.Equal(["disk space low"], tracker.Active);
    }
}

public sealed class RecordingQualityTests
{
    [Fact]
    public void FiveVideoStepsUpTo4KAt30Fps()
    {
        Assert.Equal(5, RecordingQuality.Video.Count);
        Assert.Equal(3840, RecordingQuality.Video[^1].MaxWidth);
        Assert.Equal(2160, RecordingQuality.Video[^1].MaxHeight);
        foreach (VideoLevel level in RecordingQuality.Video)
        {
            RecordingPreset preset = RecordingQuality.Preset(level.Level, 2);
            Assert.Equal(30, preset.FramesPerSecond);
            Assert.Equal(60, preset.KeyframeIntervalFrames);
            Assert.True(preset.PeakBitrate > preset.AverageBitrate);
            Assert.Equal(0, preset.MaxWidth % 2);
            Assert.Equal(0, preset.MaxHeight % 2);
        }
    }

    [Fact]
    public void TheRequestedBitratesAreKept()
    {
        Assert.Equal((4_000_000, 10_000_000), (RecordingQuality.Video[2].AverageBitrate, RecordingQuality.Video[2].PeakBitrate));
        Assert.Equal((6_000_000, 14_000_000), (RecordingQuality.Video[3].AverageBitrate, RecordingQuality.Video[3].PeakBitrate));
        Assert.Equal((12_000_000, 30_000_000), (RecordingQuality.Video[4].AverageBitrate, RecordingQuality.Video[4].PeakBitrate));
    }

    [Fact]
    public void FiveAudioStepsWindowsAccepts()
    {
        // Windows' AAC encoder takes 96, 128, 160, 192, 256 and 320 kbps for 48 kHz stereo (measured on the build machine).
        Assert.Equal([96_000, 128_000, 160_000, 192_000, 256_000], RecordingQuality.Audio.Select(a => a.Bitrate));
        Assert.Equal(128_000, RecordingPreset.Default.AudioBitrate);
    }

    [Theory]
    [InlineData(5, 3840, 2160, 12_000_000, 30_000_000)]
    [InlineData(5, 3440, 1440, 12_000_000, 30_000_000)]
    [InlineData(5, 2560, 1440, 6_000_000, 14_000_000)]
    [InlineData(5, 1920, 1080, 4_000_000, 10_000_000)]
    [InlineData(5, 1080, 1920, 4_000_000, 10_000_000)]
    [InlineData(4, 1920, 1080, 4_000_000, 10_000_000)]
    [InlineData(4, 3840, 2160, 6_000_000, 14_000_000)]
    [InlineData(3, 3840, 2160, 4_000_000, 10_000_000)]
    [InlineData(3, 1366, 768, 4_000_000, 10_000_000)]
    [InlineData(1, 3840, 2160, 1_200_000, 3_000_000)]
    public void AStepLargerThanTheMonitorUsesTheBitrateOfItsSize(int level, int width, int height, int average, int peak)
    {
        RecordingPreset preset = RecordingQuality.Preset(level, 2, new Size(width, height));
        Assert.Equal((average, peak), (preset.AverageBitrate, preset.PeakBitrate));
        Assert.Equal(RecordingQuality.Preset(level, 2).Name, preset.Name);
        Assert.Equal(RecordingQuality.Preset(level, 2).OutputSizeFor(new Size(width, height)), preset.OutputSizeFor(new Size(width, height)));
    }

    [Theory]
    [InlineData(5, 3840, 2160, 3840, 2160)]
    [InlineData(4, 3840, 2160, 2560, 1440)]
    [InlineData(3, 3840, 2160, 1920, 1080)]
    [InlineData(4, 1920, 1080, 1920, 1080)]
    [InlineData(5, 1920, 1080, 1920, 1080)]
    [InlineData(1, 1920, 1080, 854, 480)]
    [InlineData(4, 1080, 1920, 1080, 1920)]
    [InlineData(3, 2560, 1600, 1728, 1080)]
    public void MonitorsAreScaledDownNeverUp(int level, int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.Equal(new Size(expectedWidth, expectedHeight), RecordingQuality.Preset(level, 2).OutputSizeFor(new Size(width, height)));
}
