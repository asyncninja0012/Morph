using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Morph.Overlay.Wheel;

/// <summary>Win32 plumbing for overlay windows. All positions are physical pixels (the app is PerMonitorV2).</summary>
internal static class NativeWindows
{
    /// <summary>Creates the HWND and makes it a no-activate, topmost tool window that never appears in Alt+Tab.</summary>
    public static HWND Prepare(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Topmost = true;

        var hwnd = new HWND(new WindowInteropHelper(window).EnsureHandle());
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
            | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);
        return hwnd;
    }

    /// <summary>The keyboard path (IX-5) needs the wheel to take focus; drags must never (WS_EX_NOACTIVATE).</summary>
    public static void SetActivatable(HWND hwnd, bool activatable)
    {
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle = activatable ? exStyle & ~WINDOW_EX_STYLE.WS_EX_NOACTIVATE : exStyle | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);
    }

    /// <summary>
    /// Moves (and optionally shows) without activating. Called twice because crossing to a monitor with another DPI
    /// makes WPF apply the system's suggested rect; the second call lands on the exact rect.
    /// </summary>
    public static void Place(HWND hwnd, RECT r, bool show = false)
    {
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | (show ? SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW : 0);
        for (var i = 0; i < 2; i++)
        {
            PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, r.left, r.top, r.right - r.left, r.bottom - r.top, flags);
        }
    }

    public static void Hide(HWND hwnd) =>
        PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);

    public static List<RECT> MonitorRects()
    {
        var rects = new List<RECT>();
        unsafe
        {
            PInvoke.EnumDisplayMonitors(HDC.Null, (RECT*)null, (monitor, _, rect, _) =>
            {
                rects.Add(*rect);
                return true;
            }, default);
        }

        return rects;
    }

    /// <summary>A square of <paramref name="sizeDip"/> centred on the point, clamped into that monitor's work area.</summary>
    public static RECT CenteredSquare(System.Drawing.Point centre, double sizeDip)
    {
        var monitor = PInvoke.MonitorFromPoint(centre, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        PInvoke.GetMonitorInfo(monitor, ref info);
        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Failed)
        {
            dpi = 96;
        }

        var size = (int)Math.Ceiling(sizeDip * dpi / 96);
        var work = info.rcWork;
        var x = Math.Clamp(centre.X - size / 2, work.left, Math.Max(work.left, work.right - size));
        var y = Math.Clamp(centre.Y - size / 2, work.top, Math.Max(work.top, work.bottom - size));
        return new RECT(x, y, x + size, y + size);
    }

    /// <summary>What is on screen inside <paramref name="r"/> right now (physical pixels), for the wheel's frosted glass.</summary>
    public static BitmapSource? CaptureScreen(RECT r)
    {
        var (width, height) = (r.right - r.left, r.bottom - r.top);
        var screen = PInvoke.GetDC(HWND.Null);
        var memory = PInvoke.CreateCompatibleDC(screen);
        var bitmap = PInvoke.CreateCompatibleBitmap(screen, width, height);
        try
        {
            var old = PInvoke.SelectObject(memory, bitmap);
            var copied = PInvoke.BitBlt(memory, 0, 0, width, height, screen, r.left, r.top, ROP_CODE.SRCCOPY);
            PInvoke.SelectObject(memory, old);
            if (!copied)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            PInvoke.DeleteObject(bitmap);
            PInvoke.DeleteDC(memory);
            _ = PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    public static System.Drawing.Point CursorPosition() => PInvoke.GetCursorPos(out var p) ? p : default;
}
