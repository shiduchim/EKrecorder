using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using EKrecorder.Diagnostics;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Audio;

/// <summary>Which devices to record. A null id means Windows' default.</summary>
/// <param name="MicrophoneId">A pinned microphone, or null for Windows' default communications microphone.</param>
/// <param name="OutputId">A pinned output device, or null for Windows' default outputs (playback and communications).</param>
internal sealed record AudioSelection(string? MicrophoneId, string? MicrophoneName, string? OutputId, string? OutputName)
{
    public static AudioSelection Default { get; } = new(null, null, null, null);

    public string MicrophoneMode => MicrophoneId is null ? "Windows default communications microphone" : $"selected microphone \"{MicrophoneName}\"";

    public string OutputMode => OutputId is null ? "Windows default outputs (playback + communications)" : $"selected output \"{OutputName}\"";
}

/// <summary>One input's state, as the window and the report show it.</summary>
internal enum InputState
{
    Resolving,
    Running,
    Fallback,
    Lost,
    Retrying,
    NoDevice,
}

/// <summary>A snapshot of one input for the window.</summary>
internal sealed record AudioInputStatus(string Input, InputState State, string Devices, string Mode, float Level, string? Warning);

/// <summary>
/// Captures the microphone and the computer audio for one recording (or for the window's meters), and keeps both
/// alive. A supervisor thread owns Windows' device enumerator and its change notifications and runs one state
/// machine per input: Resolving → Running → Lost → Retrying → Running. When a device disappears the recording
/// goes on: that input's stretch of the timeline simply stays silent while the supervisor retries (250 ms, 500 ms,
/// 1 s, then every 2 s), follows a new Windows default, or falls back from a pinned device to the default and
/// back again when it returns. Device-change bursts are given 500 ms to settle before devices are resolved again.
/// <para>
/// Microphone: a pinned device while it is present, else Windows' default communications microphone, else the
/// default microphone. Computer audio: the loopback of Windows' default output and default communications output,
/// once each (one capture when they are the same device); or a pinned output while it is present. Nothing else is
/// captured, so virtual routing devices cannot double the sound. No device is open while EKrecorder is idle.
/// </para>
/// </summary>
internal sealed unsafe class AudioCapture : IDisposable
{
    public const double DigitalSilenceWarningSeconds = 8;

    private static readonly long[] RetryDelays = [2_500_000, 5_000_000, 10_000_000, 20_000_000]; // 250 ms .. 2 s
    private const long Settle = 5_000_000;           // 500 ms of quiet after a device event
    private const long SettleMax = 20_000_000;       // ... but at most 2 s after the first one
    private const long PeriodicCheck = 50_000_000;   // look again every 5 s anyway, in case a notification was missed
    private const long Suppression = 100_000_000;    // a microphone that failed 3 times in a row is passed over for 10 s
    private const int RingCount = 8;

    private readonly AudioSelection _selection;
    private readonly TimelineAnchor _anchor = new();
    private readonly TimelineRing[] _rings;
    private readonly Input _microphone;
    private readonly Input _computer;
    private readonly ConcurrentQueue<EndpointChange> _changes = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private readonly List<string> _events = new();
    private readonly List<CaptureWorker> _finished = new();
    private readonly Dictionary<string, long> _suppressedUntil = new();
    private readonly Dictionary<string, string> _names = new();
    private readonly Thread _thread;
    private volatile bool _stopRequested;
    private long _settleUntil;
    private long _firstChangeAt;
    private long _nextPeriodicCheck;
    private bool _reconcileNeeded = true;
    private bool _stopped;
    private long _timelineEnd;

    /// <param name="forRecording">True: audio goes onto the recording timeline. False: levels only (meters).</param>
    public AudioCapture(AudioSelection selection, bool forRecording)
    {
        _selection = selection;
        _rings = forRecording ? Enumerable.Range(0, RingCount).Select(_ => new TimelineRing()).ToArray() : [];
        _microphone = new Input("Microphone", loopback: false, selection.MicrophoneId, selection.MicrophoneMode);
        _computer = new Input("Computer audio", loopback: true, selection.OutputId, selection.OutputMode);
        _thread = new Thread(Run) { Name = "EKrecorder audio supervisor", IsBackground = true };
    }

    /// <summary>Every capture stream's place on the timeline; the mixer sums them all.</summary>
    public IReadOnlyList<TimelineRing> Rings => _rings;

    public AudioSelection Selection => _selection;

    /// <summary>Opens the devices (asynchronously; the inputs show Resolving until they run).</summary>
    public void Start() => _thread.Start();

    /// <summary>From now on, captured audio is placed on the recording timeline of <paramref name="clock"/>.</summary>
    public void BeginTimeline(TimelineClock clock)
    {
        _anchor.Start(clock);
        Event("Recording timeline started.");
    }

    /// <summary>
    /// The video has stopped: time without a device is no longer counted (the devices stay open a little longer,
    /// while the mixer collects the last audio).
    /// </summary>
    public void EndTimeline()
    {
        Interlocked.CompareExchange(ref _timelineEnd, TimelineClock.NowHns(), 0);
        Event("Video stopped; collecting the last audio.");
    }

    /// <summary>Closes every device and waits for the threads (at most a few seconds).</summary>
    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _stopRequested = true;
        _wake.Set();
        if (_thread.ThreadState != ThreadState.Unstarted && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            Log.Warn("The audio supervisor did not stop within 5 seconds.");
        }
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    /// <summary>The two inputs for the window: state, devices, level since the last call, warning.</summary>
    public (AudioInputStatus Microphone, AudioInputStatus Computer) Snapshot()
    {
        lock (_gate)
        {
            return (_microphone.Snapshot(), _computer.Snapshot());
        }
    }

    /// <summary>The time-stamped list of what happened (devices opened, lost, retried, switched, warnings).</summary>
    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    /// <summary>For the report, after <see cref="Stop"/>: one paragraph per input.</summary>
    public string Describe(Input input)
    {
        lock (_gate)
        {
            return input.Describe();
        }
    }

    public Input Microphone => _microphone;

    public Input Computer => _computer;

    /// <summary>Every capture stream that ran, for the report's details.</summary>
    public IReadOnlyList<string> StreamDetails()
    {
        lock (_gate)
        {
            return _finished.Select(DescribeWorker).ToArray();
        }
    }

    private void Run()
    {
        bool uninitialize = CoreAudio.EnterMta();
        IMMDeviceEnumerator* enumerator = null;
        EndpointNotifications? notifications = null;
        bool registered = false;
        long enumeratorRetryAt = 0;
        int enumeratorFailures = 0;
        int loopErrors = 0;
        try
        {
            Event($"Audio starting. Microphone: {_selection.MicrophoneMode}. Computer audio: {_selection.OutputMode}.");
            while (!_stopRequested)
            {
                // Nothing in one round may end the supervisor: the recording needs it until the very end.
                try
                {
                    long now = TimelineClock.NowHns();
                    if (enumerator == null && now >= enumeratorRetryAt)
                    {
                        try
                        {
                            enumerator = CoreAudio.CreateEnumerator();
                            notifications = new EndpointNotifications(OnEndpointChange);
                            HRESULT hr = enumerator->RegisterEndpointNotificationCallback(notifications.Pointer);
                            registered = hr.SUCCEEDED;
                            if (!registered)
                            {
                                Event($"Device notifications are not available ({CoreAudio.Describe(hr)}); checking every 5 s instead.");
                            }

                            if (enumeratorFailures > 0)
                            {
                                Event("Windows audio is available again.");
                            }

                            _reconcileNeeded = true;
                        }
                        catch (AudioException ex)
                        {
                            if (enumeratorFailures++ == 0)
                            {
                                Event($"Windows audio is not available: {ex.Message}. Silence is recorded; trying again every 2 s.");
                            }

                            enumeratorRetryAt = now + RetryDelays[^1];
                            SetNoDevice(_microphone, ex.Message);
                            SetNoDevice(_computer, ex.Message);
                        }
                    }

                    TakeChanges(now);
                    CheckWorkers(now);
                    if (enumerator != null && now >= _settleUntil && (_reconcileNeeded || RetryDue(now) || now >= _nextPeriodicCheck))
                    {
                        _reconcileNeeded = false;
                        _nextPeriodicCheck = now + PeriodicCheck;
                        Reconcile(enumerator, now);
                    }

                    CheckHealth(now);
                    Publish(now);
                }
                catch (Exception ex)
                {
                    if (loopErrors++ < 20)
                    {
                        Log.Error("Audio supervisor round failed; carrying on", ex);
                        Event($"Audio supervisor problem (carrying on): {ex.Message}");
                    }

                    _reconcileNeeded = true;
                    _wake.WaitOne(500);
                }

                _wake.WaitOne(100);
            }
        }
        catch (Exception ex)
        {
            Log.Error("The audio supervisor stopped because of an error", ex);
            Event($"Audio supervisor error: {ex.Message}");
        }
        finally
        {
            StopAllWorkers();
            if (registered && enumerator != null)
            {
                enumerator->UnregisterEndpointNotificationCallback(notifications!.Pointer);
            }

            notifications?.Dispose();
            if (enumerator != null)
            {
                enumerator->Release();
            }

            if (uninitialize)
            {
                CoUninitialize();
            }

            Event("Audio stopped; all devices closed.");
        }
    }

    private void OnEndpointChange(EndpointChange change)
    {
        _changes.Enqueue(change);
        _wake.Set();
    }

    /// <summary>Device events: wait until they stop coming for 500 ms (at most 2 s), then resolve again.</summary>
    private void TakeChanges(long now)
    {
        bool any = false;
        while (_changes.TryDequeue(out EndpointChange? change))
        {
            any = true;
            Log.Info($"Audio device event: {change.What} {change.DeviceId}");
        }

        if (!any)
        {
            if (_firstChangeAt != 0 && now >= _settleUntil)
            {
                _firstChangeAt = 0;
            }

            return;
        }

        if (_firstChangeAt == 0)
        {
            _firstChangeAt = now;
        }

        _settleUntil = Math.Min(now + Settle, _firstChangeAt + SettleMax);
        _reconcileNeeded = true;
    }

    private bool RetryDue(long now) => _microphone.RetryDue(now) || _computer.RetryDue(now);

    /// <summary>Decides which devices each input should capture, and starts, keeps or retires captures.</summary>
    private void Reconcile(IMMDeviceEnumerator* enumerator, long now)
    {
        // Microphone: pinned (while present) → default communications → default; each at most once.
        var micCandidates = new List<string>();
        string? defaultComms = CoreAudio.DefaultEndpointId(enumerator, EDataFlow.eCapture, ERole.eCommunications);
        string? defaultMic = CoreAudio.DefaultEndpointId(enumerator, EDataFlow.eCapture, ERole.eConsole);
        if (_selection.MicrophoneId is { } pinnedMic && CoreAudio.IsActive(enumerator, pinnedMic))
        {
            micCandidates.Add(pinnedMic);
        }

        foreach (string? id in new[] { defaultComms, defaultMic })
        {
            if (id is not null && !micCandidates.Contains(id))
            {
                micCandidates.Add(id);
            }
        }

        // A microphone that keeps failing to open is passed over for a while, if there is another one.
        List<string> usable = micCandidates.Where(id => !IsSuppressed(id, now)).ToList();
        if (usable.Count == 0)
        {
            usable = micCandidates;
        }

        string? preferredMic = _selection.MicrophoneId ?? defaultComms ?? defaultMic;
        string[] micTargets = usable.Count > 0 ? [usable[0]] : [];
        Apply(_microphone, micTargets, fallback: micTargets.Length > 0 && micTargets[0] != preferredMic, enumerator, now);

        // Computer audio: pinned output (while present), else the default and default communications outputs.
        var outputTargets = new List<string>();
        bool outputFallback = false;
        if (_selection.OutputId is { } pinnedOutput && CoreAudio.IsActive(enumerator, pinnedOutput))
        {
            outputTargets.Add(pinnedOutput);
        }
        else
        {
            outputFallback = _selection.OutputId is not null;
            foreach (ERole role in new[] { ERole.eConsole, ERole.eCommunications })
            {
                string? id = CoreAudio.DefaultEndpointId(enumerator, EDataFlow.eRender, role);
                if (id is not null && !outputTargets.Contains(id))
                {
                    outputTargets.Add(id);
                }
            }
        }

        Apply(_computer, outputTargets.ToArray(), outputFallback, enumerator, now);
    }

    private void Apply(Input input, string[] targets, bool fallback, IMMDeviceEnumerator* enumerator, long now)
    {
        if (input.Fallback != fallback)
        {
            Event(fallback
                ? $"{input.Name}: the selected device is missing; using the Windows default until it is back."
                : $"{input.Name}: back on the selected device.");
        }

        input.Fallback = fallback;
        input.NoDeviceReason = targets.Length == 0 ? $"Windows reports no {(input.Loopback ? "output device" : "microphone")}" : null;
        foreach (string id in targets)
        {
            Endpoint? endpoint = input.Find(id);
            if (endpoint is null)
            {
                endpoint = new Endpoint(id, NameFor(enumerator, id)) { RetryAt = now };
                input.Endpoints.Add(endpoint);
                if (input.Endpoints.Count > 1 || _anchor.Clock is not null)
                {
                    Event($"{input.Name}: switching to \"{endpoint.Name}\".");
                }
            }

            endpoint.Desired = true;
        }

        foreach (Endpoint endpoint in input.Endpoints)
        {
            if (endpoint.Desired && !targets.Contains(endpoint.Id))
            {
                endpoint.Desired = false;
            }
        }

        foreach (Endpoint endpoint in input.Endpoints.Where(e => e.Desired && e.Worker is null && now >= e.RetryAt))
        {
            StartWorker(input, endpoint);
        }

        RetireOldEndpoints(input);
    }

    /// <summary>
    /// Make before break: a device that is no longer wanted keeps capturing until its replacement runs, so
    /// switching to a new default leaves no hole, and a new default that will not open does not cost the working one.
    /// </summary>
    private void RetireOldEndpoints(Input input)
    {
        bool replacementsRunning = input.Endpoints.Where(e => e.Desired).All(e => e.Worker?.State == WorkerState.Running);
        foreach (Endpoint endpoint in input.Endpoints.Where(e => !e.Desired).ToList())
        {
            if (endpoint.Worker is null || endpoint.Worker.State != WorkerState.Running || replacementsRunning)
            {
                if (endpoint.Worker is not null)
                {
                    endpoint.Worker.RequestStop();
                    Event($"{input.Name}: closed \"{endpoint.Name}\".");
                    Finish(endpoint.Worker);
                }

                input.Endpoints.Remove(endpoint);
            }
        }
    }

    private void StartWorker(Input input, Endpoint endpoint)
    {
        TimelineRing? ring = null;
        if (_rings.Length > 0)
        {
            ring = FreeRing();
            if (ring is null)
            {
                Event($"{input.Name}: no free timeline buffer for \"{endpoint.Name}\"; it is metered but not recorded.");
            }
        }

        endpoint.Worker = new CaptureWorker(endpoint.Id, endpoint.Name, input.Loopback, ring, _anchor, _ => _wake.Set());
        endpoint.WasRunning = false;
        if (endpoint.FailedAttempts > 0)
        {
            Event($"{input.Name}: retrying \"{endpoint.Name}\" (attempt {endpoint.FailedAttempts + 1}).");
        }

        endpoint.Worker.Start();
    }

    /// <summary>Notices captures that started running or failed.</summary>
    private void CheckWorkers(long now)
    {
        foreach (Input input in new[] { _microphone, _computer })
        {
            foreach (Endpoint endpoint in input.Endpoints)
            {
                CaptureWorker? worker = endpoint.Worker;
                if (worker is null)
                {
                    continue;
                }

                switch (worker.State)
                {
                    case WorkerState.Running when !endpoint.WasRunning:
                        endpoint.WasRunning = true;
                        endpoint.FailedAttempts = 0;
                        _suppressedUntil.Remove(endpoint.Id);
                        Event($"{input.Name}: running on \"{endpoint.Name}\" ({worker.Format}).");
                        _reconcileNeeded |= input.Endpoints.Any(e => !e.Desired);
                        break;
                    case WorkerState.Failed or WorkerState.Stopped:
                        endpoint.FailedAttempts++;
                        long delay = RetryDelays[Math.Min(endpoint.FailedAttempts - 1, RetryDelays.Length - 1)];
                        endpoint.RetryAt = now + delay;
                        string what = endpoint.WasRunning ? "lost" : "could not open";
                        Event($"{input.Name}: {what} \"{endpoint.Name}\": {worker.Failure ?? "the stream ended"}. Silence is recorded; retrying in {delay / 10_000} ms.");
                        if (!input.Loopback && endpoint.FailedAttempts >= 3 && !IsSuppressed(endpoint.Id, now))
                        {
                            _suppressedUntil[endpoint.Id] = now + Suppression;
                            Event($"{input.Name}: \"{endpoint.Name}\" failed {endpoint.FailedAttempts} times in a row; trying another microphone for 10 s.");
                        }

                        Finish(worker);
                        endpoint.Worker = null;
                        endpoint.WasRunning = false;
                        _reconcileNeeded = true;
                        break;
                }
            }
        }
    }

    /// <summary>Digital silence on the microphone, and how long each input has been without a running capture.</summary>
    private void CheckHealth(long now)
    {
        CaptureWorker? mic = _microphone.Endpoints.FirstOrDefault(e => e.Desired && e.Worker?.State == WorkerState.Running)?.Worker;
        double silence = mic?.DigitalSilenceSeconds ?? 0;
        if (silence >= DigitalSilenceWarningSeconds && !_microphone.DigitalSilence)
        {
            _microphone.DigitalSilence = true;
            _microphone.DigitalSilenceEpisodes++;
            Event($"Microphone WARNING: \"{mic!.Name}\" has sent only exact digital silence for {silence:0} s (muted, or blocked in Windows privacy settings?). Recording continues.");
        }
        else if (silence < DigitalSilenceWarningSeconds && _microphone.DigitalSilence)
        {
            _microphone.DigitalSilence = false;
            Event("Microphone: sound is coming in again.");
        }

        // Time without a running device counts only while the recording's timeline runs.
        long end = Interlocked.Read(ref _timelineEnd);
        long counted = end != 0 ? Math.Min(now, end) : now;
        foreach (Input input in new[] { _microphone, _computer })
        {
            bool running = input.Endpoints.Any(e => e.Worker?.State == WorkerState.Running);
            if (_anchor.Clock is null)
            {
                continue;
            }

            if (!running && input.MissingSince == 0 && (end == 0 || now < end))
            {
                input.MissingSince = counted;
            }
            else if (input.MissingSince != 0 && (running || end != 0))
            {
                // Back, or the video ended: close the stretch.
                input.MissingSeconds += (counted - input.MissingSince) / 1e7;
                input.MissingSince = 0;
            }
        }
    }

    private void Publish(long now)
    {
        var transitions = new List<string>();
        lock (_gate)
        {
            foreach (Input input in new[] { _microphone, _computer })
            {
                if (input.Publish(now) is { } transition)
                {
                    transitions.Add(transition);
                }
            }
        }

        // Every state change goes into the event log: Resolving -> Running -> Lost -> Retrying -> Running.
        foreach (string transition in transitions)
        {
            Event(transition);
        }
    }

    private void SetNoDevice(Input input, string reason)
    {
        input.NoDeviceReason = reason;
    }

    private bool IsSuppressed(string id, long now) => _suppressedUntil.TryGetValue(id, out long until) && now < until;

    private TimelineRing? FreeRing()
    {
        var used = new HashSet<TimelineRing>();
        foreach (Input input in new[] { _microphone, _computer })
        {
            foreach (Endpoint endpoint in input.Endpoints)
            {
                if (endpoint.Worker?.Ring is { } ring)
                {
                    used.Add(ring);
                }
            }
        }

        lock (_gate)
        {
            foreach (CaptureWorker worker in _finished)
            {
                // A finished stream's ring is free once its thread is gone and the mixer has read everything in it.
                if (worker.Ring is { } ring && (worker.EndedHns == 0 || !ring.IsDrained))
                {
                    used.Add(ring);
                }
            }
        }

        return _rings.FirstOrDefault(r => !used.Contains(r) && r.IsDrained);
    }

    private void Finish(CaptureWorker worker)
    {
        lock (_gate)
        {
            _finished.Add(worker);
        }
    }

    private void StopAllWorkers()
    {
        var all = new List<CaptureWorker>();
        foreach (Input input in new[] { _microphone, _computer })
        {
            foreach (Endpoint endpoint in input.Endpoints)
            {
                if (endpoint.Worker is not null)
                {
                    endpoint.Worker.RequestStop();
                    all.Add(endpoint.Worker);
                    Finish(endpoint.Worker);
                    endpoint.Worker = null;
                }
            }

            if (input.MissingSince != 0)
            {
                long end = Interlocked.Read(ref _timelineEnd);
                long now = TimelineClock.NowHns();
                input.MissingSeconds += ((end != 0 ? Math.Min(now, end) : now) - input.MissingSince) / 1e7;
                input.MissingSince = 0;
            }
        }

        lock (_gate)
        {
            all.AddRange(_finished.Except(all));
        }

        foreach (CaptureWorker worker in all)
        {
            worker.RequestStop();
        }

        foreach (CaptureWorker worker in all)
        {
            if (!worker.Join(TimeSpan.FromSeconds(2)))
            {
                Log.Warn($"Audio stream \"{worker.Name}\" did not close within 2 seconds.");
            }
        }

        Publish(TimelineClock.NowHns());
    }

    private string NameFor(IMMDeviceEnumerator* enumerator, string id)
    {
        if (!_names.TryGetValue(id, out string? name))
        {
            name = CoreAudio.NameOf(enumerator, id) ?? id;
            _names[id] = name;
        }

        return name;
    }

    /// <summary>Adds a line to the event log, stamped with the recording time (or the clock time before it starts).</summary>
    private void Event(string text)
    {
        TimelineClock? clock = _anchor.Clock;
        string stamp = clock is null
            ? DateTime.Now.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture)
            : TimeSpan.FromSeconds(Math.Max(0, clock.PositionOf(TimelineClock.NowHns()) / TimelineClock.SampleRate)).ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            _events.Add($"{stamp} {text}");
        }

        Log.Info($"Audio: {text}");
    }

    private static string DescribeWorker(CaptureWorker worker)
    {
        string late = worker.Ring is { } ring ? $", late samples dropped {ring.LateSamples:N0}" : "";
        string level = worker.MaxPeak > 0 ? $"{20 * Math.Log10(worker.MaxPeak):0.0} dBFS" : "silent";
        return string.Create(CultureInfo.InvariantCulture,
            $"{(worker.Loopback ? "Computer audio" : "Microphone")} \"{worker.Name}\": {(worker.Format.Length > 0 ? worker.Format : "did not open")}; {worker.Packets:N0} packets, {worker.Frames / (double)TimelineClock.SampleRate:0.0} s; peak {level}; drift corrected {worker.DriftPpm:+0;-0;0} ppm; re-placed {worker.Resyncs} time(s); Windows-flagged gaps {worker.Discontinuities}; estimated timestamps {worker.EstimatedTimestamps}{late}; longest digital silence {worker.LongestDigitalSilenceSeconds:0.0} s{(worker.Failure is null ? "" : $"; ended: {worker.Failure}")}");
    }

    /// <summary>A device an input wants (or is switching away from), and its capture.</summary>
    internal sealed class Endpoint
    {
        public Endpoint(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }

        public string Name { get; }

        public bool Desired { get; set; } = true;

        public CaptureWorker? Worker { get; set; }

        public bool WasRunning { get; set; }

        public int FailedAttempts { get; set; }

        public long RetryAt { get; set; }
    }

    /// <summary>One input (microphone or computer audio) and its state machine.</summary>
    internal sealed class Input
    {
        private readonly List<string> _devicesUsed = new();
        private float _level;
        private bool _published;

        public Input(string name, bool loopback, string? pinnedId, string mode)
        {
            Name = name;
            Loopback = loopback;
            PinnedId = pinnedId;
            Mode = mode;
        }

        public string Name { get; }

        public bool Loopback { get; }

        public string? PinnedId { get; }

        public string Mode { get; }

        public bool Fallback { get; set; }

        public string? NoDeviceReason { get; set; }

        public bool DigitalSilence { get; set; }

        public int DigitalSilenceEpisodes { get; set; }

        public long MissingSince { get; set; }

        /// <summary>Seconds of the recording during which this input had no running device (silence was recorded).</summary>
        public double MissingSeconds { get; set; }

        public InputState State { get; private set; } = InputState.Resolving;

        public string Devices { get; private set; } = "";

        internal List<Endpoint> Endpoints { get; } = new();

        public bool RetryDue(long now) => Endpoints.Any(e => e.Desired && e.Worker is null && now >= e.RetryAt);

        internal Endpoint? Find(string id) => Endpoints.FirstOrDefault(e => e.Id == id);

        /// <summary>
        /// Works out the state and device text (called with the capture's lock held). Returns a line for the event
        /// log when the state changed.
        /// </summary>
        public string? Publish(long now)
        {
            InputState previous = State;
            List<Endpoint> desired = Endpoints.Where(e => e.Desired).ToList();
            bool anyRunning = Endpoints.Any(e => e.Worker?.State == WorkerState.Running);
            State = desired.Count == 0 && !anyRunning ? InputState.NoDevice
                : desired.Any(e => e.Worker?.State == WorkerState.Running) || anyRunning ? (Fallback ? InputState.Fallback : InputState.Running)
                : desired.Any(e => e.Worker?.State == WorkerState.Opening) ? (desired.Any(e => e.FailedAttempts > 0) ? InputState.Retrying : InputState.Resolving)
                : desired.Any(e => e.FailedAttempts > 0) ? InputState.Lost
                : InputState.Resolving;

            var parts = new List<string>();
            foreach (Endpoint endpoint in Endpoints)
            {
                string? suffix = endpoint.Worker?.State switch
                {
                    WorkerState.Running => endpoint.Desired ? null : "closing",
                    WorkerState.Opening => endpoint.FailedAttempts > 0 ? "retrying" : "opening",
                    _ => endpoint.FailedAttempts > 0 ? "lost, will retry" : "waiting",
                };
                parts.Add(suffix is null ? endpoint.Name : $"{endpoint.Name} ({suffix})");
                if (endpoint.Worker?.State == WorkerState.Running && !_devicesUsed.Contains(endpoint.Name))
                {
                    _devicesUsed.Add(endpoint.Name);
                }

                if (endpoint.Worker?.State == WorkerState.Running)
                {
                    _level = Math.Max(_level, endpoint.Worker.TakePeak());
                }
            }

            Devices = parts.Count > 0 ? string.Join(" + ", parts) : NoDeviceReason ?? "none";
            if (State == previous && _published)
            {
                return null;
            }

            _published = true;
            string detail = State switch
            {
                InputState.NoDevice => $" ({NoDeviceReason ?? "no device"}; silence is recorded)",
                InputState.Lost => " (silence is recorded until it is back)",
                InputState.Running or InputState.Fallback or InputState.Retrying => $" ({Devices})",
                _ => "",
            };
            return $"{Name}: {previous} -> {State}{detail}";
        }

        /// <summary>For the window (lock held): the level since the last snapshot is handed over and reset.</summary>
        public AudioInputStatus Snapshot()
        {
            float level = _level;
            _level = 0;
            string? warning = DigitalSilence ? "only exact digital silence: muted, or blocked by Windows privacy settings?" : null;
            return new AudioInputStatus(Name, State, Devices, Mode, level, warning);
        }

        /// <summary>For the report (lock held).</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            text.Append($"{State}; {Mode}; devices used: {(_devicesUsed.Count == 0 ? "none" : string.Join(", ", _devicesUsed.Select(d => $"\"{d}\"")))}");
            text.Append(string.Create(CultureInfo.InvariantCulture, $"; without a device for {MissingSeconds:0.0} s in total"));
            if (DigitalSilenceEpisodes > 0)
            {
                text.Append($"; digital-silence warnings: {DigitalSilenceEpisodes}");
            }

            return text.ToString();
        }
    }
}
