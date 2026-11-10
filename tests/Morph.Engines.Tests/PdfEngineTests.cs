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
