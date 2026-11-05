using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using Microsoft.Win32;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Controls;

namespace Morph.Overlay.Theme;

/// <summary>
/// Morph's look (developer's call, 5 Nov 2026): Windows 11 acrylic glass with a warm tint and Morph's own orange accent,
/// light or dark with Windows. Shared by the wheel, toasts and small windows.
/// </summary>
public static partial class MorphGlass
{
    private const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark() => Registry.GetValue(Personalize, "AppsUseLightTheme", 1) is 0;

    /// <summary>Settings → Personalization → Colors → Transparency effects; off means solid surfaces.</summary>
    public static bool TransparencyEnabled() => Registry.GetValue(Personalize, "EnableTransparency", 1) is not 0;

    /// <summary>Morph orange. Light mode is deep enough for white text (about 5:1).</summary>
    public static Color Accent(bool dark) => dark ? Rgb(0xFF, 0x9A, 0x5C) : Rgb(0xC9, 0x4F, 0x0E);

    public static Color OnAccent(bool dark) => dark ? Rgb(0x2B, 0x12, 0x04) : Colors.White;

    /// <summary>
    /// Acrylic (system backdrop) behind a normal, activatable window with a transparent WPF background. Windows shows it
    /// while the window is active and a plain colour otherwise, like other Windows 11 dialogs.
    /// Returns false when Windows can't (transparency effects off, or before Windows 11 22H2).
    /// </summary>
    public static unsafe bool TryApplyBackdrop(Window window, bool dark)
    {
        var hwnd = Prepare(window, dark, round: true);
        var backdrop = DWM_SYSTEMBACKDROP_TYPE.DWMSBT_TRANSIENTWINDOW;
        return hwnd != HWND.Null
            && PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, (uint)sizeof(DWM_SYSTEMBACKDROP_TYPE)).Succeeded;
    }

    /// <summary>
    /// Blurred, warm-tinted glass behind a no-activate surface (wheel, toast). The system backdrop turns solid on
    /// inactive windows and these are never active, so this uses the blur-behind accent instead.
    /// Returns false when transparency effects are off; draw solid colours then.
    /// </summary>
    public static unsafe bool TryApplyBlur(Window window, bool dark, bool round)
    {
        var hwnd = Prepare(window, dark, round);
        if (hwnd == HWND.Null)
        {
            return false;
        }

        var tint = dark ? Rgb(0x22, 0x16, 0x10, 0xB8) : Rgb(0xFF, 0xEC, 0xDF, 0x8C);
        var policy = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            GradientColor = (uint)(tint.A << 24 | tint.B << 16 | tint.G << 8 | tint.R), // ABGR
        };
        var data = new CompositionAttributeData { Attribute = WcaAccentPolicy, Data = &policy, SizeOfData = sizeof(AccentPolicy) };
        return SetWindowCompositionAttribute((nint)hwnd.Value, &data) != 0;
    }

    public static Color Rgb(byte r, byte g, byte b, byte a = 0xFF) => Color.FromArgb(a, r, g, b);

    // Glass everywhere in the client area: DWM frame extended, WPF clearing to transparent.
    private static unsafe HWND Prepare(Window window, bool dark, bool round)
    {
        if (!TransparencyEnabled())
        {
            return HWND.Null;
        }

        var hwnd = new HWND(new WindowInteropHelper(window).EnsureHandle());
        BOOL darkMode = dark;
        PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &darkMode, (uint)sizeof(BOOL));
        var corners = round ? DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND : DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corners, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        if (PInvoke.DwmExtendFrameIntoClientArea(hwnd, &margins).Failed)
        {
            return HWND.Null;
        }

        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
        {
            target.BackgroundColor = Colors.Transparent;
        }

        return hwnd;
    }

    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;

    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    private unsafe struct CompositionAttributeData
    {
        public int Attribute;
        public void* Data;
        public int SizeOfData;
    }

    // Hand-written: undocumented user32 export (no metadata, so CsWin32 can't generate it). Used by the Windows shell
    // and many apps for blur behind; if it ever fails, callers fall back to solid colours.
    [LibraryImport("user32.dll")]
    private static unsafe partial int SetWindowCompositionAttribute(nint hwnd, CompositionAttributeData* data);
}
