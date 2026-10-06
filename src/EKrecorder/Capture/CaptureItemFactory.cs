using System.Runtime.InteropServices;
using EKrecorder.Diagnostics;
using EKrecorder.Native;
using Windows.Graphics.Capture;

namespace EKrecorder.Capture;

/// <summary>
/// Creates a GraphicsCaptureItem for a whole monitor through IGraphicsCaptureItemInterop::CreateForMonitor,
/// the documented way for a desktop app to capture a monitor without the system picker.
/// </summary>
internal static unsafe class CaptureItemFactory
{
    private const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateForMonitor(IntPtr monitor)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(GraphicsCaptureItemClass, (uint)GraphicsCaptureItemClass.Length, out IntPtr className));
        IntPtr interop = IntPtr.Zero;
        IntPtr itemPointer = IntPtr.Zero;
        try
        {
            int hr = RoGetActivationFactory(className, in IID_IGraphicsCaptureItemInterop, out interop);
            Log.Api("RoGetActivationFactory(GraphicsCaptureItem, IGraphicsCaptureItemInterop)", hr >= 0, Win32.Hr(hr));
            Marshal.ThrowExceptionForHR(hr);

            Guid itemIid = IID_IGraphicsCaptureItem;
            // IGraphicsCaptureItemInterop: IUnknown 0-2, CreateForWindow = 3, CreateForMonitor = 4
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)ComVtable.Slot(interop, 4))(interop, monitor, &itemIid, &itemPointer);
            Log.Api($"IGraphicsCaptureItemInterop::CreateForMonitor(0x{monitor:X})", hr >= 0, Win32.Hr(hr));
            Marshal.ThrowExceptionForHR(hr);

            // FromAbi adds its own reference; ours is released below.
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            if (itemPointer != IntPtr.Zero)
            {
                Marshal.Release(itemPointer);
            }

            if (interop != IntPtr.Zero)
            {
                Marshal.Release(interop);
            }

            WindowsDeleteString(className);
        }
    }

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);
}
