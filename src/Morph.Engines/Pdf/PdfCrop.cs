using Morph.Core.Jobs;

using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Morph.Engines.Pdf;

/// <summary>
/// Crop (ED-1) for PDFs: every page gets the editor's box (points, top-left origin, page as shown, from page 1) as its CropBox,
/// clamped to that page. Nothing is removed from the file: the cut-away parts are hidden, as in Acrobat's crop.
/// </summary>
internal static class PdfCrop
{
    public static void Run(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            using var document = PdfCompressor.Open(input);
            var changed = false;
            foreach (var page in document.Pages)
            {
                // shortcut: a CropBox inherited from the page tree isn't read (rare); the MediaBox is used instead.
                var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
                var rotate = ((page.Rotate % 360) + 360) % 360;
                var (shownWidth, shownHeight) = rotate % 180 == 90 ? (box.Height, box.Width) : (box.Width, box.Height);
                var (x, y, w, h) = Presets.Crop(job.Parameters, shownWidth, shownHeight, name);
                if (w >= shownWidth - 0.5 && h >= shownHeight - 0.5)
                {
                    continue;
                }

                var (x1, y1, x2, y2) = ToPageBox((x, y, w, h), (box.X1, box.Y1, box.Width, box.Height), rotate);
                page.CropBox = new PdfRectangle(new XPoint(x1, y1), new XPoint(x2, y2));
                changed = true;
            }

            if (!changed)
            {
                skipped = $"{name} is already that size.";
                continue;
            }

            document.Save(namer.Reserve(input, ".pdf", "-cropped").Path);
            made++;
            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    /// <summary>
    /// A box on the page as shown (top-left origin, after /Rotate turns it clockwise) → PDF user space (bottom-left origin)
    /// inside <paramref name="page"/> (its CropBox or MediaBox: lower-left corner and unrotated size).
    /// </summary>
    internal static (double X1, double Y1, double X2, double Y2) ToPageBox((double X, double Y, double W, double H) shown,
        (double X, double Y, double W, double H) page, int rotate)
    {
        var (dx, dy, dw, dh) = shown;
        // The same box on the unrotated page, still top-left origin.
        var (ux, uy, uw, uh) = rotate switch
        {
            90 => (dy, page.H - dx - dw, dh, dw),
            180 => (page.W - dx - dw, page.H - dy - dh, dw, dh),
            270 => (page.W - dy - dh, dx, dh, dw),
            _ => (dx, dy, dw, dh),
        };
        var left = page.X + ux;
        var bottom = page.Y + page.H - uy - uh;
        return (left, bottom, left + uw, bottom + uh);
    }
}
