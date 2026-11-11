using System.Globalization;
using System.Text.Json.Nodes;

namespace Morph.Core.Jobs;

/// <summary>One box to black out (ED-3), in source units (pixels; PDF points on the page as shown), top-left origin; page 1-based.</summary>
public sealed record RedactBox(int Page, double X, double Y, double W, double H);

/// <summary>The redact job's parameters: { "boxes": [{ "page", "x", "y", "w", "h" }] }.</summary>
public static class Redactions
{
    private const int MaxBoxes = 1_000;

    public static JsonObject ToParameters(IEnumerable<RedactBox> boxes)
    {
        var list = new JsonArray();
        foreach (var b in boxes)
        {
            list.Add(new JsonObject { ["page"] = b.Page, ["x"] = Math.Round(b.X, 2), ["y"] = Math.Round(b.Y, 2), ["w"] = Math.Round(b.W, 2), ["h"] = Math.Round(b.H, 2) });
        }

        return new JsonObject { ["boxes"] = list };
    }

    /// <exception cref="EngineException">No boxes, or a box that isn't well-formed.</exception>
    public static IReadOnlyList<RedactBox> Parse(JsonObject? parameters)
    {
        if (parameters?["boxes"] is not JsonArray { Count: > 0 } list)
        {
            throw new EngineException("Draw a box over what to hide first.", "no boxes");
        }

        if (list.Count > MaxBoxes)
        {
            throw Bad($"{list.Count} boxes");
        }

        var boxes = new List<RedactBox>(list.Count);
        foreach (var node in list)
        {
            if (node is not JsonObject o || Number(o["x"]) is not { } x || Number(o["y"]) is not { } y
                || Number(o["w"]) is not { } w || w <= 0 || Number(o["h"]) is not { } h || h <= 0)
            {
                throw Bad(node?.ToJsonString());
            }

            var page = o["page"] is null ? 1 : Number(o["page"]) is { } n && n >= 1 && n == Math.Floor(n) && n <= int.MaxValue ? (int)n : throw Bad(o.ToJsonString());
            boxes.Add(new RedactBox(page, x, y, w, h));
        }

        return boxes;
    }

    private static EngineException Bad(string? details) => new("Morph couldn't read the boxes to hide.", $"bad redact box: {details}");

    private static double? Number(JsonNode? node) =>
        node is JsonValue && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
}
