using System.Text.RegularExpressions;

using Morph.Core.Jobs;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Geometry;

namespace Morph.Engines.Documents;

/// <summary>
/// Rebuilds a document from a PDF's text (PDF → DOCX beta, CV-5). PdfPig finds words, text blocks and reading order; then
/// bigger-than-body blocks become headings, lines that start with a bullet or number become list items, all-monospace blocks
/// become code, and bold, italic and link areas carry over to the words. Pictures and ruled tables (<see cref="PdfTables"/>) go
/// between the blocks at their height on the page.
/// </summary>
// shortcut: tables without drawn lines come out as paragraphs, and a scanned PDF comes out as page pictures without text (needs OCR, task 2.8).
internal static partial class PdfReader
{
    private const int MaxPages = 2000; // same limit as the PDF engine
    private const double MinPicture = 16; // points; smaller pictures are rules, bullets and specks, not content

    [GeneratedRegex(@"^(?:([•◦▪▫‣●○■□\-–—*])|(\d{1,3}|[a-zA-Z])[.)])$")]
    private static partial Regex ListMarker();

    public static List<Block> Read(string path)
    {
        var name = Path.GetFileName(path);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new EngineException($"{name} is password-protected; Morph can't read it yet.", ex.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EngineException($"{name} is damaged or isn't a PDF Morph can read.", ex.ToString());
        }

        using (pdf)
        {
            if (pdf.NumberOfPages > MaxPages)
            {
                throw new EngineException($"{name} has {pdf.NumberOfPages:N0} pages; Morph handles up to {MaxPages:N0}.");
            }

            var lines = new List<Line>();
            foreach (var page in pdf.GetPages())
            {
                lines.AddRange(PageLines(page));
            }

            return Build(lines);
        }
    }

    /// <param name="Bullet">Left edge of a bullet drawn as a shape (browsers and some editors do), if the line has one.</param>
    /// <param name="Element">A picture or table on a line of its own, which has no text and no block.</param>
    private sealed record Line(IReadOnlyList<Run> Runs, double Size, double Left, bool Mono, TextBlock? Block, double? Bullet, Block? Element = null);

    private static IEnumerable<Line> PageLines(Page page)
    {
        var links = page.GetHyperlinks();
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var tables = PdfTables.Find(page, words, WordRuns);

        // Pictures and tables top to bottom; each goes before the first text line (in reading order) below its top, splitting the block there.
        var elements = new Queue<(double Top, Block Element)>(page.GetImages()
            .Where(i => !i.IsImageMask && i.BoundingBox.Width >= MinPicture && i.BoundingBox.Height >= MinPicture)
            .Select(i => (i.BoundingBox.Top, Element: (Block?)Picture(i))).Where(p => p.Element is not null).Select(p => (p.Top, p.Element!))
            .Concat(tables.Select(t => (t.Top, (Block)t.Table)))
            .OrderByDescending(p => p.Top));
        if (words.Count == 0)
        {
            foreach (var (_, element) in elements)
            {
                yield return new Line([], 0, 0, false, null, null, element);
            }

            yield break;
        }

        var shapes = page.Paths.Where(p => !p.IsClipping && (p.IsFilled || p.IsStroked)).Select(p => p.GetBoundingRectangle())
            .OfType<PdfRectangle>().Where(r => r.Width is > 0 and < 10 && r.Height is > 0 and < 10).ToList();
        var blocks = UnsupervisedReadingOrderDetector.Instance.Get(DocstrumBoundingBoxes.Instance.GetBlocks(words));
        foreach (var block in blocks)
        {
            // A lone number near the top or bottom edge is a page number.
            var y = block.BoundingBox.Centroid.Y;
            if (block.TextLines.Count == 1 && block.Text.Trim().All(char.IsDigit) && (y < page.Height * 0.08 || y > page.Height * 0.92))
            {
                continue;
            }

            foreach (var line in block.TextLines)
            {
                while (elements.Count > 0 && elements.Peek().Top > line.BoundingBox.Top)
                {
                    yield return new Line([], 0, 0, false, null, null, elements.Dequeue().Element);
                }

                var runs = new List<Run>();
                foreach (var word in line.Words)
                {
                    if (runs.Count > 0)
                    {
                        runs.Add(runs[^1] with { Text = " " });
                    }

                    runs.AddRange(WordRuns(word));
                }

                // A small shape just left of the line, at its height, is its bullet.
                var size = line.Words.Average(w => w.Letters.Average(l => l.PointSize));
                var box = line.BoundingBox;
                var bullet = shapes.FirstOrDefault(r => r.Width < size * 0.6 && r.Right < box.Left && box.Left - r.Right < size * 2
                    && r.Centroid.Y > box.Bottom && r.Centroid.Y < box.Top);
                yield return new Line(runs, size, box.Left, line.Words.All(w => w.Letters.All(l => IsMono(l.FontName ?? ""))), block,
                    bullet.Width > 0 ? bullet.Left : null);
            }
        }

        foreach (var (_, element) in elements)
        {
            yield return new Line([], 0, 0, false, null, null, element);
        }

        // Formatting per letter: "bold," is a bold word and a plain comma. Each word stays its own runs, so a list
        // marker ("1.") is the first run of its line.
        List<Run> WordRuns(Word word) => Runs.Normalize(word.Letters.Select(letter =>
        {
            var font = letter.FontName ?? "";
            return new Run(letter.Value,
                Bold: letter.FontDetails.IsBold || Has(font, "Bold", "Black", "Heavy", "Semibold"),
                Italic: letter.FontDetails.IsItalic || Has(font, "Italic", "Oblique"),
                Code: IsMono(font),
                Link: links.FirstOrDefault(l => l.Bounds.Contains(letter.BoundingBox.Centroid, true))?.Uri);
        }));
    }

    // PdfPig decodes Flate and uncompressed pictures to PNG; JPEG and JPEG 2000 bytes go to Magick as they are.
    private static Image? Picture(IPdfImage image)
    {
        byte[] bytes;
        try
        {
            bytes = image.TryGetPng(out var png) ? png : image.RawMemory.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // a picture PdfPig can't decode is left out; the text still converts
        }

        return bytes.Length <= Pictures.MaxBytes ? Pictures.FromBytes(bytes, "", image.BoundingBox.Width * 96 / 72, image.BoundingBox.Height * 96 / 72) : null;
    }

    private static List<Block> Build(List<Line> lines)
    {
        var result = new List<Block>();
        if (lines.Count == 0)
        {
            return result;
        }

        // Body text is the size most characters have; distinctly bigger sizes are headings, the biggest is level 1.
        var textLines = lines.Where(l => l.Element is null).ToList();
        var body = textLines.GroupBy(l => Math.Round(l.Size, 1)).MaxBy(g => g.Sum(l => Runs.Plain(l.Runs).Length))?.Key ?? 0;
        var headingSizes = textLines.Select(l => Math.Round(l.Size, 1)).Where(s => s >= body * 1.15).Distinct().OrderDescending().ToList();

        var items = new List<(int Level, bool Ordered, Block Content)>();
        var itemLefts = new List<double>();
        foreach (var blockLines in Blocks(lines))
        {
            if (blockLines[0].Element is { } element)
            {
                Flush();
                result.Add(element);
                continue;
            }

            var size = Math.Round(blockLines.Max(l => l.Size), 1);
            if (blockLines.All(l => l.Mono))
            {
                Flush();
                result.Add(new CodeBlock(string.Join("\n", blockLines.Select(l => Runs.Plain(l.Runs)))));
                continue;
            }

            if (headingSizes.Contains(size) && blockLines.Count <= 3)
            {
                Flush();
                result.Add(new Heading(Math.Min(headingSizes.IndexOf(size) + 1, 6), Clean(Join(blockLines), keepBold: false)));
                continue;
            }

            // Within a block, a line that starts with a marker begins an item; other lines continue the current item or paragraph.
            var paragraph = new List<Line>();
            foreach (var line in blockLines)
            {
                var marker = ListMarker().Match(line.Runs[0].Text);
                if (line.Bullet is not null || (marker.Success && line.Runs.Count > 2))
                {
                    EndParagraph();
                    var left = line.Bullet ?? line.Left; // where the marker starts
                    var level = itemLefts.FindIndex(l => Math.Abs(l - left) < 6);
                    if (level < 0)
                    {
                        itemLefts.RemoveAll(l => l > left);
                        itemLefts.Add(left);
                        level = itemLefts.Count - 1;
                    }

                    var text = line.Bullet is null ? line.Runs.Skip(2).ToList() : [.. line.Runs];
                    items.Add((level, line.Bullet is null && marker.Groups[2].Success, new Paragraph(Clean(text, keepBold: true))));
                }
                else if (items.Count > 0 && paragraph.Count == 0 && line.Left > itemLefts[items[^1].Level])
                {
                    // An indented line right after an item wraps that item.
                    var last = items[^1];
                    items[^1] = last with { Content = new Paragraph(Runs.Normalize([.. ((Paragraph)last.Content).Runs, new Run(" "), .. line.Runs])) };
                }
                else
                {
                    paragraph.Add(line);
                }
            }

            EndParagraph();

            void EndParagraph()
            {
                if (paragraph.Count > 0)
                {
                    Flush();
                    result.Add(new Paragraph(Clean(Join(paragraph), keepBold: true)));
                    paragraph.Clear();
                }
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (items.Count > 0)
            {
                // A deeper first item would leave no parent; lift everything to start at level 0.
                var shift = items.Min(i => i.Level);
                var levelled = items.Select(i => i with { Level = i.Level - shift }).ToList();
                for (var i = 0; i < levelled.Count;)
                {
                    result.Add(DocxReader.BuildList(levelled, ref i, levelled[i].Level));
                }

                items.Clear();
                itemLefts.Clear();
            }
        }
    }

    // A block's lines are next to each other; a picture or table is a block of its own. Docstrum can lump a short heading in with the
    // text under it, so a jump in text size starts a new block too.
    private static IEnumerable<List<Line>> Blocks(List<Line> lines)
    {
        var block = new List<Line>();
        foreach (var line in lines)
        {
            if (block.Count > 0 && (line.Element is not null || block[0].Element is not null || line.Block != block[0].Block
                || Math.Abs(line.Size - block[^1].Size) > Math.Min(line.Size, block[^1].Size) * 0.15))
            {
                yield return block;
                block = [];
            }

            block.Add(line);
        }

        if (block.Count > 0)
        {
            yield return block;
        }
    }

    // Wrapped lines join with a space; a line ending in a hyphen joins without one and keeps it ("well-" + "known").
    private static List<Run> Join(List<Line> lines)
    {
        var runs = new List<Run>();
        foreach (var line in lines)
        {
            if (runs.Count > 0 && !runs[^1].Text.EndsWith('-'))
            {
                runs.Add(runs[^1] with { Text = " " });
            }

            runs.AddRange(line.Runs);
        }

        return runs;
    }

    // A space between two words keeps only the formatting both share, so "**a** **b**" stays one bold run.
    // A heading is bold by its style already, so its bold flag is dropped.
    internal static List<Run> Clean(List<Run> runs, bool keepBold)
    {
        var cleaned = new List<Run>(runs.Count);
        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            if (r.Text == " " && i + 1 < runs.Count)
            {
                var next = runs[i + 1];
                r = r with { Bold = r.Bold && next.Bold, Italic = r.Italic && next.Italic, Code = r.Code && next.Code, Link = r.Link == next.Link ? r.Link : null };
            }

            cleaned.Add(keepBold ? r : r with { Bold = false });
        }

        return Runs.Normalize(cleaned);
    }

    private static bool IsMono(string font) => Has(font, "Mono", "Courier", "Consolas", "Code");

    private static bool Has(string font, params string[] parts) => parts.Any(p => font.Contains(p, StringComparison.OrdinalIgnoreCase));
}
