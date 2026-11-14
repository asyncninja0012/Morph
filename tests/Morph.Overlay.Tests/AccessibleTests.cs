using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

using Morph.Overlay.Theme;

namespace Morph.Overlay.Tests;

public sealed class AccessibleTests
{
    [Fact]
    public void Text_scale_grows_text_and_width_but_not_icons() => OnSta(() =>
    {
        var heading = new TextBlock { Text = "Where results go", FontSize = 15 };
        var icon = new TextBlock { Text = "", FontSize = 10, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
        var plain = new TextBlock { Text = "inherits" };
        var window = new Window { Width = 400, FontSize = 14, SizeToContent = SizeToContent.Height, Content = new StackPanel { Children = { heading, icon, plain } } };

        Accessible.Attach(window, 1.5);
        Accessible.Walk(window, 1.5, [], (_, _) => { });

        Assert.Equal(21, window.FontSize, 3);
        Assert.Equal(22.5, heading.FontSize, 3);
        Assert.Equal(10, icon.FontSize);
        Assert.Equal(21, plain.FontSize, 3); // inherited from the window
        Assert.Equal(Math.Min(600, SystemParameters.WorkArea.Width * 0.9), window.Width, 3);
        Assert.True(double.IsNaN(window.Height)); // sized to content: left alone
    });

    [Fact]
    public void Text_scale_never_makes_a_window_wider_than_the_screen() => OnSta(() =>
    {
        var window = new Window { Width = SystemParameters.WorkArea.Width * 0.8 };
        Accessible.Attach(window, 2.25);
        Assert.Equal(SystemParameters.WorkArea.Width * 0.9, window.Width, 3);
    });

    [Fact]
    public void At_normal_size_nothing_changes_and_live_text_is_watched() => OnSta(() =>
    {
        var live = new TextBlock { Text = "0:00", FontSize = 12 };
        AutomationProperties.SetLiveSetting(live, AutomationLiveSetting.Polite);
        var window = new Window { Width = 400, Content = new StackPanel { Children = { live, new TextBlock { Text = "quiet" } } } };
        List<TextBlock> watched = [];

        Accessible.Attach(window, 1);
        Accessible.Walk(window, 1, watched, (_, _) => { });

        Assert.Equal(400, window.Width);
        Assert.Equal(12, live.FontSize);
        Assert.Equal([live], watched);
    });

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }
}
