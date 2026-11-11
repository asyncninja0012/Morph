using Morph.Core.Jobs;
using Morph.Engines.Pdf;

using PdfSharp.Pdf;

namespace Morph.Engines.Tests;

// Conversions of a real PDF run through the Worker in GoldenTests.
public sealed class PdfEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-pdf-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Damaged_pdf_fails_with_a_user_message_and_leaves_no_output()
    {
        var input = Path.Combine(_dir, "broken.pdf");
        await File.WriteAllTextAsync(input, "%PDF-1.4\nnot really a pdf", TestContext.Current.CancellationToken);
        var namer = new OutputNamer(_dir);

        var ex = await Assert.ThrowsAsync<EngineException>(() => new PdfEngine().RunAsync(
            new JobRequest(Guid.NewGuid(), "convert.png", "pdf", [input], _dir), namer, new Progress<JobProgress>(),
            TestContext.Current.CancellationToken));

        Assert.Contains("broken.pdf", ex.UserMessage, StringComparison.Ordinal);
        namer.AbortAll();
        Assert.Equal([input], Directory.GetFiles(_dir));
    }

    // A page turned 90° shows its unrotated top-left corner at the top right; a box drawn there in the editor must land on it.
    [Fact]
    public async Task Crop_follows_a_rotated_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = Path.Combine(_dir, "turned.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            (page.Width, page.Height) = (PdfSharp.Drawing.XUnit.FromPoint(200), PdfSharp.Drawing.XUnit.FromPoint(100));
            using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
            {
                gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.Red, 0, 0, 50, 50);
            }

            page.Rotate = 90;
            doc.Save(input);
        }

        var namer = new OutputNamer(_dir);
        await new PdfEngine().RunAsync(new JobRequest(Guid.NewGuid(), "crop", "pdf", [input], _dir, new() { ["x"] = 50, ["y"] = 0, ["w"] = 50, ["h"] = 50 }),
            namer, new Progress<JobProgress>(), ct);
        var cropped = Assert.Single(namer.CommitAll());

        await new PdfEngine().RunAsync(new JobRequest(Guid.NewGuid(), "convert.png", "pdf", [cropped], _dir), namer, new Progress<JobProgress>(), ct);
        using var image = new ImageMagick.MagickImage(Assert.Single(namer.CommitAll()));
        Assert.Equal((104u, 104u), (image.Width, image.Height));
        var centre = image.GetPixels().GetPixel(52, 52)!.ToColor()!;
        Assert.True(centre.R > 200 && centre.G < 60 && centre.B < 60, $"centre is {centre}, not red");
    }

    // ED-3: a redacted page's words are gone from the file, not covered: not in any object or stream (decoded), not in its
    // note annotation, not in the tag tree, not in a form field on that page. The untouched page keeps its text.
    [Fact]
    public async Task Redact_leaves_none_of_the_page_in_the_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = Path.Combine(_dir, "secret.pdf");
        using (var doc = new PdfDocument())
        {
            var font = new PdfDictionary(doc);
            (font.Elements["/Type"], font.Elements["/Subtype"], font.Elements["/BaseFont"]) = (new PdfName("/Font"), new PdfName("/Type1"), new PdfName("/Helvetica"));
            doc.Internals.AddObject(font);
            foreach (var word in new[] { "SECRET123", "PUBLIC456" })
            {
                var page = doc.AddPage();
                (page.Width, page.Height) = (PdfSharp.Drawing.XUnit.FromPoint(200), PdfSharp.Drawing.XUnit.FromPoint(100));
                var resources = new PdfDictionary(doc);
                resources.Elements["/Font"] = new PdfDictionary(doc) { Elements = { ["/F1"] = font.Reference! } };
                page.Elements["/Resources"] = resources;
                page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes($"BT /F1 12 Tf 10 50 Td ({word}) Tj ET"));
            }

            var note = new PdfDictionary(doc);
            (note.Elements["/Type"], note.Elements["/Subtype"]) = (new PdfName("/Annot"), new PdfName("/Text"));
            note.Elements["/Rect"] = new PdfRectangle(new PdfSharp.Drawing.XPoint(0, 0), new PdfSharp.Drawing.XPoint(10, 10));
            note.Elements["/Contents"] = new PdfString("SECRET123 note");
            doc.Internals.AddObject(note);
            var field = new PdfDictionary(doc);
            (field.Elements["/Type"], field.Elements["/Subtype"], field.Elements["/FT"]) = (new PdfName("/Annot"), new PdfName("/Widget"), new PdfName("/Tx"));
            field.Elements["/Rect"] = new PdfRectangle(new PdfSharp.Drawing.XPoint(0, 80), new PdfSharp.Drawing.XPoint(50, 95));
            (field.Elements["/T"], field.Elements["/V"]) = (new PdfString("name"), new PdfString("SECRET123 field"));
            doc.Internals.AddObject(field);
            doc.Pages[0].Elements["/Annots"] = new PdfArray(doc, note.Reference!, field.Reference!);
            doc.Internals.Catalog.Elements["/AcroForm"] = new PdfDictionary(doc) { Elements = { ["/Fields"] = new PdfArray(doc, field.Reference!) } };
            var tags = new PdfDictionary(doc);
            tags.Elements["/Type"] = new PdfName("/StructTreeRoot");
            tags.Elements["/ActualText"] = new PdfString("SECRET123 tag");
            doc.Internals.AddObject(tags);
            doc.Internals.Catalog.Elements["/StructTreeRoot"] = tags.Reference!;
            doc.Save(input);
        }

        var namer = new OutputNamer(_dir);
        await new PdfEngine().RunAsync(new JobRequest(Guid.NewGuid(), "redact", "pdf", [input], _dir, Redactions.ToParameters([new RedactBox(1, 5, 30, 100, 30)])),
            namer, new Progress<JobProgress>(), ct);
        var redacted = Assert.Single(namer.CommitAll());

        var everything = new System.Text.StringBuilder(System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(redacted, ct)));
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(redacted, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            Assert.Equal(2, doc.PageCount);
            Assert.Equal((200.0, 100.0), (doc.Pages[0].Width.Point, doc.Pages[0].Height.Point));
            foreach (var o in doc.Internals.GetAllObjects())
            {
                everything.Append(o.ToString());
                if (o is PdfDictionary { Stream: { } stream })
                {
                    everything.Append(System.Text.Encoding.Latin1.GetString(stream.UnfilteredValue));
                }
            }
        }

        Assert.DoesNotContain("SECRET123", everything.ToString());
        Assert.Contains("PUBLIC456", everything.ToString());
    }

    // Marks are placed on the page as shown: on a page turned 90°, shown (10,10)-(40,40) is near the unrotated bottom-left.
    [Fact]
    public async Task Annotate_follows_a_rotated_page()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = Path.Combine(_dir, "turned.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            (page.Width, page.Height) = (PdfSharp.Drawing.XUnit.FromPoint(200), PdfSharp.Drawing.XUnit.FromPoint(100));
            page.Rotate = 90;
            doc.Save(input);
        }

        var namer = new OutputNamer(_dir);
        var shapes = Annotations.ToParameters([new AnnotationShape("rect", [(10, 10), (40, 40)], "#2FB344", 4), new AnnotationShape("text", [(10, 100)], "#2FB344", 4, "Hi")]);
        await new PdfEngine().RunAsync(new JobRequest(Guid.NewGuid(), "annotate", "pdf", [input], _dir, shapes), namer, new Progress<JobProgress>(), ct);
        var annotated = Assert.Single(namer.CommitAll());

        await new PdfEngine().RunAsync(new JobRequest(Guid.NewGuid(), "convert.png", "pdf", [annotated], _dir), namer, new Progress<JobProgress>(), ct);
        using var image = new ImageMagick.MagickImage(Assert.Single(namer.CommitAll()));
        Assert.Equal((208u, 417u), (image.Width, image.Height));
        var pixels = image.GetPixels();
        var edge = pixels.GetPixel(21, 52)!.ToColor()!; // shown (10, 25): the box's left edge
        Assert.True(edge.G > 140 && edge.R < 110, $"left edge is {edge}, not green");
        var inside = pixels.GetPixel(52, 52)!.ToColor()!;
        Assert.True(inside.R > 200 && inside.G > 200, $"inside is {inside}, not white");
        var text = 0;
        for (var x = 21; x < 80; x++)
        {
            for (var y = 208; y < 270; y++)
            {
                var c = pixels.GetPixel(x, y)!.ToColor()!;
                text += c.G > 140 && c.R < 110 ? 1 : 0;
            }
        }

        Assert.True(text > 10, "the text isn't upright where it was put");
    }

    [Fact]
    public async Task Merging_a_password_protected_pdf_says_so_and_leaves_no_output()
    {
        var open = Path.Combine(_dir, "open.pdf");
        var locked = Path.Combine(_dir, "locked.pdf");
        foreach (var path in new[] { open, locked })
        {
            using var doc = new PdfDocument();
            doc.AddPage();
            if (path == locked)
            {
                doc.SecuritySettings.UserPassword = "secret";
            }

            doc.Save(path);
        }

        var namer = new OutputNamer(_dir);
        var ex = await Assert.ThrowsAsync<EngineException>(() => new PdfEngine().RunAsync(
            new JobRequest(Guid.NewGuid(), "merge", "pdf", [open, locked], _dir), namer, new Progress<JobProgress>(),
            TestContext.Current.CancellationToken));

        Assert.Equal("locked.pdf is password-protected.", ex.UserMessage);
        namer.AbortAll();
        Assert.Equal([locked, open], Directory.GetFiles(_dir).Order());
    }
}
