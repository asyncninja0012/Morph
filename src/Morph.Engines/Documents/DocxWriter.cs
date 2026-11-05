using System.Globalization;
using System.Text;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Morph.Engines.Documents;

/// <summary>
/// <see cref="Block"/>s → DOCX with the Open XML SDK, using Word's built-in style names (Heading 1–6, List Paragraph,
/// Table Grid, Hyperlink) so the result restyles like any Word document.
/// </summary>
internal sealed class DocxWriter
{
    private const int Indent = 720; // twips: half an inch per list level

    private readonly MainDocumentPart _main;
    private readonly List<(int Id, bool Ordered, int Level)> _lists = [];

    private DocxWriter(MainDocumentPart main) => _main = main;

    public static void Write(IReadOnlyList<Block> blocks, string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var writer = new DocxWriter(main);
        var body = new W.Body();
        foreach (var block in blocks)
        {
            body.Append(writer.Block(block, 0));
        }

        main.Document = new W.Document(body);
        Feed(main.AddNewPart<StyleDefinitionsPart>(), Styles);
        if (writer._lists.Count > 0)
        {
            Feed(main.AddNewPart<NumberingDefinitionsPart>(), writer.Numbering());
        }
    }

    private IEnumerable<OpenXmlElement> Block(Block block, int depth) => block switch
    {
        Heading h => [Para(Inlines(h.Runs), $"Heading{Math.Clamp(h.Level, 1, 6)}")],
        Paragraph p => [Para(Inlines(p.Runs), depth > 0 ? "ListParagraph" : null, indent: depth * Indent)],
        ListBlock l => List(l, depth),
        Table t => [TableElement(t)],
        CodeBlock c => c.Text.TrimEnd('\n').Split('\n').Select(line => Para(Inlines([new Run(line)]), "SourceCode", indent: depth * Indent)),
        _ => [],
    };

    // Each list gets its own numbering instance, so an ordered list starts again at 1.
    private IEnumerable<OpenXmlElement> List(ListBlock list, int depth)
    {
        var id = _lists.Count + 1;
        _lists.Add((id, list.Ordered, depth));
        foreach (var item in list.Items)
        {
            for (var i = 0; i < item.Count; i++)
            {
                if (i == 0 && item[0] is Paragraph or Heading)
                {
                    var runs = item[0] is Paragraph p ? p.Runs : ((Heading)item[0]).Runs;
                    var numbering = new W.NumberingProperties(new W.NumberingLevelReference { Val = depth }, new W.NumberingId { Val = id });
                    yield return Para(Inlines(runs), "ListParagraph", numbering: numbering);
                }
                else
                {
                    foreach (var element in Block(item[i], depth + 1))
                    {
                        yield return element;
                    }
                }
            }
        }
    }

    private static W.Paragraph Para(IEnumerable<OpenXmlElement> content, string? style, W.NumberingProperties? numbering = null, int indent = 0)
    {
        var props = new W.ParagraphProperties();
        if (style is not null)
        {
            props.Append(new W.ParagraphStyleId { Val = style });
        }

        if (numbering is not null)
        {
            props.Append(numbering);
        }

        if (indent > 0)
        {
            props.Append(new W.Indentation { Left = indent.ToString(CultureInfo.InvariantCulture) });
        }

        var paragraph = new W.Paragraph();
        if (props.HasChildren)
        {
            paragraph.Append(props);
        }

        paragraph.Append(content);
        return paragraph;
    }

    private W.Table TableElement(Table table)
    {
        var columns = table.Rows.Max(r => r.Count);
        var grid = new W.TableGrid();
        for (var i = 0; i < columns; i++)
        {
            grid.Append(new W.GridColumn { Width = (9000 / columns).ToString(CultureInfo.InvariantCulture) });
        }

        var result = new W.Table(
            new W.TableProperties(new W.TableStyle { Val = "TableGrid" }, new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct }),
            grid);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = new W.TableRow();
            if (r == 0)
            {
                row.Append(new W.TableRowProperties(new W.TableHeader()));
            }

            for (var c = 0; c < columns; c++)
            {
                var runs = c < table.Rows[r].Count ? table.Rows[r][c] : [];
                row.Append(new W.TableCell(Para(Inlines(r == 0 ? [.. runs.Select(x => x with { Bold = true })] : runs), null)));
            }

            result.Append(row);
        }

        return result;
    }

    private IEnumerable<OpenXmlElement> Inlines(IReadOnlyList<Run> runs)
    {
        foreach (var run in runs)
        {
            var props = new W.RunProperties();
            if (run.Code || run.Link is not null)
            {
                props.Append(new W.RunStyle { Val = run.Code ? "InlineCode" : "Hyperlink" });
            }

            if (run.Bold)
            {
                props.Append(new W.Bold());
            }

            if (run.Italic)
            {
                props.Append(new W.Italic());
            }

            var element = new W.Run(props);
            var lines = run.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    element.Append(new W.Break());
                }

                element.Append(new W.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            }

            yield return run.Link is { } link ? Hyperlink(link, element) : element;
        }
    }

    private OpenXmlElement Hyperlink(string link, W.Run run)
    {
        if (link.StartsWith('#'))
        {
            return new W.Hyperlink(run) { Anchor = link[1..] };
        }

        return Uri.TryCreate(link, UriKind.RelativeOrAbsolute, out var uri)
            ? new W.Hyperlink(run) { Id = _main.AddHyperlinkRelationship(uri, isExternal: true).Id }
            : run;
    }

    private string Numbering()
    {
        var xml = new StringBuilder("""<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""");
        string[] bullets = ["•", "◦", "▪"];
        string[] formats = ["decimal", "lowerLetter", "lowerRoman"];
        for (var ordered = 0; ordered < 2; ordered++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""<w:abstractNum w:abstractNumId="{ordered}"><w:multiLevelType w:val="hybridMultilevel"/>""");
            for (var l = 0; l < 9; l++)
            {
                var (format, text) = ordered == 1 ? (formats[l % 3], $"%{l + 1}.") : ("bullet", bullets[l % 3]);
                xml.Append(CultureInfo.InvariantCulture,
                    $"""<w:lvl w:ilvl="{l}"><w:start w:val="1"/><w:numFmt w:val="{format}"/><w:lvlText w:val="{text}"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="{(l + 1) * Indent}" w:hanging="360"/></w:pPr></w:lvl>""");
            }

            xml.Append("</w:abstractNum>");
        }

        foreach (var (id, ordered, level) in _lists)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""<w:num w:numId="{id}"><w:abstractNumId w:val="{(ordered ? 1 : 0)}"/>""")
                .Append(ordered ? $"""<w:lvlOverride w:ilvl="{level}"><w:startOverride w:val="1"/></w:lvlOverride>""" : "")
                .Append("</w:num>");
        }

        return xml.Append("</w:numbering>").ToString();
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        part.FeedData(stream);
    }

    private static readonly string Styles = """
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr></w:rPrDefault>
        <w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="259" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
        """
        + string.Concat(new[] { 32, 28, 26, 24, 22, 22 }.Select((size, i) => $"""
        <w:style w:type="paragraph" w:styleId="Heading{i + 1}"><w:name w:val="heading {i + 1}"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="{i}"/></w:pPr><w:rPr><w:b/><w:sz w:val="{size}"/><w:szCs w:val="{size}"/></w:rPr></w:style>
        """))
        + """
        <w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:after="40"/><w:contextualSpacing/></w:pPr></w:style>
        <w:style w:type="paragraph" w:styleId="SourceCode"><w:name w:val="Source Code"/><w:basedOn w:val="Normal"/><w:pPr><w:shd w:val="clear" w:color="auto" w:fill="F2F2F2"/><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:style>
        <w:style w:type="character" w:default="1" w:styleId="DefaultParagraphFont"><w:name w:val="Default Paragraph Font"/><w:uiPriority w:val="1"/><w:semiHidden/></w:style>
        <w:style w:type="character" w:styleId="InlineCode"><w:name w:val="Inline Code"/><w:basedOn w:val="DefaultParagraphFont"/><w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/></w:rPr></w:style>
        <w:style w:type="character" w:styleId="Hyperlink"><w:name w:val="Hyperlink"/><w:basedOn w:val="DefaultParagraphFont"/><w:rPr><w:color w:val="0563C1"/><w:u w:val="single"/></w:rPr></w:style>
        <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:semiHidden/><w:tblPr><w:tblInd w:w="0" w:type="dxa"/><w:tblCellMar><w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
        <w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:basedOn w:val="TableNormal"/><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:tblPr><w:tblBorders><w:top w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:left w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:bottom w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:right w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:insideH w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:insideV w:val="single" w:sz="4" w:space="0" w:color="auto"/></w:tblBorders></w:tblPr></w:style>
        </w:styles>
        """;
}
