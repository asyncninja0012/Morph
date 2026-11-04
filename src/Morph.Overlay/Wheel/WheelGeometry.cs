using System.Windows;
using System.Windows.Media;

namespace Morph.Overlay.Wheel;

/// <summary>
/// Ring maths for design A. Angles are degrees clockwise from 12 o'clock; wedge 0 is centred at the top.
/// Coordinates are DIPs relative to the wheel centre, y pointing down.
/// </summary>
internal static class WheelGeometry
{
    public const double DiscRadius = 200;
    public const double OuterRadius = 190;
    public const double InnerRadius = 84;
    public const double CentreRadius = 78;
    public const double GapDegrees = 4;
    /// <summary>Icon and label sit stacked (icon above) around the point at this radius on the wedge's centre line.</summary>
    public const double ContentRadius = 137;

    /// <returns>The wedge under the point, or null for the centre (cancel) and outside the disc.</returns>
    public static int? HitTest(double dx, double dy, int count)
    {
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (count <= 0 || distance < InnerRadius || distance > DiscRadius)
        {
            return null;
        }

        // Gaps belong to the nearest wedge: no dead zones while the user is aiming mid-drag.
        var step = 360.0 / count;
        var angle = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360 + step / 2) % 360;
        return (int)(angle / step) % count;
    }

    public static Point PointAt(double angleDegrees, double radius)
    {
        var a = angleDegrees * Math.PI / 180;
        return new Point(radius * Math.Sin(a), -radius * Math.Cos(a));
    }

    public static Geometry Wedge(int index, int count, Point centre)
    {
        var step = 360.0 / count;
        var start = index * step - step / 2 + GapDegrees / 2;
        var end = start + step - GapDegrees;
        var large = end - start > 180;

        var figure = new PathFigure { StartPoint = centre + (Vector)PointAt(start, OuterRadius), IsClosed = true };
        figure.Segments.Add(new ArcSegment(centre + (Vector)PointAt(end, OuterRadius), new Size(OuterRadius, OuterRadius), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(centre + (Vector)PointAt(end, InnerRadius), true));
        figure.Segments.Add(new ArcSegment(centre + (Vector)PointAt(start, InnerRadius), new Size(InnerRadius, InnerRadius), 0, large, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
