using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;

using ImageMagick;

using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Engines.Imaging;
using Morph.Engines.Pdf;

using Windows.Graphics.Imaging;

using WinOcr = Windows.Media.Ocr.OcrEngine;

namespace Morph.Engines.Ocr;

/// <summary>
/// OCR to TXT (IT-3) with Windows.Media.Ocr in the user's display languages; built into Windows, nothing to download.
/// Images are read upright by Magick; PDF pages are rendered by PDFium at 300 DPI. One photo.txt per file, pages split by a blank line.
/// </summary>
public sealed class OcrEngine : IEngine
{
    private const double PdfDpi = 300;

    public string Id => "ocr";

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (job.ToolId != "ocr")
        {
            throw new EngineException("This tool isn't available yet.", $"ocr: unsupported tool {job.ToolId}");
        }

        var ocr = WinOcr.TryCreateFromUserProfileLanguages()
            ?? throw new EngineException("Windows has no text recognition for your languages. Add a language in Settings > Time & language.");
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            var done = i;
            void Report(double fraction) => progress.Report(new JobProgress((done + fraction) / job.Inputs.Count, null));

            var text = new StringBuilder();
            if ((await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id == "pdf")
            {
                nint doc;
                int pages;
                lock (Pdfium.Sync)
                {
                    (doc, pages) = PdfEngine.Open(input);
                }

                try
                {
                    for (var p = 0; p < pages; p++)
                    {
                        ct.ThrowIfCancellationRequested();
                        MagickImage page;
                        lock (Pdfium.Sync)
                        {
                            page = PdfEngine.RenderPage(doc, p, name, PdfDpi);
                        }

                        using (page)
                        {
                            text.Append(p > 0 ? "\r\n\r\n" : "").Append(await RecognizeAsync(ocr, page, ct).ConfigureAwait(false));
                        }

                        Report((p + 1.0) / pages);
                    }
                }
                finally
                {
                    lock (Pdfium.Sync)
                    {
                        Pdfium.FPDF_CloseDocument(doc);
                    }
                }
            }
            else
            {
                using var image = await ImagingEngine.ReadAsync(input, ct).ConfigureAwait(false);
                text.Append(await RecognizeAsync(ocr, image, ct).ConfigureAwait(false));
                Report(1);
            }

            if (string.IsNullOrWhiteSpace(text.ToString()))
            {
                skipped = $"Morph found no text in {name}.";
                continue;
            }

            await File.WriteAllTextAsync(namer.Reserve(input, ".txt").Path, text.ToString(), ct).ConfigureAwait(false); // UTF-8, no BOM
            made++;
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    // Windows OCR takes BGRA pixels up to MaxImageDimension a side; transparency goes onto white, as text is usually dark.
    private static async Task<string> RecognizeAsync(WinOcr ocr, MagickImage image, CancellationToken ct)
    {
        var max = WinOcr.MaxImageDimension;
        if (image.Width > max || image.Height > max)
        {
            image.Resize(new MagickGeometry(max, max));
        }

        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaOption.Remove);
        using var pixels = image.GetPixelsUnsafe();
        var bgra = pixels.ToByteArray(PixelMapping.BGRA)!;
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, (int)image.Width, (int)image.Height, BitmapAlphaMode.Premultiplied);
        var result = await ocr.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        return string.Join("\r\n", result.Lines.Select(l => l.Text));
    }
}
