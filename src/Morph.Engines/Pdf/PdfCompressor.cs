using ImageMagick;

using Morph.Core.Jobs;

using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Morph.Engines.Pdf;

/// <summary>
/// Compress for PDF (IT-1) with PDFsharp: photos inside the PDF are re-encoded as smaller JPEGs, content streams get
/// Flate. Text and vector drawings stay as they are, so a PDF of mostly text barely shrinks.
/// "Fit under X MB" tries Balanced, then Small, then Tiny until the file fits.
/// </summary>
// shortcut: images are capped by pixel count, not by the size they're drawn at on the page (that needs content-stream
// parsing); CMYK, 16-bit, indexed and masked images are left alone. Revisit if real PDFs barely shrink.
internal static class PdfCompressor
{
    private const int MaxPages = 2_000; // PLAN §5 limit
    private const long MaxImagePixels = 100_000_000;

    private sealed record Level(int LongEdge, uint Quality);

    private static readonly Level Small = new(1200, 55);
    private static readonly Level Balanced = new(2000, 70);
    private static readonly Level High = new(3000, 85);
    private static readonly Level Tiny = new(900, 40);

    /// <returns>Why the file was skipped (already small enough), or null when <paramref name="output"/> holds the result.</returns>
    public static string? Compress(string input, string output, Preset preset, long? limit, CancellationToken ct)
    {
        var name = Path.GetFileName(input);
        if (limit is { } bytes)
        {
            if (new FileInfo(input).Length <= bytes)
            {
                return $"{name} is already under {Presets.Megabytes(bytes)} MB.";
            }

            foreach (var level in new[] { Balanced, Small, Tiny })
            {
                Write(input, output, level, ct);
                if (new FileInfo(output).Length <= bytes)
                {
                    return null;
                }
            }

            return $"Morph couldn't get {name} under {Presets.Megabytes(bytes)} MB; most of it is text, fonts or drawings, which it keeps as they are.";
        }

        Write(input, output, preset switch { Preset.Small => Small, Preset.Balanced => Balanced, _ => High }, ct);
        return new FileInfo(output).Length < new FileInfo(input).Length ? null : $"{name} is already as small as Morph can make it at this setting.";
    }

    private static void Write(string input, string output, Level level, CancellationToken ct)
    {
        using var document = Open(input);
        var masks = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        var images = document.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Stream is not null && d.Elements.GetName("/Subtype") == "/Image")
            .ToList();
        foreach (var image in images)
        {
            if (image.Elements.GetReference("/SMask") is { } mask)
            {
                masks.Add(mask.Value); // a soft mask is transparency, not a photo
            }
        }

        foreach (var image in images.Where(i => !masks.Contains(i)))
        {
            ct.ThrowIfCancellationRequested();
            Shrink(image, level);
        }

        document.Options.CompressContentStreams = true;
        document.Options.NoCompression = false;
        document.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;
        document.Save(output);
    }

    internal static PdfDocument Open(string input, PdfDocumentOpenMode mode = PdfDocumentOpenMode.Modify)
    {
        var name = Path.GetFileName(input);
        var locked = false;
        PdfDocument? document;
        try
        {
            // An aborted password prompt makes Open return null.
            document = PdfReader.Open(input, mode, args =>
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
            document = null;
        }

        if (document is null)
        {
            throw new EngineException($"{name} is password-protected.", "PDFsharp: password required");
        }

        if (document.PageCount > MaxPages)
        {
            document.Dispose();
            throw new EngineException($"{name} has {document.PageCount:N0} pages; Morph handles up to {MaxPages:N0}.");
        }

        return document;
    }

    // Re-encodes one image XObject as a JPEG at the level, if that makes it smaller. Only 8-bit grey or RGB, as JPEG or Flate.
    private static void Shrink(PdfDictionary image, Level level)
    {
        var e = image.Elements;
        var (width, height) = (e.GetInteger("/Width"), e.GetInteger("/Height"));
        var space = ColourComponents(e.GetValue("/ColorSpace"));
        var filter = e.GetValue("/Filter") switch
        {
            PdfName n => n.Value,
            PdfArray { Elements.Count: 1 } a when a.Elements[0] is PdfName n => n.Value,
            _ => null,
        };
        if (width <= 0 || height <= 0 || (long)width * height > MaxImagePixels || space is not (1 or 3)
            || e.GetBoolean("/ImageMask") || e.ContainsKey("/Decode") || e.ContainsKey("/Mask")
            || (filter != "/DCTDecode" && (filter != "/FlateDecode" || e.GetInteger("/BitsPerComponent") != 8)))
        {
            return;
        }

        var original = image.Stream!.Value;
        try
        {
            using var picture = new MagickImage();
            if (filter == "/DCTDecode")
            {
                picture.Read(original, new MagickReadSettings { Format = MagickFormat.Jpeg });
            }
            else
            {
                var pixels = image.Stream.UnfilteredValue;
                if (pixels.Length != width * height * space)
                {
                    return; // a predictor or layout PDFsharp didn't undo
                }

                picture.ReadPixels(space == 3 ? pixels : Expand(pixels), new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
            }

            picture.ColorType = space == 1 ? ColorType.Grayscale : ColorType.TrueColor; // the JPEG must match /ColorSpace

            if (Presets.Fit(width, height, level.LongEdge) is var (w, h))
            {
                picture.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
            }

            picture.Quality = level.Quality;
            picture.Strip();
            var jpeg = picture.ToByteArray(MagickFormat.Jpeg);
            if (jpeg.Length >= original.Length)
            {
                return;
            }

            image.Stream.Value = jpeg;
            e.SetName("/Filter", "/DCTDecode");
            e.Remove("/DecodeParms");
            e.SetInteger("/Length", jpeg.Length);
            e.SetInteger("/Width", (int)picture.Width);
            e.SetInteger("/Height", (int)picture.Height);
            e.SetInteger("/BitsPerComponent", 8);
        }
        catch (MagickException)
        {
            // An image Magick can't decode stays as it was; the rest of the PDF still shrinks.
        }
    }

    // Grey → RGB bytes, for ReadPixels, which only takes colour mappings.
    private static byte[] Expand(byte[] grey)
    {
        var rgb = new byte[grey.Length * 3];
        for (var i = 0; i < grey.Length; i++)
        {
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = grey[i];
        }

        return rgb;
    }

    // 1 or 3 for DeviceGray/DeviceRGB/CalGray/CalRGB and ICC-based equivalents; 0 for anything else.
    private static int ColourComponents(PdfItem? space)
    {
        if (space is PdfReference reference)
        {
            space = reference.Value;
        }

        return space switch
        {
            PdfName { Value: "/DeviceGray" } => 1,
            PdfName { Value: "/DeviceRGB" } => 3,
            PdfArray { Elements.Count: 2 } a when a.Elements[0] is PdfName { Value: "/CalGray" } => 1,
            PdfArray { Elements.Count: 2 } a when a.Elements[0] is PdfName { Value: "/CalRGB" } => 3,
            PdfArray { Elements.Count: 2 } a when a.Elements[0] is PdfName { Value: "/ICCBased" } =>
                (a.Elements[1] is PdfReference { Value: PdfDictionary icc } ? icc.Elements.GetInteger("/N") : 0) is var n and (1 or 3) ? n : 0,
            _ => 0,
        };
    }
}
