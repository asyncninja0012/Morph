using ImageMagick;

namespace Morph.Engines.Documents;

/// <summary>Turns image bytes from a document into an <see cref="Image"/> block: PNG, JPEG and GIF stay as they are, anything else becomes PNG.</summary>
internal static class Pictures
{
    public const long MaxBytes = 50L * 1024 * 1024; // per picture; a bigger one is left out

    /// <param name="width">Display width in CSS pixels, or 0 for the picture's own size (same for height; one alone keeps the aspect).</param>
    /// <returns>Null when Magick can't read the bytes; a picture that can't be read is left out, like a browser shows nothing.</returns>
    public static Image? FromBytes(byte[] bytes, string alt, double width = 0, double height = 0)
    {
        try
        {
            var info = new MagickImageInfo(bytes);
            var format = info.Format switch
            {
                MagickFormat.Png or MagickFormat.Png8 or MagickFormat.Png24 or MagickFormat.Png32 => "png",
                MagickFormat.Jpeg or MagickFormat.Jpg => "jpeg",
                MagickFormat.Gif => "gif",
                _ => null,
            };
            if (format is null)
            {
                using var image = new MagickImage(bytes);
                bytes = image.ToByteArray(MagickFormat.Png);
                format = "png";
            }

            if (info.Width == 0 || info.Height == 0)
            {
                return null;
            }

            var (w, h) = (width, height) switch
            {
                ( > 0, > 0) => (width, height),
                ( > 0, _) => (width, width * info.Height / info.Width),
                (_, > 0) => (height * info.Width / info.Height, height),
                _ => (info.Width, info.Height),
            };
            return new Image(bytes, format, Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)), alt);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>
    /// An HTML or Markdown image source: a base64 data: URI, or a file in <paramref name="folder"/> or below it. Never fetched from
    /// the network, and never a path outside the document's folder (a downloaded page can't pull in other files on the disk).
    /// </summary>
    public static Image? FromSource(string source, string? folder, string alt, double width = 0, double height = 0)
    {
        source = source.Trim();
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0 || !source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                return FromBytes(Convert.FromBase64String(source[(comma + 1)..]), alt, width, height);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        if (folder is null || source.Length == 0 || source.Contains(':', StringComparison.Ordinal) || source[0] is '/' or '\\')
        {
            return null; // a URL, a drive path or a rooted path
        }

        var relative = Uri.UnescapeDataString(source.Split('?', '#')[0]);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var file = new FileInfo(path);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !file.Exists || file.Length > MaxBytes)
        {
            return null;
        }

        return FromBytes(File.ReadAllBytes(path), alt, width, height);
    }
}
