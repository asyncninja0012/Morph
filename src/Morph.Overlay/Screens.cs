using System.Windows;

using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Morph.Overlay;

public static class Screens
{
    /// <summary>
    /// The work area (screen minus taskbar) of the monitor under the cursor, in that monitor's DIPs: the size a
    /// PerMonitorV2 window has to fit once it opens there (WindowStartupLocation.CenterScreen picks the same monitor).
    /// </summary>
    public static Size WorkAreaAtCursor()
    {
        var monitor = PInvoke.MonitorFromPoint(PInvoke.GetCursorPos(out var p) ? p : default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return new Size(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        }

        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Failed)
        {
            dpi = 96;
        }

        var work = info.rcWork;
        return new Size((work.right - work.left) * 96.0 / dpi, (work.bottom - work.top) * 96.0 / dpi);
    }
}
