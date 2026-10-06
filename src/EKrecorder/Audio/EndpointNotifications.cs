using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EKrecorder.Diagnostics;
using TerraFX.Interop.Windows;

namespace EKrecorder.Audio;

/// <summary>One audio endpoint change reported by Windows.</summary>
internal sealed record EndpointChange(string What, string? DeviceId);

/// <summary>
/// An IMMNotificationClient for Windows' audio endpoint changes (device added, removed, enabled, disabled, new
/// default). Windows calls it on its own threads; each callback only hands a small event to the supervisor and
/// returns, as Core Audio requires: no Core Audio calls and no waiting inside a callback.
/// <para>
/// It is a hand-made COM object: a native block whose first field points at a function table of
/// [UnmanagedCallersOnly] methods. Its reference count is fixed, because this class owns its lifetime: it is
/// freed only after Windows has been told to stop calling it.
/// </para>
/// </summary>
internal sealed unsafe class EndpointNotifications : IDisposable
{
    private static readonly void** FunctionTable = CreateFunctionTable();

    private readonly Action<EndpointChange> _post;
    private GCHandle _self;
    private NativeClient* _native;

    public EndpointNotifications(Action<EndpointChange> post)
    {
        _post = post;
        _self = GCHandle.Alloc(this);
        _native = (NativeClient*)NativeMemory.AllocZeroed((nuint)sizeof(NativeClient));
        _native->Vtbl = FunctionTable;
        _native->Owner = GCHandle.ToIntPtr(_self);
    }

    /// <summary>The COM pointer to pass to RegisterEndpointNotificationCallback.</summary>
    public IMMNotificationClient* Pointer => (IMMNotificationClient*)_native;

    /// <summary>Frees the object. Call only after UnregisterEndpointNotificationCallback has returned.</summary>
    public void Dispose()
    {
        if (_native != null)
        {
            NativeMemory.Free(_native);
            _native = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private static void** CreateFunctionTable()
    {
        // IUnknown (3) + IMMNotificationClient (5). Allocated once and never freed: it lives as long as the process.
        void** table = (void**)NativeMemory.Alloc((nuint)(8 * sizeof(void*)));
        table[0] = (delegate* unmanaged[Stdcall]<NativeClient*, Guid*, void**, int>)&QueryInterface;
        table[1] = (delegate* unmanaged[Stdcall]<NativeClient*, uint>)&AddRef;
        table[2] = (delegate* unmanaged[Stdcall]<NativeClient*, uint>)&Release;
        table[3] = (delegate* unmanaged[Stdcall]<NativeClient*, char*, uint, int>)&OnDeviceStateChanged;
        table[4] = (delegate* unmanaged[Stdcall]<NativeClient*, char*, int>)&OnDeviceAdded;
        table[5] = (delegate* unmanaged[Stdcall]<NativeClient*, char*, int>)&OnDeviceRemoved;
        table[6] = (delegate* unmanaged[Stdcall]<NativeClient*, EDataFlow, ERole, char*, int>)&OnDefaultDeviceChanged;
        table[7] = (delegate* unmanaged[Stdcall]<NativeClient*, char*, PROPERTYKEY, int>)&OnPropertyValueChanged;
        return table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(NativeClient* self, Guid* iid, void** result)
    {
        if (*iid == IID.IID_IUnknown || *iid == IID.IID_IMMNotificationClient)
        {
            *result = self;
            return 0;
        }

        *result = null;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(NativeClient* self) => 2;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(NativeClient* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceStateChanged(NativeClient* self, char* deviceId, uint newState)
    {
        string state = newState switch
        {
            1 => "active",
            2 => "disabled",
            4 => "not present",
            8 => "unplugged",
            _ => $"state {newState}",
        };
        Post(self, $"device {state}", deviceId);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceAdded(NativeClient* self, char* deviceId)
    {
        Post(self, "device added", deviceId);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceRemoved(NativeClient* self, char* deviceId)
    {
        Post(self, "device removed", deviceId);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDefaultDeviceChanged(NativeClient* self, EDataFlow flow, ERole role, char* deviceId)
    {
        string kind = flow == EDataFlow.eCapture ? "microphone" : "output";
        Post(self, $"new default {kind} ({role})", deviceId);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnPropertyValueChanged(NativeClient* self, char* deviceId, PROPERTYKEY key)
    {
        // Property changes (names, volumes, icons) come in floods and never decide which device to record;
        // a format change that matters shows up as an error in the capture stream itself.
        return 0;
    }

    private static void Post(NativeClient* self, string what, char* deviceId)
    {
        // Nothing may escape into Windows' thread: an exception here would end the process.
        try
        {
            if (GCHandle.FromIntPtr(self->Owner).Target is EndpointNotifications owner)
            {
                owner._post(new EndpointChange(what, deviceId == null ? null : new string(deviceId)));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Handling an audio device notification failed", ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeClient
    {
        public void** Vtbl;
        public nint Owner;
    }
}
