using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Morph.Editors;

// WPF panels have no UI Automation peer, so Narrator never reached an editor's focusable surface (crop box, timeline, pages) or the
// name of a chip group, however AutomationProperties.Name was set (PRD §7, task 5.2). These are the same panels with one.

/// <summary>A focusable editor surface (picture, timeline): Narrator reads its name, the keys to use, when it gets focus.</summary>
public sealed class SurfaceGrid : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new NamedPeer(this, AutomationControlType.Pane);
}

/// <summary><see cref="SurfaceGrid"/> for the page organizer's wrapping grid of pages.</summary>
public sealed class SurfaceWrapPanel : WrapPanel
{
    protected override AutomationPeer OnCreateAutomationPeer() => new NamedPeer(this, AutomationControlType.Pane);
}

/// <summary>A row of chips (shape, tool, colour): Narrator says the group's name with the chip.</summary>
public sealed class ChipGroup : StackPanel
{
    protected override AutomationPeer OnCreateAutomationPeer() => new NamedPeer(this, AutomationControlType.Group);
}

internal sealed class NamedPeer(FrameworkElement owner, AutomationControlType type) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => type;

    protected override string GetClassNameCore() => Owner.GetType().Name;
}
