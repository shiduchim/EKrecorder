using System.Diagnostics;
using EKrecorder.Diagnostics;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Audio;

/// <summary>Where a capture stream is in its life.</summary>
internal enum WorkerState
{
    Opening,
    Running,
    Failed,
    Stopped,
}

/// <summary>Set once, when the recording's timeline starts; capture threads read it for every packet.</summary>
internal sealed class TimelineAnchor
{
    private TimelineClock? _clock;

    public TimelineClock? Clock => Volatile.Read(ref _clock);

    public void Start(TimelineClock clock) => Volatile.Write(ref _clock, clock);
}

/// <summary>
/// One WASAPI capture stream on its own thread: a microphone, or an output device's loopback (what it plays).
/// Shared mode, event-driven, 500 ms buffer so a garbage-collection pause cannot overflow it. Windows is asked to
/// deliver 48 kHz float in the wanted channel count; if a device refuses, its own mix format is taken and converted
/// here. Each packet is placed on the recording timeline by its QPC timestamp (<see cref="TimelineWriter"/>).
/// Any failure (device unplugged, disabled, reconfigured, the audio service restarting) ends the thread with
/// <see cref="WorkerState.Failed"/>; the supervisor decides when to try again. Nothing is allocated per packet.
/// </summary>
internal sealed unsafe class CaptureWorker
{
    private const long BufferDuration = 5_000_000;        // 500 ms, in 100-ns units
    private const long StallTimeout = 30_000_000;         // a microphone silent for 3 s (no packets at all) is stuck
    private const long MaxTimestampSkew = 20_000_000;     // a packet time more than 2 s from now is not trusted
    private const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const uint AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;
    private const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT and KSDATAFORMAT_SUBTYPE_PCM (ksmedia.h).
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly TimelineRing? _ring;
    private readonly TimelineAnchor _anchor;
    private readonly Action<CaptureWorker> _changed;
    private readonly Thread _thread;
    private volatile bool _stopRequested;
    private int _state = (int)WorkerState.Opening;
    private float _peakSinceRead;
    private long _zeroFrames;
    private int _rate = TimelineClock.SampleRate;
    private TimelineWriter? _writer;

    /// <param name="ring">Where the audio goes on the timeline; null when only levels are wanted (meters).</param>
    /// <param name="changed">Called (on this worker's thread) whenever <see cref="State"/> changes.</param>
    public CaptureWorker(string endpointId, string name, bool loopback, TimelineRing? ring, TimelineAnchor anchor, Action<CaptureWorker> changed)
    {
        EndpointId = endpointId;
        Name = name;
        Loopback = loopback;
        _ring = ring;
        _anchor = anchor;
        _changed = changed;
        Channels = loopback ? 2 : 1;
        _thread = new Thread(Run)
        {
            Name = $"EKrecorder audio: {(loopback ? "loopback" : "microphone")} {name}",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
    }

    public string EndpointId { get; }

    public string Name { get; }

    /// <summary>True for computer audio (loopback of an output device), false for a microphone.</summary>
    public bool Loopback { get; }

    /// <summary>1 for a microphone (mono, centred in the mix), 2 for computer audio.</summary>
    public int Channels { get; }

    public TimelineRing? Ring => _ring;

    public WorkerState State => (WorkerState)Volatile.Read(ref _state);

    /// <summary>Why it failed, in words (with the HRESULT).</summary>
    public string? Failure { get; private set; }

    /// <summary>The stream's format: what the device mixes at, and what EKrecorder receives.</summary>
    public string Format { get; private set; } = "";

    public long Packets { get; private set; }

    public long Frames { get; private set; }

    /// <summary>Packets Windows flagged as not following on from the previous one (data was lost in between).</summary>
    public long Discontinuities { get; private set; }

    /// <summary>Packets whose QPC time was missing or implausible, so it was estimated from the arrival time.</summary>
    public long EstimatedTimestamps { get; private set; }

    /// <summary>The highest level seen, 0 to 1 (full scale).</summary>
    public float MaxPeak { get; private set; }

    /// <summary>How long the stream has carried nothing but exact digital zero, right now.</summary>
    public double DigitalSilenceSeconds => Volatile.Read(ref _zeroFrames) / (double)_rate;

    /// <summary>The longest stretch of exact digital zero.</summary>
    public double LongestDigitalSilenceSeconds { get; private set; }

    /// <summary>The device clock's drift against the QPC clock, as corrected (ppm).</summary>
    public double DriftPpm => _writer?.CorrectionPpm ?? 0;

    /// <summary>Times the stream had to be re-placed by more than 20 ms (gaps, glitches, loopback silence).</summary>
    public long Resyncs => _writer?.Resyncs ?? 0;

    /// <summary>When the worker opened the stream, and when it ended (QPC, 100-ns units; 0 when not yet).</summary>
    public long RunningSinceHns { get; private set; }

    public long EndedHns { get; private set; }

    /// <summary>When the thread was started (QPC, 100-ns units); a stream still opening long after this is stuck.</summary>
    public long StartedHns { get; private set; }

    public void Start()
    {
        StartedHns = TimelineClock.NowHns();
        _thread.Start();
    }

    /// <summary>Asks the thread to close the stream; it notices within about 100 ms.</summary>
    public void RequestStop() => _stopRequested = true;

    /// <summary>Waits for the thread to end. False if it did not (a driver call hanging); it is a background thread.</summary>
    public bool Join(TimeSpan timeout) => _thread.ThreadState == System.Threading.ThreadState.Unstarted || _thread.Join(timeout);

    /// <summary>The highest level since the last call (for meters).</summary>
    public float TakePeak() => Interlocked.Exchange(ref _peakSinceRead, 0f);

    private void Run()
    {
        bool uninitialize = CoreAudio.EnterMta();
        nint mmcss = CoreAudio.EnterMmcss();
        IMMDeviceEnumerator* enumerator = null;
        IMMDevice* device = null;
        IAudioClient* client = null;
        IAudioCaptureClient* capture = null;
        WAVEFORMATEX* mixFormat = null;
        HANDLE ready = default;
        bool started = false;
        try
        {
            enumerator = CoreAudio.CreateEnumerator();
            device = CoreAudio.OpenDevice(enumerator, EndpointId);
            if (device == null)
            {
                throw new AudioException("the device no longer exists");
            }

            uint deviceState;
            CoreAudio.Check(device->GetState(&deviceState), "IMMDevice::GetState");
            if (deviceState != DEVICE.DEVICE_STATE_ACTIVE)
            {
                throw new AudioException($"the device is not active (state {deviceState})");
            }

            client = Activate(device);
            CoreAudio.Check(client->GetMixFormat(&mixFormat), "IAudioClient::GetMixFormat");
            CaptureFormat deviceFormat = FormatOf(mixFormat);
            CaptureFormat format = InitializeStream(device, ref client, mixFormat, deviceFormat);
            _rate = format.SampleRate;

            ready = CreateEventW(null, FALSE, FALSE, null);
            if (ready.Value == null)
            {
                throw new AudioException("CreateEvent failed");
            }

            CoreAudio.Check(client->SetEventHandle(ready), "IAudioClient::SetEventHandle");
            uint bufferFrames;
            CoreAudio.Check(client->GetBufferSize(&bufferFrames), "IAudioClient::GetBufferSize");
            CoreAudio.Check(client->GetService(__uuidof<IAudioCaptureClient>(), (void**)&capture), "IAudioClient::GetService(IAudioCaptureClient)");

            var converter = new SampleConverter(format, Channels);
            var samples = new float[(int)bufferFrames * Channels];
            _writer = _ring is null ? null : new TimelineWriter(_ring, format.SampleRate, Channels);
            Log.Info($"Audio {(Loopback ? "loopback" : "microphone")} \"{Name}\": {Format}; buffer {bufferFrames} frames");

            CoreAudio.Check(client->Start(), "IAudioClient::Start");
            started = true;
            RunningSinceHns = TimelineClock.NowHns();
            SetState(WorkerState.Running);
            Capture(capture, ready, converter, samples);
            SetState(WorkerState.Stopped);
        }
        catch (Exception ex)
        {
            Failure = ex is AudioException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Log.Warn($"Audio {(Loopback ? "loopback" : "microphone")} \"{Name}\" failed: {Failure}");
            SetState(WorkerState.Failed);
        }
        finally
        {
            EndedHns = TimelineClock.NowHns();
            if (started)
            {
                client->Stop();
            }

            if (capture != null)
            {
                capture->Release();
            }

            if (client != null)
            {
                client->Release();
            }

            if (mixFormat != null)
            {
                CoTaskMemFree(mixFormat);
            }

            if (device != null)
            {
                device->Release();
            }

            if (enumerator != null)
            {
                enumerator->Release();
            }

            if (ready.Value != null)
            {
                CloseHandle(ready);
            }

            CoreAudio.LeaveMmcss(mmcss);
            if (uninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static IAudioClient* Activate(IMMDevice* device)
    {
        IAudioClient* client;
        CoreAudio.Check(device->Activate(__uuidof<IAudioClient>(), CoreAudio.ClsctxAll, null, (void**)&client), "IMMDevice::Activate(IAudioClient)");
        return client;
    }

    /// <summary>
    /// Opens the stream: 48 kHz float in our channel count, converted by Windows, or else the device's own format
    /// (converted here).
    /// </summary>
    private CaptureFormat InitializeStream(IMMDevice* device, ref IAudioClient* client, WAVEFORMATEX* mixFormat, CaptureFormat deviceFormat)
    {
        uint common = (uint)AUDCLNT.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | (Loopback ? (uint)AUDCLNT.AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
        WAVEFORMATEXTENSIBLE wanted = FloatFormat(TimelineClock.SampleRate, Channels);
        uint convert = AUDCLNT.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | (uint)AUDCLNT.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, common | convert, BufferDuration, 0, &wanted.Format, null);
        if (hr.SUCCEEDED)
        {
            Format = $"device mixes at {deviceFormat.Describe()}; Windows converts to 48000 Hz float, {Channels} channel(s)";
            return new CaptureFormat(SampleType.Float32, Channels, TimelineClock.SampleRate, wanted.dwChannelMask);
        }

        // A client can be initialized only once; a fresh one takes the device's own format.
        Log.Decision($"Audio \"{Name}\": Windows would not convert to 48 kHz float ({CoreAudio.Describe(hr)}); using the device format {deviceFormat.Describe()} and converting in EKrecorder.");
        client->Release();
        client = null;
        client = Activate(device);
        CoreAudio.Check(client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, common, BufferDuration, 0, mixFormat, null), "IAudioClient::Initialize");
        Format = $"device format {deviceFormat.Describe()}, converted by EKrecorder";
        return deviceFormat;
    }

    private void Capture(IAudioCaptureClient* capture, HANDLE ready, SampleConverter converter, float[] samples)
    {
        long lastPacket = TimelineClock.NowHns();
        TimelineClock? placedOn = null;
        while (!_stopRequested)
        {
            // Wake on the device's signal, or after 100 ms anyway: a loopback stream sends nothing in silence.
            WaitForSingleObject(ready, 100);
            for (;;)
            {
                uint next;
                CoreAudio.Check(capture->GetNextPacketSize(&next), "IAudioCaptureClient::GetNextPacketSize");
                if (next == 0 || _stopRequested)
                {
                    break;
                }

                byte* data;
                uint frames;
                uint flags;
                ulong devicePosition;
                ulong qpcPosition;
                HRESULT hr = capture->GetBuffer(&data, &frames, &flags, &devicePosition, &qpcPosition);
                if (hr.Value == AUDCLNT_S_BUFFER_EMPTY)
                {
                    break;
                }

                CoreAudio.Check(hr, "IAudioCaptureClient::GetBuffer");
                long now = TimelineClock.NowHns();
                try
                {
                    placedOn = Process(data, (int)frames, flags, (long)qpcPosition, now, converter, samples, placedOn);
                }
                finally
                {
                    hr = capture->ReleaseBuffer(frames);
                }

                CoreAudio.Check(hr, "IAudioCaptureClient::ReleaseBuffer");
                lastPacket = now;
            }

            if (!Loopback && TimelineClock.NowHns() - lastPacket > StallTimeout)
            {
                throw new AudioException("the microphone delivered no audio for 3 seconds");
            }
        }
    }

    private TimelineClock? Process(byte* data, int frames, uint flags, long qpcPosition, long now, SampleConverter converter, float[] samples, TimelineClock? placedOn)
    {
        bool silentFlag = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
        float peak = converter.Convert(silentFlag ? null : data, frames, samples, out bool allZero);
        if ((flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0 && Packets > 0)
        {
            Discontinuities++;
        }

        Packets++;
        Frames += frames;
        MaxPeak = Math.Max(MaxPeak, peak);
        float previous = Volatile.Read(ref _peakSinceRead);
        if (peak > previous)
        {
            Interlocked.CompareExchange(ref _peakSinceRead, peak, previous);
        }

        long zero = allZero ? _zeroFrames + frames : 0;
        Volatile.Write(ref _zeroFrames, zero);
        LongestDigitalSilenceSeconds = Math.Max(LongestDigitalSilenceSeconds, zero / (double)_rate);

        // When the first frame was captured (a loopback packet: when it was played).
        long start = qpcPosition;
        if ((flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0 || qpcPosition <= 0 || Math.Abs(qpcPosition - now) > MaxTimestampSkew)
        {
            start = now - (frames * 10_000_000L / _rate);
            EstimatedTimestamps++;
        }

        TimelineClock? clock = _anchor.Clock;
        if (_writer is not null && clock is not null)
        {
            if (!ReferenceEquals(clock, placedOn))
            {
                _writer.Restart();
            }

            bool gap = (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0 && Packets > 1;
            _writer.Write(samples, frames, clock.PositionOf(start), gap);
        }

        return clock;
    }

    private void SetState(WorkerState state)
    {
        Volatile.Write(ref _state, (int)state);
        try
        {
            _changed(this);
        }
        catch (Exception ex)
        {
            Log.Error("Reporting an audio stream's state failed", ex);
        }
    }

    private static WAVEFORMATEXTENSIBLE FloatFormat(int rate, int channels)
    {
        WAVEFORMATEXTENSIBLE format = default;
        format.Format.wFormatTag = (ushort)WAVE.WAVE_FORMAT_EXTENSIBLE;
        format.Format.nChannels = (ushort)channels;
        format.Format.nSamplesPerSec = (uint)rate;
        format.Format.wBitsPerSample = 32;
        format.Format.nBlockAlign = (ushort)(channels * 4);
        format.Format.nAvgBytesPerSec = (uint)(rate * channels * 4);
        format.Format.cbSize = (ushort)(sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX));
        format.Samples.wValidBitsPerSample = 32;
        format.dwChannelMask = channels == 1 ? (uint)SPEAKER.SPEAKER_FRONT_CENTER : (uint)(SPEAKER.SPEAKER_FRONT_LEFT | SPEAKER.SPEAKER_FRONT_RIGHT);
        format.SubFormat = FloatSubFormat;
        return format;
    }

    /// <summary>Reads a WAVEFORMATEX (or WAVEFORMATEXTENSIBLE) into the converter's terms.</summary>
    private static CaptureFormat FormatOf(WAVEFORMATEX* format)
    {
        int tag = format->wFormatTag;
        Guid subFormat = Guid.Empty;
        uint mask = 0;
        if (tag == WAVE.WAVE_FORMAT_EXTENSIBLE && format->cbSize >= 22)
        {
            var extensible = (WAVEFORMATEXTENSIBLE*)format;
            subFormat = extensible->SubFormat;
            mask = extensible->dwChannelMask;
        }

        bool isFloat = tag == WAVE.WAVE_FORMAT_IEEE_FLOAT || subFormat == FloatSubFormat;
        bool isPcm = tag == WAVE.WAVE_FORMAT_PCM || subFormat == PcmSubFormat;
        SampleType type = (isFloat, isPcm, format->wBitsPerSample) switch
        {
            (true, _, 32) => SampleType.Float32,
            (_, true, 16) => SampleType.Int16,
            (_, true, 24) => SampleType.Int24,
            (_, true, 32) => SampleType.Int32,
            _ => throw new AudioException($"unsupported audio format (tag 0x{tag:X}, {format->wBitsPerSample} bits)"),
        };
        return new CaptureFormat(type, format->nChannels, (int)format->nSamplesPerSec, mask);
    }
}
