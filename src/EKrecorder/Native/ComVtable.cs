namespace EKrecorder.Native;

/// <summary>
/// Reads COM vtable slots so a method can be called through a function pointer.
/// Slot numbers count the inherited methods: IUnknown uses 0-2, IDXGIObject adds 3-6, and so on.
/// Each call site names the interface and method it calls.
/// </summary>
internal static unsafe class ComVtable
{
    public static void* Slot(IntPtr comObject, int index) => (*(void***)comObject)[index];
}
