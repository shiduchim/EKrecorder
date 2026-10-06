using System.Globalization;

namespace EKrecorder.Recording;

/// <summary>
/// What a recording looks like: the largest output size, frame rate, H.264 bitrates and keyframe interval.
/// The output size is fixed for the whole recording.
/// </summary>
internal sealed record RecordingPreset(
    string Name,
    int MaxWidth,
    int MaxHeight,
    int FramesPerSecond,
    int AverageBitrate,
    int PeakBitrate,
    int KeyframeIntervalSeconds)
{
    /// <summary>The default: at most 1920×1080, 15 fps, about 2 Mbps average and 6 Mbps peak, a keyframe every 2 s.</summary>
    public static RecordingPreset Default { get; } = new("1080p, 15 fps", 1920, 1080, 15, 2_000_000, 6_000_000, 2);

    public int KeyframeIntervalFrames => FramesPerSecond * KeyframeIntervalSeconds;

    /// <summary>Start time of output frame <paramref name="index"/> in 100-ns units (the Media Foundation time unit).</summary>
    public long FrameTime(long index) => index * 10_000_000L / FramesPerSecond;

    /// <summary>
    /// Fits the monitor inside MaxWidth × MaxHeight, keeps its aspect ratio and never upscales. Both sides are even,
    /// as 4:2:0 video needs.
    /// </summary>
    public Size OutputSizeFor(Size source)
    {
        double scale = Math.Min(1.0, Math.Min((double)MaxWidth / source.Width, (double)MaxHeight / source.Height));
        int width = Math.Min(MaxWidth, Math.Max(2, 2 * (int)Math.Round(source.Width * scale / 2, MidpointRounding.AwayFromZero)));
        int height = Math.Min(MaxHeight, Math.Max(2, 2 * (int)Math.Round(source.Height * scale / 2, MidpointRounding.AwayFromZero)));
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
        $"{Name}: at most {MaxWidth}x{MaxHeight}, {FramesPerSecond} fps, H.264, {AverageBitrate / 1e6:0.#} Mbps average / {PeakBitrate / 1e6:0.#} Mbps peak, keyframe every {KeyframeIntervalSeconds} s");
}
