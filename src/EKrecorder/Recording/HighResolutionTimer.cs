using System.Diagnostics;
using EKrecorder.Diagnostics;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace EKrecorder.Recording;

/// <summary>
/// Waits until a QueryPerformanceCounter time with about 1 ms accuracy and no busy waiting, using a
/// high-resolution waitable timer (Windows 10 1803+). Falls back to a normal waitable timer (about 15 ms accuracy).
/// </summary>
internal sealed unsafe class HighResolutionTimer : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;

    private HANDLE _timer;

    public HighResolutionTimer()
    {
        _timer = CreateWaitableTimerExW(null, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, (uint)TIMER_ALL_ACCESS);
        IsHighResolution = _timer.Value != null;
        if (!IsHighResolution)
        {
            Log.Decision("High-resolution waitable timer not available; using a normal waitable timer for frame pacing.");
            _timer = CreateWaitableTimerExW(null, null, 0, (uint)TIMER_ALL_ACCESS);
        }
    }

    public bool IsHighResolution { get; }

    /// <summary>Returns at (or just after) the Stopwatch timestamp <paramref name="due"/>.</summary>
    public void WaitUntil(long due)
    {
        long remaining = due - Stopwatch.GetTimestamp();
        if (remaining <= 0)
        {
            return;
        }

        // Relative due time: negative, in 100-ns units.
        LARGE_INTEGER dueTime = default;
        dueTime.QuadPart = -Math.Max(1, remaining * 10_000_000L / Stopwatch.Frequency);
        if (_timer.Value != null && SetWaitableTimer(_timer, &dueTime, 0, null, null, FALSE))
        {
            WaitForSingleObject(_timer, INFINITE);
        }
        else
        {
            Thread.Sleep(TimeSpan.FromTicks(remaining * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        }
    }

    public void Dispose()
    {
        if (_timer.Value != null)
        {
            CloseHandle(_timer);
            _timer = default;
        }
    }
}
