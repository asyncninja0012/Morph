using System.Globalization;
using System.Text;

using ImageMagick;

using Morph.Core.Formats;
using Morph.Core.Jobs;

using PdfSharp.Pdf;

namespace Morph.Engines.Pdf;

/// <summary>
/// PDF → JPG/PNG (one image per page at 150 DPI) and PDF → TXT (CV-2) with PDFium. Tool ids: convert.jpg/png/txt.
/// A multi-page PDF gives report-1.png, report-2.png, ... next to the original.
/// Images → PDF (convert.pdf) and merge PDFs and images (merge) with PDFsharp, see <see cref="PdfBuilder"/>.
/// </summary>
public sealed class PdfEngine : IEngine
{
    private const int MaxPages = 2_000; // PLAN §5 limit
    private const double Dpi = 150;
    private const float MaxEdge = 10_000; // px; a page may claim to be 200 inches wide

    public string Id => "pdf";

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (job.ToolId is "convert.pdf" or "merge")
        {
            await BuildAsync(job, namer, progress, ct).ConfigureAwait(false);
            return;
        }

        if (job.ToolId == "compress")
        {
            Compress(job, namer, progress, ct);
            return;
        }

        var target = job.ToolId switch
        {
            "convert.jpg" => MagickFormat.Jpeg,
            "convert.png" => MagickFormat.Png,
            "convert.txt" => MagickFormat.Txt,
            _ => throw new EngineException("This conversion isn't available yet.", $"pdf: unsupported tool {job.ToolId}"),
        };
        var quality = job.Parameters?["quality"]?.GetValue<int>();

        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            if ((await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id != "pdf")
            {
                throw new EngineException($"{Path.GetFileName(input)} isn't a PDF.");
            }

            var done = i;
            lock (Pdfium.Sync)
            {
                Convert(input, target, quality, namer, f => progress.Report(new JobProgress((done + f) / job.Inputs.Count, null)), ct);
            }
        }
    }

    // A file already small enough is skipped; the job fails only when every file was.
    private static void Compress(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var pending = namer.Reserve(job.Inputs[i], ".pdf", "-compressed");
            if (PdfCompressor.Compress(job.Inputs[i], pending.Path, Presets.Compress(job.Parameters), Presets.TargetBytes(job.Parameters), ct) is { } reason)
            {
                namer.Discard(pending);
                skipped = reason;
            }
            else
            {
                made++;
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    // convert.pdf: one PDF per image. merge: one PDF of every input, named after the first (report-merged.pdf).
    private static async Task BuildAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var merge = job.ToolId == "merge";
        var document = new PdfDocument();
        try
        {
            for (var i = 0; i < job.Inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await PdfBuilder.AddAsync(document, job.Inputs[i], ct).ConfigureAwait(false);
                if (!merge)
                {
                    document.Save(namer.Reserve(job.Inputs[i], ".pdf").Path);
                    document.Dispose();
                    document = new PdfDocument();
                }

                progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
            }

            if (merge)
            {
                document.Save(namer.Reserve(job.Inputs[0], ".pdf", "-merged").Path);
            }
        }
        finally
        {
            document.Dispose();
        }
    }

    private static void Convert(string input, MagickFormat target, int? quality, OutputNamer namer, Action<double> progress, CancellationToken ct)
    {
        var name = Path.GetFileName(input);
        var doc = Pdfium.FPDF_LoadDocument(input, null);
        if (doc == 0)
        {
            var error = Pdfium.FPDF_GetLastError();
            throw error == Pdfium.ErrPassword
                ? new EngineException($"{name} is password-protected.", "PDFium: password required")
                : new EngineException($"{name} is damaged or isn't a PDF Morph can read.", $"PDFium error {error}");
        }

        try
        {
            var pages = Pdfium.FPDF_GetPageCount(doc);
            if (pages <= 0)
            {
                throw new EngineException($"{name} is damaged or has no pages.", $"page count {pages}");
            }

            if (pages > MaxPages)
            {
                throw new EngineException($"{name} has {pages:N0} pages; Morph handles up to {MaxPages:N0}.");
            }

            var text = target == MagickFormat.Txt ? new StringBuilder() : null;
            var digits = pages.ToString(CultureInfo.InvariantCulture).Length;
            for (var p = 0; p < pages; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = Pdfium.FPDF_LoadPage(doc, p);
                if (page == 0)
                {
                    throw new EngineException($"Page {p + 1} of {name} is damaged.", $"PDFium error {Pdfium.FPDF_GetLastError()}");
                }

                try
                {
                    if (text is not null)
                    {
                        AppendText(page, text.Append(p > 0 ? "\r\n\r\n" : ""));
                    }
                    else
                    {
                        var suffix = pages > 1 ? "-" + (p + 1).ToString("D" + digits, CultureInfo.InvariantCulture) : "";
                        var extension = target == MagickFormat.Jpeg ? ".jpg" : ".png";
                        Render(page, target, quality, namer.Reserve(input, extension, suffix).Path);
                    }
                }
                finally
                {
                    Pdfium.FPDF_ClosePage(page);
                }

                progress((p + 1.0) / pages);
            }

            if (text is not null)
            {
                if (string.IsNullOrWhiteSpace(text.ToString()))
                {
                    throw new EngineException($"{name} has no text to extract. It may be a scanned document.");
                }

                File.WriteAllText(namer.Reserve(input, ".txt").Path, text.ToString()); // UTF-8, no BOM
            }
        }
        finally
        {
            Pdfium.FPDF_CloseDocument(doc);
        }
    }

    private static unsafe void Render(nint page, MagickFormat format, int? quality, string path)
    {
        var (widthPt, heightPt) = (Pdfium.FPDF_GetPageWidthF(page), Pdfium.FPDF_GetPageHeightF(page));
        var scale = Math.Min(Dpi / 72, MaxEdge / Math.Max(1, Math.Max(widthPt, heightPt)));
        var width = Math.Max(1, (int)Math.Round(widthPt * scale));
        var height = Math.Max(1, (int)Math.Round(heightPt * scale));

        var bitmap = Pdfium.FPDFBitmap_Create(width, height, 0); // BGRx, 4 bytes per pixel so no row padding
        if (bitmap == 0)
        {
            throw new EngineException("A page is too large to turn into an image.", $"FPDFBitmap_Create {width}x{height} failed");
        }

        try
        {
            Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Pdfium.RenderAnnotations);
            if (Pdfium.FPDFBitmap_GetStride(bitmap) != width * 4)
            {
                throw new InvalidOperationException("Unexpected PDFium bitmap stride.");
            }

            using var image = new MagickImage();
            image.ReadPixels(new ReadOnlySpan<byte>((void*)Pdfium.FPDFBitmap_GetBuffer(bitmap), width * height * 4),
                new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
            image.Alpha(AlphaOption.Off); // the x byte of BGRx isn't alpha
            image.Density = new Density(Dpi);
            if (quality is { } q && format == MagickFormat.Jpeg)
            {
                image.Quality = (uint)Math.Clamp(q, 1, 100);
            }

            image.Write(path, format);
        }
        finally
        {
            Pdfium.FPDFBitmap_Destroy(bitmap);
        }
    }

    private static unsafe void AppendText(nint page, StringBuilder text)
    {
        var textPage = Pdfium.FPDFText_LoadPage(page);
        if (textPage == 0)
        {
            return;
        }

        try
        {
            var count = Pdfium.FPDFText_CountChars(textPage);
            if (count <= 0)
            {
                return;
            }

            var buffer = new char[count + 1];
            int written;
            fixed (char* b = buffer)
            {
                written = Pdfium.FPDFText_GetText(textPage, 0, count, b);
            }

            text.Append(buffer, 0, Math.Max(0, written - 1)); // drop the NUL
        }
        finally
        {
            Pdfium.FPDFText_ClosePage(textPage);
        }
    }
}
