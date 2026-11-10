using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Morph.Engines.Documents;

/// <summary>
/// HTML → <see cref="Block"/>s. A forgiving tag scanner, not a browser parser: it reads headings, paragraphs, lists, tables,
/// pre/code, bold/italic, links and images (data: URIs and files next to the page, <see cref="Pictures.FromSource"/>), keeps the text of everything else, and drops scripts and styles.
/// </summary>
internal sealed partial class HtmlReader
{
    private static readonly HashSet<string> Skipped = ["script", "style", "title", "template", "noscript", "svg", "math"];

    private static readonly HashSet<string> Breaks =
    [
        "p", "div", "section", "article", "header", "footer", "main", "nav", "aside", "blockquote", "figure", "figcaption",
        "dl", "dt", "dd", "address", "hr", "body", "form", "fieldset", "details", "summary", "caption",
    ];

    private readonly List<Block> _root = [];
    private readonly List<Run> _runs = [];
    private readonly Stack<(bool Ordered, List<List<Block>> Items, List<Block> Target)> _lists = new();
    private readonly Stack<(List<List<List<Run>>> Rows, List<Block> Target)> _tables = new();
    private readonly Stack<string?> _links = new();
    private readonly string? _folder;
    private List<Run>? _cell;
    private int _heading, _bold, _italic, _code;
    private bool _pre;

    private HtmlReader(string? folder) => _folder = folder;

    /// <param name="folder">The page's folder, for images given as relative paths; null reads only data: images.</param>
    public static List<Block> Read(string html, string? folder = null)
    {
        var reader = new HtmlReader(folder);
        reader.Scan(html);
        return reader._root;
    }

    [GeneratedRegex("""<!--.*?-->|<![^>]*>|<\?[^>]*>|<(/?)([a-zA-Z][a-zA-Z0-9]*)((?:[^>"']|"[^"]*"|'[^']*')*)>""", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex("""(?:^|\s)([a-zA-Z-]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))""")]
    private static partial Regex Attribute();

    private static string? Attr(string attributes, string name) => Attribute().Matches(attributes)
        .Where(m => m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
        .Select(m => WebUtility.HtmlDecode(m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value))
        .FirstOrDefault();

    // width="300" or width="300px"; percentages and other units fall back to the picture's own size.
    private static double Pixels(string attributes, string name) =>
        double.TryParse(Attr(attributes, name)?.Trim().Replace("px", "", StringComparison.OrdinalIgnoreCase), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    private void Scan(string html)
    {
        var at = 0;
        for (var m = Tag().Match(html); m.Success; m = Tag().Match(html, at))
        {
            Text(html[at..m.Index]);
            at = m.Index + m.Length;
            if (!m.Groups[2].Success)
            {
                continue; // comment, doctype, processing instruction
            }

            var name = m.Groups[2].Value.ToLowerInvariant();
            if (m.Groups[1].Length > 0)
            {
                Close(name);
            }
            else if (Skipped.Contains(name) && !m.Groups[3].Value.EndsWith('/'))
            {
                var end = html.IndexOf("</" + name, at, StringComparison.OrdinalIgnoreCase);
                at = end < 0 ? html.Length : end;
            }
            else
            {
                Open(name, m.Groups[3].Value);
            }
        }

        Text(html[at..]);
        Flush();
        while (_tables.Count > 0)
        {
            Close("table");
        }

        while (_lists.Count > 0)
        {
            Close("ul");
        }
    }

    private void Text(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        text = WebUtility.HtmlDecode(_pre ? text : Space().Replace(text, " "));
        _runs.Add(new Run(text, _bold > 0, _italic > 0, _code > 0, _links.Count > 0 ? _links.Peek() : null));
    }

    private void Open(string name, string attributes)
    {
        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                Flush();
                _heading = name[1] - '0';
                break;
            case "br":
                _runs.Add(new Run("\n"));
                break;
            case "pre":
                Flush();
                _pre = true;
                break;
            case "b" or "strong":
                _bold++;
                break;
            case "i" or "em":
                _italic++;
                break;
            case "code" or "kbd" or "samp" or "tt":
                _code++;
                break;
            case "a":
                _links.Push(Attr(attributes, "href"));
                break;
            case "img" when _cell is null: // shortcut: an image inside a table cell is dropped (cells hold text runs only)
                if (Attr(attributes, "src") is { } src
                    && Pictures.FromSource(src, _folder, Attr(attributes, "alt") ?? "", Pixels(attributes, "width"), Pixels(attributes, "height")) is { } image)
                {
                    Flush();
                    Target().Add(image);
                }

                break;
            case "ul" or "ol":
                Flush();
                _lists.Push((name == "ol", [], Target()));
                break;
            case "li":
                Flush();
                if (_lists.Count > 0)
                {
                    _lists.Peek().Items.Add([]);
                }

                break;
            case "table":
                Flush();
                _cell = null;
                _tables.Push(([], Target()));
                break;
            case "tr" when _tables.Count > 0:
                Flush();
                _cell = null;
                _tables.Peek().Rows.Add([]);
                break;
            case "td" or "th" when _tables.Count > 0:
                Flush();
                if (_tables.Peek().Rows.Count == 0)
                {
                    _tables.Peek().Rows.Add([]);
                }

                _cell = [];
                _tables.Peek().Rows[^1].Add(_cell);
                break;
            default:
                if (Breaks.Contains(name))
                {
                    Flush();
                }

                break;
        }
    }

    private void Close(string name)
    {
        switch (name)
        {
            case "b" or "strong":
                _bold = Math.Max(0, _bold - 1);
                break;
            case "i" or "em":
                _italic = Math.Max(0, _italic - 1);
                break;
            case "code" or "kbd" or "samp" or "tt":
                _code = Math.Max(0, _code - 1);
                break;
            case "a" when _links.Count > 0:
                _links.Pop();
                break;
            case "pre":
                Flush();
                _pre = false;
                break;
            case "ul" or "ol" when _lists.Count > 0:
                Flush();
                var list = _lists.Pop();
                var items = list.Items.Where(i => i.Count > 0).ToList();
                if (items.Count > 0)
                {
                    list.Target.Add(new ListBlock(list.Ordered, items));
                }

                break;
            case "td" or "th":
                Flush();
                _cell = null;
                break;
            case "table" when _tables.Count > 0:
                Flush();
                _cell = null;
                var table = _tables.Pop();
                if (table.Rows.Any(r => r.Count > 0))
                {
                    table.Target.Add(new Table([.. table.Rows.Where(r => r.Count > 0)]));
                }

                break;
            default:
                if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" || Breaks.Contains(name))
                {
                    Flush();
                }

                break;
        }
    }

    // Where a finished block goes: the open list item, else the list's own container, else the document.
    private List<Block> Target() =>
        _lists.Count == 0 ? _root : _lists.Peek().Items.Count > 0 ? _lists.Peek().Items[^1] : _lists.Peek().Target;

    private void Flush()
    {
        var runs = Runs.Normalize(_runs);
        _runs.Clear();
        var heading = _heading;
        _heading = 0;
        if (_pre)
        {
            var code = Runs.Plain(runs).TrimStart('\r', '\n').TrimEnd();
            if (code.Length > 0)
            {
                Target().Add(new CodeBlock(code));
            }

            return;
        }

        if (runs.Count > 0)
        {
            runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
            runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
            runs = Runs.Normalize(runs);
        }

        if (runs.Count == 0 || string.IsNullOrWhiteSpace(Runs.Plain(runs)))
        {
            return;
        }

        if (_cell is not null)
        {
            _cell.AddRange(_cell.Count > 0 ? [new Run(" "), .. runs] : runs);
        }
        else
        {
            Target().Add(heading > 0 ? new Heading(heading, runs) : new Paragraph(runs));
        }
    }
}
