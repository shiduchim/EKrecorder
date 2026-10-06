using System.Globalization;

namespace EKrecorder.Recording;

/// <summary>
/// What a recording looks like: the largest output size, frame rate, H.264 bitrates, keyframe interval and the AAC
/// bitrate. The output size is fixed for the whole recording.
/// </summary>
internal sealed record RecordingPreset(
    string Name,
    int MaxWidth,
    int MaxHeight,
    int FramesPerSecond,
    int AverageBitrate,
    int PeakBitrate,
    int KeyframeIntervalSeconds,
    int AudioBitrate = 128_000)
{
    /// <summary>The recommended preset (see <see cref="RecordingQuality"/>).</summary>
    public static RecordingPreset Default => RecordingQuality.Preset(RecordingQuality.DefaultVideoLevel, RecordingQuality.DefaultAudioLevel);

    public int KeyframeIntervalFrames => FramesPerSecond * KeyframeIntervalSeconds;

    /// <summary>Start time of output frame <paramref name="index"/> in 100-ns units (the Media Foundation time unit).</summary>
    public long FrameTime(long index) => index * 10_000_000L / FramesPerSecond;

    /// <summary>
    /// Fits the monitor inside MaxWidth × MaxHeight, keeps its aspect ratio and never upscales. The limit turns with
    /// the monitor: a portrait monitor fits inside MaxHeight × MaxWidth (1080×1920), not into a 608×1080 strip.
    /// Both sides are even, as 4:2:0 video needs.
    /// </summary>
    public Size OutputSizeFor(Size source)
    {
        bool portrait = source.Height > source.Width;
        int maxWidth = portrait ? MaxHeight : MaxWidth;
        int maxHeight = portrait ? MaxWidth : MaxHeight;
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        int width = Math.Min(maxWidth, Math.Max(2, 2 * (int)Math.Round(source.Width * scale / 2, MidpointRounding.AwayFromZero)));
        int height = Math.Min(maxHeight, Math.Max(2, 2 * (int)Math.Round(source.Height * scale / 2, MidpointRounding.AwayFromZero)));
        if (width > source.Width)
        {
            width = source.Width & ~1;
        }

        if (height > source.Height)
        {
            height = source.Height & ~1;
        }

        return new Size(width, height);
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Name}: at most {MaxWidth}x{MaxHeight}, {FramesPerSecond} fps, H.264, {AverageBitrate / 1e6:0.#} Mbps average / {PeakBitrate / 1e6:0.#} Mbps peak, keyframe every {KeyframeIntervalSeconds} s; AAC {AudioBitrate / 1000} kbps");
}

/// <summary>One step of the video quality slider.</summary>
internal sealed record VideoLevel(int Level, string Label, int MaxWidth, int MaxHeight, int AverageBitrate, int PeakBitrate);

/// <summary>One step of the audio quality slider (bitrates Windows' AAC encoder accepts for 48 kHz stereo).</summary>
internal sealed record AudioLevel(int Level, string Label, int Bitrate);

/// <summary>
/// The quality steps the Settings window offers. Video is always 30 fps with a keyframe every 2 seconds; each step
/// sets the largest size (a monitor is never enlarged) and the bitrate. Audio is AAC-LC, 48 kHz stereo.
/// </summary>
internal static class RecordingQuality
{
    public const int FramesPerSecond = 30;
    public const int KeyframeIntervalSeconds = 2;

    /// <summary>1440p: on a 4K screen Revit lines and small text stay sharp; a 1080p screen is recorded at full size with room to spare.</summary>
    public const int DefaultVideoLevel = 4;

    /// <summary>128 kbps: plenty for voices on calls.</summary>
    public const int DefaultAudioLevel = 2;

    public static IReadOnlyList<VideoLevel> Video { get; } =
    [
        new(1, "480p", 854, 480, 1_200_000, 3_000_000),
        new(2, "720p", 1280, 720, 2_500_000, 6_000_000),
        new(3, "1080p", 1920, 1080, 4_000_000, 10_000_000),
        new(4, "1440p", 2560, 1440, 6_000_000, 14_000_000),
        new(5, "4K", 3840, 2160, 10_000_000, 20_000_000),
    ];

    public static IReadOnlyList<AudioLevel> Audio { get; } =
    [
        new(1, "96 kbps", 96_000),
        new(2, "128 kbps", 128_000),
        new(3, "160 kbps", 160_000),
        new(4, "192 kbps", 192_000),
        new(5, "256 kbps", 256_000),
    ];

    public static VideoLevel VideoLevelOf(int level) => Video[Math.Clamp(level, 1, Video.Count) - 1];

    public static AudioLevel AudioLevelOf(int level) => Audio[Math.Clamp(level, 1, Audio.Count) - 1];

    public static RecordingPreset Preset(int videoLevel, int audioLevel)
    {
        VideoLevel video = VideoLevelOf(videoLevel);
        AudioLevel audio = AudioLevelOf(audioLevel);
        return new RecordingPreset(
            $"{video.Label}, {FramesPerSecond} fps", video.MaxWidth, video.MaxHeight, FramesPerSecond, video.AverageBitrate, video.PeakBitrate, KeyframeIntervalSeconds, audio.Bitrate);
    }

    /// <summary>About how much an hour takes at most (the bitrates are averages; a still screen takes less).</summary>
    public static double GigabytesPerHour(int videoLevel, int audioLevel) =>
        (VideoLevelOf(videoLevel).AverageBitrate + AudioLevelOf(audioLevel).Bitrate) / 8.0 * 3600 / 1e9;
}
