using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Morph.Editors.Tests;

public sealed class NamedPanelsTests
{
    // Plain WPF panels have no automation peer, so Narrator never heard these names (task 5.2).
    [Fact]
    public void Editor_surfaces_and_chip_groups_reach_narrator()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Null(UIElementAutomationPeer.CreatePeerForElement(Named(new Grid())));
                Assert.Equal((AutomationControlType.Pane, "Crop box"), Peer(Named(new SurfaceGrid { Focusable = true })));
                Assert.Equal((AutomationControlType.Pane, "Crop box"), Peer(Named(new SurfaceWrapPanel { Focusable = true })));
                Assert.Equal((AutomationControlType.Group, "Crop box"), Peer(Named(new ChipGroup())));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static T Named<T>(T element)
        where T : UIElement
    {
        AutomationProperties.SetName(element, "Crop box");
        return element;
    }

    private static (AutomationControlType, string) Peer(UIElement element)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element)!;
        return (peer.GetAutomationControlType(), peer.GetName());
    }
}
