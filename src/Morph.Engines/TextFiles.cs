using System.Text;

namespace Morph.Engines;

/// <summary>Reading text files of unknown encoding (CSV and subtitles are often saved by old tools in Windows-1252).</summary>
internal static class TextFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A BOM wins (UTF-8/16/32); otherwise UTF-8 if the bytes are valid UTF-8, else Windows-1252. Line endings become "\n".</summary>
    public static async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        string text;
        if (bytes is [0xEF, 0xBB, 0xBF, ..] or [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        else
        {
            try
            {
                text = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding(1252).GetString(bytes);
            }
        }

        return text.ReplaceLineEndings("\n");
    }
}
