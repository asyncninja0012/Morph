using Markdig;
using Markdig.Syntax.Inlines;

using Md = Markdig.Syntax;
using MdTables = Markdig.Extensions.Tables;

namespace Morph.Engines.Documents;

/// <summary>Markdown (CommonMark + GitHub pipe tables) → <see cref="Block"/>s, with Markdig.</summary>
internal static class MarkdownReader
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    public static List<Block> Read(string markdown) => Blocks(Markdown.Parse(markdown, Pipeline));

    private static List<Block> Blocks(IEnumerable<Md.Block> blocks)
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
                    result.Add(new Paragraph(Inlines(p.Inline)));
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
                    result.AddRange(HtmlReader.Read(html.Lines.ToString()));
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

    private static List<Run> Inlines(ContainerInline? inlines)
    {
        var runs = new List<Run>();
        Walk(inlines, new Run(""), runs);
        return Runs.Normalize(runs);
    }

    private static void Walk(Inline? inline, Run style, List<Run> runs)
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
                Children(emphasis, emphasis.DelimiterCount >= 2 ? style with { Bold = true } : style with { Italic = true }, runs);
                break;
            case LinkInline link:
                Children(link, link.IsImage ? style : style with { Link = link.Url }, runs); // an image keeps its alt text
                break;
            case ContainerInline container:
                Children(container, style, runs);
                break;
        }
    }

    private static void Children(ContainerInline container, Run style, List<Run> runs)
    {
        foreach (var child in container)
        {
            Walk(child, style, runs);
        }
    }
}
