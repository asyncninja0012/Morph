using System.Globalization;
using System.Text.Json.Nodes;

namespace Morph.Core.Jobs;

/// <summary>One page of the new PDF: page <paramref name="Source"/> (1-based) of the original, turned <paramref name="Rotate"/> more degrees clockwise.</summary>
/// <param name="SplitAfter">A new file starts after this page.</param>
public sealed record PagePick(int Source, int Rotate = 0, bool SplitAfter = false);

/// <summary>
/// The page organizer's parameters (ED-5): { "pages": [{ "src", "rotate" }], "splitAfter": [positions] } — the new order, each
/// page's extra turn (0/90/180/270), and the 1-based positions in that order after which a new file starts.
/// </summary>
public static class PageOrder
{
    private const int MaxPages = 10_000;

    public static JsonObject ToParameters(IReadOnlyList<PagePick> pages)
    {
        var list = new JsonArray();
        var splits = new JsonArray();
        for (var i = 0; i < pages.Count; i++)
        {
            list.Add(new JsonObject { ["src"] = pages[i].Source, ["rotate"] = pages[i].Rotate });
            if (pages[i].SplitAfter && i < pages.Count - 1)
            {
                splits.Add(i + 1);
            }
        }

        return new JsonObject { ["pages"] = list, ["splitAfter"] = splits };
    }

    /// <summary>The new order cut into files.</summary>
    /// <exception cref="EngineException">No pages, or a page or split that isn't well-formed.</exception>
    public static IReadOnlyList<IReadOnlyList<PagePick>> Parse(JsonObject? parameters)
    {
        if (parameters?["pages"] is not JsonArray { Count: > 0 } list)
        {
            throw new EngineException("Keep at least one page.", "no pages");
        }

        if (list.Count > MaxPages)
        {
            throw Bad($"{list.Count} pages");
        }

        var pages = new List<PagePick>(list.Count);
        foreach (var node in list)
        {
            if (node is not JsonObject o || Integer(o["src"]) is not { } src || src < 1
                || (o["rotate"] is null ? 0 : Integer(o["rotate"])) is not { } turn || turn % 90 != 0)
            {
                throw Bad(node?.ToJsonString());
            }

            pages.Add(new PagePick(src, ((turn % 360) + 360) % 360));
        }

        var splits = new SortedSet<int>();
        foreach (var node in parameters["splitAfter"] as JsonArray ?? [])
        {
            if (Integer(node) is not { } at || at < 1 || at >= pages.Count)
            {
                throw Bad($"split {node?.ToJsonString()}");
            }

            splits.Add(at);
        }

        var files = new List<IReadOnlyList<PagePick>>();
        var start = 0;
        foreach (var end in splits.Append(pages.Count))
        {
            files.Add(pages[start..end]);
            start = end;
        }

        return files;
    }

    private static EngineException Bad(string? details) => new("Morph couldn't read the new page order.", $"bad page order: {details}");

    private static int? Integer(JsonNode? node) =>
        node is JsonValue && int.TryParse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}

/// <summary>
/// The page organizer's thumbnails: the preview job with { "sheet": first page } renders up to <see cref="Batch"/> pages from there
/// into one grid, <see cref="Columns"/> across, each fitted and centred in a <see cref="Cell"/>-pixel square (transparent around).
/// The preview's <see cref="PreviewImage.Pages"/> is the PDF's page count.
/// </summary>
public static class PageSheet
{
    public const int Columns = 10;
    public const int Cell = 120;
    public const int Batch = 100;
}
