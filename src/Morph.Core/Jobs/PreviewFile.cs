using System.Buffers.Binary;

namespace Morph.Core.Jobs;

/// <summary>A downscaled picture of a file for an editor, made by the Worker.</summary>
/// <param name="Bgra">Width × Height pixels, 4 bytes each (blue, green, red, straight alpha), rows top to bottom.</param>
/// <param name="SourceWidth">The file's own width in the unit editors return: pixels for images and video, points for PDF pages.</param>
public sealed record PreviewImage(int Width, int Height, double SourceWidth, double SourceHeight, byte[] Bgra);

/// <summary>
/// The Worker → App preview format: raw pixels behind a 28-byte header, so the App shows a dropped file without decoding it
/// (untrusted input is parsed only in the Worker). Header: "MPV1", width, height (int32 LE), source width, height (double LE).
/// </summary>
public static class PreviewFile
{
    /// <summary>Longest preview edge the App asks for and accepts.</summary>
    public const int MaxEdge = 4096;

    public const string ToolId = "preview";

    private const int HeaderSize = 28;
    private static ReadOnlySpan<byte> Magic => "MPV1"u8;

    public static async Task WriteAsync(string path, PreviewImage preview, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), preview.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), preview.Height);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(12), preview.SourceWidth);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(20), preview.SourceHeight);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await file.WriteAsync(header, ct).ConfigureAwait(false);
        await file.WriteAsync(preview.Bgra, ct).ConfigureAwait(false);
    }

    /// <exception cref="InvalidDataException">The file isn't a well-formed preview within <see cref="MaxEdge"/>.</exception>
    public static async Task<PreviewImage> ReadAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.Length < HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a Morph preview.");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var sourceWidth = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(12));
        var sourceHeight = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(20));
        if (width is < 1 or > MaxEdge || height is < 1 or > MaxEdge || bytes.Length != HeaderSize + (long)width * height * 4
            || !double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight) || sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new InvalidDataException($"Bad preview header: {width}x{height}, source {sourceWidth}x{sourceHeight}, {bytes.Length} bytes.");
        }

        return new PreviewImage(width, height, sourceWidth, sourceHeight, bytes[HeaderSize..]);
    }
}
