using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Morph.Engines.Documents;

/// <summary>Plain text: blocks separated by a blank line, "- " and "1. " list markers, tab-separated table rows, CRLF.</summary>
internal static class PlainTextWriter
{
    public static string Write(IReadOnlyList<Block> blocks) =>
        string.Join("\n\n", blocks.Select(b => Block(b, ""))).ReplaceLineEndings("\r\n") + "\r\n";

    private static string Block(Block block, string indent) => block switch
    {
        Heading h => Runs.Plain(h.Runs),
        Paragraph p => Runs.Plain(p.Runs),
        ListBlock l => string.Join("\n", l.Items.Select((item, i) => Item(item, indent, l.Ordered ? $"{i + 1}. " : "- "))),
        Table t => string.Join("\n", t.Rows.Select(r => string.Join("\t", r.Select(Runs.Plain)))),
        CodeBlock c => c.Text,
        _ => "",
    };

    private static string Item(IReadOnlyList<Block> item, string indent, string marker) =>
        indent + marker + string.Join("\n", item.Select((b, i) => i == 0 ? Block(b, indent + "   ") : b is ListBlock ? Block(b, indent + "   ") : indent + "   " + Block(b, indent + "   ")));
}

/// <summary>Markdown (CommonMark + GitHub pipe tables).</summary>
internal static partial class MarkdownWriter
{
    public static string Write(IReadOnlyList<Block> blocks) => string.Join("\n\n", blocks.Select(b => Block(b))) + "\n";

    [GeneratedRegex(@"^(\s*)(?:([#>+\-=])|(\d+)([.)]))")]
    private static partial Regex LineStart();

    private static string Block(Block block) => block switch
    {
        Heading h => new string('#', Math.Clamp(h.Level, 1, 6)) + " " + Inline(h.Runs).Replace("\\\n", " ", StringComparison.Ordinal),
        Paragraph p => Inline(p.Runs),
        ListBlock l => string.Join("\n", l.Items.Select((item, i) => Item(item, l.Ordered ? $"{i + 1}. " : "- "))),
        Table t => Table(t),
        CodeBlock c => Fence(c.Text) + "\n" + c.Text.TrimEnd('\n') + "\n" + Fence(c.Text),
        _ => "",
    };

    // Continuation lines and nested lists are indented to the item's text, so they stay inside the item.
    private static string Item(IReadOnlyList<Block> item, string marker)
    {
        var pad = new string(' ', marker.Length);
        var body = new StringBuilder();
        for (var i = 0; i < item.Count; i++)
        {
            body.Append(i == 0 ? "" : item[i] is ListBlock ? "\n" : "\n\n").Append(Block(item[i])); // a nested list stays tight
        }

        return marker + body.ToString().Replace("\n", "\n" + pad, StringComparison.Ordinal).Replace("\n" + pad + "\n", "\n\n", StringComparison.Ordinal);
    }

    private static string Table(Table table)
    {
        var columns = table.Rows.Max(r => r.Count);
        string Row(IReadOnlyList<IReadOnlyList<Run>> row) =>
            "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(i => i < row.Count ? Inline(row[i]).Replace("\\\n", "<br>", StringComparison.Ordinal) : "")) + " |";

        var lines = new List<string> { Row(table.Rows[0]), "|" + string.Concat(Enumerable.Repeat(" --- |", columns)) };
        lines.AddRange(table.Rows.Skip(1).Select(Row));
        return string.Join("\n", lines);
    }

    private static string Fence(string code)
    {
        var fence = "```";
        while (code.Contains(fence, StringComparison.Ordinal))
        {
            fence += "`";
        }

        return fence;
    }

    private static string Inline(IReadOnlyList<Run> runs)
    {
        var text = new StringBuilder();
        foreach (var run in runs)
        {
            // Markers can't sit next to spaces ("** bold**" isn't bold), so spaces stay outside them.
            var core = run.Text.Trim(' ');
            if (core.Length == 0)
            {
                text.Append(run.Text);
                continue;
            }

            var s = run.Code ? Code(core) : Escape(core);
            s = run.Italic ? $"*{s}*" : s;
            s = run.Bold ? $"**{s}**" : s;
            s = run.Link is { } link ? $"[{s}](<{link.Replace(">", "%3E", StringComparison.Ordinal)}>)" : s;
            text.Append(run.Text[..run.Text.IndexOf(core, StringComparison.Ordinal)]).Append(s).Append(' ', run.Text.Length - run.Text.TrimEnd(' ').Length);
        }

        // A line that starts like a heading, quote or list item would turn into one; escape it.
        var lines = text.ToString().Split('\n').Select(l => LineStart().Replace(l, m => m.Groups[1].Value + (m.Groups[2].Success ? "\\" + m.Groups[2].Value : m.Groups[3].Value + "\\" + m.Groups[4].Value), 1));
        return string.Join("\\\n", lines);
    }

    private static string Code(string text)
    {
        var ticks = "`";
        while (text.Contains(ticks, StringComparison.Ordinal))
        {
            ticks += "`";
        }

        return text.StartsWith('`') || text.EndsWith('`') ? $"{ticks} {text} {ticks}" : ticks + text + ticks;
    }

    private static string Escape(string text)
    {
        var s = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '&' or '~')
            {
                s.Append('\\');
            }

            s.Append(c);
        }

        return s.ToString();
    }
}

/// <summary>A standalone HTML page with a small readable style.</summary>
internal static class HtmlWriter
{
    private const string Style = "body{font-family:'Segoe UI',system-ui,sans-serif;line-height:1.5;max-width:46em;margin:2em auto;padding:0 1em}"
        + "table{border-collapse:collapse}th,td{border:1px solid #bbb;padding:.3em .6em;text-align:left}"
        + "pre{background:#f4f4f4;padding:.8em;overflow:auto}code{font-family:Consolas,monospace}";

    public static string Write(IReadOnlyList<Block> blocks, string title) =>
        $"<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
        + $"<title>{WebUtility.HtmlEncode(title)}</title>\n<style>{Style}</style>\n</head>\n<body>\n"
        + string.Join("\n", blocks.Select(Block)) + "\n</body>\n</html>\n";

    private static string Block(Block block) => block switch
    {
        Heading h => $"<h{Math.Clamp(h.Level, 1, 6)}>{Inline(h.Runs)}</h{Math.Clamp(h.Level, 1, 6)}>",
        Paragraph p => $"<p>{Inline(p.Runs)}</p>",
        ListBlock l => (l.Ordered ? "<ol>\n" : "<ul>\n")
            + string.Concat(l.Items.Select(i => "<li>" + (i is [Paragraph only] ? Inline(only.Runs) : string.Join("\n", i.Select(Block))) + "</li>\n"))
            + (l.Ordered ? "</ol>" : "</ul>"),
        Table t => "<table>\n"
            + string.Concat(t.Rows.Select((r, i) => "<tr>" + string.Concat(r.Select(c => i == 0 ? $"<th>{Inline(c)}</th>" : $"<td>{Inline(c)}</td>")) + "</tr>\n"))
            + "</table>",
        CodeBlock c => $"<pre><code>{WebUtility.HtmlEncode(c.Text)}</code></pre>",
        _ => "",
    };

    private static string Inline(IReadOnlyList<Run> runs) => string.Concat(runs.Select(run =>
    {
        var s = WebUtility.HtmlEncode(run.Text).Replace("\n", "<br>\n", StringComparison.Ordinal);
        s = run.Code ? $"<code>{s}</code>" : s;
        s = run.Italic ? $"<em>{s}</em>" : s;
        s = run.Bold ? $"<strong>{s}</strong>" : s;
        return run.Link is { } link ? $"<a href=\"{WebUtility.HtmlEncode(link)}\">{s}</a>" : s;
    }));
}
