using Morph.Overlay.Input;

namespace Morph.Overlay.Tests;

public sealed class DragTrackerTests
{
    private readonly DragTracker _tracker = new(dragWidth: 4, dragHeight: 4);

    [Fact]
    public void Moving_inside_the_drag_rectangle_is_not_a_drag()
    {
        _tracker.ButtonDown(100, 100);

        Assert.False(_tracker.Move(102, 98));
        Assert.False(_tracker.ButtonUp());
    }

    [Fact]
    public void Leaving_the_rectangle_starts_the_drag_once_and_button_up_ends_it()
    {
        _tracker.ButtonDown(100, 100);

        Assert.True(_tracker.Move(103, 100));
        Assert.False(_tracker.Move(150, 150));
        Assert.True(_tracker.ButtonUp());
        Assert.False(_tracker.ButtonUp());
    }

    [Fact]
    public void Movement_without_a_press_is_ignored()
    {
        Assert.False(_tracker.Move(500, 500));
        Assert.False(_tracker.IsWatching);
    }

    [Fact]
    public void Reset_drops_the_press()
    {
        _tracker.ButtonDown(0, 0);
        _tracker.Reset();

        Assert.False(_tracker.Move(50, 0));
        Assert.False(_tracker.ButtonUp());
    }
}
