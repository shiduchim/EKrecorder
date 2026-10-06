using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using EKrecorder.Native;
using Microsoft.Win32;

namespace EKrecorder.Diagnostics;

/// <summary>Facts about the machine and process that belong at the top of every log and report.</summary>
internal static class EnvironmentInfo
{
    public static string AppVersion =>
        typeof(EnvironmentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>"1.0.0" (without the build's commit id), for the Settings window.</summary>
    public static string ShortVersion => AppVersion.Split('+')[0];

    /// <summary>True when the UI thread runs PerMonitorV2, so every coordinate the spike uses is a physical pixel.</summary>
    public static bool IsPerMonitorV2 =>
        Win32.AreDpiAwarenessContextsEqual(Win32.GetThreadDpiAwarenessContext(), Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    public static IReadOnlyList<string> Describe() =>
    [
        $"EKrecorder {AppVersion}",
        $"Windows: {WindowsVersion()}",
        $".NET: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture} process",
        $"DPI awareness: {DpiAwarenessText()}; WinForms HighDpiMode = {Application.HighDpiMode}",
        $"Processors: {Environment.ProcessorCount} logical",
    ];

    public static string DpiAwarenessText()
    {
        if (IsPerMonitorV2)
        {
            return "PerMonitorV2";
        }

        int awareness = Win32.GetAwarenessFromDpiAwarenessContext(Win32.GetThreadDpiAwarenessContext());
        return $"NOT PerMonitorV2 (DPI_AWARENESS = {awareness})";
    }

    public static string WindowsVersion()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            string installation = key?.GetValue("InstallationType") as string ?? "";
            string display = key?.GetValue("DisplayVersion") as string ?? "";
            string build = key?.GetValue("CurrentBuildNumber") as string
                ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
            string revision = key?.GetValue("UBR")?.ToString() ?? "0";

            // ProductName still says "Windows 10" on Windows 11; the build number tells them apart.
            if (installation == "Client" && int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            }

            string version = display.Length > 0 ? $" {display}" : "";
            return $"{product}{version} (build {build}.{revision})";
        }
        catch (Exception ex)
        {
            return $"{Environment.OSVersion.VersionString} (registry not readable: {ex.Message})";
        }
    }
}
