using System.Diagnostics;
using System.Globalization;
using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using EKrecorder.Overlays;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace EKrecorder.Capture;

/// <summary>What the test needs from the window that starts it. <see cref="AskYesNo"/> is null in self-test mode.</summary>
internal sealed record CaptureTestOptions(
    MonitorInfo Monitor,
    IReadOnlyList<MonitorInfo> AllMonitors,
    string OutputFolder,
    Func<string, bool>? AskYesNo);

/// <summary>
/// The Step 1 spike test. Captures one whole monitor with Windows.Graphics.Capture for about 10 seconds while the
/// recording triangle (and, for two seconds, the Identify numbers) are on screen, all excluded from capture. Then it
/// checks the captured frames, asks two Yes/No questions, and writes report.txt with pictures.
/// <para>
/// Timeline after StartCapture: 1 s frame "before the triangle"; 1.3 s triangle appears; 4.5-6.5 s Identify
/// numbers; one checked frame every second up to 10 s; stop at 10.4 s.
/// </para>
/// </summary>
internal sealed class CaptureTest
{
    private const double IndicatorOnAt = 1.3;
    private const double IdentifyOnAt = 4.5;
    private const double IdentifyOffAt = 6.5;
    private const double StopAt = 10.4;

    // Pixel checks: per-channel colour tolerance, and the share of pixels that must match to count as "visible".
    private const int BlueTolerance = 16;
    private const int ProbeTolerance = 48;
    private const double VisibleShare = 0.5;
    private const double IdentifyVisibleShare = 0.3;

    private const string SessionClass = "Windows.Graphics.Capture.GraphicsCaptureSession";

    private static readonly int[] SampleSeconds = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    private static readonly int[] FullFrameSeconds = [1, 3, 6, 9];

    private readonly CaptureTestOptions _options;
    private readonly IProgress<string> _status;
    private readonly FrameGeometry _geometry;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private readonly List<Task<SampleResult>> _samples = new();
    private readonly RecordingIndicator _indicator = new();
    private readonly CaptureProbe _probe = new();
    private readonly List<string> _files = new();
    private IdentifyOverlays? _identify;

    // Shared with the capture thread (FrameArrived). Counters are guarded by _gate.
    private SampleRequest? _pendingSample;
    private IDirect3DDevice? _device;
    private SizeInt32 _contentSize;
    private long _frameCount;
    private double _firstFrameAt = -1;
    private double _lastFrameAt;
    private double _latencySumMs;
    private double _latencyMaxMs;
    private string? _firstFrameInfo;
    private volatile bool _itemClosed;

    // Facts for the report.
    private IReadOnlyList<string> _environment = [];
    private bool _perMonitorV2;
    private string _dpiAwareness = "";
    private bool _captureSupported;
    private AccessResult _programmaticAccess = new(false, "not requested");
    private AccessResult _borderlessAccess = new(false, "not requested");
    private bool _borderPropertyPresent;
    private bool? _borderRequiredAfterSet;
    private string? _borderSetError;
    private string _frameRateCap = "not set";
    private string? _deviceDescription;
    private SizeInt32? _itemSize;
    private string? _captureError;
    private bool _captureStarted;
    private double _captureSeconds;
    private double _cpuPercentOfOneCore = double.NaN;
    private bool _indicatorAttempted;
    private bool _indicatorShown;
    private bool? _identifyAllExcluded;
    private int _identifyShownCount;
    private IReadOnlyList<string> _identifyDetails = [];
    private bool _probeShown;
    private bool? _userSawTriangle;
    private bool? _userSawBorder;

    public CaptureTest(CaptureTestOptions options, IProgress<string> status)
    {
        _options = options;
        _status = status;
        _geometry = new FrameGeometry(options.Monitor);
    }

    public async Task<TestReport> RunAsync()
    {
        MonitorInfo monitor = _options.Monitor;
        Directory.CreateDirectory(_options.OutputFolder);
        Log.Info($"===== Capture test of {monitor.Summary}");
        Log.Info($"Results folder: {_options.OutputFolder}");
        _environment = EnvironmentInfo.Describe();
        _perMonitorV2 = EnvironmentInfo.IsPerMonitorV2;
        _dpiAwareness = EnvironmentInfo.DpiAwarenessText();

        _captureSupported = IsCaptureSupported();
        if (_captureSupported)
        {
            _programmaticAccess = await RequestAccessAsync(GraphicsCaptureAccessKind.Programmatic);
            _borderlessAccess = await RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            await CaptureAsync(monitor);
        }

        if (_options.AskYesNo is { } ask && _captureStarted)
        {
            if (_indicatorShown)
            {
                _userSawTriangle = ask($"Did you see the small blue triangle in the bottom-right corner of {monitor.Name} during the test?");
            }

            _userSawBorder = ask($"Did you see a yellow border around {monitor.Name} during the test?");
            Log.Info($"Answers: saw the triangle = {Text(_userSawTriangle)}, saw a yellow border = {Text(_userSawBorder)}");
        }

        _status.Report("Saving the results…");
        List<SampleResult> samples = CollectSamples();
        // PNG encoding of full frames takes a moment; keep it off the UI thread.
        return await Task.Run(() => WriteResults(samples));
    }

    private static bool IsCaptureSupported()
    {
        try
        {
            bool supported = GraphicsCaptureSession.IsSupported();
            Log.Api("GraphicsCaptureSession.IsSupported()", supported, supported.ToString());
            return supported;
        }
        catch (Exception ex)
        {
            Log.Error("GraphicsCaptureSession.IsSupported() threw", ex);
            return false;
        }
    }

    /// <summary>Asks Windows whether this app may capture (Programmatic) and may hide the yellow border (Borderless).</summary>
    private static async Task<AccessResult> RequestAccessAsync(GraphicsCaptureAccessKind kind)
    {
        if (!ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
        {
            Log.Warn("GraphicsCaptureAccess is not available on this Windows build.");
            return new AccessResult(false, "not available on this Windows build");
        }

        try
        {
            AppCapabilityAccessStatus status = await GraphicsCaptureAccess.RequestAccessAsync(kind);
            bool allowed = status == AppCapabilityAccessStatus.Allowed;
            Log.Api($"GraphicsCaptureAccess.RequestAccessAsync({kind})", allowed, status.ToString());
            return new AccessResult(allowed, status.ToString());
        }
        catch (Exception ex)
        {
            Log.Error($"GraphicsCaptureAccess.RequestAccessAsync({kind}) threw", ex);
            return new AccessResult(false, $"error {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8})");
        }
    }

    private async Task CaptureAsync(MonitorInfo monitor)
    {
        CaptureDevice? device = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        bool subscribed = false;
        try
        {
            _status.Report($"Preparing to capture {monitor.Name}…");
            device = CaptureDevice.CreateForMonitor(monitor.Handle);
            _device = device.Device;
            _deviceDescription = device.Description;

            GraphicsCaptureItem item = CaptureItemFactory.CreateForMonitor(monitor.Handle);
            item.Closed += (_, _) =>
            {
                _itemClosed = true;
                Log.Warn("GraphicsCaptureItem.Closed: the monitor went away; the test stops early.");
            };
            _itemSize = item.Size;
            _contentSize = item.Size;
            Log.Info($"Capture item \"{item.DisplayName}\": {item.Size.Width}x{item.Size.Height}; monitor is {monitor.Bounds.Width}x{monitor.Bounds.Height}");

            // Free-threaded: frames arrive on a worker thread. A WinForms app has no DispatcherQueue for Create().
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = pool.CreateCaptureSession(item);
            ConfigureSession(session);

            // The control marker goes up first, so frames flow from the start even on a still screen.
            _probeShown = _probe.Show(monitor);

            pool.FrameArrived += OnFrameArrived;
            subscribed = true;
            using Process process = Process.GetCurrentProcess();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            _clock.Start();
            session.StartCapture();
            _captureStarted = true;
            Log.Info("GraphicsCaptureSession.StartCapture() called.");

            await RunTimelineAsync(monitor);

            pool.FrameArrived -= OnFrameArrived;
            subscribed = false;
            _captureSeconds = _clock.Elapsed.TotalSeconds;
            process.Refresh();
            if (_captureSeconds > 0.5)
            {
                _cpuPercentOfOneCore = (process.TotalProcessorTime - cpuBefore).TotalSeconds / _captureSeconds * 100;
            }
            long frames;
            lock (_gate)
            {
                frames = _frameCount;
            }

            Log.Info($"Capture stopped after {_captureSeconds:0.0} s; {frames} frame(s) arrived.");
        }
        catch (Exception ex)
        {
            _captureError = $"{ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})";
            Log.Error("The capture could not run", ex);
        }
        finally
        {
            if (subscribed && pool is not null)
            {
                pool.FrameArrived -= OnFrameArrived;
            }

            session?.Dispose();
            pool?.Dispose();
            // Frame copies still in flight must finish before the device goes away.
            await WaitForSamplesAsync();
            _indicator.Dispose();
            _probe.Dispose();
            CloseIdentify();
            device?.Dispose();
        }
    }

    private void ConfigureSession(GraphicsCaptureSession session)
    {
        // The yellow border. Needs Windows 11 and borderless access; the result is read back, never assumed.
        _borderPropertyPresent = ApiInformation.IsPropertyPresent(SessionClass, "IsBorderRequired");
        if (_borderPropertyPresent)
        {
            if (!_borderlessAccess.Allowed)
            {
                Log.Decision($"Borderless access is \"{_borderlessAccess.Text}\"; setting IsBorderRequired = false anyway to see what Windows does.");
            }

            try
            {
                session.IsBorderRequired = false;
                _borderRequiredAfterSet = session.IsBorderRequired;
                Log.Api("GraphicsCaptureSession.IsBorderRequired = false", _borderRequiredAfterSet == false, $"reads back {_borderRequiredAfterSet}");
            }
            catch (Exception ex)
            {
                _borderSetError = $"{ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})";
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
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) && ApiInformation.IsPropertyPresent(SessionClass, "MinUpdateInterval"))
        {
            try
            {
                session.MinUpdateInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 15);
                _frameRateCap = $"MinUpdateInterval = {session.MinUpdateInterval.TotalMilliseconds:0.0} ms (at most 15 frames per second)";
                Log.Api("GraphicsCaptureSession.MinUpdateInterval = 1/15 s", true, _frameRateCap);
            }
            catch (Exception ex)
            {
                _frameRateCap = $"setting MinUpdateInterval failed: {ex.Message}";
                Log.Error("Setting GraphicsCaptureSession.MinUpdateInterval failed", ex);
            }
        }
        else
        {
            _frameRateCap = "not available (MinUpdateInterval needs Windows 11 24H2); frames come whenever the screen changes";
            Log.Decision($"Frame rate cap {_frameRateCap}.");
        }
    }

    private async Task RunTimelineAsync(MonitorInfo monitor)
    {
        int nextSample = 0;
        bool indicatorDone = false;
        bool identifyOnDone = false;
        bool identifyOffDone = false;
        double lastTopmost = 0;
        string lastStatus = "";
        while (!_itemClosed)
        {
            double t = _clock.Elapsed.TotalSeconds;
            if (t >= StopAt)
            {
                break;
            }

            if (!indicatorDone && t >= IndicatorOnAt)
            {
                indicatorDone = true;
                _indicatorAttempted = true;
                _indicatorShown = _indicator.Show(monitor);
            }

            if (!identifyOnDone && t >= IdentifyOnAt)
            {
                identifyOnDone = true;
                _identify = IdentifyOverlays.Show(_options.AllMonitors);
                _identifyAllExcluded = _identify.AllExcluded;
                _identifyShownCount = _identify.ShownCount;
                _identifyDetails = _identify.Describe();
            }

            if (!identifyOffDone && t >= IdentifyOffAt)
            {
                identifyOffDone = true;
                CloseIdentify();
            }

            if (nextSample < SampleSeconds.Length && t >= SampleSeconds[nextSample])
            {
                RequestSample(SampleSeconds[nextSample++]);
            }

            // Another topmost window (the taskbar, for example) can be raised above the overlays; put them back.
            if (t - lastTopmost >= 1)
            {
                lastTopmost = t;
                _indicator.KeepOnTop();
                _probe.KeepOnTop();
            }

            string left = Math.Ceiling(StopAt - t).ToString("0", CultureInfo.InvariantCulture);
            string status = t >= IdentifyOnAt && t < IdentifyOffAt
                ? $"Capturing {monitor.Name}: {left} s left. Identify numbers are up; they must not appear in the capture."
                : $"Capturing {monitor.Name}: {left} s left. Watch the bottom-right corner and the screen edges.";
            if (status != lastStatus)
            {
                _status.Report(status);
                lastStatus = status;
            }

            await Task.Delay(40);
        }
    }

    private void RequestSample(int second)
    {
        bool identifyUp = _identify?.IsShownOn(_options.Monitor) == true;
        string label = second.ToString("00", CultureInfo.InvariantCulture) + "s"
            + (second == SampleSeconds[0] ? "-before-triangle" : "")
            + (identifyUp ? "-identify" : "");
        var request = new SampleRequest(second, label, FullFrameSeconds.Contains(second), _indicator.IsShown, identifyUp);
        if (Interlocked.Exchange(ref _pendingSample, request) is { } missed)
        {
            AddMissedSample(missed);
        }
    }

    private void AddMissedSample(SampleRequest request)
    {
        Log.Warn($"Sample {request.Label}: no frame arrived in time.");
        lock (_gate)
        {
            _samples.Add(Task.FromResult(new SampleResult(request) { Error = "no frame arrived" }));
        }
    }

    /// <summary>Runs on a capture worker thread for every frame. Counts it and, when one is requested, hands it off for checking.</summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            double now = _clock.Elapsed.TotalSeconds;
            // SystemRelativeTime is on the QueryPerformanceCounter clock, the same clock Stopwatch reads.
            double latencyMs = (Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()) - frame.SystemRelativeTime).TotalMilliseconds;
            SizeInt32 size = frame.ContentSize;
            string? firstFrameInfo = null;
            lock (_gate)
            {
                _frameCount++;
                if (_firstFrameAt < 0)
                {
                    _firstFrameAt = now;
                    _firstFrameInfo = firstFrameInfo =
                        $"first frame {now * 1000:0} ms after StartCapture: content {size.Width}x{size.Height}, delivered {latencyMs:0.0} ms after composition";
                }

                _lastFrameAt = now;
                _latencySumMs += latencyMs;
                _latencyMaxMs = Math.Max(_latencyMaxMs, latencyMs);
            }

            if (firstFrameInfo is not null)
            {
                Log.Info($"Capture: {firstFrameInfo}");
            }

            if (size.Width != _contentSize.Width || size.Height != _contentSize.Height)
            {
                Log.Warn($"Captured size changed from {_contentSize.Width}x{_contentSize.Height} to {size.Width}x{size.Height}; recreating the frame pool.");
                _contentSize = size;
                sender.Recreate(_device!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                return;
            }

            if (Interlocked.Exchange(ref _pendingSample, null) is not { } request)
            {
                return;
            }

            Task<SampleResult> sample = ProcessSampleAsync(frame, request, now);
            frame = null; // the sample task disposes it
            lock (_gate)
            {
                _samples.Add(sample);
            }
        }
        catch (Exception ex)
        {
            Log.Error("FrameArrived handler failed", ex);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private async Task<SampleResult> ProcessSampleAsync(Direct3D11CaptureFrame frame, SampleRequest request, double frameAt)
    {
        var result = new SampleResult(request) { FrameAt = frameAt };
        try
        {
            SoftwareBitmap bitmap;
            try
            {
                using IDirect3DSurface surface = frame.Surface;
                bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface, BitmapAlphaMode.Premultiplied);
            }
            finally
            {
                frame.Dispose(); // hands the buffer back to the frame pool
            }

            CapturedImage image;
            using (bitmap)
            {
                image = CapturedImage.FromSoftwareBitmap(bitmap);
            }

            result.GotFrame = true;
            result.Width = image.Width;
            result.Height = image.Height;
            Analyze(image, result);
            result.Corner = image.Crop(_geometry.Corner);
            if (request.IdentifyShown)
            {
                result.IdentifyArea = image.Crop(_geometry.Identify);
            }

            if (request.SaveFullFrame)
            {
                result.FullFrame = image;
            }

            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"Sample {request.Label}: frame at {frameAt:0.00} s, {image.Width}x{image.Height}; triangle area blue {result.TriangleBlueShare:P0}, control marker {result.ProbeShare:P0}, Identify area blue {result.IdentifyBlueShare:P0}"));
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            Log.Error($"Sample {request.Label} could not be read", ex);
        }

        return result;
    }

    private void Analyze(CapturedImage image, SampleResult result)
    {
        Point[] interior = _geometry.TriangleInterior;
        var trianglePixels = new byte[interior.Length * 3];
        int blue = 0;
        int counted = 0;
        for (int i = 0; i < interior.Length; i++)
        {
            Point p = interior[i];
            if (!image.Contains(p))
            {
                continue;
            }

            (byte b, byte g, byte r) = image.Bgr(p.X, p.Y);
            trianglePixels[i * 3] = b;
            trianglePixels[(i * 3) + 1] = g;
            trianglePixels[(i * 3) + 2] = r;
            counted++;
            if (IsNear(b, g, r, OverlayArt.IndicatorBlue, BlueTolerance))
            {
                blue++;
            }
        }

        result.TrianglePixels = trianglePixels;
        result.TriangleBlueShare = counted == 0 ? 0 : (double)blue / counted;
        result.ProbeShare = Share(image, Rectangle.Inflate(_geometry.Probe, -1, -1),
            (b, g, r) => IsNear(b, g, r, CaptureProbe.ColorA, ProbeTolerance) || IsNear(b, g, r, CaptureProbe.ColorB, ProbeTolerance));
        // The middle of the badge only: its rounded corners and white number are left out.
        int inset = _geometry.Identify.Width / 6;
        result.IdentifyBlueShare = Share(image, Rectangle.Inflate(_geometry.Identify, -inset, -inset),
            (b, g, r) => IsNear(b, g, r, OverlayArt.IndicatorBlue, BlueTolerance));
    }

    private static double Share(CapturedImage image, Rectangle area, Func<byte, byte, byte, bool> matches)
    {
        Rectangle clipped = Rectangle.Intersect(area, new Rectangle(0, 0, image.Width, image.Height));
        int hits = 0;
        int total = 0;
        for (int y = clipped.Top; y < clipped.Bottom; y++)
        {
            for (int x = clipped.Left; x < clipped.Right; x++)
            {
                (byte b, byte g, byte r) = image.Bgr(x, y);
                total++;
                if (matches(b, g, r))
                {
                    hits++;
                }
            }
        }

        return total == 0 ? 0 : (double)hits / total;
    }

    private static bool IsNear(byte b, byte g, byte r, Color color, int tolerance) =>
        Math.Abs(b - color.B) <= tolerance && Math.Abs(g - color.G) <= tolerance && Math.Abs(r - color.R) <= tolerance;

    private async Task WaitForSamplesAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = _samples.ToArray<Task>();
        }

        Task all = Task.WhenAll(pending);
        if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15))) != all)
        {
            Log.Warn("Some frame copies did not finish within 15 seconds.");
        }
    }

    private List<SampleResult> CollectSamples()
    {
        if (Interlocked.Exchange(ref _pendingSample, null) is { } missed)
        {
            AddMissedSample(missed);
        }

        Task<SampleResult>[] tasks;
        lock (_gate)
        {
            tasks = _samples.ToArray();
        }

        // ProcessSampleAsync never throws, so a finished task always has a result.
        return tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).OrderBy(s => s.Request.Second).ToList();
    }

    private void CloseIdentify()
    {
        _identify?.Dispose();
        _identify = null;
    }

    private TestReport WriteResults(List<SampleResult> samples)
    {
        string folder = _options.OutputFolder;
        var report = new TestReport();
        try
        {
            // Compare the triangle's pixels with the frame taken before the triangle appeared.
            byte[]? before = samples.FirstOrDefault(s => s.GotFrame && !s.Request.IndicatorShown)?.TrianglePixels;
            foreach (SampleResult sample in samples)
            {
                if (before is not null && sample.TrianglePixels is not null && sample.Request.IndicatorShown)
                {
                    sample.TriangleChange = MeanDifference(before, sample.TrianglePixels);
                }
            }

            SaveImages(samples);
            AddChecks(report, samples);
            AddDetails(report, samples);
            report.Save(Path.Combine(folder, "report.txt"));
            Log.Info($"Report written: {Path.Combine(folder, "report.txt")}");
        }
        catch (Exception ex)
        {
            Log.Error("Writing the results failed", ex);
            report.Add(TestReport.Fail, $"Writing the results failed: {ex.Message}");
        }
        finally
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, "log.txt"), Log.Snapshot());
            }
            catch (Exception ex)
            {
                Log.Error("Saving log.txt failed", ex);
            }
        }

        return report;
    }

    private void SaveImages(List<SampleResult> samples)
    {
        string folder = _options.OutputFolder;

        void Save(SampleResult? sample, string fileName, Action<string> write)
        {
            try
            {
                write(Path.Combine(folder, fileName));
                sample?.Files.Add(fileName);
                _files.Add(fileName);
            }
            catch (Exception ex)
            {
                Log.Error($"Saving {fileName} failed", ex);
            }
        }

        // What a failure would look like: the "before" corner with the triangle painted in where it sits on screen.
        if (samples.FirstOrDefault(s => s.Corner is not null && !s.Request.IndicatorShown)?.Corner is { } beforeCorner)
        {
            var at = new Point(_geometry.Triangle.X - _geometry.Corner.X, _geometry.Triangle.Y - _geometry.Corner.Y);
            OverlayBitmap triangle = OverlayArt.CornerTriangle(_geometry.Triangle.Width, OverlayArt.IndicatorBlue);
            Save(null, "corner-REFERENCE-if-the-triangle-were-captured-x8.png",
                path => beforeCorner.WithOverlay(triangle, at).SavePng(path, 8));
        }

        foreach (SampleResult sample in samples)
        {
            if (sample.Corner is { } corner)
            {
                Save(sample, $"corner-{sample.Request.Label}-x8.png", path => corner.SavePng(path, 8));
            }

            if (sample.IdentifyArea is { } area)
            {
                Save(sample, $"identify-area-{sample.Request.Label}.png", path => area.SavePng(path));
            }

            if (sample.FullFrame is { } full)
            {
                Save(sample, $"frame-{sample.Request.Label}.png", path => full.SavePng(path));
                sample.FullFrame = null;
            }
        }
    }

    private void AddChecks(TestReport report, List<SampleResult> samples)
    {
        MonitorInfo monitor = _options.Monitor;
        const string Pass = TestReport.Pass, Fail = TestReport.Fail, Warn = TestReport.Warn, Info = TestReport.Info;

        // Monitors and DPI
        report.Add(_perMonitorV2 ? Pass : Fail, $"DPI awareness of the test window's thread: {_dpiAwareness}");
        report.Add(_options.AllMonitors.Count > 0 ? Pass : Fail,
            $"{_options.AllMonitors.Count} monitor(s) found, numbered left to right: "
            + string.Join("; ", _options.AllMonitors.Select(m => $"{m.Name} {m.Bounds.Width}x{m.Bounds.Height} at x={m.Bounds.X}")));

        // The capture itself
        if (!_captureSupported)
        {
            report.Add(Fail, "Windows.Graphics.Capture is not supported on this PC");
            return;
        }

        report.Add(Pass, "Windows.Graphics.Capture is supported");

        long frames;
        double latencyAverage;
        double latencyMax;
        lock (_gate)
        {
            frames = _frameCount;
            latencyAverage = frames > 0 ? _latencySumMs / frames : 0;
            latencyMax = _latencyMaxMs;
        }

        // A desktop app can capture even when this permission reads "Denied" (seen on the build machine), so the
        // permission only fails the test when the capture itself did not work.
        if (_programmaticAccess.Allowed)
        {
            report.Add(Pass, "Windows allows this app to capture the screen (GraphicsCaptureAccess: Allowed)");
        }
        else if (frames > 0)
        {
            report.Add(Info, $"GraphicsCaptureAccess reports \"{_programmaticAccess.Text}\" for screen capture, but the capture worked anyway");
        }
        else
        {
            report.Add(Fail, $"Windows may be blocking screen capture for this app (GraphicsCaptureAccess: {_programmaticAccess.Text})");
        }

        if (_captureError is not null)
        {
            report.Add(Fail, $"The capture could not run: {_captureError}");
        }

        if (_deviceDescription is not null)
        {
            report.Add(Info, $"Capture runs on {_deviceDescription}");
        }

        if (_itemSize is { } itemSize)
        {
            bool whole = itemSize.Width == monitor.Bounds.Width && itemSize.Height == monitor.Bounds.Height;
            report.Add(whole ? Pass : Fail,
                $"Capture covers the whole of {monitor.Name}: {itemSize.Width}x{itemSize.Height} captured, monitor is {monitor.Bounds.Width}x{monitor.Bounds.Height}");
        }

        if (_captureStarted)
        {
            report.Add(frames > 0 ? Pass : Fail, frames > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{frames} frames arrived in {_captureSeconds:0.0} s ({frames / Math.Max(_captureSeconds, 0.001):0.0} per second; frames only come when something on the screen changes)")
                : "No frames arrived");
        }

        if (frames > 0)
        {
            report.Add(Info, string.Create(CultureInfo.InvariantCulture,
                $"Frames were delivered {latencyAverage:0.0} ms after composition on average, {latencyMax:0.0} ms at most (both measured on the QueryPerformanceCounter clock)"));
        }

        if (!double.IsNaN(_cpuPercentOfOneCore))
        {
            report.Add(Info, string.Create(CultureInfo.InvariantCulture,
                $"CPU while capturing: {_cpuPercentOfOneCore:0.0}% of one core on average, including this test's own frame copies"));
        }

        report.Add(Info, $"Frame rate cap: {_frameRateCap}");

        // The yellow capture border
        if (!_borderPropertyPresent)
        {
            report.Add(Fail, "This Windows build cannot turn the capture border off (GraphicsCaptureSession.IsBorderRequired is missing)");
        }
        else if (_borderSetError is not null)
        {
            report.Add(Fail, $"Turning the capture border off failed: {_borderSetError}");
        }
        else if (_borderRequiredAfterSet == false)
        {
            // Whether the border really disappeared is the Yes/No question below; Windows draws it on the screen.
            report.Add(Pass,
                $"Capture border turned off through the API: IsBorderRequired reads back False (GraphicsCaptureAccess for borderless: {_borderlessAccess.Text})");
        }
        else if (_borderRequiredAfterSet == true)
        {
            report.Add(Fail, $"Windows kept the capture border on: IsBorderRequired reads back True (borderless access: {_borderlessAccess.Text})");
        }

        if (_userSawBorder is bool sawBorder)
        {
            report.Add(sawBorder ? Fail : Pass, sawBorder ? "You saw a yellow capture border" : "You saw no yellow capture border");
        }
        else if (_captureStarted)
        {
            report.Add(Info, "Yellow border: not asked (self-test). Windows draws it on the screen, so a person has to confirm it.");
        }

        // The recording triangle
        if (_indicatorAttempted)
        {
            report.Add(_indicator.ExclusionVerified ? Pass : Fail,
                $"Triangle excluded from capture while still hidden: {_indicator.ExclusionDetail}");
            report.Add(_indicatorShown ? Pass : Fail, _indicatorShown
                ? $"Triangle was on screen: {_indicator.SizePx}x{_indicator.SizePx} px in the bottom-right corner, at {_indicator.Bounds}"
                : "Triangle was not shown (see the line above and the log)");
        }

        if (_userSawTriangle is bool sawTriangle)
        {
            report.Add(sawTriangle ? Pass : Fail, sawTriangle ? "You saw the blue triangle" : "You did not see the blue triangle");
        }

        List<SampleResult> checkedFrames = samples.Where(s => s.GotFrame).ToList();
        List<SampleResult> withTriangle = checkedFrames.Where(s => s.Request.IndicatorShown).ToList();
        int triangleVisible = withTriangle.Count(s => s.TriangleBlueShare >= VisibleShare);
        if (_indicatorShown && withTriangle.Count == 0)
        {
            report.Add(Fail, "No frame was captured while the triangle was on screen, so its absence could not be checked");
        }
        else if (_indicatorShown)
        {
            report.Add(triangleVisible == 0 ? Pass : Fail, triangleVisible == 0
                ? $"Triangle is NOT in the capture: absent from all {withTriangle.Count} frames checked while it was on screen"
                : $"Triangle IS in the capture: visible in {triangleVisible} of {withTriangle.Count} frames checked");
        }

        // The Identify numbers
        if (_identifyAllExcluded is bool identifyExcluded)
        {
            report.Add(identifyExcluded ? Pass : Fail,
                $"Identify numbers excluded from capture while still hidden ({_identifyShownCount} shown)");
        }

        List<SampleResult> withIdentify = checkedFrames.Where(s => s.Request.IdentifyShown).ToList();
        int identifyVisible = withIdentify.Count(s => s.IdentifyBlueShare >= IdentifyVisibleShare);
        if (withIdentify.Count > 0)
        {
            report.Add(identifyVisible == 0 ? Pass : Fail, identifyVisible == 0
                ? $"Identify number is NOT in the capture: absent from all {withIdentify.Count} frames checked while it was on screen"
                : $"Identify number IS in the capture: visible in {identifyVisible} of {withIdentify.Count} frames checked");
        }
        else if (_identifyShownCount > 0)
        {
            report.Add(Warn, "No frame was captured while the Identify numbers were on screen");
        }

        // The control marker shows whether the capture can see that corner at all.
        if (_probeShown && checkedFrames.Count > 0)
        {
            int probeSeen = checkedFrames.Count(s => s.ProbeShare >= VisibleShare);
            report.Add(probeSeen == checkedFrames.Count ? Pass : Warn, probeSeen == checkedFrames.Count
                ? $"Control marker (deliberately NOT excluded) is in all {checkedFrames.Count} checked frames, so the capture does see that corner and the triangle's absence is real"
                : $"Control marker is in only {probeSeen} of {checkedFrames.Count} checked frames; something may have covered the corner, so the triangle check is less certain");
        }

        int failedSamples = samples.Count(s => !s.GotFrame);
        if (failedSamples > 0)
        {
            report.Add(Warn, $"{failedSamples} of {samples.Count} planned frame checks got no frame (see the log)");
        }
    }

    private void AddDetails(TestReport report, List<SampleResult> samples)
    {
        MonitorInfo monitor = _options.Monitor;
        report.AddSection("Tested monitor", [monitor.Summary, $"Windows device {monitor.GdiDeviceName}; id {monitor.StableId}"]);
        report.AddSection("All monitors (EKrecorder numbers them left to right)",
            _options.AllMonitors.Select(m => $"{m.Summary}; id {m.StableId}"));
        report.AddSection("Windows and app", _environment);

        long frames;
        string firstFrame;
        double firstAt;
        double lastAt;
        lock (_gate)
        {
            frames = _frameCount;
            firstFrame = _firstFrameInfo ?? "no frames";
            firstAt = _firstFrameAt;
            lastAt = _lastFrameAt;
        }

        report.AddSection("Capture",
        [
            $"Windows allows capture (Programmatic): {_programmaticAccess.Text}",
            $"Windows allows hiding the border (Borderless): {_borderlessAccess.Text}",
            $"IsBorderRequired after setting it to False: {_borderRequiredAfterSet?.ToString() ?? "not set"}{(_borderSetError is null ? "" : $" ({_borderSetError})")}",
            $"Frame rate cap: {_frameRateCap}",
            $"Device: {_deviceDescription ?? "none"}",
            $"Capture item size: {(_itemSize is { } itemSize ? $"{itemSize.Width}x{itemSize.Height}" : "none")}",
            $"Frames: {frames}; {firstFrame}",
            string.Create(CultureInfo.InvariantCulture, $"First and last frame at {firstAt:0.00} s and {lastAt:0.00} s after StartCapture"),
            $"Error: {_captureError ?? "none"}",
        ]);

        report.AddSection("Recording triangle",
        [
            $"Size {_indicator.SizePx}x{_indicator.SizePx} px ({RecordingIndicator.LogicalSize} logical px at {monitor.ScalePercent}%), screen position {_indicator.Bounds}",
            $"Exclusion: {_indicator.ExclusionDetail}",
            $"Shown: {_indicatorShown}",
        ]);

        report.AddSection("Identify numbers (shown 4.5 to 6.5 s into the test)",
            _identifyDetails.Count > 0 ? _identifyDetails : ["not shown"]);

        var lines = new List<string>
        {
            "Each line is one captured frame. \"blue\" = share of the triangle's pixels that are indicator blue in the capture",
            "(0% is right). \"change\" = average colour change of those pixels against the first frame, taken before the",
            "triangle appeared (near 0 is right). \"marker\" = the control square that is NOT excluded (100% is right).",
            "",
        };
        foreach (SampleResult s in samples)
        {
            string onScreen = $"triangle {(s.Request.IndicatorShown ? "on" : "off")}, Identify {(s.Request.IdentifyShown ? "on" : "off")}";
            lines.Add(s.GotFrame
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{s.Request.Label,-26} at {s.FrameAt:0.00} s  {onScreen}  blue {s.TriangleBlueShare:P0}  change {(s.TriangleChange is double c ? c.ToString("0.0", CultureInfo.InvariantCulture) : "-")}  marker {s.ProbeShare:P0}  Identify-area blue {s.IdentifyBlueShare:P0}")
                : $"{s.Request.Label,-26} no frame: {s.Error}");
        }

        report.AddSection("Frames checked", lines);

        report.AddSection("Files in this folder",
        [
            "report.txt  this report",
            "log.txt  every Windows API call and decision during the session",
            "corner-*-x8.png  the bottom-right corner of each checked frame, enlarged 8 times",
            "corner-REFERENCE-if-the-triangle-were-captured-x8.png  what those corners would show if the exclusion failed",
            "identify-area-*.png  the middle of the monitor while the Identify number was up (it must not show)",
            "frame-*.png  whole captured frames, to check the edges for a border",
            $"{_files.Count} picture(s) saved",
        ]);

        report.AddSection("If something failed",
        [
            "Triangle or Identify number IN the capture: the exclusion did not work on this PC. Send this report.",
            "Capture or border access not \"Allowed\": open Settings > Privacy & security, look for \"Screenshot borders\" and",
            "\"Screenshots and apps\", allow desktop apps, then run the test again.",
            "Yellow border seen although the API turned it off: send this report.",
            "No frames: make sure the PC is unlocked and the monitor is awake, then run the test again.",
            $"The full session log is also in {Log.FilePath ?? "(no log file)"}",
        ]);
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        int count = Math.Min(a.Length, b.Length);
        if (count == 0)
        {
            return 0;
        }

        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return (double)sum / count;
    }

    private static string Text(bool? value) => value switch
    {
        true => "Yes",
        false => "No",
        null => "not asked",
    };

    private sealed record AccessResult(bool Allowed, string Text);
}
