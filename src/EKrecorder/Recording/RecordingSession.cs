using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// screen has not changed, so the file has a constant frame rate. Frames stay on the GPU on the normal path.
/// </para>
/// </summary>
internal sealed unsafe class RecordingSession
{
    private const int PoolBuffers = 3;
    private const int MaxLoggedErrors = 50;
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly object _frameLock = new();
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private readonly TaskCompletionSource<RecordingSession> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _borderlessAccess;
    private HeldFrame? _latest;
    private bool _latestUnused;
    private bool _captureClosed;
    private bool _cpuHasFrame;
    private int _captureErrors;
    private SizeInt32 _poolSize;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item; // kept in a field so it (and its Closed handler) lives as long as the recording
    private volatile bool _stopRequested;
    private volatile bool _itemClosed;

    private RecordingSession(MonitorInfo monitor, RecordingPreset preset, string temporaryPath, string borderlessAccess)
    {
        Monitor = monitor;
        Preset = preset;
        TemporaryPath = temporaryPath;
        _borderlessAccess = borderlessAccess;
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

    public IReadOnlyList<string> HardwareEncoders { get; private set; } = [];

    public EncoderInfo? Encoder { get; private set; }

    /// <summary>Output frames written so far (slot count, including skipped ones, decides the duration).</summary>
    public long Slots { get; private set; }

    public bool FileFinalized { get; private set; }

    public string? StopReason { get; private set; }

    /// <summary>Completes when the recording has stopped and the file is closed (also after a self-stop or error).</summary>
    public Task Completion => _finished.Task;

    /// <summary>Starts recording; returns once the first frame has been captured and the encoder is ready.</summary>
    public static Task<RecordingSession> StartAsync(MonitorInfo monitor, RecordingPreset preset, string temporaryPath, string borderlessAccess)
    {
        var session = new RecordingSession(monitor, preset, temporaryPath, borderlessAccess);
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
        IMFDXGIDeviceManager* manager = null;
        IDXGIAdapter3* adapter3 = null;
        GpuFrameConverter? gpu = null;
        GpuSamplePool? samples = null;
        CpuFrameConverter? cpu = null;
        H264Mp4Writer? writer = null;
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

            if (device.HasVideoSupport)
            {
                manager = TryCreateDeviceManager(d3dDevice);
                if (manager != null)
                {
                    gpu = GpuFrameConverter.TryCreate(d3dDevice, CaptureSize, OutputSize, Preset.FramesPerSecond);
                    if (gpu != null)
                    {
                        samples = GpuSamplePool.TryCreate(manager, OutputSize, Preset.FramesPerSecond);
                        if (samples == null || !CanConvertInto(gpu, samples))
                        {
                            samples?.Dispose();
                            samples = null;
                            gpu.Dispose();
                            gpu = null;
                        }
                    }
                }
            }

            if (gpu == null)
            {
                Log.Decision("Scaling and colour conversion fall back to the CPU.");
                cpu = new CpuFrameConverter(d3dDevice, OutputSize);
            }

            ConverterDescription = gpu?.Description ?? cpu!.Description;
            writer = H264Mp4Writer.Create(TemporaryPath, OutputSize, Preset, manager, gpuInput: gpu != null, device.Adapter);
            Encoder = writer.Encoder;

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

            started = true;
            _started.TrySetResult(this);
            RunFrames(gpu, samples, cpu, writer, adapter3);
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

            if (writer != null)
            {
                WriterStatistics = writer.Statistics();
                if (Stats.FramesWritten > 0)
                {
                    long finalizeStart = Stopwatch.GetTimestamp();
                    try
                    {
                        writer.FinishFile();
                        FileFinalized = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Finishing the MP4 file failed", ex);
                        Stats.AddError($"Finishing the MP4 file failed: {ex.Message}");
                    }

                    Stats.FinalizeSeconds = Stopwatch.GetElapsedTime(finalizeStart).TotalSeconds;
                }

                writer.Dispose();
            }

            // The encoder is gone now, so every sample is back in the pool.
            samples?.Dispose();
            gpu?.Dispose();
            cpu?.Dispose();
            if (manager != null)
            {
                manager->Release();
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

    private void RunFrames(GpuFrameConverter? gpu, GpuSamplePool? samples, CpuFrameConverter? cpu, H264Mp4Writer writer, IDXGIAdapter3* adapter3)
    {
        using var timer = new HighResolutionTimer();
        PacingTimer = timer.IsHighResolution ? "high-resolution waitable timer" : "standard waitable timer";
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuAtStart = process.TotalProcessorTime;
        long frequency = Stopwatch.Frequency;
        int fps = Preset.FramesPerSecond;
        long start = Stopwatch.GetTimestamp();
        Stats.StartTimestamp = start;
        Stats.StartedLocal = DateTime.Now;
        Log.Info($"Recording: pacing {fps} fps with a {PacingTimer}.");

        long slot = 0;
        long nextResourceSample = 0;
        int lateWarnings = 0;
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
            ProduceFrame(slot, gpu, samples, cpu, writer);
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

        Stats.EndTimestamp = Stopwatch.GetTimestamp();
        process.Refresh();
        Stats.CpuTime = process.TotalProcessorTime - cpuAtStart;
        Stats.PrivateBytesPeak = Math.Max(Stats.PrivateBytesPeak, process.PrivateMemorySize64);
        SampleResources(adapter3);
    }

    /// <summary>Encodes output frame <paramref name="slot"/> from the newest captured frame.</summary>
    private void ProduceFrame(long slot, GpuFrameConverter? gpu, GpuSamplePool? samples, CpuFrameConverter? cpu, H264Mp4Writer writer)
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
                return;
            }

            fresh = _latestUnused;
            if (gpu is not null)
            {
                if (!samples!.TryTake(out sample, out ID3D11Texture2D* target, out uint subresource))
                {
                    // Every GPU sample is still with the encoder: it has fallen behind. Drop this slot.
                    Stats.FramesDroppedEncoderBusy++;
                    return;
                }

                try
                {
                    gpu.Convert(frame.Texture, frame.ContentSize, target, subresource);
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
            else if (fresh || !_cpuHasFrame)
            {
                cpu!.CopyFrom(frame.Texture, frame.ContentSize);
                convertOnCpu = true;
            }

            _latestUnused = false;
        }

        // Outside the lock: the GPU work is already queued; the CPU path waits for its copy here.
        long writeStart = Stopwatch.GetTimestamp();
        if (gpu is not null)
        {
            try
            {
                writer.WriteSample(sample, time, duration);
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
                cpu!.Convert();
                _cpuHasFrame = true;
            }

            writer.WriteNv12(cpu!.LastFrame, time, duration);
        }

        Stats.WriteMaxMs = Math.Max(Stats.WriteMaxMs, Stopwatch.GetElapsedTime(writeStart).TotalMilliseconds);
        Stats.FramesWritten++;
        if (!fresh)
        {
            Stats.FramesDuplicated++;
        }
    }

    /// <summary>Checks that the video processor can write into the pool's textures before the recording relies on it.</summary>
    private static bool CanConvertInto(GpuFrameConverter gpu, GpuSamplePool samples)
    {
        if (!samples.TryTake(out IMFSample* sample, out ID3D11Texture2D* target, out uint subresource))
        {
            return false;
        }

        try
        {
            gpu.CheckTarget(target, subresource);
            return true;
        }
        catch (MediaFoundationException ex)
        {
            Log.Error("The video processor cannot write into the encoder's GPU textures", ex);
            return false;
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
