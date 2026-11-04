using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Morph.Overlay.Wheel;

/// <summary>
/// Invisible full-monitor drop target under the wheel (PLAN §3, D2). It proves an OLE file drag is in progress
/// (DragEnter arrives) and swallows drops outside the wheel so they cancel instead of landing in Explorer (IX-4).
/// A bare HwndSource, not a Window: WPF's Window rewrites WS_EX_LAYERED on Show, which made this an opaque black screen.
/// </summary>
internal sealed class DropCatcherWindow : IDisposable
{
    private readonly HwndSource _source;

    public DropCatcherWindow()
    {
        const WINDOW_EX_STYLE exStyle = WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
            | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST;
        _source = new HwndSource(new HwndSourceParameters("Morph drop catcher")
        {
            WindowStyle = unchecked((int)WINDOW_STYLE.WS_POPUP),
            ExtendedWindowStyle = (int)exStyle,
            Width = 1,
            Height = 1,
        });
        Hwnd = new HWND(_source.Handle);
        // WPF clears WS_EX_LAYERED in WM_STYLECHANGING unless UsesPerPixelOpacity (which would mean a full-monitor
        // bitmap). Keep the bit, then set constant alpha 1/255: invisible, still hit-testable, composited by DWM.
        _source.AddHook(KeepLayered);
        PInvoke.SetWindowLongPtr(Hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);
        PInvoke.SetLayeredWindowAttributes(Hwnd, default, 1, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);

        // Background must be non-null to be hit-testable.
        Surface = new Border { Background = Brushes.Black, AllowDrop = true };
        _source.RootVisual = Surface;
    }

    public HWND Hwnd { get; }

    /// <summary>Raises DragEnter/DragOver/DragLeave/Drop.</summary>
    public UIElement Surface { get; }

    public void ShowOn(RECT monitor) => NativeWindows.Place(Hwnd, monitor, show: true);

    public void Hide() => NativeWindows.Hide(Hwnd);

    public void Dispose() => _source.Dispose();

    private static unsafe IntPtr KeepLayered(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)PInvoke.WM_STYLECHANGING && (int)wParam == (int)WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
        {
            var style = (STYLESTRUCT*)lParam;
            style->styleNew |= (uint)WINDOW_EX_STYLE.WS_EX_LAYERED;
            handled = true;
        }

        return IntPtr.Zero;
    }
}
