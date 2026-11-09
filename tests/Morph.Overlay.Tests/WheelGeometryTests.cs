using Morph.Overlay.Wheel;

namespace Morph.Overlay.Tests;

public sealed class WheelGeometryTests
{
    [Theory]
    // 6 wedges centred at 0°, 60°, ... 300°; a point exactly on a boundary belongs to the clockwise-next wedge.
    [InlineData(0, -82, 0)]      // 0°, straight up
    [InlineData(82, 0, 2)]       // 90°, boundary of wedges 1 and 2
    [InlineData(0, 82, 3)]       // 180°
    [InlineData(-82, 0, 5)]      // 270°, boundary of wedges 4 and 5
    [InlineData(-6, -82, 0)]     // just left of top
    public void Ring_points_map_to_wedges(double dx, double dy, int expected) =>
        Assert.Equal(expected, WheelGeometry.HitTest(dx, dy, 6));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -45)]   // inside the centre circle: cancel
    [InlineData(0, -121)]  // outside the disc
    public void Centre_and_outside_are_not_wedges(double dx, double dy) =>
        Assert.Null(WheelGeometry.HitTest(dx, dy, 6));

    [Fact]
    public void The_gap_between_wedges_belongs_to_a_neighbour()
    {
        // 6 wedges: boundary between 0 and 1 at 30°; a point 1° past it lies in the drawn gap.
        var p = WheelGeometry.PointAt(31, WheelGeometry.ContentRadius);
        Assert.Equal(1, WheelGeometry.HitTest(p.X, p.Y, 6));
    }

    [Fact]
    public void Every_wedge_centre_hits_its_own_wedge_for_6_to_8_wedges()
    {
        for (var count = 6; count <= 8; count++)
        {
            for (var i = 0; i < count; i++)
            {
                var p = WheelGeometry.PointAt(i * 360.0 / count, WheelGeometry.ContentRadius);
                Assert.Equal(i, WheelGeometry.HitTest(p.X, p.Y, count));
            }
        }
    }

    [Fact]
    public void Presets_fan_out_around_their_wedge()
    {
        // 6 wedges, wedge 1 at 60°; 3 presets at 26°, 60°, 94°.
        Assert.Equal(26, WheelGeometry.ChoiceAngle(1, 6, 0, 3), 6);
        for (var i = 0; i < 3; i++)
        {
            var p = WheelGeometry.PointAt(WheelGeometry.ChoiceAngle(1, 6, i, 3), WheelGeometry.RingContentRadius);
            Assert.Equal(i, WheelGeometry.HitTestChoice(p.X, p.Y, 1, 6, 3));
        }
    }

    [Theory]
    [InlineData(60, 100)]  // on the disc: the wedges' business
    [InlineData(60, 190)]  // past the ring
    [InlineData(150, 150)] // on the ring, beside the presets
    public void Outside_the_presets_is_no_preset(double angle, double radius)
    {
        var p = WheelGeometry.PointAt(angle, radius);
        Assert.Null(WheelGeometry.HitTestChoice(p.X, p.Y, 1, 6, 3));
    }

    [Fact]
    public void The_gap_between_disc_and_ring_counts_as_the_preset() =>
        Assert.Equal(1, WheelGeometry.HitTestChoice(WheelGeometry.PointAt(60, 122).X, WheelGeometry.PointAt(60, 122).Y, 1, 6, 3));
}
