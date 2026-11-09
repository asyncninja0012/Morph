using System.Windows;
using System.Windows.Media;

namespace Morph.Overlay.Wheel;

/// <summary>
/// Ring maths for design A. Angles are degrees clockwise from 12 o'clock; wedge 0 is centred at the top.
/// Coordinates are DIPs relative to the wheel centre, y pointing down.
/// </summary>
internal static class WheelGeometry
{
    // Design A proportions at ~0.6x the mockup, sized like the reference wheel (docs/reference/).
    public const double DiscRadius = 120;
    public const double OuterRadius = 114;
    public const double InnerRadius = 50;
    public const double CentreRadius = 46;
    public const double GapDegrees = 4;
    /// <summary>Icon and label sit stacked (icon above) around the point at this radius on the wedge's centre line.</summary>
    public const double ContentRadius = 82;

    // Outer ring of presets (Compress → Small/Balanced/High), fanned out around its wedge's centre line.
    public const double RingInnerRadius = 124;
    public const double RingOuterRadius = 176;
    public const double RingContentRadius = 150;
    public const double ChoiceDegrees = 34;

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

    /// <summary>Centre angle of choice <paramref name="index"/> of <paramref name="count"/> around wedge <paramref name="parent"/>.</summary>
    public static double ChoiceAngle(int parent, int parentCount, int index, int count) =>
        parent * 360.0 / parentCount + (index - (count - 1) / 2.0) * ChoiceDegrees;

    /// <returns>The choice under the point, or null. Anything past the disc's edge counts, so the gap before the ring isn't dead.</returns>
    public static int? HitTestChoice(double dx, double dy, int parent, int parentCount, int count)
    {
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= DiscRadius || distance > RingOuterRadius)
        {
            return null;
        }

        var angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
        var first = ChoiceAngle(parent, parentCount, 0, count) - ChoiceDegrees / 2;
        var offset = ((angle - first) % 360 + 360) % 360;
        return offset < count * ChoiceDegrees ? (int)(offset / ChoiceDegrees) : null;
    }

    public static Point PointAt(double angleDegrees, double radius)
    {
        var a = angleDegrees * Math.PI / 180;
        return new Point(radius * Math.Sin(a), -radius * Math.Cos(a));
    }

    public static Geometry Wedge(int index, int count, Point centre) =>
        Sector(index * 360.0 / count, 360.0 / count - GapDegrees, InnerRadius, OuterRadius, centre);

    public static Geometry Choice(int parent, int parentCount, int index, int count, Point centre) =>
        Sector(ChoiceAngle(parent, parentCount, index, count), ChoiceDegrees - GapDegrees, RingInnerRadius, RingOuterRadius, centre);

    /// <summary>The ring band behind a wedge's choices, from the disc out, so the pointer never crosses a see-through gap.</summary>
    public static Geometry ChoiceBand(int parent, int parentCount, int count, Point centre) =>
        Sector(parent * 360.0 / parentCount, count * ChoiceDegrees, OuterRadius, RingOuterRadius, centre);

    private static Geometry Sector(double mid, double span, double inner, double outer, Point centre)
    {
        var start = mid - span / 2;
        var end = mid + span / 2;
        var large = span > 180;

        var figure = new PathFigure { StartPoint = centre + (Vector)PointAt(start, outer), IsClosed = true };
        figure.Segments.Add(new ArcSegment(centre + (Vector)PointAt(end, outer), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(centre + (Vector)PointAt(end, inner), true));
        figure.Segments.Add(new ArcSegment(centre + (Vector)PointAt(start, inner), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
