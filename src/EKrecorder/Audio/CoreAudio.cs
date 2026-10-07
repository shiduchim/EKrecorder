using System.Runtime.InteropServices;
using EKrecorder.Diagnostics;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Audio;

/// <summary>An audio endpoint as Windows lists it.</summary>
internal sealed record AudioDevice(string Id, string Name);

/// <summary>A failed Core Audio call, with the step and a readable reason.</summary>
internal sealed class AudioException : Exception
{
    public AudioException(string operation, int hr)
        : base($"{operation} failed: {CoreAudio.Describe(hr)}")
    {
        HResult = hr;
    }

    public AudioException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Core Audio (MMDevice API) helpers: the device enumerator, default endpoints, names and lists, MMCSS and COM
/// apartment set-up. Everything here runs on EKrecorder's own audio threads (multithreaded apartment), never on the
/// window's thread.
/// </summary>
internal static unsafe class CoreAudio
{
    public const uint ClsctxAll = 0x17;
    private const ushort VT_LPWSTR = 31;
    private const int E_NOTFOUND = unchecked((int)0x80070490);

    // functiondiscoverykeys_devpkey.h: PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
    private static readonly Guid FriendlyNameFormat = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    /// <summary>Joins the multithreaded apartment; true when the caller must call CoUninitialize later.</summary>
    public static bool EnterMta()
    {
        HRESULT hr = CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);
        if (hr.FAILED)
        {
            // RPC_E_CHANGED_MODE: the thread already has another apartment. COM still works; do not uninitialize.
            Log.Warn($"CoInitializeEx(MTA) on {Thread.CurrentThread.Name}: {Describe(hr)}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Registers the thread with MMCSS ("Audio"), which raises its priority while audio runs. Returns the handle
    /// for <see cref="LeaveMmcss"/>, or 0 when the service refused (the thread then runs at its own priority).
    /// </summary>
    public static nint EnterMmcss(string task = "Audio")
    {
        nint handle = AvSetMmThreadCharacteristicsW(task, out _);
        if (handle == 0)
        {
            Log.Warn($"MMCSS registration (\"{task}\") for {Thread.CurrentThread.Name} failed: error {Marshal.GetLastPInvokeError()}");
        }

        return handle;
    }

    public static void LeaveMmcss(nint handle)
    {
        if (handle != 0)
        {
            AvRevertMmThreadCharacteristics(handle);
        }
    }

    public static IMMDeviceEnumerator* CreateEnumerator()
    {
        IMMDeviceEnumerator* enumerator;
        Check(CoCreateInstance(Ptr(in CLSID.CLSID_MMDeviceEnumerator), null, ClsctxAll, __uuidof<IMMDeviceEnumerator>(), (void**)&enumerator), "CoCreateInstance(MMDeviceEnumerator)");
        return enumerator;
    }

    /// <summary>The id of Windows' default endpoint for a flow and role, or null when there is none.</summary>
    public static string? DefaultEndpointId(IMMDeviceEnumerator* enumerator, EDataFlow flow, ERole role)
    {
        IMMDevice* device = null;
        HRESULT hr = enumerator->GetDefaultAudioEndpoint(flow, role, &device);
        if (hr.FAILED)
        {
            if (hr.Value != E_NOTFOUND)
            {
                Log.Warn($"GetDefaultAudioEndpoint({flow}, {role}): {Describe(hr)}");
            }

            return null;
        }

        try
        {
            return IdOf(device);
        }
        finally
        {
            device->Release();
        }
    }

    /// <summary>True when the endpoint exists and is enabled and plugged in.</summary>
    public static bool IsActive(IMMDeviceEnumerator* enumerator, string id)
    {
        IMMDevice* device = OpenDevice(enumerator, id);
        if (device == null)
        {
            return false;
        }

        try
        {
            uint state;
            return device->GetState(&state).SUCCEEDED && state == DEVICE.DEVICE_STATE_ACTIVE;
        }
        finally
        {
            device->Release();
        }
    }

    /// <summary>The endpoint's friendly name ("Headset Microphone (Jabra Evolve2 65)"), or null.</summary>
    public static string? NameOf(IMMDeviceEnumerator* enumerator, string id)
    {
        IMMDevice* device = OpenDevice(enumerator, id);
        if (device == null)
        {
            return null;
        }

        try
        {
            return NameOf(device);
        }
        finally
        {
            device->Release();
        }
    }

    /// <summary>The microphones Windows lists as active, by name.</summary>
    public static List<AudioDevice> ListMicrophones() => ListActive(EDataFlow.eCapture);

    /// <summary>The output devices Windows lists as active, by name.</summary>
    public static List<AudioDevice> ListOutputs() => ListActive(EDataFlow.eRender);

    /// <summary>The active endpoints for a flow (render = outputs, capture = microphones), with names.</summary>
    public static List<AudioDevice> ListActive(EDataFlow flow)
    {
        var devices = new List<AudioDevice>();
        bool uninitialize = EnterMta();
        IMMDeviceEnumerator* enumerator = null;
        IMMDeviceCollection* collection = null;
        try
        {
            enumerator = CreateEnumerator();
            Check(enumerator->EnumAudioEndpoints(flow, DEVICE.DEVICE_STATE_ACTIVE, &collection), "IMMDeviceEnumerator::EnumAudioEndpoints");
            uint count;
            Check(collection->GetCount(&count), "IMMDeviceCollection::GetCount");
            for (uint i = 0; i < count; i++)
            {
                IMMDevice* device = null;
                if (collection->Item(i, &device).FAILED)
                {
                    continue;
                }

                try
                {
                    string? id = IdOf(device);
                    if (id is not null)
                    {
                        devices.Add(new AudioDevice(id, NameOf(device) ?? id));
                    }
                }
                finally
                {
                    device->Release();
                }
            }
        }
        catch (AudioException ex)
        {
            Log.Warn($"Listing audio devices ({flow}): {ex.Message}");
        }
        finally
        {
            if (collection != null)
            {
                collection->Release();
            }

            if (enumerator != null)
            {
                enumerator->Release();
            }

            if (uninitialize)
            {
                CoUninitialize();
            }
        }

        devices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return devices;
    }

    /// <summary>
    /// Windows' defaults by name, for the Settings window: the microphone a recording uses with "Windows default"
    /// (the communications microphone, else the default one) and the outputs it records (default playback and
    /// default communications, once each).
    /// </summary>
    public static (string? Microphone, IReadOnlyList<string> Outputs) DefaultNames()
    {
        bool uninitialize = EnterMta();
        IMMDeviceEnumerator* enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            string? microphoneId = DefaultMicrophoneId(enumerator);
            string? microphone = microphoneId is null ? null : NameOf(enumerator, microphoneId);
            var outputs = new List<string>();
            foreach (ERole role in new[] { ERole.eConsole, ERole.eCommunications })
            {
                if (DefaultEndpointId(enumerator, EDataFlow.eRender, role) is { } id && NameOf(enumerator, id) is { } name && !outputs.Contains(name))
                {
                    outputs.Add(name);
                }
            }

            return (microphone, outputs);
        }
        catch (AudioException ex)
        {
            Log.Warn($"Reading Windows' default audio devices: {ex.Message}");
            return (null, []);
        }
        finally
        {
            if (enumerator != null)
            {
                enumerator->Release();
            }

            if (uninitialize)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>
    /// Whether the microphone <paramref name="id"/> (null: the one "Windows default" records) is muted in Windows,
    /// or null when that cannot be told. Reading this does not open the microphone.
    /// </summary>
    public static bool? IsMicrophoneMuted(string? id) => MicrophoneMute(id, unmute: false);

    /// <summary>Unmutes the microphone in Windows. True when that worked.</summary>
    public static bool UnmuteMicrophone(string? id) => MicrophoneMute(id, unmute: true) == false;

    private static bool? MicrophoneMute(string? id, bool unmute)
    {
        bool uninitialize = EnterMta();
        IMMDeviceEnumerator* enumerator = null;
        IMMDevice* device = null;
        IAudioEndpointVolume* volume = null;
        try
        {
            enumerator = CreateEnumerator();
            string? deviceId = id ?? DefaultMicrophoneId(enumerator);
            device = deviceId is null ? null : OpenDevice(enumerator, deviceId);
            if (device == null || device->Activate(__uuidof<IAudioEndpointVolume>(), ClsctxAll, null, (void**)&volume).FAILED || volume == null)
            {
                return null;
            }

            if (unmute && volume->SetMute(FALSE, null).FAILED)
            {
                return null;
            }

            BOOL muted;
            return volume->GetMute(&muted).SUCCEEDED ? muted != FALSE : null;
        }
        catch (AudioException)
        {
            return null;
        }
        finally
        {
            if (volume != null)
            {
                volume->Release();
            }

            if (device != null)
            {
                device->Release();
            }

            if (enumerator != null)
            {
                enumerator->Release();
            }

            if (uninitialize)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>The microphone "Windows default" records: the communications microphone, else the default one.</summary>
    private static string? DefaultMicrophoneId(IMMDeviceEnumerator* enumerator) =>
        DefaultEndpointId(enumerator, EDataFlow.eCapture, ERole.eCommunications) ?? DefaultEndpointId(enumerator, EDataFlow.eCapture, ERole.eConsole);

    /// <summary>The endpoint, or null when it does not exist (AddRef'ed; release it).</summary>
    public static IMMDevice* OpenDevice(IMMDeviceEnumerator* enumerator, string id)
    {
        IMMDevice* device = null;
        fixed (char* text = id)
        {
            return enumerator->GetDevice(text, &device).SUCCEEDED ? device : null;
        }
    }

    public static void Check(HRESULT hr, string operation)
    {
        if (hr.FAILED)
        {
            throw new AudioException(operation, hr);
        }
    }

    /// <summary>A Core Audio HRESULT in words, for the log and the report.</summary>
    public static string Describe(int hr)
    {
        string? meaning = unchecked((uint)hr) switch
        {
            0x88890004 => "AUDCLNT_E_DEVICE_INVALIDATED: the device was unplugged, disabled or reconfigured",
            0x88890008 => "AUDCLNT_E_UNSUPPORTED_FORMAT: the device does not take this format",
            0x8889000A => "AUDCLNT_E_DEVICE_IN_USE: another app uses the device exclusively",
            0x88890010 => "AUDCLNT_E_SERVICE_NOT_RUNNING: the Windows audio service is not running",
            0x88890026 => "AUDCLNT_E_RESOURCES_INVALIDATED: the device's settings changed",
            0x88890001 => "AUDCLNT_E_NOT_INITIALIZED",
            0x88890002 => "AUDCLNT_E_ALREADY_INITIALIZED",
            0x88890003 => "AUDCLNT_E_WRONG_ENDPOINT_TYPE",
            0x88890017 => "AUDCLNT_E_BUFFER_SIZE_ERROR",
            0x8889000F => "AUDCLNT_E_ENDPOINT_CREATE_FAILED: Windows could not open the device",
            0x80070490 => "E_NOTFOUND: no such device",
            0x80070005 => "E_ACCESSDENIED: access denied (check Settings > Privacy & security > Microphone)",
            0x80070057 => "E_INVALIDARG",
            0x80004001 => "E_NOTIMPL",
            0x8000000E => "E_ILLEGAL_METHOD_CALL",
            0x800401F0 => "CO_E_NOTINITIALIZED",
            _ => null,
        };
        return meaning is null ? $"HRESULT 0x{hr:X8}" : $"HRESULT 0x{hr:X8} ({meaning})";
    }

    public static Guid* Ptr(in Guid guid) => Recording.MediaFoundation.Ptr(in guid);

    private static string? IdOf(IMMDevice* device)
    {
        char* id = null;
        if (device->GetId(&id).FAILED || id == null)
        {
            return null;
        }

        try
        {
            return new string(id);
        }
        finally
        {
            CoTaskMemFree(id);
        }
    }

    private static string? NameOf(IMMDevice* device)
    {
        IPropertyStore* store = null;
        if (device->OpenPropertyStore(STGM.STGM_READ, &store).FAILED || store == null)
        {
            return null;
        }

        try
        {
            PROPERTYKEY key = new() { fmtid = FriendlyNameFormat, pid = 14 };
            PROPVARIANT value = default;
            if (store->GetValue(&key, &value).FAILED)
            {
                return null;
            }

            try
            {
                // PROPVARIANT: the type tag is the first 16 bits, the value starts at byte 8.
                char* text = *(ushort*)&value == VT_LPWSTR ? *(char**)((byte*)&value + 8) : null;
                return text == null ? null : new string(text);
            }
            finally
            {
                PropVariantClear(&value);
            }
        }
        finally
        {
            store->Release();
        }
    }

    [DllImport("avrt.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AvSetMmThreadCharacteristicsW(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(nint handle);
}
