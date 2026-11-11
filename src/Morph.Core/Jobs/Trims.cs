using System.Globalization;
using System.Text.Json.Nodes;

namespace Morph.Core.Jobs;

/// <summary>One part to keep, in seconds from the start of the file.</summary>
public sealed record TrimSegment(double Start, double End);

/// <summary>
/// The trim job's parameters (ED-4): { "segments": [{ "start", "end" }], "mode": "fast" | "exact" }. Each segment becomes its own
/// file, in order. Fast copies the streams (seconds, no loss; each part starts at the keyframe at or before its start); exact
/// re-encodes so each part starts and ends where it was marked.
/// </summary>
public static class Trims
{
    private const int MaxSegments = 100;

    public static JsonObject ToParameters(IEnumerable<TrimSegment> segments, bool exact)
    {
        var list = new JsonArray();
        foreach (var s in segments)
        {
            list.Add(new JsonObject { ["start"] = Math.Round(s.Start, 3), ["end"] = Math.Round(s.End, 3) });
        }

        return new JsonObject { ["segments"] = list, ["mode"] = exact ? "exact" : "fast" };
    }

    /// <exception cref="EngineException">No segments, too many, or one that isn't well-formed.</exception>
    public static (IReadOnlyList<TrimSegment> Segments, bool Exact) Parse(JsonObject? parameters)
    {
        if (parameters?["segments"] is not JsonArray { Count: > 0 } list)
        {
            throw new EngineException("Mark the part to keep first.", "no segments");
        }

        if (list.Count > MaxSegments)
        {
            throw Bad($"{list.Count} segments");
        }

        var mode = (string?)parameters["mode"] ?? "fast";
        if (mode is not ("fast" or "exact"))
        {
            throw Bad($"mode {mode}");
        }

        var segments = new List<TrimSegment>(list.Count);
        foreach (var node in list)
        {
            if (node is not JsonObject o || Number(o["start"]) is not { } start || Number(o["end"]) is not { } end || start < 0 || end <= start)
            {
                throw Bad(node?.ToJsonString());
            }

            segments.Add(new TrimSegment(start, end));
        }

        return (segments, mode == "exact");
    }

    private static EngineException Bad(string? details) => new("Morph couldn't read where to cut.", $"bad trim: {details}");

    private static double? Number(JsonNode? node) =>
        node is JsonValue && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
}
