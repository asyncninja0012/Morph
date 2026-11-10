using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Morph.Core.Jobs;

/// <summary>
/// One mark from the annotate editor (ED-2), in source units (pixels; PDF points on the page as shown), top-left origin.
/// Arrow, rect, ellipse and pixelate have two points (start and end, or two corners); text has one (its top-left corner);
/// highlight is a freehand line through all its points.
/// </summary>
/// <param name="Color">"#RRGGBB".</param>
/// <param name="Stroke">Line width; text is <see cref="Annotations.FontSize"/> of it, highlight <see cref="Annotations.HighlightWidth"/>.</param>
/// <param name="Page">1-based (PDF); pictures have only page 1.</param>
public sealed record AnnotationShape(string Type, IReadOnlyList<(double X, double Y)> Points, string Color, double Stroke, string? Text = null, int Page = 1);

/// <summary>
/// The annotate job's parameters, shared by the editor (which draws the preview) and the engines (which draw the file), so
/// both draw the same: { "shapes": [{ "type", "points": [[x, y], ...], "color", "stroke", "text", "page" }] }.
/// </summary>
public static partial class Annotations
{
    public static readonly string[] Types = ["arrow", "rect", "ellipse", "text", "highlight", "pixelate"];

    /// <summary>Highlighter ink lets the page show through.</summary>
    public const double HighlightOpacity = 0.4;

    private const int MaxShapes = 1_000;
    private const int MaxPoints = 20_000;
    private const int MaxText = 500;

    public static double FontSize(double stroke) => stroke * 5;

    public static double HighlightWidth(double stroke) => stroke * 4;

    /// <summary>Pixelate cell size for a source: about 80 cells across its longest edge, at least 6.</summary>
    public static double PixelBlock(double sourceWidth, double sourceHeight) => Math.Max(6, Math.Max(sourceWidth, sourceHeight) / 80);

    /// <summary>
    /// An arrow's line (it stops inside the head, so a round cap doesn't poke past the tip) and its filled head: tip, then the
    /// two back corners.
    /// </summary>
    public static ((double X, double Y) LineEnd, (double X, double Y)[] Head) Arrow((double X, double Y) from, (double X, double Y) to, double stroke)
    {
        var (dx, dy) = (to.X - from.X, to.Y - from.Y);
        var length = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
        var (ux, uy) = (dx / length, dy / length);
        var head = Math.Min(length, Math.Max(stroke * 4, 10));
        var (bx, by) = (to.X - ux * head, to.Y - uy * head);
        var half = head * 0.5;
        return ((to.X - ux * head * 0.5, to.Y - uy * head * 0.5), [to, (bx - uy * half, by + ux * half), (bx + uy * half, by - ux * half)]);
    }

    /// <summary>Two corners in any order → left, top, width, height.</summary>
    public static (double X, double Y, double W, double H) Box(AnnotationShape shape)
    {
        var (a, b) = (shape.Points[0], shape.Points[^1]);
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    public static JsonObject ToParameters(IEnumerable<AnnotationShape> shapes)
    {
        var list = new JsonArray();
        foreach (var s in shapes)
        {
            var points = new JsonArray();
            foreach (var (x, y) in s.Points)
            {
                points.Add(new JsonArray(Math.Round(x, 2), Math.Round(y, 2)));
            }

            var shape = new JsonObject { ["type"] = s.Type, ["points"] = points, ["color"] = s.Color, ["stroke"] = Math.Round(s.Stroke, 2), ["page"] = s.Page };
            if (s.Text is not null)
            {
                shape["text"] = s.Text;
            }

            list.Add(shape);
        }

        return new JsonObject { ["shapes"] = list };
    }

    /// <exception cref="EngineException">No shapes, or a shape that isn't well-formed.</exception>
    public static IReadOnlyList<AnnotationShape> Parse(JsonObject? parameters)
    {
        if (parameters?["shapes"] is not JsonArray { Count: > 0 } list)
        {
            throw new EngineException("Draw something first.", "no shapes");
        }

        if (list.Count > MaxShapes)
        {
            throw Bad($"{list.Count} shapes");
        }

        var shapes = new List<AnnotationShape>(list.Count);
        foreach (var node in list)
        {
            if (node is not JsonObject o || (string?)o["type"] is not { } type || !Types.Contains(type)
                || o["points"] is not JsonArray { Count: > 0 and <= MaxPoints } raw)
            {
                throw Bad(node?.ToJsonString());
            }

            var points = new List<(double, double)>(raw.Count);
            foreach (var p in raw)
            {
                if (p is not JsonArray { Count: 2 } xy || Number(xy[0]) is not { } x || Number(xy[1]) is not { } y)
                {
                    throw Bad(p?.ToJsonString());
                }

                points.Add((x, y));
            }

            var needed = type switch { "text" => points.Count == 1, "highlight" => points.Count >= 2, _ => points.Count == 2 };
            var color = (string?)o["color"] is { } c && Hex().IsMatch(c) ? c : throw Bad($"color {o["color"]?.ToJsonString()}");
            var stroke = Number(o["stroke"]) is { } s and > 0 and <= 10_000 ? s : throw Bad($"stroke {o["stroke"]?.ToJsonString()}");
            var page = o["page"] is null ? 1 : Number(o["page"]) is { } n && n >= 1 && n == Math.Floor(n) && n <= int.MaxValue ? (int)n : throw Bad($"page {o["page"]?.ToJsonString()}");
            var text = (string?)o["text"];
            if (!needed || (type == "text" && (string.IsNullOrWhiteSpace(text) || text.Length > MaxText)))
            {
                throw Bad(o.ToJsonString());
            }

            shapes.Add(new AnnotationShape(type, points, color, stroke, type == "text" ? text : null, page));
        }

        return shapes;
    }

    private static EngineException Bad(string? details) => new("Morph couldn't read the drawing.", $"bad annotation: {details}");

    // Any finite JSON number: a JsonObject built in code holds an int or double where parsed JSON holds a JsonElement.
    private static double? Number(JsonNode? node) =>
        node is JsonValue && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex Hex();
}
