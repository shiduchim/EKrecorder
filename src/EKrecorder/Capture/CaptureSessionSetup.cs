using EKrecorder.Diagnostics;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace EKrecorder.Capture;

/// <summary>Windows' answer to a graphics-capture permission request.</summary>
internal sealed record CaptureAccessResult(bool Allowed, string Text);

/// <summary>
/// The one place that asks Windows for capture permissions and configures a GraphicsCaptureSession, shared by the
/// capture test and the recorder: yellow border off, mouse cursor on, frame rate capped where Windows supports it.
/// Every setting is read back and logged, never assumed.
/// </summary>
internal static class CaptureSessionSetup
{
    private const string SessionClass = "Windows.Graphics.Capture.GraphicsCaptureSession";

    /// <summary>What <see cref="Configure"/> managed to set.</summary>
    internal sealed record Result(bool BorderPropertyPresent, bool? BorderRequiredAfterSet, string? BorderError, string FrameRateCap)
    {
        public string Describe() =>
            $"border: {(!BorderPropertyPresent ? "cannot be turned off on this Windows build" : BorderError ?? $"IsBorderRequired = {BorderRequiredAfterSet}")}; frame rate cap: {FrameRateCap}";
    }

    /// <summary>Asks whether this app may capture (Programmatic) or hide the yellow border (Borderless).</summary>
    public static async Task<CaptureAccessResult> RequestAccessAsync(GraphicsCaptureAccessKind kind)
    {
        if (!ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
        {
            Log.Warn("GraphicsCaptureAccess is not available on this Windows build.");
            return new CaptureAccessResult(false, "not available on this Windows build");
        }

        try
        {
            AppCapabilityAccessStatus status = await GraphicsCaptureAccess.RequestAccessAsync(kind);
            bool allowed = status == AppCapabilityAccessStatus.Allowed;
            Log.Api($"GraphicsCaptureAccess.RequestAccessAsync({kind})", allowed, status.ToString());
            return new CaptureAccessResult(allowed, status.ToString());
        }
        catch (Exception ex)
        {
            Log.Error($"GraphicsCaptureAccess.RequestAccessAsync({kind}) threw", ex);
            return new CaptureAccessResult(false, $"error {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8})");
        }
    }

    /// <summary>Configures a session before StartCapture.</summary>
    public static Result Configure(GraphicsCaptureSession session, int framesPerSecond, string borderlessAccess)
    {
        // The yellow border. Needs Windows 11; the result is read back, never assumed.
        bool borderPropertyPresent = ApiInformation.IsPropertyPresent(SessionClass, "IsBorderRequired");
        bool? borderRequiredAfterSet = null;
        string? borderError = null;
        if (borderPropertyPresent)
        {
            if (borderlessAccess != nameof(AppCapabilityAccessStatus.Allowed))
            {
                Log.Decision($"Borderless access is \"{borderlessAccess}\"; setting IsBorderRequired = false anyway to see what Windows does.");
            }

            try
            {
                session.IsBorderRequired = false;
                borderRequiredAfterSet = session.IsBorderRequired;
                Log.Api("GraphicsCaptureSession.IsBorderRequired = false", borderRequiredAfterSet == false, $"reads back {borderRequiredAfterSet}");
            }
            catch (Exception ex)
            {
                borderError = $"{ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})";
                Log.Error("Setting GraphicsCaptureSession.IsBorderRequired = false failed", ex);
            }
        }
        else
        {
            Log.Warn("GraphicsCaptureSession.IsBorderRequired is not available on this Windows build; the border stays on.");
        }

        try
        {
            session.IsCursorCaptureEnabled = true;
            Log.Api("GraphicsCaptureSession.IsCursorCaptureEnabled = true", true, $"reads back {session.IsCursorCaptureEnabled}");
        }
        catch (Exception ex)
        {
            Log.Error("Setting GraphicsCaptureSession.IsCursorCaptureEnabled failed", ex);
        }

        // Windows 11 24H2 and later can cap the frame rate inside the capture itself, which keeps the load low.
        string frameRateCap;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) && ApiInformation.IsPropertyPresent(SessionClass, "MinUpdateInterval"))
        {
            try
            {
                session.MinUpdateInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / framesPerSecond);
                frameRateCap = $"MinUpdateInterval = {session.MinUpdateInterval.TotalMilliseconds:0.0} ms (at most {framesPerSecond} frames per second)";
                Log.Api($"GraphicsCaptureSession.MinUpdateInterval = 1/{framesPerSecond} s", true, frameRateCap);
            }
            catch (Exception ex)
            {
                frameRateCap = $"setting MinUpdateInterval failed: {ex.Message}";
                Log.Error("Setting GraphicsCaptureSession.MinUpdateInterval failed", ex);
            }
        }
        else
        {
            frameRateCap = "not available (MinUpdateInterval needs Windows 11 24H2); frames come whenever the screen changes";
            Log.Decision($"Frame rate cap {frameRateCap}.");
        }

        return new Result(borderPropertyPresent, borderRequiredAfterSet, borderError, frameRateCap);
    }
}
