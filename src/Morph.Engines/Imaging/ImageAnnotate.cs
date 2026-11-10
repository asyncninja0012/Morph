using ImageMagick;
using ImageMagick.Drawing;

using Morph.Core.Jobs;

namespace Morph.Engines.Imaging;

/// <summary>Annotate (ED-2) for pictures: draws the editor's marks onto the upright picture with Magick, in order.</summary>
internal static class ImageAnnotate
{
    private static readonly string Font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");

    /// <summary>Draws the page-1 shapes; false when there are none (all were drawn on later pages of a PDF).</summary>
    public static bool Draw(MagickImage image, IReadOnlyList<AnnotationShape> shapes)
    {
        var drawn = false;
        foreach (var shape in shapes.Where(s => s.Page == 1))
        {
            var color = new MagickColor(shape.Color);
            var (x, y, w, h) = Annotations.Box(shape);
            var line = new Drawables().StrokeColor(color).StrokeWidth(shape.Stroke).FillColor(MagickColors.Transparent)
                .StrokeLineCap(LineCap.Round).StrokeLineJoin(LineJoin.Round);
            switch (shape.Type)
            {
                case "rect":
                    line.Rectangle(x, y, x + w, y + h).Draw(image);
                    break;
                case "ellipse":
                    line.Ellipse(x + w / 2, y + h / 2, w / 2, h / 2, 0, 360).Draw(image);
                    break;
                case "arrow":
                    var (end, head) = Annotations.Arrow(shape.Points[0], shape.Points[1], shape.Stroke);
                    line.Line(shape.Points[0].X, shape.Points[0].Y, end.X, end.Y).Draw(image);
                    new Drawables().FillColor(color).StrokeColor(MagickColors.Transparent).Polygon(head.Select(p => new PointD(p.X, p.Y))).Draw(image);
                    break;
                case "highlight":
                    var ink = new MagickColor(color) { A = (byte)(255 * Annotations.HighlightOpacity) };
                    new Drawables().StrokeColor(ink).StrokeWidth(Annotations.HighlightWidth(shape.Stroke)).FillColor(MagickColors.Transparent)
                        .StrokeLineCap(LineCap.Round).StrokeLineJoin(LineJoin.Round).Polyline(shape.Points.Select(p => new PointD(p.X, p.Y))).Draw(image);
                    break;
                case "pen":
                    line.Polyline(shape.Points.Select(p => new PointD(p.X, p.Y))).Draw(image);
                    break;
                case "text":
                    var text = new Drawables().FillColor(color).StrokeColor(MagickColors.Transparent).FontPointSize(Annotations.FontSize(shape.Stroke))
                        .Gravity(Gravity.Northwest);
                    if (File.Exists(Font))
                    {
                        text.Font(Font);
                    }

                    text.Text(x, y, shape.Text!).Draw(image);
                    break;
                case "pixelate":
                    Pixelate(image, x, y, w, h);
                    break;
            }

            drawn = true;
        }

        return drawn;
    }

    private static void Pixelate(MagickImage image, double x, double y, double w, double h)
    {
        var left = (int)Math.Clamp(Math.Floor(x), 0, image.Width - 1);
        var top = (int)Math.Clamp(Math.Floor(y), 0, image.Height - 1);
        var width = (uint)Math.Clamp(Math.Ceiling(x + w) - left, 1, image.Width - left);
        var height = (uint)Math.Clamp(Math.Ceiling(y + h) - top, 1, image.Height - top);
        var block = Annotations.PixelBlock(image.Width, image.Height);
        using var part = image.CloneArea(left, top, width, height);
        part.Sample(new MagickGeometry((uint)Math.Max(1, Math.Ceiling(width / block)), (uint)Math.Max(1, Math.Ceiling(height / block))) { IgnoreAspectRatio = true });
        part.Sample(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
        image.Composite(part, left, top, CompositeOperator.Copy);
    }
}
