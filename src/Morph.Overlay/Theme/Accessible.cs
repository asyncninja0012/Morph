using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Morph.Overlay.Theme;

/// <summary>
/// What WPF doesn't do on its own for PRD §7 (task 5.2): Windows' text size (Settings → Accessibility → Text size), Narrator hearing
/// <c>AutomationProperties.LiveSetting</c> text when it changes, and announcements from windows that never take focus (the wheel).
/// <see cref="MorphTheme.Apply"/> attaches it to every Morph window.
/// </summary>
public static class Accessible
{
    private static readonly ConditionalWeakTable<Window, object> Attached = [];
    private static readonly DependencyPropertyDescriptor TextChanged = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    /// <summary>Windows text size, 1 to 2.25.</summary>
    public static double TextScale
    {
        get
        {
            try
            {
                return Math.Clamp(new Windows.UI.ViewManagement.UISettings().TextScaleFactor, 1, 2.25);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                return 1; // no WinRT UI settings (e.g. a service session): normal size
            }
        }
    }

    /// <summary>Has Narrator (any UI Automation client) read <paramref name="text"/> now, interrupting what it read from Morph before.</summary>
    public static void Announce(UIElement element, string text)
    {
        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, "Morph");
    }

    internal static void Attach(Window window) => Attach(window, TextScale);

    internal static void Attach(Window window, double scale)
    {
        if (!Attached.TryAdd(window, Attached))
        {
            return;
        }

        if (scale > 1)
        {
            // Fluent controls inherit the window's font size; widths grow with the text, but never past the screen.
            window.FontSize *= scale;
            var area = SystemParameters.WorkArea;
            window.Width = Grow(window.Width, scale, area.Width);
            window.MinWidth = Grow(window.MinWidth, scale, area.Width);
            if (window.SizeToContent is SizeToContent.Manual or SizeToContent.Width)
            {
                window.Height = Grow(window.Height, scale, area.Height);
                window.MinHeight = Grow(window.MinHeight, scale, area.Height);
            }
        }

        List<TextBlock> live = [];
        EventHandler announce = (sender, _) =>
        {
            if (sender is TextBlock t && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            {
                UIElementAutomationPeer.CreatePeerForElement(t)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        };
        window.Loaded += (_, _) => Walk(window, scale, live, announce);
        window.Closed += (_, _) => live.ForEach(t => TextChanged.RemoveValueChanged(t, announce)); // the descriptor holds them strongly
    }

    // Font sizes Morph set itself (headings, labels) scale too (the window's own was done in Attach); icon glyphs don't (Windows' guidance), nor does picture content
    // (annotations are drawn later, so the walk never sees them).
    internal static void Walk(DependencyObject node, double scale, List<TextBlock> live, EventHandler announce)
    {
        if (scale > 1 && node is not Window && node.ReadLocalValue(TextElement.FontSizeProperty) is double size && !IsIconFont(node))
        {
            node.SetValue(TextElement.FontSizeProperty, size * scale);
        }

        if (node is TextBlock text && AutomationProperties.GetLiveSetting(text) != AutomationLiveSetting.Off && !live.Contains(text))
        {
            TextChanged.AddValueChanged(text, announce);
            live.Add(text);
        }

        // The logical tree: every element Morph's XAML and code put in the window (templates only hold icons and bound rows).
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            Walk(child, scale, live, announce);
        }
    }

    private static bool IsIconFont(DependencyObject node) =>
        node.GetValue(TextElement.FontFamilyProperty) is FontFamily f && (f.Source.Contains("Icons", StringComparison.Ordinal) || f.Source.Contains("MDL2", StringComparison.Ordinal));

    private static double Grow(double value, double scale, double max) =>
        double.IsNaN(value) || value == 0 ? value : Math.Max(value, Math.Min(value * scale, max * 0.9));
}
