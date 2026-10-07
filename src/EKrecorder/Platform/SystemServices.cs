using System.Runtime.InteropServices;
using EKrecorder.App;
using EKrecorder.Diagnostics;
using Microsoft.Win32;

namespace EKrecorder.Platform;

/// <summary>
/// Keeps Windows from going to sleep on its own while a recording runs (a power request, as media players use; the
/// screen may still turn off). Windows' power settings are not changed. Dispose ends the request.
/// </summary>
internal sealed class KeepAwake : IDisposable
{
    private IntPtr _request;
    private IntPtr _reason;
    private bool _threadState;

    private KeepAwake()
    {
    }

    public static KeepAwake Start(string reason)
    {
        var keepAwake = new KeepAwake { _reason = Marshal.StringToHGlobalUni(reason) };
        var context = new AppNative.REASON_CONTEXT
        {
            Version = AppNative.POWER_REQUEST_CONTEXT_VERSION,
            Flags = AppNative.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            SimpleReasonString = keepAwake._reason,
        };
        IntPtr request = AppNative.PowerCreateRequest(ref context);
        if (request != IntPtr.Zero && request != new IntPtr(-1) && AppNative.PowerSetRequest(request, AppNative.PowerRequestSystemRequired))
        {
            keepAwake._request = request;
            Log.Info("Sleep is held off while recording (power request: system required).");
            return keepAwake;
        }

        Log.Api("PowerCreateRequest/PowerSetRequest", false, $"error {Marshal.GetLastWin32Error()}; using SetThreadExecutionState");
        if (request != IntPtr.Zero && request != new IntPtr(-1))
        {
            AppNative.CloseHandle(request);
        }

        // Fallback: tied to the calling (window) thread until cleared.
        keepAwake._threadState = AppNative.SetThreadExecutionState(AppNative.ES_CONTINUOUS | AppNative.ES_SYSTEM_REQUIRED) != 0;
        return keepAwake;
    }

    public void Dispose()
    {
        if (_request != IntPtr.Zero)
        {
            AppNative.PowerClearRequest(_request, AppNative.PowerRequestSystemRequired);
            AppNative.CloseHandle(_request);
            _request = IntPtr.Zero;
            Log.Info("Sleep is allowed again.");
        }

        if (_threadState)
        {
            AppNative.SetThreadExecutionState(AppNative.ES_CONTINUOUS);
            _threadState = false;
        }

        if (_reason != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_reason);
            _reason = IntPtr.Zero;
        }
    }
}

/// <summary>
/// "Start EKrecorder with Windows": a value in the current user's Run key (no administrator rights needed) that starts
/// EKrecorder quietly in the tray.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EKrecorder";

    public static string Command => $"\"{Environment.ProcessPath}\" --background";

    /// <summary>Adds (pointing at this EKrecorder.exe) or removes the Run value. Returns the problem, or null.</summary>
    public static string? Apply(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            string? current = key.GetValue(ValueName) as string;
            if (enabled)
            {
                if (!string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
                    Log.Info($"Start with Windows: on ({Command}).");
                }
            }
            else if (current is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Start with Windows: off.");
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error("Changing the Start with Windows setting failed", ex);
            return ex.Message;
        }
    }
}

/// <summary>
/// The global start/stop shortcut, registered with Windows on the app's message window. Another program that already
/// owns a combination makes RegisterHotKey fail; that is reported, never ignored.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int Id = 1;
    private const int TestId = 2;
    private readonly IntPtr _window;
    private bool _registered;

    public HotkeyManager(IntPtr window)
    {
        _window = window;
    }

    /// <summary>The shortcut that is registered now (empty when none is).</summary>
    public Hotkey Current { get; private set; }

    public bool Suspended { get; private set; }

    public static string Taken => "EKrecorder couldn't use that shortcut because another program is already using it.";

    /// <summary>Registers <paramref name="hotkey"/> instead of the current one. On failure nothing is registered and the reason is returned.</summary>
    public string? Register(Hotkey hotkey)
    {
        Unregister();
        Current = default;
        if (hotkey.IsEmpty)
        {
            return null;
        }

        if (!AppNative.RegisterHotKey(_window, Id, (uint)hotkey.Modifiers | AppNative.MOD_NOREPEAT, (uint)hotkey.Key))
        {
            int error = Marshal.GetLastWin32Error();
            Log.Api($"RegisterHotKey({hotkey.ToDisplay()})", false, $"error {error}");
            return error == AppNative.ERROR_HOTKEY_ALREADY_REGISTERED ? Taken : $"Windows refused that shortcut (error {error}).";
        }

        _registered = true;
        Current = hotkey;
        Log.Info($"Start/stop shortcut: {hotkey.ToDisplay()}.");
        return null;
    }

    /// <summary>
    /// Whether Windows would give us <paramref name="hotkey"/>: it is registered for a moment on
    /// <paramref name="testWindow"/>. The shortcut EKrecorder already holds counts as free.
    /// </summary>
    public bool IsFree(IntPtr testWindow, Hotkey hotkey)
    {
        if (hotkey.IsEmpty || hotkey == Current)
        {
            return true;
        }

        if (!AppNative.RegisterHotKey(testWindow, TestId, (uint)hotkey.Modifiers | AppNative.MOD_NOREPEAT, (uint)hotkey.Key))
        {
            return false;
        }

        AppNative.UnregisterHotKey(testWindow, TestId);
        return true;
    }

    /// <summary>While the Settings window records a new shortcut, the old one must not fire.</summary>
    public void Suspend()
    {
        if (!Suspended && _registered)
        {
            AppNative.UnregisterHotKey(_window, Id);
            _registered = false;
        }

        Suspended = true;
    }

    public void Resume()
    {
        if (!Suspended)
        {
            return;
        }

        Suspended = false;
        if (!Current.IsEmpty && !_registered)
        {
            _registered = AppNative.RegisterHotKey(_window, Id, (uint)Current.Modifiers | AppNative.MOD_NOREPEAT, (uint)Current.Key);
            if (!_registered)
            {
                Log.Warn($"The shortcut {Current.ToDisplay()} could not be registered again after the Settings window.");
            }
        }
    }

    public void Dispose() => Unregister();

    private void Unregister()
    {
        if (_registered)
        {
            AppNative.UnregisterHotKey(_window, Id);
            _registered = false;
        }
    }
}

/// <summary>
/// One EKrecorder per Windows session. The first instance holds a named mutex; a second start only signals the first
/// one (to show Settings, or to exit for the installer) and ends.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _exit;
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _listener;

    private SingleInstance(Mutex mutex, EventWaitHandle show, EventWaitHandle exit)
    {
        _mutex = mutex;
        _show = show;
        _exit = exit;
    }

    /// <summary>Raised on a background thread when another start asks for the Settings window.</summary>
    public event Action? ShowRequested;

    /// <summary>Raised on a background thread when another start asks this one to exit (the installer).</summary>
    public event Action? ExitRequested;

    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(false, MutexName(name));
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous EKrecorder ended without letting go (it crashed); the mutex is ours now.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return null;
        }

        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName(name));
        var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitName(name));
        return new SingleInstance(mutex, show, exit);
    }

    public static bool Signal(string name, bool exit)
    {
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(exit ? ExitName(name) : ShowName(name));

            // This process was started by the user and may bring a window to the front; the running copy, which
            // opens Settings, may not unless it is given that right.
            AppNative.AllowSetForegroundWindow(AppNative.ASFW_ANY);
            return handle.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Waits until no EKrecorder holds the mutex (the running one has exited). True if that happened in time.</summary>
    public static bool WaitUntilGone(string name, TimeSpan timeout)
    {
        using var mutex = new Mutex(false, MutexName(name));
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (acquired)
        {
            mutex.ReleaseMutex();
        }

        return acquired;
    }

    public void StartListening()
    {
        _listener = new Thread(Listen) { Name = "EKrecorder instance listener", IsBackground = true };
        _listener.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _listener?.Join(TimeSpan.FromSeconds(2));
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released already, or not owned by this thread.
        }

        _mutex.Dispose();
        _show.Dispose();
        _exit.Dispose();
        _stop.Dispose();
    }

    private void Listen()
    {
        WaitHandle[] handles = [_stop, _show, _exit];
        while (true)
        {
            int signaled = WaitHandle.WaitAny(handles);
            if (signaled == 0)
            {
                return;
            }

            if (signaled == 1)
            {
                ShowRequested?.Invoke();
            }
            else
            {
                ExitRequested?.Invoke();
            }
        }
    }

    private static string MutexName(string name) => $@"Local\{name}.Running";

    private static string ShowName(string name) => $@"Local\{name}.Show";

    private static string ExitName(string name) => $@"Local\{name}.Exit";
}

/// <summary>
/// EKrecorder's hidden top-level window. It receives the global shortcut and the messages Windows sends to every
/// top-level window when it shuts down, logs off, goes to sleep or changes displays.
/// </summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    public MessageWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "EKrecorder",
            Style = 0,
            ExStyle = Win32Styles.WS_EX_TOOLWINDOW,
        });
    }

    public event Action? HotkeyPressed;

    /// <summary>Windows asks whether it may end the session; return true when a recording is running.</summary>
    public Func<bool>? QueryEndSession { get; set; }

    /// <summary>The session is ending now (shutdown, restart, log off): finish quickly.</summary>
    public event Action? EndingSession;

    /// <summary>Windows is going to sleep or hibernate now.</summary>
    public event Action? Suspending;

    public event Action? Resumed;

    public event Action? DisplayChanged;

    /// <summary>Another program asked EKrecorder to close (for example taskkill without /F).</summary>
    public event Action? CloseRequested;

    public void Dispose() => DestroyHandle();

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case AppNative.WM_HOTKEY:
                HotkeyPressed?.Invoke();
                return;

            case AppNative.WM_QUERYENDSESSION:
                if (QueryEndSession?.Invoke() == true)
                {
                    // Shown by Windows if saving takes a moment; shutdown is not refused.
                    AppNative.ShutdownBlockReasonCreate(Handle, "Saving your recording…");
                }

                m.Result = 1;
                return;

            case AppNative.WM_ENDSESSION:
                if (m.WParam != IntPtr.Zero)
                {
                    EndingSession?.Invoke();
                }

                AppNative.ShutdownBlockReasonDestroy(Handle);
                m.Result = IntPtr.Zero;
                return;

            case AppNative.WM_POWERBROADCAST:
                long kind = m.WParam.ToInt64();
                if (kind == AppNative.PBT_APMSUSPEND)
                {
                    Suspending?.Invoke();
                }
                else if (kind is AppNative.PBT_APMRESUMEAUTOMATIC or AppNative.PBT_APMRESUMESUSPEND)
                {
                    Resumed?.Invoke();
                }

                m.Result = 1;
                return;

            case AppNative.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                break;

            case AppNative.WM_CLOSE:
                // Not destroyed here: the shortcut, shutdown and sleep handling live in this window. EKrecorder
                // saves any recording and exits instead.
                CloseRequested?.Invoke();
                m.Result = IntPtr.Zero;
                return;
        }

        base.WndProc(ref m);
    }

    private static class Win32Styles
    {
        public const int WS_EX_TOOLWINDOW = 0x00000080;
    }
}
