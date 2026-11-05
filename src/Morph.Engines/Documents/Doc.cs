namespace Morph.Engines.Documents;

// The documents hub (PLAN §6): every reader builds this tree and every writer takes it, so N formats need N readers + N writers.
// shortcut: images, block quotes, page breaks and rules are dropped; add blocks here when a format needs them.

/// <summary>A span of text with one formatting. "\n" inside <see cref="Text"/> is a line break.</summary>
internal sealed record Run(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);

internal abstract record Block;

internal sealed record Heading(int Level, IReadOnlyList<Run> Runs) : Block;

internal sealed record Paragraph(IReadOnlyList<Run> Runs) : Block;

/// <summary>Each item holds blocks: its text, then any nested lists.</summary>
internal sealed record ListBlock(bool Ordered, IReadOnlyList<IReadOnlyList<Block>> Items) : Block;

/// <summary>Rows of cells; the first row is the header.</summary>
internal sealed record Table(IReadOnlyList<IReadOnlyList<IReadOnlyList<Run>>> Rows) : Block;

internal sealed record CodeBlock(string Text) : Block;

internal static class Runs
{
    /// <summary>Joins neighbours with the same formatting and drops empty runs.</summary>
    public static List<Run> Normalize(IEnumerable<Run> runs)
    {
        var result = new List<Run>();
        foreach (var run in runs.Where(r => r.Text.Length > 0).Select(r => r.Link is null || SafeLink(r.Link) ? r : r with { Link = null }))
        {
            if (result.Count > 0 && result[^1] with { Text = "" } == run with { Text = "" })
            {
                result[^1] = result[^1] with { Text = result[^1].Text + run.Text };
            }
            else
            {
                result.Add(run);
            }
        }

        return result;
    }

    // Links come from untrusted files and land in HTML/DOCX the user may click: web, mail and in-document links only.
    private static bool SafeLink(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.Scheme is "http" or "https" or "mailto" : !link.Contains(':', StringComparison.Ordinal);

    public static string Plain(IEnumerable<Run> runs) => string.Concat(runs.Select(r => r.Text));

    /// <summary>All text of a block tree, one block per line; used for TXT output and "is it empty" checks.</summary>
    public static string Plain(IEnumerable<Block> blocks) => string.Join("\n", blocks.SelectMany(Lines));

    private static IEnumerable<string> Lines(Block block) => block switch
    {
        Heading h => [Plain(h.Runs)],
        Paragraph p => [Plain(p.Runs)],
        ListBlock l => l.Items.SelectMany(i => i.SelectMany(Lines)),
        Table t => t.Rows.Select(r => string.Join("\t", r.Select(Plain))),
        CodeBlock c => [c.Text],
        _ => [],
    };
}
