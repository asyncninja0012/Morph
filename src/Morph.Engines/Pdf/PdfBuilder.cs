using ImageMagick;

using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Engines.Imaging;

using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Morph.Engines.Pdf;

/// <summary>Builds new PDFs with PDFsharp: images → PDF (CV-2) and merge (IT-3), in input order.</summary>
internal static class PdfBuilder
{
    private const int MaxPages = 2_000; // PLAN §5 limit
    private const double MaxEdge = 842; // pt: A4's long side, so a 12 MP photo makes a printable page, not a 42-inch one

    /// <summary>Appends <paramref name="input"/>: every page of a PDF, or one page per image.</summary>
    public static async Task AddAsync(PdfDocument output, string input, CancellationToken ct)
    {
        if ((await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id == "pdf")
        {
            AddPdf(output, input);
        }
        else
        {
            using var image = await ImagingEngine.ReadAsync(input, ct).ConfigureAwait(false);
            AddImage(output, image);
        }

        if (output.PageCount > MaxPages)
        {
            throw new EngineException($"That makes more than {MaxPages:N0} pages; Morph handles up to {MaxPages:N0}.");
        }
    }

    private static void AddPdf(PdfDocument output, string input)
    {
        var name = Path.GetFileName(input);
        var locked = false;
        PdfDocument? source;
        try
        {
            // An aborted password prompt makes Open return null.
            source = PdfReader.Open(input, PdfDocumentOpenMode.Import, args =>
            {
                locked = true;
                args.Abort = true;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !locked)
        {
            throw new EngineException($"{name} is damaged or isn't a PDF Morph can read.", ex.ToString());
        }
        catch when (locked)
        {
            source = null;
        }

        if (locked || source is null)
        {
            throw new EngineException($"{name} is password-protected.", "PDFsharp: password required");
        }

        using (source)
        {
            foreach (var page in source.Pages)
            {
                output.AddPage(page);
            }
        }
    }

    private static void AddImage(PdfDocument output, MagickImage image)
    {
        // Page size from the image's own DPI (scans say 300), else 96; then shrunk to fit MaxEdge.
        var density = image.Density.ChangeUnits(DensityUnit.PixelsPerInch);
        var dpi = density.X >= 72 ? density.X : 96;
        var pointsPerPixel = 72 / dpi;
        var scale = pointsPerPixel * Math.Min(1, MaxEdge / (Math.Max(image.Width, image.Height) * pointsPerPixel));
        var (width, height) = (image.Width * scale, image.Height * scale);

        // shortcut: JPEGs are re-encoded at q92 (EXIF rotation is already applied); pass the original bytes through if size matters.
        var jpeg = image.Format == MagickFormat.Jpeg;
        using var bytes = new MemoryStream();
        if (jpeg)
        {
            image.Quality = 92;
        }

        image.Write(bytes, jpeg ? MagickFormat.Jpeg : image.HasAlpha ? MagickFormat.Png32 : MagickFormat.Png24);
        bytes.Position = 0;

        var page = output.AddPage();
        page.Width = XUnit.FromPoint(width);
        page.Height = XUnit.FromPoint(height);
        using var gfx = XGraphics.FromPdfPage(page);
        using var picture = XImage.FromStream(bytes);
        gfx.DrawImage(picture, 0, 0, width, height);
    }
}
