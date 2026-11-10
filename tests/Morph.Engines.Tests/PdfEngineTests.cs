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
