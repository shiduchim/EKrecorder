using System.Runtime.InteropServices;
using EKrecorder.Diagnostics;
using EKrecorder.Native;

namespace EKrecorder.Overlays;

/// <summary>
/// The one implementation of EKrecorder's small on-screen markers (the recording triangle, the Identify numbers and
/// the self-test probe). It is a native layered top-level window, not a Form and not a full-screen overlay:
/// borderless, click-through, always on top, never activated, and hidden from Alt+Tab and the taskbar.
/// Its pixels are pushed with UpdateLayeredWindow.
/// <para>
/// When created with <c>excludeFromCapture</c>, WDA_EXCLUDEFROMCAPTURE is applied and read back while the window is
/// still hidden. If that cannot be verified, the window refuses to show.
/// </para>
/// </summary>
internal sealed class OverlayWindow : NativeWindow, IDisposable
{
    private OverlayWindow(string name, bool excludeFromCapture)
    {
        Name = name;
        ExcludeFromCapture = excludeFromCapture;
        ExclusionDetail = excludeFromCapture ? "not applied yet" : "not excluded (on purpose)";
    }

    public event EventHandler? DpiChanged;

    public string Name { get; }

    public bool ExcludeFromCapture { get; }

    /// <summary>True only when GetWindowDisplayAffinity read back WDA_EXCLUDEFROMCAPTURE.</summary>
    public bool ExclusionVerified { get; private set; }

    public string ExclusionDetail { get; private set; }

    public Rectangle Bounds { get; private set; }

    public bool IsShown { get; private set; }

    /// <summary>Creates the window hidden and, if asked, excludes it from capture. Never shows it.</summary>
    public static OverlayWindow Create(string name, Rectangle bounds, bool excludeFromCapture)
    {
        var window = new OverlayWindow(name, excludeFromCapture) { Bounds = bounds };
        window.CreateHandle(new CreateParams
        {
            Caption = $"EKrecorder {name}",
            // No WS_VISIBLE: the window stays hidden until ShowNoActivate.
            Style = Win32.WS_POPUP,
            ExStyle = Win32.WS_EX_LAYERED      // per-pixel alpha through UpdateLayeredWindow
                | Win32.WS_EX_TRANSPARENT       // with WS_EX_LAYERED: clicks go to whatever is underneath
                | Win32.WS_EX_TOPMOST          // always on top
                | Win32.WS_EX_TOOLWINDOW       // not in Alt+Tab or the taskbar
                | Win32.WS_EX_NOACTIVATE,      // never takes focus
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
        });
        Log.Info($"[{name}] created hidden window 0x{window.Handle:X} at {bounds}: layered, click-through, topmost, tool window, no-activate");

        if (excludeFromCapture)
        {
            window.ApplyCaptureExclusion();
        }

        return window;
    }

    /// <summary>Replaces the window's pixels and position. Works while the window is hidden.</summary>
    public bool SetContent(OverlayBitmap bitmap, Point location, bool logSuccess = true)
    {
        if (Handle == IntPtr.Zero)
        {
            return false;
        }

        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memoryDc = Win32.CreateCompatibleDC(screenDc);
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = bitmap.Width,
            biHeight = -bitmap.Height, // negative: top-down rows, matching OverlayBitmap
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32.BI_RGB,
        };
        IntPtr dib = Win32.CreateDIBSection(memoryDc, ref header, Win32.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        bool ok = false;
        string result;
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            result = $"CreateDIBSection failed: {Win32.LastErrorText()}";
        }
        else
        {
            Marshal.Copy(bitmap.Pixels, 0, bits, bitmap.Pixels.Length);
            IntPtr previous = Win32.SelectObject(memoryDc, dib);
            var destination = new Win32.POINT(location.X, location.Y);
            var size = new Win32.SIZE(bitmap.Width, bitmap.Height);
            var source = new Win32.POINT(0, 0);
            var blend = new Win32.BLENDFUNCTION
            {
                BlendOp = Win32.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = Win32.AC_SRC_ALPHA, // the pixels are premultiplied BGRA
            };
            ok = Win32.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, Win32.ULW_ALPHA);
            result = ok ? $"{bitmap.Width}x{bitmap.Height} at ({location.X},{location.Y})" : Win32.LastErrorText();
            Win32.SelectObject(memoryDc, previous);
        }

        if (dib != IntPtr.Zero)
        {
            Win32.DeleteObject(dib);
        }

        Win32.DeleteDC(memoryDc);
        Win32.ReleaseDC(IntPtr.Zero, screenDc);

        if (ok)
        {
            Bounds = new Rectangle(location, new Size(bitmap.Width, bitmap.Height));
        }

        if (!ok || logSuccess)
        {
            Log.Api($"[{Name}] UpdateLayeredWindow", ok, result);
        }

        return ok;
    }

    /// <summary>Shows the window without activating it. Refuses if capture exclusion was requested but not verified.</summary>
    public bool ShowNoActivate()
    {
        if (Handle == IntPtr.Zero)
        {
            return false;
        }

        if (ExcludeFromCapture && !ExclusionVerified)
        {
            Log.Decision($"[{Name}] not shown, because its exclusion from capture is not verified.");
            return false;
        }

        Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
        KeepOnTop();

        bool visible = Win32.IsWindowVisible(Handle);
        int hr = Win32.DwmGetWindowAttribute(Handle, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int));
        IsShown = visible && (hr < 0 || cloaked == 0);
        Log.Api($"[{Name}] ShowWindow(SW_SHOWNOACTIVATE) + SetWindowPos(HWND_TOPMOST)", IsShown,
            $"visible = {visible}, DWM cloaked = {(hr < 0 ? $"unknown ({Win32.Hr(hr)})" : cloaked.ToString(System.Globalization.CultureInfo.InvariantCulture))}, bounds {Bounds}");
        return IsShown;
    }

    /// <summary>Moves the window back to the top of the topmost band (other topmost windows can cover it).</summary>
    public void KeepOnTop()
    {
        if (Handle != IntPtr.Zero
            && !Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE))
        {
            Log.Api($"[{Name}] SetWindowPos(HWND_TOPMOST)", false, Win32.LastErrorText());
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
            Log.Info($"[{Name}] window destroyed");
        }

        IsShown = false;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Win32.WM_MOUSEACTIVATE:
                m.Result = Win32.MA_NOACTIVATE;
                return;
            case Win32.WM_NCHITTEST:
                m.Result = Win32.HTTRANSPARENT;
                return;
            case Win32.WM_DPICHANGED:
                // The owner re-renders at the new size; the suggested rectangle in lParam is not used.
                Log.Info($"[{Name}] WM_DPICHANGED, new DPI {m.WParam.ToInt64() & 0xFFFF}");
                DpiChanged?.Invoke(this, EventArgs.Empty);
                m.Result = IntPtr.Zero;
                return;
        }

        base.WndProc(ref m);
    }

    private void ApplyCaptureExclusion()
    {
        bool set = Win32.SetWindowDisplayAffinity(Handle, Win32.WDA_EXCLUDEFROMCAPTURE);
        string setResult = set ? "TRUE" : $"FALSE, {Win32.LastErrorText()}";
        bool read = Win32.GetWindowDisplayAffinity(Handle, out uint affinity);
        string readResult = read ? $"0x{affinity:X2}" : $"failed, {Win32.LastErrorText()}";

        ExclusionVerified = set && read && affinity == Win32.WDA_EXCLUDEFROMCAPTURE;
        ExclusionDetail = $"SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) = {setResult}; GetWindowDisplayAffinity = {readResult}";
        Log.Api($"[{Name}] exclude from capture (window still hidden)", ExclusionVerified, ExclusionDetail);
        if (!ExclusionVerified)
        {
            Log.Decision($"[{Name}] exclusion from capture could not be verified, so this window will never be shown.");
        }
    }
}
