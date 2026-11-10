using Morph.Core.Jobs;

using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Morph.Engines.Pdf;

/// <summary>
/// Annotate (ED-2) for PDFs: the editor's marks are drawn into each page's content as vectors (text stays text, in Segoe UI).
/// Coordinates are points on the page as shown (after /Rotate), top-left origin, like crop. Pixelate isn't offered: covering
/// a PDF doesn't remove what's under it, which is Redact's job.
/// </summary>
internal static class PdfAnnotate
{
    static PdfAnnotate() => GlobalFontSettings.FontResolver = new SegoeUi();

    public static void Run(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var shapes = Annotations.Parse(job.Parameters);
        if (shapes.Any(s => s.Type == "pixelate"))
        {
            throw new EngineException("Pixelate works on pictures only. To hide part of a PDF, use Redact.", "pixelate on pdf");
        }

        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            using var document = PdfCompressor.Open(input);
            var drawn = false;
            foreach (var onPage in shapes.GroupBy(s => s.Page))
            {
                if (onPage.Key > document.PageCount)
                {
                    continue; // drawn on a longer PDF of the same drop ("Same for all")
                }

                var page = document.Pages[onPage.Key - 1];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                gfx.MultiplyTransform(ShownToPage(page));
                foreach (var shape in onPage)
                {
                    Draw(gfx, shape);
                }

                drawn = true;
            }

            if (!drawn)
            {
                skipped = $"{name} doesn't have the pages you drew on.";
                continue;
            }

            document.Save(namer.Reserve(input, ".pdf", "-annotated").Path);
            made++;
            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    /// <summary>
    /// The page as shown (top-left of its visible box, after /Rotate turns it clockwise) → XGraphics page space (top-left of the
    /// MediaBox, unrotated, y down). The rotation is the same as <see cref="PdfCrop.ToPageBox"/>'s, per point.
    /// </summary>
    internal static XMatrix ShownToPage(PdfPage page)
    {
        // shortcut: a CropBox inherited from the page tree isn't read (rare); the MediaBox is used instead.
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        var media = page.MediaBox;
        var (w, h) = (box.Width, box.Height);
        var (ox, oy) = (box.X1 - media.X1, media.Y2 - box.Y2);
        // x' = x·M11 + y·M21 + OffsetX, y' = x·M12 + y·M22 + OffsetY
        return (((page.Rotate % 360) + 360) % 360) switch
        {
            90 => new XMatrix(0, -1, 1, 0, ox, oy + h),
            180 => new XMatrix(-1, 0, 0, -1, ox + w, oy + h),
            270 => new XMatrix(0, 1, -1, 0, ox + w, oy),
            _ => new XMatrix(1, 0, 0, 1, ox, oy),
        };
    }

    private static void Draw(XGraphics gfx, AnnotationShape shape)
    {
        var color = Color(shape.Color);
        var pen = new XPen(color, shape.Stroke) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
        var (x, y, w, h) = Annotations.Box(shape);
        switch (shape.Type)
        {
            case "rect":
                gfx.DrawRectangle(pen, x, y, w, h);
                break;
            case "ellipse":
                gfx.DrawEllipse(pen, x, y, w, h);
                break;
            case "arrow":
                var (end, head) = Annotations.Arrow(shape.Points[0], shape.Points[1], shape.Stroke);
                gfx.DrawLine(pen, shape.Points[0].X, shape.Points[0].Y, end.X, end.Y);
                gfx.DrawPolygon(new XSolidBrush(color), [.. head.Select(p => new XPoint(p.X, p.Y))], XFillMode.Winding);
                break;
            case "highlight":
                var ink = XColor.FromArgb((int)(255 * Annotations.HighlightOpacity), color.R, color.G, color.B);
                gfx.DrawLines(new XPen(ink, Annotations.HighlightWidth(shape.Stroke)) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round },
                    [.. shape.Points.Select(p => new XPoint(p.X, p.Y))]);
                break;
            case "pen":
                gfx.DrawLines(pen, [.. shape.Points.Select(p => new XPoint(p.X, p.Y))]);
                break;
            case "text":
                gfx.DrawString(shape.Text!, new XFont("Segoe UI", Annotations.FontSize(shape.Stroke)), new XSolidBrush(color), x, y, XStringFormats.TopLeft);
                break;
        }
    }

    // PDFsharp's own Windows font lookup doesn't find Segoe UI in this build; text is only ever Segoe UI regular.
    private sealed class SegoeUi : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new("segoeui");

        public byte[] GetFont(string faceName) => File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"));
    }

    private static XColor Color(string hex) => XColor.FromArgb(Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));
}
