using System.Windows;

using Morph.Editors.Crop;

namespace Morph.Editors.Tests;

public sealed class CropMathTests
{
    private static readonly Size Source = new(400, 300);

    [Fact]
    public void Largest_box_of_a_shape_is_centred()
    {
        Assert.Equal(new Rect(50, 0, 300, 300), CropMath.Largest(Source, 1));
        Assert.Equal(new Rect(0, 37.5, 400, 225), CropMath.Largest(Source, 16.0 / 9));
        Assert.Equal(new Rect(Source), CropMath.Largest(Source, null));
    }

    [Fact]
    public void Moving_stops_at_the_edges()
    {
        Assert.Equal(new Rect(300, 0, 100, 100), CropMath.Move(new Rect(250, 50, 100, 100), new Vector(500, -500), Source));
    }

    [Fact]
    public void A_corner_drag_keeps_the_opposite_corner()
    {
        var box = CropMath.Resize(new Rect(100, 100, 100, 100), CropHandle.BottomRight, new Point(250, 180), Source, null, 1);

        Assert.Equal(new Rect(100, 100, 150, 80), box);
    }

    [Fact]
    public void Dragging_past_the_opposite_corner_flips_the_box()
    {
        var box = CropMath.Resize(new Rect(100, 100, 100, 100), CropHandle.BottomRight, new Point(50, 60), Source, null, 1);

        Assert.Equal(new Rect(50, 60, 50, 40), box);
    }

    [Fact]
    public void A_corner_drag_with_a_shape_keeps_it_and_stays_inside()
    {
        var box = CropMath.Resize(new Rect(0, 0, 100, 100), CropHandle.BottomRight, new Point(390, 120), Source, 1, 1);

        Assert.Equal(new Rect(0, 0, 300, 300), box); // the wider move drives (390 × 390), then the height limit shrinks it
    }

    [Fact]
    public void An_edge_drag_with_a_shape_grows_the_other_side_about_its_centre()
    {
        var box = CropMath.Resize(new Rect(100, 100, 100, 100), CropHandle.Right, new Point(260, 150), Source, 1, 1);

        Assert.Equal(new Rect(100, 70, 160, 160), box);
    }

    [Fact]
    public void A_free_edge_drag_moves_only_that_edge()
    {
        var box = CropMath.Resize(new Rect(100, 100, 100, 100), CropHandle.Top, new Point(999, -40), Source, null, 1);

        Assert.Equal(new Rect(100, 0, 100, 200), box);
    }

    [Fact]
    public void The_box_never_gets_smaller_than_the_minimum()
    {
        var box = CropMath.Resize(new Rect(100, 100, 100, 100), CropHandle.BottomRight, new Point(101, 101), Source, null, 16);

        Assert.Equal(new Rect(100, 100, 16, 16), box);
    }
}
