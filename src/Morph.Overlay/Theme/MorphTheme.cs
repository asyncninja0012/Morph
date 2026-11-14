using System.Windows;
using System.Windows.Media;

namespace Morph.Overlay.Theme;

/// <summary>
/// Morph glass for Fluent windows (App and editors) (<see cref="MorphGlass"/>): Morph orange in place of the Windows accent,
/// and the surface brushes the XAML uses (MorphSurfaceBrush for the window, MorphCardBrush for rows).
/// </summary>
public static class MorphTheme
{
    public static void Apply(FrameworkElement root, bool dark, bool glass)
    {
        if (root is Window window)
        {
            Accessible.Attach(window);
        }

        if (SystemParameters.HighContrast)
        {
            // Fluent's own high-contrast styles colour the controls; Morph's surfaces become the system window colours.
            root.Resources["MorphSurfaceBrush"] = SystemColors.WindowBrush;
            root.Resources["MorphCardBrush"] = SystemColors.WindowBrush;
            root.Resources["MorphCardStrokeBrush"] = SystemColors.WindowTextBrush;
            return;
        }

        var accent = MorphGlass.Accent(dark);
        var onAccent = MorphGlass.OnAccent(dark);
        var hover = Blend(accent, dark ? Colors.White : Colors.Black, 0.1);
        var pressed = Blend(accent, dark ? Colors.Black : Colors.White, 0.2);

        Set(root, "AccentTextFillColorPrimaryBrush", accent);
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentButtonBackground", "AccentButtonBorderBrush", "ToggleButtonBackgroundChecked",
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked", "TextControlFocusedBorderBrush",
            "SliderTrackValueFill", "SliderThumbBackground" })
        {
            Set(root, key, accent);
        }

        foreach (var key in new[] { "AccentFillColorSecondaryBrush", "AccentButtonBackgroundPointerOver", "AccentButtonBorderBrushPointerOver", "ToggleButtonBackgroundCheckedPointerOver",
            "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
            "SliderTrackValueFillPointerOver", "SliderThumbBackgroundPointerOver" })
        {
            Set(root, key, hover);
        }

        foreach (var key in new[] { "AccentFillColorTertiaryBrush", "AccentButtonBackgroundPressed", "AccentButtonBorderBrushPressed", "ToggleButtonBackgroundCheckedPressed", "ToggleButtonBorderBrushCheckedPressed",
            "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeCheckedPressed",
            "SliderTrackValueFillPressed", "SliderThumbBackgroundPressed" })
        {
            Set(root, key, pressed);
        }

        foreach (var key in new[] { "TextOnAccentFillColorPrimaryBrush", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPressed" })
        {
            Set(root, key, onAccent);
        }

        // Glass: warm translucent tints over the blur. Solid: opaque warm greys for transparency effects off.
        Set(root, "MorphSurfaceBrush", (dark, glass) switch
        {
            (false, true) => MorphGlass.Rgb(0xFF, 0xF4, 0xEC, 0x59),
            (true, true) => MorphGlass.Rgb(0x1F, 0x15, 0x10, 0x73),
            (false, false) => MorphGlass.Rgb(0xFB, 0xF7, 0xF4),
            (true, false) => MorphGlass.Rgb(0x2A, 0x24, 0x20),
        });
        Set(root, "MorphCardBrush", (dark, glass) switch
        {
            (false, true) => MorphGlass.Rgb(0xFF, 0xFF, 0xFF, 0x80),
            (true, true) => MorphGlass.Rgb(0xFF, 0xFF, 0xFF, 0x14),
            (false, false) => Colors.White,
            (true, false) => MorphGlass.Rgb(0x36, 0x2F, 0x2A),
        });
        Set(root, "MorphCardStrokeBrush", dark ? MorphGlass.Rgb(0xFF, 0xFF, 0xFF, 0x1F) : MorphGlass.Rgb(0xFF, 0xFF, 0xFF, 0xB3));
    }

    private static void Set(FrameworkElement root, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        root.Resources[key] = brush;
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(a.A,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
