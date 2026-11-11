using ImageMagick;

using Morph.Core.Jobs;

using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Morph.Engines.Pdf;

/// <summary>
/// Redact (ED-3) for PDFs, as PLAN D4: each page with a box is rendered by PDFium at 300 DPI with the boxes burned in solid
/// black, and that picture replaces the page; text, images, links and form fields that were on it are gone from the file, not
/// covered, and so are the tag tree and (on those pages) form fields, which can hold the same words. Other pages are untouched;
/// links and bookmarks that pointed at a redacted page now go nowhere. Boxes are points on the page as shown (after /Rotate), top-left origin.
/// </summary>
internal static class PdfRedact
{
    private const double Dpi = 300;

    public static void Run(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var boxes = Redactions.Parse(job.Parameters);
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            var pictures = Render(input, name, boxes, ct);
            try
            {
                if (pictures.Count == 0)
                {
                    skipped = $"{name} doesn't have the pages you drew on.";
                    continue;
                }

                using var document = PdfCompressor.Open(input);
                var removed = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
                foreach (var (index, picture) in pictures)
                {
                    Replace(document, index, picture, removed);
                }

                Scrub(document, removed);
                document.Save(namer.Reserve(input, ".pdf", "-redacted").Path);
                made++;
            }
            finally
            {
                foreach (var picture in pictures.Values)
                {
                    picture.Dispose();
                }
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    // Page index → the page as shown, boxes burned in, as JPEG bytes in a stream.
    private static Dictionary<int, MemoryStream> Render(string input, string name, IReadOnlyList<RedactBox> boxes, CancellationToken ct)
    {
        var pictures = new Dictionary<int, MemoryStream>();
        lock (Pdfium.Sync)
        {
            var (doc, pages) = PdfEngine.Open(input);
            try
            {
                foreach (var onPage in boxes.GroupBy(b => b.Page).Where(g => g.Key <= pages))
                {
                    ct.ThrowIfCancellationRequested();
                    using var image = PdfEngine.RenderPage(doc, onPage.Key - 1, name, Dpi);
                    var widthPt = PageWidth(doc, onPage.Key - 1);
                    var k = image.Width / widthPt; // pixels per point (less than 300/72 on a huge page)
                    foreach (var b in onPage)
                    {
                        var left = (int)Math.Clamp(Math.Floor(b.X * k), 0, image.Width);
                        var top = (int)Math.Clamp(Math.Floor(b.Y * k), 0, image.Height);
                        var right = (int)Math.Clamp(Math.Ceiling((b.X + b.W) * k), 0, image.Width);
                        var bottom = (int)Math.Clamp(Math.Ceiling((b.Y + b.H) * k), 0, image.Height);
                        if (right > left && bottom > top)
                        {
                            using var black = new MagickImage(MagickColors.Black, (uint)(right - left), (uint)(bottom - top));
                            image.Composite(black, left, top, CompositeOperator.Copy);
                        }
                    }

                    image.Quality = 90;
                    var bytes = new MemoryStream();
                    image.Write(bytes, MagickFormat.Jpeg);
                    bytes.Position = 0;
                    pictures[onPage.Key - 1] = bytes;
                }
            }
            catch
            {
                foreach (var picture in pictures.Values)
                {
                    picture.Dispose();
                }

                throw;
            }
            finally
            {
                Pdfium.FPDF_CloseDocument(doc);
            }
        }

        return pictures;
    }

    private static double PageWidth(nint doc, int index)
    {
        var page = Pdfium.FPDF_LoadPage(doc, index);
        try
        {
            return Pdfium.FPDF_GetPageWidthF(page); // as shown: PDFium applies /Rotate
        }
        finally
        {
            Pdfium.FPDF_ClosePage(page);
        }
    }

    // A fresh page in the old one's place holds only the picture, at the shown size. The old page is emptied, not only
    // unlinked: a bookmark or link may still point at it, and whatever is reachable gets saved.
    private static void Replace(PdfDocument document, int index, MemoryStream picture, HashSet<PdfItem> removed)
    {
        var old = document.Pages[index];
        var box = old.Elements.ContainsKey("/CropBox") ? old.CropBox : old.MediaBox;
        var turned = ((old.Rotate % 360) + 360) % 360 % 180 == 90;
        var (w, h) = turned ? (box.Height, box.Width) : (box.Width, box.Height);
        if (old.Elements.GetArray("/Annots") is { } annots)
        {
            foreach (var item in annots.Elements)
            {
                removed.Add((item as PdfReference)?.Value ?? item);
            }
        }

        var fresh = document.Pages.Insert(index);
        (fresh.Width, fresh.Height) = (XUnit.FromPoint(w), XUnit.FromPoint(h));
        using (var gfx = XGraphics.FromPdfPage(fresh))
        using (var image = XImage.FromStream(picture))
        {
            gfx.DrawImage(image, 0, 0, w, h);
        }

        document.Pages.Remove(old);
        old.Elements.Clear();
        old.Elements["/Type"] = new PdfName("/Page");
    }

    // What else can still hold the old page's words: the tag tree (alt text, actual text) and form field values.
    private static void Scrub(PdfDocument document, HashSet<PdfItem> removed)
    {
        document.Internals.Catalog.Elements.Remove("/StructTreeRoot");
        document.Internals.Catalog.Elements.Remove("/MarkInfo");
        if (document.Internals.Catalog.Elements.GetDictionary("/AcroForm") is { } form && form.Elements.GetArray("/Fields") is { } fields)
        {
            Prune(fields, removed);
        }
    }

    // Drops fields whose widgets were on a redacted page; a parent left without kids goes too. True: the array is now empty.
    private static bool Prune(PdfArray fields, HashSet<PdfItem> removed)
    {
        for (var i = fields.Elements.Count - 1; i >= 0; i--)
        {
            var field = (fields.Elements[i] as PdfReference)?.Value as PdfDictionary ?? fields.Elements[i] as PdfDictionary;
            if (field is null)
            {
                continue;
            }

            var gone = removed.Contains(field) || (field.Elements.GetArray("/Kids") is { } kids && Prune(kids, removed));
            if (gone)
            {
                fields.Elements.RemoveAt(i);
            }
        }

        return fields.Elements.Count == 0;
    }
}
