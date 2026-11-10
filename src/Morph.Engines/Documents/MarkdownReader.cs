using Markdig;
using Markdig.Syntax.Inlines;

using Md = Markdig.Syntax;
using MdTables = Markdig.Extensions.Tables;

namespace Morph.Engines.Documents;

/// <summary>Markdown (CommonMark + GitHub pipe tables) → <see cref="Block"/>s, with Markdig. An image in a paragraph becomes an
/// <see cref="Image"/> after it (data: URIs and files next to the document only); one Morph can't load keeps its alt text.</summary>
internal sealed class MarkdownReader(string? folder)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    /// <param name="folder">The document's folder, for images given as relative paths.</param>
    public static List<Block> Read(string markdown, string? folder = null) => new MarkdownReader(folder).Blocks(Markdown.Parse(markdown, Pipeline));

    private List<Block> Blocks(IEnumerable<Md.Block> blocks)
    {
        var result = new List<Block>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Md.HeadingBlock h:
                    result.Add(new Heading(h.Level, Inlines(h.Inline)));
                    break;
                case Md.ParagraphBlock p:
                    var images = new List<Image>();
                    var runs = Inlines(p.Inline, images);
                    if (runs.Count > 0)
                    {
                        result.Add(new Paragraph(runs));
                    }

                    result.AddRange(images);
                    break;
                case Md.ListBlock l:
                    result.Add(new ListBlock(l.IsOrdered, [.. l.OfType<Md.ListItemBlock>().Select(i => (IReadOnlyList<Block>)Blocks(i))]));
                    break;
                case MdTables.Table t:
                    result.Add(new Table([.. t.OfType<MdTables.TableRow>().Select(r =>
                        (IReadOnlyList<IReadOnlyList<Run>>)[.. r.OfType<MdTables.TableCell>().Select(c =>
                            (IReadOnlyList<Run>)Blocks(c).OfType<Paragraph>().SelectMany(p => p.Runs).ToList())])]));
                    break;
                case Md.HtmlBlock html:
                    result.AddRange(HtmlReader.Read(html.Lines.ToString(), folder));
                    break;
                case Md.CodeBlock code: // fenced and indented
                    result.Add(new CodeBlock(code.Lines.ToString()));
                    break;
                case Md.ContainerBlock container: // block quotes keep their text, not the quote
                    result.AddRange(Blocks(container));
                    break;
            }
        }

        return result;
    }

    /// <param name="images">Where loaded images go; null keeps every image as its alt text (headings).</param>
    private List<Run> Inlines(ContainerInline? inlines, List<Image>? images = null)
    {
        var runs = new List<Run>();
        Walk(inlines, new Run(""), runs, images);
        return Runs.Normalize(runs);
    }

    private void Walk(Inline? inline, Run style, List<Run> runs, List<Image>? images)
    {
        switch (inline)
        {
            case LiteralInline literal:
                runs.Add(style with { Text = literal.Content.ToString() });
                break;
            case CodeInline code:
                runs.Add(style with { Text = code.Content, Code = true });
                break;
            case HtmlEntityInline entity:
                runs.Add(style with { Text = entity.Transcoded.ToString() });
                break;
            case LineBreakInline lineBreak:
                runs.Add(style with { Text = lineBreak.IsHard ? "\n" : " " });
                break;
            case HtmlInline html when html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase):
                runs.Add(style with { Text = "\n" });
                break;
            case AutolinkInline auto:
                runs.Add(style with { Text = auto.Url, Link = auto.IsEmail ? "mailto:" + auto.Url : auto.Url });
                break;
            case EmphasisInline emphasis:
                Children(emphasis, emphasis.DelimiterCount >= 2 ? style with { Bold = true } : style with { Italic = true }, runs, images);
                break;
            case LinkInline { IsImage: true } link when images is not null
                && Pictures.FromSource(link.Url ?? "", folder, Runs.Plain(Inlines(link))) is { } image:
                images.Add(image);
                break;
            case LinkInline link:
                Children(link, link.IsImage ? style : style with { Link = link.Url }, runs, images); // an image keeps its alt text
                break;
            case ContainerInline container:
                Children(container, style, runs, images);
                break;
        }
    }

    private void Children(ContainerInline container, Run style, List<Run> runs, List<Image>? images)
    {
        foreach (var child in container)
        {
            Walk(child, style, runs, images);
        }
    }
}
