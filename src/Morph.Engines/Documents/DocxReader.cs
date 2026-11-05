using System.Text;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Morph.Engines.Documents;

/// <summary>
/// DOCX → <see cref="Block"/>s with the Open XML SDK. Headings come from "Heading N"/"Title" styles, lists from numbering,
/// code from styles or fonts named like code. Deleted tracked changes are skipped; inserted ones are kept.
/// </summary>
internal sealed class DocxReader
{
    private static readonly string[] CodeFonts = ["consolas", "courier new", "cascadia code", "cascadia mono", "lucida console"];

    private readonly MainDocumentPart _main;
    private readonly Dictionary<string, W.Style> _styles;
    private readonly List<Block> _blocks = [];
    private readonly List<(int Level, bool Ordered, Block Content)> _items = [];
    private readonly StringBuilder _code = new();

    private DocxReader(MainDocumentPart main)
    {
        _main = main;
        _styles = (main.StyleDefinitionsPart?.Styles?.Elements<W.Style>() ?? [])
            .Where(s => s.StyleId?.Value is not null)
            .DistinctBy(s => s.StyleId!.Value!)
            .ToDictionary(s => s.StyleId!.Value!);
    }

    public static List<Block> Read(string path)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var main = document.MainDocumentPart;
        var reader = new DocxReader(main!);
        reader.Blocks(main?.Document?.Body?.ChildElements ?? []);
        reader.FlushPending();
        return reader._blocks;
    }

    private void Blocks(IEnumerable<OpenXmlElement> elements)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case W.Paragraph p:
                    Paragraph(p);
                    break;
                case W.Table t:
                    FlushPending();
                    var rows = t.Elements<W.TableRow>()
                        .Select(r => (IReadOnlyList<IReadOnlyList<Run>>)[.. r.Elements<W.TableCell>().Select(Cell)])
                        .Where(r => r.Count > 0)
                        .ToList();
                    if (rows.Count > 0)
                    {
                        _blocks.Add(new Table(rows));
                    }

                    break;
                case W.SdtBlock or W.CustomXmlBlock or W.SdtContentBlock:
                    Blocks(element.ChildElements);
                    break;
            }
        }
    }

    private List<Run> Cell(W.TableCell cell)
    {
        var runs = new List<Run>();
        foreach (var p in cell.Descendants<W.Paragraph>().Select(Inlines).Where(r => r.Count > 0))
        {
            runs.AddRange(runs.Count > 0 ? [new Run(" "), .. p] : p);
        }

        return Runs.Normalize(runs);
    }

    private void Paragraph(W.Paragraph p)
    {
        var style = Style(p.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        var name = Name(style);
        var numbering = p.ParagraphProperties?.NumberingProperties ?? style?.StyleParagraphProperties?.NumberingProperties;
        var numId = numbering?.NumberingId?.Val?.Value ?? 0;
        var runs = Inlines(p);

        if (numId > 0 && runs.Count > 0)
        {
            FlushCode();
            var level = numbering?.NumberingLevelReference?.Val?.Value ?? 0;
            _items.Add((level, Ordered(numId, level), new Paragraph(runs)));
            return;
        }

        if (name.Contains("code", StringComparison.Ordinal) || name.Contains("preformatted", StringComparison.Ordinal))
        {
            FlushList();
            _code.Append(_code.Length > 0 ? "\n" : "").Append(Runs.Plain(runs));
            return;
        }

        if (runs.Count == 0)
        {
            return; // an empty paragraph is spacing, not content
        }

        FlushPending();
        var heading = name == "title" ? 1 : name.StartsWith("heading", StringComparison.Ordinal) && int.TryParse(name["heading".Length..], out var n) && n is >= 1 and <= 6 ? n : 0;
        _blocks.Add(heading > 0 ? new Heading(heading, runs) : new Paragraph(runs));
    }

    private List<Run> Inlines(W.Paragraph p)
    {
        var runs = new List<Run>();
        Inline(p, null, runs);
        var normalized = Runs.Normalize(runs);
        return string.IsNullOrWhiteSpace(Runs.Plain(normalized)) ? [] : normalized;
    }

    private void Inline(OpenXmlElement parent, string? link, List<Run> runs)
    {
        foreach (var element in parent.ChildElements)
        {
            switch (element)
            {
                case W.Run r:
                    var style = RunStyle(r.RunProperties) with { Link = link };
                    foreach (var child in r.ChildElements)
                    {
                        var text = child switch
                        {
                            W.Text t => t.Text,
                            W.TabChar => "\t",
                            W.Break b when b.Type?.Value != W.BreakValues.Page => "\n",
                            W.CarriageReturn => "\n",
                            W.NoBreakHyphen => "-",
                            _ => null,
                        };
                        if (text is not null)
                        {
                            runs.Add(style with { Text = text });
                        }
                    }

                    break;
                case W.Hyperlink h:
                    var target = h.Id?.Value is { } id ? _main.HyperlinkRelationships.FirstOrDefault(x => x.Id == id)?.Uri.OriginalString
                        : h.Anchor?.Value is { } anchor ? "#" + anchor : null;
                    Inline(h, target ?? link, runs);
                    break;
                case W.DeletedRun or W.MoveFromRun or W.ParagraphProperties or W.FieldCode:
                    break;
                default: // inserted runs, content controls, smart tags, simple fields
                    Inline(element, link, runs);
                    break;
            }
        }
    }

    private Run RunStyle(W.RunProperties? props)
    {
        var name = Name(Style(props?.RunStyle?.Val?.Value));
        var font = props?.RunFonts?.Ascii?.Value?.ToLowerInvariant();
        return new Run("",
            Bold: On(props?.Bold) || name == "strong",
            Italic: On(props?.Italic) || name == "emphasis",
            Code: name.Contains("code", StringComparison.Ordinal) || (font is not null && CodeFonts.Contains(font)));
    }

    private static bool On(W.OnOffType? value) => value is not null && (value.Val is null || value.Val.Value);

    private W.Style? Style(string? id) => id is not null ? _styles.GetValueOrDefault(id) : null;

    // "heading 1" and "Heading1" both become "heading1".
    private static string Name(W.Style? style) =>
        (style?.StyleName?.Val?.Value ?? style?.StyleId?.Value ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private bool Ordered(int numId, int level)
    {
        var numbering = _main.NumberingDefinitionsPart?.Numbering;
        var instance = numbering?.Elements<W.NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
        var abstractNum = numbering?.Elements<W.AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == instance?.AbstractNumId?.Val?.Value);
        var format = abstractNum?.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == level)?.NumberingFormat?.Val?.Value;
        return format is { } f && f != W.NumberFormatValues.Bullet && f != W.NumberFormatValues.None;
    }

    private void FlushPending()
    {
        FlushList();
        FlushCode();
    }

    private void FlushCode()
    {
        if (_code.Length > 0)
        {
            _blocks.Add(new CodeBlock(_code.ToString()));
            _code.Clear();
        }
    }

    private void FlushList()
    {
        var i = 0;
        while (i < _items.Count)
        {
            _blocks.Add(BuildList(_items, ref i, _items[i].Level));
        }

        _items.Clear();
    }

    // Flat (level, item) paragraphs → nested lists: a deeper item belongs to the item before it.
    internal static ListBlock BuildList(List<(int Level, bool Ordered, Block Content)> items, ref int i, int level)
    {
        var ordered = items[i].Ordered;
        var result = new List<List<Block>>();
        while (i < items.Count && items[i].Level >= level)
        {
            if (items[i].Level > level && result.Count > 0)
            {
                result[^1].Add(BuildList(items, ref i, items[i].Level));
            }
            else if (items[i].Ordered != ordered)
            {
                break;
            }
            else
            {
                result.Add([items[i].Content]);
                i++;
            }
        }

        return new ListBlock(ordered, result);
    }
}
