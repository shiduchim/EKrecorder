using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EKrecorder.Audio;
using EKrecorder.Capture;
using EKrecorder.Diagnostics;
using EKrecorder.Monitors;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using static EKrecorder.Recording.MediaFoundation;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Recording;

/// <summary>
/// One recording of one whole monitor: Windows.Graphics.Capture → GPU scaling and NV12 conversion → Media Foundation
/// H.264 → MP4 in %LocalAppData%\EKrecorder\InProgress.
/// <para>
/// Everything runs on one dedicated thread that paces the output at the preset frame rate (15 fps) on the
/// QueryPerformanceCounter clock. Each tick encodes the newest captured frame, or the previous one again when the
/// screen has not changed, so the file has a constant frame rate.
/// </para>
/// <para>
/// Audio (microphone and computer audio, see <see cref="AudioCapture"/>) starts before the video set-up so the
/// devices are open in time. Its timeline starts with video frame 0, on the same QPC clock, and the mixer ends it
/// exactly with the last video frame.
/// </para>
/// <para>
/// How frames reach the encoder is chosen at the start, best first: GPU frames into a hardware encoder (nothing leaves
/// the GPU), CPU frames into a hardware encoder, CPU frames into the software encoder. The first frame tests each
/// set-up: if it is refused, the reason and a full description of what was sent go into the report, and the next
/// set-up is tried.
/// </para>
/// </summary>
internal sealed unsafe class RecordingSession
{
    public const string GpuSetup = "GPU frames -> hardware encoder";
    public const string CpuHardwareSetup = "CPU frames -> hardware encoder";
    public const string CpuSoftwareSetup = "CPU frames -> software encoder";

    private const int PoolBuffers = 3;
    private const int MaxLoggedErrors = 50;
    private const uint RenderTarget = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly object _frameLock = new();
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private readonly TaskCompletionSource<RecordingSession> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _borderlessAccess;
    private readonly bool _simulateFirstSetupFailure;
    private readonly AudioSelection? _audioSelection;
    private TimelineClock? _audioClock;
    private readonly List<string> _fallbacks = new();
    private readonly StringBuilder _setupFailures = new();
    private HeldFrame? _latest;
    private bool _latestUnused;
    private bool _captureClosed;
    private int _captureErrors;
    private SizeInt32 _poolSize;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item; // kept in a field so it (and its Closed handler) lives as long as the recording
    private volatile bool _stopRequested;
    private volatile bool _itemClosed;

    private RecordingSession(MonitorInfo monitor, RecordingPreset preset, string temporaryPath, string borderlessAccess, AudioSelection? audio, bool simulateFirstSetupFailure)
    {
        _audioSelection = audio;
        Monitor = monitor;
        Preset = preset;
        TemporaryPath = temporaryPath;
        _borderlessAccess = borderlessAccess;
        _simulateFirstSetupFailure = simulateFirstSetupFailure;
    }

    public MonitorInfo Monitor { get; }

    public RecordingPreset Preset { get; }

    public string TemporaryPath { get; }

    public RecordingStats Stats { get; } = new();

    public Size CaptureSize { get; private set; }

    public Size OutputSize { get; private set; }

    public string DeviceDescription { get; private set; } = "";

    public string ConverterDescription { get; private set; } = "";

    public string CaptureSettings { get; private set; } = "";

    public string PacingTimer { get; private set; } = "";

    public string WriterStatistics { get; private set; } = "";

    /// <summary>The sink writer's counters for the audio track.</summary>
    public string AudioStatistics { get; private set; } = "";

    public IReadOnlyList<string> HardwareEncoders { get; private set; } = [];

    public EncoderInfo? Encoder { get; private set; }

    /// <summary>How frames reach the file in this recording, for example "GPU frames (...) -> NVIDIA hardware H.264 encoder".</summary>
    public string FramePath { get; private set; } = "";

    /// <summary>True when frames stay on the GPU from capture to encoder.</summary>
    public bool UsesGpuPath { get; private set; }

    /// <summary>Set-ups that were skipped or failed before the one in use, each with the reason.</summary>
    public IReadOnlyList<string> Fallbacks => _fallbacks;

    /// <summary>Full details of every set-up whose first frame was refused; empty when none was.</summary>
    public string SetupFailures => _setupFailures.ToString();

    /// <summary>Full details when the encoder failed after the recording had started; empty when it did not.</summary>
    public string RecordingFailure { get; private set; } = "";

    /// <summary>GPU path: the sample pool's summary (buffer lengths, textures). Empty on the CPU paths.</summary>
    public string GpuSamples { get; private set; } = "";

    /// <summary>The input media type given to the sink writer.</summary>
    public string EncoderInputType { get; private set; } = "";

    /// <summary>What the encoder's input stream asks of its samples (IMFTransform::GetInputStreamAttributes).</summary>
    public string EncoderInputStream { get; private set; } = "";

    /// <summary>The microphone and computer-audio capture (null when recording without audio).</summary>
    public AudioCapture? Audio { get; private set; }

    /// <summary>The audio mixer (null until the recording runs, or without an audio track).</summary>
    public AudioMixer? Mixer { get; private set; }

    /// <summary>Why the file has no audio track, when it should have one.</summary>
    public string? AudioError { get; private set; }

    /// <summary>
    /// How much later than captured the audio is placed: half a video frame, the average age of the screen picture
    /// a video frame shows (the newest captured frame at that moment), so sound and picture line up on average.
    /// </summary>
    public TimeSpan AudioOffset => TimeSpan.FromTicks(TimeSpan.TicksPerSecond / (2 * Preset.FramesPerSecond));

    /// <summary>Output frames so far (slot count, including dropped ones, decides the duration).</summary>
    public long Slots { get; private set; }

    public bool FileFinalized { get; private set; }

    public string? StopReason { get; private set; }

    /// <summary>Completes when the recording has stopped and the file is closed (also after a self-stop or error).</summary>
    public Task Completion => _finished.Task;

    /// <summary>
    /// Starts recording; returns once the first frame has been captured and accepted by the encoder.
    /// <paramref name="audio"/>: which microphone and computer audio to record (null: no audio track).
    /// <paramref name="simulateFirstSetupFailure"/> (self-test only) makes the first set-up's first frame fail, to
    /// prove that the next set-up takes over and that the failure is reported.
    /// </summary>
    public static Task<RecordingSession> StartAsync(
        MonitorInfo monitor, RecordingPreset preset, string temporaryPath, string borderlessAccess, AudioSelection? audio, bool simulateFirstSetupFailure = false)
    {
        var session = new RecordingSession(monitor, preset, temporaryPath, borderlessAccess, audio, simulateFirstSetupFailure);
        var thread = new Thread(session.Run)
        {
            Name = "EKrecorder recording",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
        return session._started.Task;
    }

    /// <summary>Asks the recording to stop; the task completes once the MP4 is finished and closed.</summary>
    public Task StopAsync(string reason)
    {
        StopReason ??= reason;
        _stopRequested = true;
        return _finished.Task;
    }

    private void Run()
    {
        CaptureDevice? device = null;
        IDXGIAdapter3* adapter3 = null;
        Route? route = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        bool started = false;
        try
        {
            Log.Info($"===== Recording {Monitor.Summary}");
            Log.Info($"Preset: {Preset.Describe()}");
            Log.Info($"Temporary file: {TemporaryPath}");
            EnsureStarted();

            device = CaptureDevice.CreateForMonitor(Monitor.Handle, forRecording: true);
            _winrtDevice = device.Device;
            DeviceDescription = device.Description;
            var d3dDevice = (ID3D11Device*)device.NativeDevice;
            // The capture, the video processor and the encoder all use this device from different threads.
            EnableMultithreadProtection(d3dDevice);
            adapter3 = TryGetAdapter3(d3dDevice);

            GraphicsCaptureItem item = CaptureItemFactory.CreateForMonitor(Monitor.Handle);
            _item = item;
            item.Closed += (_, _) =>
            {
                StopReason ??= "the monitor was disconnected or switched off";
                _itemClosed = true;
                Log.Warn("GraphicsCaptureItem.Closed: the monitor is gone; the recording stops and is saved.");
            };
            _poolSize = item.Size;
            CaptureSize = new Size(item.Size.Width, item.Size.Height);
            OutputSize = Preset.OutputSizeFor(CaptureSize);
            Log.Decision($"Output size {OutputSize.Width}x{OutputSize.Height} for a {CaptureSize.Width}x{CaptureSize.Height} monitor "
                + (OutputSize == CaptureSize ? "(no scaling)." : "(scaled down, aspect ratio kept, never upscaled)."));

            HardwareEncoders = H264Mp4Writer.ListHardwareEncoders();
            if (_audioSelection is not null)
            {
                // The devices open in the background while the video is set up (a Bluetooth headset can take a while).
                Audio = new AudioCapture(_audioSelection, forRecording: true);
                Audio.Start();
            }

            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, item.Size);
            session = pool.CreateCaptureSession(item);
            CaptureSettings = CaptureSessionSetup.Configure(session, Preset.FramesPerSecond, _borderlessAccess).Describe();
            pool.FrameArrived += OnFrameArrived;
            session.StartCapture();
            Log.Info("Capture started; waiting for the first frame.");
            if (!_firstFrame.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("Windows.Graphics.Capture delivered no frame within 5 seconds.");
            }

            route = OpenRoute(device, d3dDevice, out long firstFrameWritten);
            StartMixer(route);
            started = true;
            _started.TrySetResult(this);
            RunFrames(route, adapter3, firstFrameWritten);
        }
        catch (Exception ex)
        {
            Stats.AddError(ex.Message);
            if (!started)
            {
                Log.Error("The recording could not start", ex);
                _started.TrySetException(ex);
            }
            else
            {
                Log.Error("The recording stopped because of an error", ex);
                StopReason ??= "an error (see Errors)";
                if (route is not null)
                {
                    RecordFailureDetails(route, "the recording stopped because of an error", ex);
                }
            }
        }
        finally
        {
            if (Stats.StartTimestamp != 0 && Stats.EndTimestamp == 0)
            {
                Stats.EndTimestamp = Stopwatch.GetTimestamp();
            }

            // Stop the capture first, then let go of the frame we hold. Cleanup must never throw, or the window
            // would wait for this recording forever.
            try
            {
                if (pool != null)
                {
                    pool.FrameArrived -= OnFrameArrived;
                }

                session?.Dispose();
                pool?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Stopping the capture failed", ex);
            }

            lock (_frameLock)
            {
                _captureClosed = true;
                try
                {
                    _latest?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("Releasing the last captured frame failed", ex);
                }

                _latest = null;
            }

            FinishAudio();
            if (route != null)
            {
                CloseRoute(route);
            }

            if (adapter3 != null)
            {
                adapter3->Release();
            }

            try
            {
                device?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("Releasing the capture device failed", ex);
            }

            _item = null;
            if (!FileFinalized && Stats.FramesWritten == 0)
            {
                TryDelete(TemporaryPath);
            }

            _started.TrySetException(new InvalidOperationException("The recording ended before it started."));
            Log.Info($"Recording ended ({StopReason ?? "stopped"}): {Stats.FramesWritten} frames written, {Stats.FramesDuplicated} duplicated, {Stats.FramesDropped} dropped; file {(FileFinalized ? "finished" : "NOT finished")}.");
            _finished.TrySetResult();
        }
    }

    /// <summary>
    /// Sets up how frames reach the file and writes frame 0, which is the test of the set-up. Tries GPU frames into a
    /// hardware encoder, then CPU frames into a hardware encoder, then CPU frames into the software encoder. Returns
    /// the set-up that accepted frame 0 and the time it was written.
    /// </summary>
    private Route OpenRoute(CaptureDevice device, ID3D11Device* d3dDevice, out long firstFrameWritten)
    {
        var setups = new List<(string Name, Func<Route?> Open)>();
        if (device.HasVideoSupport)
        {
            setups.Add((GpuSetup, () => TryOpenGpuRoute(device, d3dDevice)));
        }
        else
        {
            Fallback(GpuSetup, "this GPU device has no Direct3D 11 video support");
        }

        setups.Add((CpuHardwareSetup, () => OpenCpuRoute(device, d3dDevice, hardware: true)));
        setups.Add((CpuSoftwareSetup, () => OpenCpuRoute(device, d3dDevice, hardware: false)));

        bool simulate = _simulateFirstSetupFailure;
        foreach ((string name, Func<Route?> open) in setups)
        {
            Log.Info($"Trying {name}.");
            Route? route;
            try
            {
                route = open();
            }
            catch (Exception ex)
            {
                Log.Error($"{name}: could not be set up", ex);
                Fallback(name, ex.Message);
                TryDelete(TemporaryPath);
                continue;
            }

            if (route is null)
            {
                // TryOpenGpuRoute has already noted why.
                TryDelete(TemporaryPath);
                continue;
            }

            try
            {
                if (simulate)
                {
                    simulate = false;
                    route.Writer.SimulateWriteFailure = true;
                }

                firstFrameWritten = Stopwatch.GetTimestamp();
                // The audio timeline starts with this frame, before it is encoded, so the file's first moments have
                // sound too. A refused set-up re-anchors it on the next set-up's frame 0.
                AnchorAudio(route, firstFrameWritten);
                if (!ProduceFrame(0, route))
                {
                    throw new InvalidOperationException("Frame 0 could not be written.");
                }

                FramePath = route.Describe();
                UsesGpuPath = route.Gpu is not null;
                Encoder = route.Writer.Encoder;
                ConverterDescription = route.ConverterDescription;
                EncoderInputType = route.Writer.InputType;
                EncoderInputStream = route.Writer.EncoderInputStream;
                Log.Info($"Frame 0 accepted. In use: {FramePath}");
                return route;
            }
            catch (Exception ex)
            {
                RecordSetupFailure(name, route, ex);
                route.Dispose();
                TryDelete(TemporaryPath);
                lock (_frameLock)
                {
                    // The next set-up starts again from the newest captured frame.
                    _latestUnused = _latest is not null;
                }
            }
        }

        throw new InvalidOperationException($"No way to record could be set up. {string.Join(" | ", _fallbacks)}");
    }

    /// <summary>
    /// Starts the audio timeline at video frame 0 (minus <see cref="AudioOffset"/>) and the mixer that feeds the
    /// file's audio track.
    /// </summary>
    private void AnchorAudio(Route route, long firstFrameWritten)
    {
        if (Audio is null || !route.Writer.HasAudio)
        {
            return;
        }

        long startHns = TimelineClock.ToHns(firstFrameWritten, Stopwatch.Frequency) - AudioOffset.Ticks;
        _audioClock = new TimelineClock(startHns);
        Audio.BeginTimeline(_audioClock);
    }

    /// <summary>Starts the mixer that feeds the file's audio track, once the set-up is chosen.</summary>
    private void StartMixer(Route route)
    {
        if (Audio is null)
        {
            return;
        }

        if (!route.Writer.HasAudio || _audioClock is null)
        {
            // No audio track: nothing to record, so no device stays open.
            AudioError = route.Writer.AudioError ?? "the file has no audio track";
            Stats.AddError($"No audio track: {AudioError}");
            Audio.Stop();
            return;
        }

        Mixer = new AudioMixer(Audio.Rings, _audioClock, route.Writer.WriteAudio);
        Mixer.Start();
    }

    /// <summary>Writes the audio to the end of the last video frame, then closes the audio devices.</summary>
    private void FinishAudio()
    {
        try
        {
            Audio?.EndTimeline();
            if (Mixer is not null)
            {
                long endFrame = Slots * TimelineClock.SampleRate / Preset.FramesPerSecond;
                if (!Mixer.Finish(endFrame, TimeSpan.FromSeconds(5)))
                {
                    Stats.AddError("The audio mixer did not finish within 5 s; the end of the audio track may be missing.");
                }

                if (Mixer.Error is not null)
                {
                    Stats.AddError($"The audio track ended early: {Mixer.Error}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Finishing the audio failed", ex);
            Stats.AddError($"Finishing the audio failed: {ex.Message}");
        }
        finally
        {
            // Whatever happened above, no audio device stays open after the recording.
            try
            {
                Audio?.Stop();
            }
            catch (Exception ex)
            {
                Log.Error("Closing the audio devices failed", ex);
            }
        }
    }

    /// <summary>GPU frames into a hardware encoder, or null (with the reason in <see cref="Fallbacks"/>).</summary>
    private Route? TryOpenGpuRoute(CaptureDevice device, ID3D11Device* d3dDevice)
    {
        IMFDXGIDeviceManager* manager = TryCreateDeviceManager(d3dDevice);
        if (manager == null)
        {
            Fallback(GpuSetup, "Media Foundation could not share the GPU device with the encoder (see the log)");
            return null;
        }

        GpuFrameConverter? gpu = null;
        H264Mp4Writer? writer = null;
        GpuSamplePool? samples = null;
        try
        {
            gpu = GpuFrameConverter.TryCreate(d3dDevice, CaptureSize, OutputSize, Preset.FramesPerSecond);
            if (gpu is null)
            {
                Fallback(GpuSetup, "the Direct3D 11 video processor cannot scale and convert these frames (see the log)");
                return null;
            }

            writer = H264Mp4Writer.Create(TemporaryPath, OutputSize, Preset, manager, gpuInput: true, hardwareAllowed: true, device.Adapter, withAudio: Audio is not null);
            if (!writer.Encoder.IsHardware)
            {
                Fallback(GpuSetup, "no hardware encoder accepted the settings (GPU frames are only used with a hardware encoder)");
                return null;
            }

            samples = CreateSamplePool(manager, gpu, writer, out string? problem);
            if (samples is null)
            {
                Fallback(GpuSetup, $"no GPU textures that both the video processor and the encoder can use: {problem}");
                return null;
            }

            var route = new Route(GpuSetup, writer, gpu, samples, null, manager);
            writer = null;
            gpu = null;
            samples = null;
            manager = null;
            return route;
        }
        finally
        {
            // The writer first: releasing it releases the encoder, which hands any GPU sample back to the pool.
            writer?.Dispose();
            samples?.Dispose();
            gpu?.Dispose();
            if (manager != null)
            {
                manager->Release();
            }
        }
    }

    /// <summary>CPU frames into the encoder; a hardware one when allowed and available, else the software one.</summary>
    private Route OpenCpuRoute(CaptureDevice device, ID3D11Device* d3dDevice, bool hardware)
    {
        var cpu = new CpuFrameConverter(d3dDevice, OutputSize);
        try
        {
            // No device manager: a hardware encoder then takes frames from memory and runs on its own GPU device.
            H264Mp4Writer writer = H264Mp4Writer.Create(TemporaryPath, OutputSize, Preset, null, gpuInput: false, hardwareAllowed: hardware, device.Adapter, withAudio: Audio is not null);
            var route = new Route(hardware ? CpuHardwareSetup : CpuSoftwareSetup, writer, null, null, cpu, null);
            cpu = null;
            return route;
        }
        finally
        {
            cpu?.Dispose();
        }
    }

    /// <summary>
    /// The GPU textures the video processor draws into and the encoder reads. When the encoder names bind flags for
    /// its input textures, those are tried first (on top of RENDER_TARGET, which the video processor needs), then
    /// RENDER_TARGET alone. Each candidate is checked by drawing a view onto one of its textures.
    /// </summary>
    private GpuSamplePool? CreateSamplePool(IMFDXGIDeviceManager* manager, GpuFrameConverter gpu, H264Mp4Writer writer, out string? problem)
    {
        problem = null;
        var candidates = new List<(uint BindFlags, bool Shared)>();
        uint requested = writer.RequestedBindFlags ?? 0;
        if ((requested & ~RenderTarget) != 0 || writer.RequestedSharedWithoutMutex)
        {
            candidates.Add((requested | RenderTarget, writer.RequestedSharedWithoutMutex));
            Log.Decision($"The encoder asks for input textures with bind {MediaFoundationDiagnostics.BindFlagsText(requested)}{(writer.RequestedSharedWithoutMutex ? ", shared" : "")}; trying that first.");
        }

        candidates.Add((RenderTarget, false));
        foreach ((uint bindFlags, bool shared) in candidates)
        {
            GpuSamplePool? samples = GpuSamplePool.TryCreate(manager, OutputSize, Preset.FramesPerSecond, bindFlags, shared);
            if (samples is null)
            {
                problem = $"the GPU video samples (bind {MediaFoundationDiagnostics.BindFlagsText(bindFlags)}) could not be created (see the log)";
                continue;
            }

            problem = CheckPool(gpu, samples);
            if (problem is null)
            {
                return samples;
            }

            samples.Dispose();
        }

        return null;
    }

    private void RecordSetupFailure(string name, Route route, Exception ex)
    {
        Log.Error($"{name}: frame 0 was refused", ex);
        Fallback(name, $"frame 0 was refused: {ex.Message}");
        string details;
        try
        {
            details = FailureDetails($"{name}: frame 0 was refused", route, ex);
        }
        catch (Exception describeError)
        {
            // Never let the description stop the fallback.
            Log.Error("Describing the refused set-up failed", describeError);
            details = $"--- {name}: frame 0 was refused{Environment.NewLine}Error: {ex.Message}{Environment.NewLine}(describing it failed: {describeError.Message}){Environment.NewLine}";
        }

        _setupFailures.Append(details);
        Log.Info($"Details of the refused set-up:{Environment.NewLine}{details}");
    }

    private void RecordFailureDetails(Route route, string what, Exception ex)
    {
        try
        {
            string details = FailureDetails($"{route.Name}: {what}", route, ex);
            RecordingFailure += details;
            Log.Info($"Failure details:{Environment.NewLine}{details}");
        }
        catch (Exception describeError)
        {
            Log.Error("Describing the failure failed", describeError);
        }
    }

    /// <summary>The error, the sample that was sent (when known), the GPU samples and everything about the encoder.</summary>
    private static string FailureDetails(string title, Route route, Exception ex)
    {
        var text = new StringBuilder();
        text.AppendLine($"--- {title}");
        text.AppendLine($"Error: {ex.Message}");
        if (ex is MediaFoundationException { Details: { } details })
        {
            text.AppendLine($"Sample sent: {details}");
        }

        text.AppendLine($"Frame conversion: {route.ConverterDescription}");
        if (route.Samples is not null)
        {
            text.AppendLine($"GPU samples: {route.Samples.Summary}");
        }

        text.AppendLine(route.Writer.Diagnostics());
        return text.ToString();
    }

    private void Fallback(string setup, string reason)
    {
        _fallbacks.Add($"{setup}: {reason}");
        Log.Decision($"Not using {setup}: {reason}.");
    }

    /// <summary>Finishes the file (if any frame was written) and releases the set-up.</summary>
    private void CloseRoute(Route route)
    {
        WriterStatistics = route.Writer.Statistics();
        AudioStatistics = route.Writer.AudioStatistics();
        GpuSamples = route.Samples?.Summary ?? "";
        if (Stats.FramesWritten > 0)
        {
            long finalizeStart = Stopwatch.GetTimestamp();
            try
            {
                route.Writer.FinishFile();
                FileFinalized = true;
            }
            catch (Exception ex)
            {
                Log.Error("Finishing the MP4 file failed", ex);
                Stats.AddError($"Finishing the MP4 file failed: {ex.Message}");
                RecordFailureDetails(route, "finishing the MP4 file failed", ex);
            }

            Stats.FinalizeSeconds = Stopwatch.GetElapsedTime(finalizeStart).TotalSeconds;
        }

        try
        {
            route.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Releasing the encoder and converters failed", ex);
        }
    }

    private void RunFrames(Route route, IDXGIAdapter3* adapter3, long start)
    {
        using var timer = new HighResolutionTimer();
        PacingTimer = timer.IsHighResolution ? "high-resolution waitable timer" : "standard waitable timer";
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuAtStart = process.TotalProcessorTime;
        long frequency = Stopwatch.Frequency;
        int fps = Preset.FramesPerSecond;
        Stats.StartTimestamp = start;
        Stats.StartedLocal = DateTime.Now;
        Log.Info($"Recording: pacing {fps} fps with a {PacingTimer}.");

        // Frame 0 was written when the set-up was tested; the clock starts there.
        long slot = 1;
        Slots = 1;
        long nextResourceSample = 0;
        int lateWarnings = 0;
        try
        {
            while (!_stopRequested && !_itemClosed)
            {
                long due = start + (slot * frequency / fps);
                timer.WaitUntil(due);
                long now = Stopwatch.GetTimestamp();
                double lateMs = (now - due) * 1000.0 / frequency;
                Stats.Ticks++;
                Stats.LatenessSumMs += lateMs;
                Stats.LatenessMaxMs = Math.Max(Stats.LatenessMaxMs, lateMs);

                long behind = (now - due) * fps / frequency;
                if (behind > 0)
                {
                    // Held up for more than a frame: the slots that already passed are dropped (the picture holds).
                    Stats.FramesDroppedLate += behind;
                    slot += behind;
                    if (lateWarnings++ < 10)
                    {
                        Log.Warn($"Pacing fell {behind} frame(s) behind ({lateMs:0} ms late); those frames are dropped.");
                    }
                }

                long workStart = Stopwatch.GetTimestamp();
                ProduceFrame(slot, route);
                double workMs = Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;
                Stats.WorkSumMs += workMs;
                Stats.WorkMaxMs = Math.Max(Stats.WorkMaxMs, workMs);
                slot++;
                Slots = slot;

                if (slot >= nextResourceSample)
                {
                    nextResourceSample = slot + (2 * fps);
                    SampleResources(adapter3);
                }
            }
        }
        finally
        {
            Stats.EndTimestamp = Stopwatch.GetTimestamp();
            process.Refresh();
            Stats.CpuTime = process.TotalProcessorTime - cpuAtStart;
            Stats.PrivateBytesPeak = Math.Max(Stats.PrivateBytesPeak, process.PrivateMemorySize64);
            SampleResources(adapter3);
        }
    }

    /// <summary>Encodes output frame <paramref name="slot"/> from the newest captured frame. False if it was dropped.</summary>
    private bool ProduceFrame(long slot, Route route)
    {
        long time = Preset.FrameTime(slot);
        long duration = Preset.FrameTime(slot + 1) - time;
        bool fresh;
        bool convertOnCpu = false;
        IMFSample* sample = null;
        lock (_frameLock)
        {
            HeldFrame? frame = _latest;
            if (frame is null)
            {
                Stats.FramesDroppedLate++;
                return false;
            }

            fresh = _latestUnused;
            if (route.Gpu is not null)
            {
                if (!route.Samples!.TryTake(out sample, out ID3D11Texture2D* target, out uint subresource))
                {
                    // Every GPU sample is still with the encoder: it has fallen behind. Drop this slot.
                    Stats.FramesDroppedEncoderBusy++;
                    return false;
                }

                try
                {
                    route.Gpu.Convert(frame.Texture, frame.ContentSize, target, subresource);
                }
                catch
                {
                    sample->Release();
                    throw;
                }
                finally
                {
                    target->Release();
                }
            }
            else if (fresh || !route.CpuHasFrame)
            {
                route.Cpu!.CopyFrom(frame.Texture, frame.ContentSize);
                convertOnCpu = true;
            }

            _latestUnused = false;
        }

        // Outside the lock: the GPU work is already queued; the CPU path waits for its copy here.
        long writeStart = Stopwatch.GetTimestamp();
        if (route.Gpu is not null)
        {
            try
            {
                route.Writer.WriteSample(sample, time, duration);
            }
            finally
            {
                sample->Release();
            }
        }
        else
        {
            if (convertOnCpu)
            {
                route.Cpu!.Convert();
                route.CpuHasFrame = true;
            }

            route.Writer.WriteNv12(route.Cpu!.LastFrame, time, duration);
        }

        Stats.WriteMaxMs = Math.Max(Stats.WriteMaxMs, Stopwatch.GetElapsedTime(writeStart).TotalMilliseconds);
        Stats.FramesWritten++;
        if (!fresh)
        {
            Stats.FramesDuplicated++;
        }

        return true;
    }

    /// <summary>
    /// Takes one sample from a new pool (which also sets its buffer length) and checks that the video processor can
    /// draw into its texture. Null when it can; otherwise why not.
    /// </summary>
    private static string? CheckPool(GpuFrameConverter gpu, GpuSamplePool samples)
    {
        IMFSample* sample;
        ID3D11Texture2D* target;
        uint subresource;
        try
        {
            if (!samples.TryTake(out sample, out target, out subresource))
            {
                return "the new sample pool had no free sample";
            }
        }
        catch (MediaFoundationException ex)
        {
            Log.Error("Preparing a GPU sample failed", ex);
            return ex.Message;
        }

        try
        {
            gpu.CheckTarget(target, subresource);
            return null;
        }
        catch (MediaFoundationException ex)
        {
            Log.Error("The video processor cannot draw into the encoder's GPU textures", ex);
            return ex.Message;
        }
        finally
        {
            target->Release();
            sample->Release();
        }
    }

    /// <summary>Runs on a capture thread for every captured frame: keeps the newest one, lets go of the one before.</summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        HeldFrame? held = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            Stats.CountCaptureFrame();
            SizeInt32 size = frame.ContentSize;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                Log.Warn($"Monitor resolution changed from {_poolSize.Width}x{_poolSize.Height} to {size.Width}x{size.Height}; the output keeps {OutputSize.Width}x{OutputSize.Height} and fits the picture in.");
                _poolSize = size;
                sender.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, size);
            }

            // The surface wrapper is not disposed on purpose: Close() on it could close the frame's own surface.
            held = new HeldFrame(frame, TextureOf(frame.Surface), new Size(size.Width, size.Height));
            frame = null;
            HeldFrame? replaced;
            lock (_frameLock)
            {
                if (_captureClosed)
                {
                    replaced = held;
                }
                else
                {
                    replaced = _latest;
                    if (replaced is not null && _latestUnused)
                    {
                        Stats.CountReplacedCaptureFrame();
                    }

                    _latest = held;
                    _latestUnused = true;
                }

                held = null;
            }

            replaced?.Dispose();
            _firstFrame.Set();
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _captureErrors) <= MaxLoggedErrors)
            {
                Log.Error("Handling a captured frame failed", ex);
                Stats.AddError($"Capture: {ex.Message}");
            }
        }
        finally
        {
            held?.Dispose();
            frame?.Dispose();
        }
    }

    private void SampleResources(IDXGIAdapter3* adapter3)
    {
        Stats.WorkingSetPeakBytes = Math.Max(Stats.WorkingSetPeakBytes, Environment.WorkingSet);
        if (adapter3 == null)
        {
            return;
        }

        ulong used = 0;
        DXGI_QUERY_VIDEO_MEMORY_INFO local;
        if (adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local).SUCCEEDED)
        {
            used += local.CurrentUsage;
        }

        DXGI_QUERY_VIDEO_MEMORY_INFO shared;
        if (adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &shared).SUCCEEDED)
        {
            used += shared.CurrentUsage;
        }

        Stats.GpuMemoryPeakBytes = Math.Max(Stats.GpuMemoryPeakBytes, used);
    }

    /// <summary>The ID3D11Texture2D behind a captured surface (AddRef'ed).</summary>
    private static ID3D11Texture2D* TextureOf(IDirect3DSurface surface)
    {
        nint unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        IUnknown* access = null;
        try
        {
            Guid iid = IID_IDirect3DDxgiInterfaceAccess;
            Check(((IUnknown*)unknown)->QueryInterface(&iid, (void**)&access), "QueryInterface(IDirect3DDxgiInterfaceAccess)");
            ID3D11Texture2D* texture;
            // IDirect3DDxgiInterfaceAccess: IUnknown 0-2, GetInterface = 3
            int hr = ((delegate* unmanaged[Stdcall]<IUnknown*, Guid*, void**, int>)(*(void***)access)[3])(access, __uuidof<ID3D11Texture2D>(), (void**)&texture);
            Check(hr, "IDirect3DDxgiInterfaceAccess::GetInterface(ID3D11Texture2D)");
            return texture;
        }
        finally
        {
            if (access != null)
            {
                access->Release();
            }

            Marshal.Release(unknown);
        }
    }

    private static void EnableMultithreadProtection(ID3D11Device* device)
    {
        ID3D10Multithread* multithread;
        HRESULT hr = device->QueryInterface(__uuidof<ID3D10Multithread>(), (void**)&multithread);
        if (hr.FAILED)
        {
            Log.Api("ID3D11Device::QueryInterface(ID3D10Multithread)", false, Describe(hr));
            return;
        }

        multithread->SetMultithreadProtected(TRUE);
        bool enabled = multithread->GetMultithreadProtected();
        multithread->Release();
        Log.Api("ID3D10Multithread::SetMultithreadProtected(TRUE)", enabled, $"protected = {enabled}");
    }

    private static IMFDXGIDeviceManager* TryCreateDeviceManager(ID3D11Device* device)
    {
        uint resetToken;
        IMFDXGIDeviceManager* manager;
        HRESULT hr = MFCreateDXGIDeviceManager(&resetToken, &manager);
        if (hr.FAILED)
        {
            Log.Api("MFCreateDXGIDeviceManager", false, Describe(hr));
            return null;
        }

        hr = manager->ResetDevice((IUnknown*)device, resetToken);
        if (hr.FAILED)
        {
            Log.Api("IMFDXGIDeviceManager::ResetDevice", false, Describe(hr));
            manager->Release();
            return null;
        }

        Log.Api("MFCreateDXGIDeviceManager + ResetDevice", true, "the encoder can take frames straight from the capture GPU");
        return manager;
    }

    private static IDXGIAdapter3* TryGetAdapter3(ID3D11Device* device)
    {
        IDXGIDevice* dxgiDevice;
        if (device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgiDevice).FAILED)
        {
            return null;
        }

        IDXGIAdapter* adapter;
        HRESULT hr = dxgiDevice->GetAdapter(&adapter);
        dxgiDevice->Release();
        if (hr.FAILED)
        {
            return null;
        }

        IDXGIAdapter3* adapter3;
        hr = adapter->QueryInterface(__uuidof<IDXGIAdapter3>(), (void**)&adapter3);
        adapter->Release();
        return hr.SUCCEEDED ? adapter3 : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }

    /// <summary>One way from captured frame to file: the converter, the GPU samples (GPU path only) and the writer.</summary>
    private sealed class Route : IDisposable
    {
        private IMFDXGIDeviceManager* _manager;

        public Route(string name, H264Mp4Writer writer, GpuFrameConverter? gpu, GpuSamplePool? samples, CpuFrameConverter? cpu, IMFDXGIDeviceManager* manager)
        {
            Name = name;
            Writer = writer;
            Gpu = gpu;
            Samples = samples;
            Cpu = cpu;
            _manager = manager;
        }

        public string Name { get; }

        public H264Mp4Writer Writer { get; }

        public GpuFrameConverter? Gpu { get; }

        public GpuSamplePool? Samples { get; }

        public CpuFrameConverter? Cpu { get; }

        /// <summary>CPU path: the converter holds a converted frame, which an unchanged screen reuses.</summary>
        public bool CpuHasFrame { get; set; }

        public string ConverterDescription => Gpu?.Description ?? Cpu?.Description ?? "";

        public string Describe() => (Gpu is not null
            ? "GPU frames (Direct3D 11 video processor; frames stay on the GPU)"
            : "CPU frames (copied to memory, scaled on the CPU)") + $" -> {Writer.Encoder.Summary}";

        public void Dispose()
        {
            // The writer first: releasing it releases the encoder, which hands every GPU sample back to the pool.
            Writer.Dispose();
            Samples?.Dispose();
            Gpu?.Dispose();
            Cpu?.Dispose();
            if (_manager != null)
            {
                _manager->Release();
                _manager = null;
            }
        }
    }

    /// <summary>A captured frame we keep until a newer one arrives, with its texture.</summary>
    private sealed class HeldFrame : IDisposable
    {
        private readonly Direct3D11CaptureFrame _frame;

        public HeldFrame(Direct3D11CaptureFrame frame, ID3D11Texture2D* texture, Size contentSize)
        {
            _frame = frame;
            Texture = texture;
            ContentSize = contentSize;
        }

        public ID3D11Texture2D* Texture { get; private set; }

        public Size ContentSize { get; }

        public void Dispose()
        {
            if (Texture != null)
            {
                Texture->Release();
                Texture = null;
            }

            _frame.Dispose();
        }
    }
}
