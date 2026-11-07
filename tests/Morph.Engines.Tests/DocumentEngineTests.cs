using System.IO.Compression;

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

using Morph.Core.Jobs;
using Morph.Engines.Documents;

namespace Morph.Engines.Tests;

// Each format pair runs on the sample documents through the Worker in GoldenTests; these cover the edges.
public sealed class DocumentEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-doc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Docx_output_is_valid_open_xml()
    {
        var output = await ConvertAsync("all.md", """
            # Title

            Text with **bold *both***, a line\
            break and [a link](https://example.com/?a=1&b=2).

            1. One
               - Nested bullet

                 Second paragraph
            2. Two

            | A | B | C |
            | --- | --- | --- |
            | 1 |

            ```
            code
            ```
            """, "docx");

        using var document = WordprocessingDocument.Open(output, isEditable: false);
        var errors = new OpenXmlValidator().Validate(document, TestContext.Current.CancellationToken).Select(e => $"{e.Path?.XPath}: {e.Description}");
        Assert.True(!errors.Any(), string.Join(" | ", errors));
    }

    [Fact]
    public void Html_drops_scripts_and_styles_and_decodes_entities()
    {
        var blocks = HtmlReader.Read("<style>p{}</style><p>Fish &amp; chips&nbsp;&lt;3<script>alert('x')</script></p><!-- note -->");

        Assert.Equal("Fish & chips\u00a0<3", Runs.Plain(blocks));
    }

    [Fact]
    public void Links_other_than_web_mail_or_relative_are_dropped()
    {
        var runs = ((Paragraph)Assert.Single(HtmlReader.Read(
            """<p><a href="javascript:alert(1)">a</a> <a href="file:///C:/x">b</a> <a href="https://x.test/">c</a> <a href="notes.html#top">d</a></p>"""))).Runs;

        Assert.Equal("a b c d", Runs.Plain(runs));
        Assert.Equal(["https://x.test/", "notes.html#top"], runs.Select(r => r.Link).OfType<string>());
    }

    [Fact]
    public void Markdown_escapes_text_that_looks_like_markup()
    {
        string[] lines = ["# not a heading", "1. not a list", "*not italic* and_snake_case", "a | pipe and a [bracket]"];
        var markdown = MarkdownWriter.Write([.. lines.Select(l => new Paragraph([new Run(l)]))]);

        var blocks = MarkdownReader.Read(markdown);

        Assert.Equal(lines, blocks.Select(b => Runs.Plain(((Paragraph)b).Runs)));
    }

    [Fact]
    public async Task Empty_document_fails_with_a_user_message_and_leaves_no_output()
    {
        var input = Path.Combine(_dir, "empty.md");
        await File.WriteAllTextAsync(input, "\n\n<!-- nothing -->\n", TestContext.Current.CancellationToken);
        var namer = new OutputNamer(_dir);

        var ex = await Assert.ThrowsAsync<EngineException>(() => new DocumentEngine().RunAsync(
            new JobRequest(Guid.NewGuid(), "convert.docx", "document", [input], _dir), namer, new Progress<JobProgress>(),
            TestContext.Current.CancellationToken));

        Assert.Equal("empty.md has no text to convert.", ex.UserMessage);
        namer.AbortAll();
        Assert.Equal([input], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Damaged_docx_fails_with_a_user_message()
    {
        var input = Path.Combine(_dir, "broken.docx");
        using (var zip = ZipFile.Open(input, ZipArchiveMode.Create))
        {
            await using var writer = new StreamWriter(await zip.CreateEntry("word/document.xml").OpenAsync(TestContext.Current.CancellationToken));
            await writer.WriteAsync("<w:document");
        }

        var ex = await Assert.ThrowsAsync<EngineException>(() => new DocumentEngine().RunAsync(
            new JobRequest(Guid.NewGuid(), "convert.md", "document", [input], _dir), new OutputNamer(_dir), new Progress<JobProgress>(),
            TestContext.Current.CancellationToken));

        Assert.Equal("broken.docx is damaged or isn't a Word document Morph can read.", ex.UserMessage);
    }

    [Fact]
    public async Task Pdf_to_docx_rebuilds_headings_lists_and_formatting()
    {
        // Through Morph's own PDF output (WebView2), so the PDF has real fonts, sizes and link areas.
        var pdf = await ConvertAsync("round.md", """
            # Big title

            Plain text with **bold**, *italic* and a [link](https://example.com/).

            ## Smaller heading

            - First item
            - Second item
              - Nested item

            1. One
            2. Two

            A closing paragraph long enough to wrap onto a second line when it is laid out on an A4 page with normal margins, so the reader has to join the lines back.
            """, "pdf");

        var blocks = await DocumentEngine.ReadAsync(pdf, TestContext.Current.CancellationToken);
        Assert.Equal("""
            H1 Big title
            P Plain text with [b:bold], [i:italic] and a [link:https://example.com/].
            H2 Smaller heading
            UL[First item; Second item UL[Nested item]]
            OL[One; Two]
            P A closing paragraph long enough to wrap onto a second line when it is laid out on an A4 page with normal margins, so the reader has to join the lines back.
            """.ReplaceLineEndings("\n"), string.Join("\n", blocks.Select(Dump)));
    }

    private static string Dump(Block block) => block switch
    {
        Heading h => $"H{h.Level} {Dump(h.Runs)}",
        Paragraph p => $"P {Dump(p.Runs)}",
        ListBlock l => (l.Ordered ? "OL[" : "UL[") + string.Join("; ", l.Items.Select(i => string.Join(" ", i.Select(b => b is Paragraph p ? Dump(p.Runs) : Dump(b))))) + "]",
        _ => block.GetType().Name,
    };

    // "[b:bold]", "[i:italic]", "[c:code]", "[text:url]" for a link.
    private static string Dump(IReadOnlyList<Run> runs) => string.Concat(runs.Select(r =>
        r.Link is { } link ? $"[{r.Text}:{link}]" : r.Bold ? $"[b:{r.Text}]" : r.Italic ? $"[i:{r.Text}]" : r.Code ? $"[c:{r.Text}]" : r.Text));

    [Fact]
    public async Task Pdf_without_text_fails_with_a_user_message()
    {
        var input = Path.Combine(_dir, "scan.pdf");
        var png = Path.Combine(_dir, "scan.png");
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 50, 50))
        {
            await image.WriteAsync(png, ImageMagick.MagickFormat.Png, TestContext.Current.CancellationToken);
        }

        var namer = new OutputNamer(_dir);
        await new Pdf.PdfEngine().RunAsync(new JobRequest(Guid.NewGuid(), "convert.pdf", "pdf", [png], _dir), namer, new Progress<JobProgress>(),
            TestContext.Current.CancellationToken);
        File.Move(Assert.Single(namer.CommitAll()), input);

        var ex = await Assert.ThrowsAsync<EngineException>(() => new DocumentEngine().RunAsync(
            new JobRequest(Guid.NewGuid(), "convert.docx", "document", [input], _dir), new OutputNamer(_dir), new Progress<JobProgress>(),
            TestContext.Current.CancellationToken));

        Assert.Equal("scan.pdf has no text to convert.", ex.UserMessage);
    }

    private async Task<string> ConvertAsync(string name, string content, string to)
    {
        var input = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(input, content, TestContext.Current.CancellationToken);
        var namer = new OutputNamer(_dir);
        await new DocumentEngine().RunAsync(new JobRequest(Guid.NewGuid(), $"convert.{to}", "document", [input], _dir), namer,
            new Progress<JobProgress>(), TestContext.Current.CancellationToken);
        return Assert.Single(namer.CommitAll());
    }
}
