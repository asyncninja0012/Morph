namespace Morph.Engines.Documents;

/// <summary>Plain text → paragraphs: a blank line starts a new one, other line ends stay as line breaks.</summary>
internal static class PlainTextReader
{
    public static List<Block> Read(string text) =>
    [
        .. text.ReplaceLineEndings("\n").Split("\n\n")
            .Select(p => p.Trim('\n'))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Paragraph([new Run(p)])),
    ];
}
