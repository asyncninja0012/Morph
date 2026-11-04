using Morph.Overlay.Wheel;

namespace Morph.Overlay.Tests;

public sealed class WheelGeometryTests
{
    [Theory]
    // 6 wedges centred at 0°, 60°, ... 300°; a point exactly on a boundary belongs to the clockwise-next wedge.
    [InlineData(0, -137, 0)]     // 0°, straight up
    [InlineData(137, 0, 2)]      // 90°, boundary of wedges 1 and 2
    [InlineData(0, 137, 3)]      // 180°
    [InlineData(-137, 0, 5)]     // 270°, boundary of wedges 4 and 5
    [InlineData(-10, -137, 0)]   // just left of top
    public void Ring_points_map_to_wedges(double dx, double dy, int expected) =>
        Assert.Equal(expected, WheelGeometry.HitTest(dx, dy, 6));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -80)]   // inside the centre circle: cancel
    [InlineData(0, -201)]  // outside the disc
    public void Centre_and_outside_are_not_wedges(double dx, double dy) =>
        Assert.Null(WheelGeometry.HitTest(dx, dy, 6));

    [Fact]
    public void The_gap_between_wedges_belongs_to_a_neighbour()
    {
        // 6 wedges: boundary between 0 and 1 at 30°; a point 1° past it lies in the drawn gap.
        var p = WheelGeometry.PointAt(31, 137);
        Assert.Equal(1, WheelGeometry.HitTest(p.X, p.Y, 6));
    }

    [Fact]
    public void Every_wedge_centre_hits_its_own_wedge_for_6_to_8_wedges()
    {
        for (var count = 6; count <= 8; count++)
        {
            for (var i = 0; i < count; i++)
            {
                var p = WheelGeometry.PointAt(i * 360.0 / count, 137);
                Assert.Equal(i, WheelGeometry.HitTest(p.X, p.Y, count));
            }
        }
    }
}
