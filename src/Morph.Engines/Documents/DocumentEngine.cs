using System.Text;

using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Documents;

/// <summary>
/// DOCX, Markdown, HTML and TXT among each other and to PDF, pictures included (task 2.12), and PDF to DOCX as a beta (CV-2, CV-5, tasks 2.3-2.5). Every input is read into the <see cref="Block"/>
/// tree (Doc.cs), then written out; PDF is the HTML page printed by WebView2, or Word's own export for DOCX when Word is installed.
/// Tool ids: convert.docx/md/html/txt/pdf.
/// </summary>
public sealed class DocumentEngine : IEngine
{
    private const long MaxBytes = 50L * 1024 * 1024; // a text document this large is almost certainly not one
    private const long MaxPdfBytes = 500L * 1024 * 1024; // PDFs are big from their images; PdfPig holds the whole file in memory

    public string Id => "document";

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var target = job.ToolId switch
        {
            "convert.docx" or "convert.md" or "convert.html" or "convert.txt" or "convert.pdf" => job.ToolId["convert.".Length..],
            _ => throw new EngineException("This conversion isn't available yet.", $"document: unsupported tool {job.ToolId}"),
        };

        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            var pictures = 0;
            var blocks = await ReadAsync(input, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(Runs.Plain(blocks)) && (target == "txt" || !HasPicture(blocks)))
            {
                throw new EngineException($"{Path.GetFileName(input)} has no text to convert.");
            }

            var output = namer.Reserve(input, "." + target).Path;
            if (target == "docx")
            {
                DocxWriter.Write(blocks, output);
            }
            else if (target == "pdf")
            {
                if (FileFormat.FromExtension(Path.GetExtension(input)) == FileFormat.Docx && WordPdfExporter.IsAvailable)
                {
                    await WordPdfExporter.ExportAsync(input, output, ct).ConfigureAwait(false);
                }
                else
                {
                    await HtmlPdfPrinter.PrintAsync(HtmlWriter.Write(blocks, Path.GetFileNameWithoutExtension(input)), output, ct).ConfigureAwait(false);
                }
            }
            else
            {
                var text = target switch
                {
                    "md" => MarkdownWriter.Write(blocks, SavePicture),
                    "html" => HtmlWriter.Write(blocks, Path.GetFileNameWithoutExtension(input)),
                    _ => PlainTextWriter.Write(blocks),
                };
                await File.WriteAllTextAsync(output, text, new UTF8Encoding(false), ct).ConfigureAwait(false); // UTF-8, no BOM
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));

            // Markdown can't hold pictures: each goes next to the output as report-1.png, report-2.jpg, ...
            string SavePicture(Image image)
            {
                var path = namer.Reserve(input, image.Format == "jpeg" ? ".jpg" : "." + image.Format, $"-{++pictures}").Path;
                File.WriteAllBytes(path, image.Bytes);
                return Path.GetFileName(path);
            }
        }
    }

    private static bool HasPicture(IEnumerable<Block> blocks) => blocks.Any(b => b is Image || (b is ListBlock l && l.Items.Any(HasPicture)));

    internal static async Task<List<Block>> ReadAsync(string input, CancellationToken ct)
    {
        var name = Path.GetFileName(input);
        var format = (await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id;
        var max = format == "pdf" ? MaxPdfBytes : MaxBytes;
        if (new FileInfo(input).Length > max)
        {
            throw new EngineException($"{name} is over {max / 1024 / 1024} MB; Morph converts documents up to that size.");
        }

        if (format == "pdf")
        {
            return PdfReader.Read(input);
        }

        if (format == "docx")
        {
            try
            {
                return DocxReader.Read(input);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not EngineException)
            {
                throw new EngineException($"{name} is damaged or isn't a Word document Morph can read.", ex.ToString());
            }
        }

        // File.ReadAllText honours a BOM (UTF-8/16/32) and otherwise reads UTF-8.
        var text = (await File.ReadAllTextAsync(input, ct).ConfigureAwait(false)).ReplaceLineEndings("\n");
        return format switch
        {
            "md" => MarkdownReader.Read(text, Path.GetDirectoryName(Path.GetFullPath(input))),
            "html" => HtmlReader.Read(text, Path.GetDirectoryName(Path.GetFullPath(input))),
            "txt" => PlainTextReader.Read(text),
            _ => throw new EngineException($"{name} isn't a document Morph can convert.", $"document: input format {format}"),
        };
    }
}
