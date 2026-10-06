namespace EKrecorder.Capture;

/// <summary>A request for the next captured frame to be copied and checked, with what was on screen at that moment.</summary>
internal sealed record SampleRequest(
    int Second,
    string Label,
    bool SaveFullFrame,
    bool IndicatorShown,
    bool IdentifyShown);

/// <summary>What one checked frame showed.</summary>
internal sealed class SampleResult
{
    public SampleResult(SampleRequest request) => Request = request;

    public SampleRequest Request { get; }

    public bool GotFrame { get; set; }

    public string? Error { get; set; }

    /// <summary>Seconds after StartCapture when the frame arrived.</summary>
    public double FrameAt { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>BGR of every fully covered triangle pixel, to compare with the frame taken before the triangle.</summary>
    public byte[]? TrianglePixels { get; set; }

    /// <summary>Share of the triangle's pixels that are indicator blue in the captured frame.</summary>
    public double TriangleBlueShare { get; set; }

    /// <summary>Average colour change in the triangle's pixels against the frame taken before the triangle (0-255).</summary>
    public double? TriangleChange { get; set; }

    /// <summary>Share of the control marker's pixels that show one of its two colours.</summary>
    public double ProbeShare { get; set; }

    /// <summary>Share of the middle of the Identify badge's area that is badge blue.</summary>
    public double IdentifyBlueShare { get; set; }

    public CapturedImage? FullFrame { get; set; }

    public CapturedImage? Corner { get; set; }

    public CapturedImage? IdentifyArea { get; set; }

    public List<string> Files { get; } = new();
}
