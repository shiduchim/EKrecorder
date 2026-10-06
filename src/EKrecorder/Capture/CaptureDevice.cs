using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using EKrecorder.Diagnostics;
using EKrecorder.Native;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace EKrecorder.Capture;

/// <summary>
/// The Direct3D 11 device that Windows.Graphics.Capture delivers frames on, wrapped as a WinRT IDirect3DDevice.
/// It is created on the GPU that drives the captured monitor when that GPU can be found, so frames do not have to
/// cross between graphics adapters; otherwise on the default GPU, and as a last resort on WARP (software).
/// </summary>
internal sealed unsafe class CaptureDevice : IDisposable
{
    private const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private CaptureDevice(IDirect3DDevice device, string description)
    {
        Device = device;
        Description = description;
    }

    public IDirect3DDevice Device { get; }

    public string Description { get; }

    public static CaptureDevice CreateForMonitor(IntPtr monitor)
    {
        IntPtr adapter = FindAdapterForMonitor(monitor, out string adapterName);
        try
        {
            if (adapter != IntPtr.Zero
                && TryCreate(adapter, D3D_DRIVER_TYPE_UNKNOWN, $"the GPU driving this monitor (\"{adapterName}\")", out CaptureDevice? device))
            {
                return device;
            }

            Log.Decision("Using the default GPU for capture.");
            if (TryCreate(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, "the default GPU", out device))
            {
                return device;
            }

            Log.Decision("No hardware Direct3D 11 device; falling back to WARP (software rendering).");
            if (TryCreate(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, "WARP (software)", out device))
            {
                return device;
            }

            throw new InvalidOperationException("No Direct3D 11 device could be created (hardware and WARP both failed). See the log.");
        }
        finally
        {
            if (adapter != IntPtr.Zero)
            {
                Marshal.Release(adapter);
            }
        }
    }

    public void Dispose() => Device.Dispose();

    private static bool TryCreate(IntPtr adapter, int driverType, string label, [NotNullWhen(true)] out CaptureDevice? created)
    {
        created = null;
        int hr = D3D11CreateDevice(adapter, driverType, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0,
            D3D11_SDK_VERSION, out IntPtr d3dDevice, out int featureLevel, out IntPtr immediateContext);
        Log.Api($"D3D11CreateDevice({label}, BGRA support)", hr >= 0,
            hr >= 0 ? $"feature level {FeatureLevelText(featureLevel)}" : Win32.Hr(hr));
        if (hr < 0)
        {
            return false;
        }

        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr inspectable = IntPtr.Zero;
        try
        {
            hr = Marshal.QueryInterface(d3dDevice, in IID_IDXGIDevice, out dxgiDevice);
            if (hr < 0)
            {
                Log.Api("ID3D11Device::QueryInterface(IDXGIDevice)", false, Win32.Hr(hr));
                return false;
            }

            string actualAdapter = DeviceAdapterName(dxgiDevice);
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable);
            if (hr < 0)
            {
                Log.Api("CreateDirect3D11DeviceFromDXGIDevice", false, Win32.Hr(hr));
                return false;
            }

            // FromAbi adds its own reference; ours is released in the finally block.
            IDirect3DDevice device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            created = new CaptureDevice(device, $"{label}: \"{actualAdapter}\", feature level {FeatureLevelText(featureLevel)}");
            Log.Info($"Capture device ready on {created.Description}");
            return true;
        }
        finally
        {
            if (inspectable != IntPtr.Zero)
            {
                Marshal.Release(inspectable);
            }

            if (dxgiDevice != IntPtr.Zero)
            {
                Marshal.Release(dxgiDevice);
            }

            if (immediateContext != IntPtr.Zero)
            {
                Marshal.Release(immediateContext);
            }

            Marshal.Release(d3dDevice);
        }
    }

    /// <summary>Returns an AddRef'ed IDXGIAdapter1 whose output is <paramref name="monitor"/>, or zero.</summary>
    private static IntPtr FindAdapterForMonitor(IntPtr monitor, out string adapterName)
    {
        adapterName = "";
        int hr = CreateDXGIFactory1(in IID_IDXGIFactory1, out IntPtr factory);
        if (hr < 0)
        {
            Log.Api("CreateDXGIFactory1", false, Win32.Hr(hr));
            return IntPtr.Zero;
        }

        try
        {
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                // IDXGIFactory1::EnumAdapters1 (IUnknown 0-2, IDXGIObject 3-6, IDXGIFactory 7-11, EnumAdapters1 = 12)
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)ComVtable.Slot(factory, 12))(factory, a, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND)
                {
                    break;
                }

                if (hr < 0)
                {
                    Log.Api($"IDXGIFactory1::EnumAdapters1({a})", false, Win32.Hr(hr));
                    break;
                }

                bool owns = false;
                string name = AdapterName(adapter);
                for (uint o = 0; ; o++)
                {
                    IntPtr output;
                    // IDXGIAdapter::EnumOutputs (IDXGIObject ends at 6, EnumOutputs = 7)
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)ComVtable.Slot(adapter, 7))(adapter, o, &output);
                    if (hr < 0)
                    {
                        break; // DXGI_ERROR_NOT_FOUND: no more outputs
                    }

                    DXGI_OUTPUT_DESC description;
                    // IDXGIOutput::GetDesc (IDXGIObject ends at 6, GetDesc = 7)
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)ComVtable.Slot(output, 7))(output, &description);
                    Marshal.Release(output);
                    if (hr < 0)
                    {
                        continue;
                    }

                    bool match = description.Monitor == monitor;
                    Log.Info($"DXGI: GPU {a} \"{name}\" drives {new string((char*)description.DeviceName)} (HMONITOR 0x{description.Monitor:X}){(match ? "  <- selected monitor" : "")}");
                    owns |= match;
                }

                if (owns)
                {
                    adapterName = name;
                    return adapter; // the caller releases it
                }

                Marshal.Release(adapter);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        Log.Warn("DXGI: no GPU output matches the selected monitor.");
        return IntPtr.Zero;
    }

    private static string DeviceAdapterName(IntPtr dxgiDevice)
    {
        IntPtr adapter;
        // IDXGIDevice::GetAdapter (IDXGIObject ends at 6, GetAdapter = 7)
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)ComVtable.Slot(dxgiDevice, 7))(dxgiDevice, &adapter);
        if (hr < 0)
        {
            return $"unknown GPU (IDXGIDevice::GetAdapter {Win32.Hr(hr)})";
        }

        try
        {
            return AdapterName(adapter);
        }
        finally
        {
            Marshal.Release(adapter);
        }
    }

    private static string AdapterName(IntPtr adapter)
    {
        DXGI_ADAPTER_DESC description;
        // IDXGIAdapter::GetDesc (EnumOutputs = 7, GetDesc = 8)
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC*, int>)ComVtable.Slot(adapter, 8))(adapter, &description);
        return hr < 0
            ? $"unknown GPU (IDXGIAdapter::GetDesc {Win32.Hr(hr)})"
            : $"{new string((char*)description.Description)} (vendor 0x{description.VendorId:X4}, device 0x{description.DeviceId:X4})";
    }

    private static string FeatureLevelText(int level) => $"{(level >> 12) & 0xF}_{(level >> 8) & 0xF}";

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out int pFeatureLevel,
        out IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    /// <summary>DXGI_OUTPUT_DESC (96 bytes on x64). WCHARs are declared as ushort so the struct stays blittable.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC
    {
        public fixed ushort DeviceName[32];
        public Win32.RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    /// <summary>DXGI_ADAPTER_DESC (304 bytes on x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_ADAPTER_DESC
    {
        public fixed ushort Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Win32.LUID AdapterLuid;
    }
}
