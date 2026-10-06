using System.Runtime.InteropServices;
using EKrecorder.Diagnostics;
using EKrecorder.Native;

namespace EKrecorder.Monitors;

/// <summary>Finds the connected monitors and numbers them left to right.</summary>
internal static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var handles = new List<IntPtr>();
        bool enumerated = Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            handles.Add(monitor);
            return true;
        }, IntPtr.Zero);
        Log.Api("EnumDisplayMonitors", enumerated, $"{handles.Count} monitor(s)");

        Dictionary<string, (string FriendlyName, string DevicePath)> names = ReadDisplayConfigNames();
        var found = new List<MonitorInfo>();
        foreach (IntPtr handle in handles)
        {
            var info = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(handle, ref info))
            {
                Log.Api($"GetMonitorInfo(0x{handle:X})", false, Win32.LastErrorText());
                continue;
            }

            int hr = Win32.GetDpiForMonitor(handle, Win32.MDT_EFFECTIVE_DPI, out uint dpi, out _);
            if (hr < 0 || dpi == 0)
            {
                Log.Api($"GetDpiForMonitor({info.szDevice})", false, $"{Win32.Hr(hr)}; using 96 DPI");
                dpi = 96;
            }

            names.TryGetValue(info.szDevice, out var name);
            string friendlyName = name.FriendlyName ?? "";
            // The monitor's device path survives reboots and resolution changes; it is the identity to remember.
            string stableId = !string.IsNullOrEmpty(name.DevicePath)
                ? name.DevicePath
                : DeviceInterfacePath(info.szDevice) ?? info.szDevice;

            found.Add(new MonitorInfo(
                Number: 0,
                Handle: handle,
                GdiDeviceName: info.szDevice,
                Bounds: info.rcMonitor.ToRectangle(),
                WorkArea: info.rcWork.ToRectangle(),
                IsPrimary: (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                Dpi: dpi,
                FriendlyName: friendlyName,
                StableId: stableId));
        }

        // Left to right, then top to bottom; the stable id breaks exact ties so the order can never flip.
        List<MonitorInfo> ordered = found
            .OrderBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ThenBy(m => m.StableId, StringComparer.Ordinal)
            .Select((m, index) => m with { Number = index + 1 })
            .ToList();

        foreach (MonitorInfo monitor in ordered)
        {
            Log.Info($"{monitor.Summary}; {monitor.GdiDeviceName}; HMONITOR 0x{monitor.Handle:X}; work area {monitor.WorkArea}; id {monitor.StableId}");
        }

        return ordered;
    }

    /// <summary>
    /// Maps each GDI device name (\\.\DISPLAY1) to the monitor's friendly name and device path through the
    /// display-configuration API. Returns an empty map if that API fails; callers fall back to EnumDisplayDevices.
    /// </summary>
    private static Dictionary<string, (string FriendlyName, string DevicePath)> ReadDisplayConfigNames()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Win32.DISPLAYCONFIG_PATH_INFO[] paths;
            int error;
            int attempts = 0;
            do
            {
                error = Win32.GetDisplayConfigBufferSizes(Win32.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (error != Win32.ERROR_SUCCESS)
                {
                    Log.Api("GetDisplayConfigBufferSizes", false, $"error {error}");
                    return result;
                }

                paths = new Win32.DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new Win32.DISPLAYCONFIG_MODE_INFO[modeCount];
                error = Win32.QueryDisplayConfig(Win32.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (error == Win32.ERROR_SUCCESS)
                {
                    Array.Resize(ref paths, (int)pathCount);
                }
            }
            // The display setup can change between the two calls; the documented answer is to ask again.
            while (error == Win32.ERROR_INSUFFICIENT_BUFFER && ++attempts < 5);

            if (error != Win32.ERROR_SUCCESS)
            {
                Log.Api("QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)", false, $"error {error}");
                return result;
            }

            foreach (Win32.DISPLAYCONFIG_PATH_INFO path in paths)
            {
                var source = new Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };
                int sourceError = Win32.DisplayConfigGetDeviceInfo(ref source);

                var target = new Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id,
                    },
                };
                int targetError = Win32.DisplayConfigGetDeviceInfo(ref target);

                if (sourceError != Win32.ERROR_SUCCESS)
                {
                    Log.Api("DisplayConfigGetDeviceInfo(GET_SOURCE_NAME)", false, $"error {sourceError}");
                    continue;
                }

                string friendly = targetError == Win32.ERROR_SUCCESS ? target.monitorFriendlyDeviceName ?? "" : "";
                string devicePath = targetError == Win32.ERROR_SUCCESS ? target.monitorDevicePath ?? "" : "";
                Log.Api($"DisplayConfigGetDeviceInfo({source.viewGdiDeviceName})", targetError == Win32.ERROR_SUCCESS,
                    targetError == Win32.ERROR_SUCCESS ? $"\"{friendly}\" {devicePath}" : $"target name error {targetError}");

                // In duplicate (clone) mode one source has several targets; the first one names it.
                result.TryAdd(source.viewGdiDeviceName, (friendly, devicePath));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading monitor names with QueryDisplayConfig failed", ex);
        }

        return result;
    }

    private static string? DeviceInterfacePath(string gdiDeviceName)
    {
        var device = new Win32.DISPLAY_DEVICE { cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
        bool ok = Win32.EnumDisplayDevices(gdiDeviceName, 0, ref device, Win32.EDD_GET_DEVICE_INTERFACE_NAME);
        Log.Api($"EnumDisplayDevices({gdiDeviceName}, EDD_GET_DEVICE_INTERFACE_NAME)", ok,
            ok ? $"\"{device.DeviceString}\" {device.DeviceID}" : "no monitor device");
        return ok && !string.IsNullOrEmpty(device.DeviceID) ? device.DeviceID : null;
    }
}
