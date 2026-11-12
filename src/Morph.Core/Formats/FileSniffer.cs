using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Morph.Core.Formats;

public interface IFileSniffer
{
    ValueTask<FileFormat> DetectAsync(string path, CancellationToken ct);
}

/// <summary>
/// Magic bytes first (64 KB header), extension only as a tie-breaker inside one container family
/// (ISO-BMFF mp4/m4a/mov) and for plain-text formats, which have no magic. ZIP files are opened to tell
/// DOCX/XLSX/PPTX/ODF apart from plain archives. A renamed file is reported as what it really is.
/// </summary>
public sealed class FileSniffer : IFileSniffer
{
    public const int HeaderSize = 64 * 1024;

    public static FileSniffer Instance { get; } = new();

    public async ValueTask<FileFormat> DetectAsync(string path, CancellationToken ct)
    {
        if (Directory.Exists(path))
        {
            return FileFormat.Folder;
        }

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        var buffer = new byte[(int)Math.Min(HeaderSize, file.Length)];
        var n = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        var format = Detect(buffer.AsSpan(0, n), Path.GetExtension(path));
        if (format != FileFormat.Zip)
        {
            return format;
        }

        file.Position = 0;
        return await InspectZipAsync(file, Path.GetExtension(path), ct).ConfigureAwait(false);
    }

    /// <summary>Format from the file header alone. Returns <see cref="FileFormat.Zip"/> for any ZIP that isn't ODF.</summary>
    internal static FileFormat Detect(ReadOnlySpan<byte> h, string extension)
    {
        var claimed = FileFormat.FromExtension(extension);
        switch (h)
        {
            case [0xFF, 0xD8, 0xFF, ..]: return FileFormat.Jpg;
            case [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, ..]: return FileFormat.Png;
            case [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..]: return FileFormat.Gif;
            case [(byte)'B', (byte)'M', ..] when h.Length >= 18 // "BM" alone matches text; check the DIB header size too
                && BinaryPrimitives.ReadUInt32LittleEndian(h[14..]) is 12 or 40 or 52 or 56 or 64 or 108 or 124:
                return FileFormat.Bmp;
            case [(byte)'I', (byte)'I', 0x2A, 0x00, ..] or [(byte)'M', (byte)'M', 0x00, 0x2A, ..]: return FileFormat.Tiff;
            case [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, .. var riff] when riff.Length >= 4:
                return Encoding.ASCII.GetString(riff[..4]) switch
                {
                    "WEBP" => FileFormat.Webp,
                    "WAVE" => FileFormat.Wav,
                    "AVI " => FileFormat.Avi,
                    _ => FileFormat.Unknown,
                };
            case [(byte)'f', (byte)'L', (byte)'a', (byte)'C', ..]: return FileFormat.Flac;
            case [(byte)'O', (byte)'g', (byte)'g', (byte)'S', ..]:
                return h.Length >= 36 && h[28..36].SequenceEqual("OpusHead"u8) ? FileFormat.Opus : FileFormat.Ogg;
            case [(byte)'I', (byte)'D', (byte)'3', ..]: return FileFormat.Mp3;
            // ASF header object GUID. shortcut: every ASF is WMV; an audio-only .wma would then fail in the media engine.
            case [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C, ..]: return FileFormat.Wmv;
            case [0x1A, 0x45, 0xDF, 0xA3, ..]: return h[..Math.Min(h.Length, 64)].IndexOf("webm"u8) >= 0 ? FileFormat.Webm : FileFormat.Mkv;
            case [(byte)'P', (byte)'K', 0x03, 0x04, ..]: return OdfFromFirstEntry(h) ?? FileFormat.Zip;
            case [(byte)'P', (byte)'K', 0x05, 0x06, ..]: return FileFormat.Zip; // empty archive
            case [0x1F, 0x8B, ..]: return FileFormat.Gz;
            // OLE compound file (old Office). shortcut: the stream names that tell DOC/XLS/PPT apart can sit anywhere in the file, so the
            // extension decides; a renamed one fails in Office or LibreOffice instead. Read the directory if that matters.
            case [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, ..]: return claimed.Id is "doc" or "xls" or "ppt" ? claimed : FileFormat.Unknown;
            case [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', ..]: return IsoBmff(h, claimed);
        }

        if (h[..Math.Min(h.Length, 1024)].IndexOf("%PDF-"u8) >= 0)
        {
            return FileFormat.Pdf; // the spec allows junk before the marker
        }

        if (h.Length >= 262 && h[257..262].SequenceEqual("ustar"u8))
        {
            return FileFormat.Tar;
        }

        if (h.Length >= 8 && Encoding.ASCII.GetString(h[4..8]) is "moov" or "mdat" or "wide" or "free" or "skip")
        {
            return FileFormat.Mov; // old QuickTime files have no ftyp box
        }

        // MPEG audio frame sync without an ID3 tag; layer bits 00 would be ADTS AAC.
        if (h is [0xFF, var b1, ..] && (b1 & 0xE0) == 0xE0 && (b1 & 0x06) != 0)
        {
            return FileFormat.Mp3;
        }

        var text = h is [0xEF, 0xBB, 0xBF, ..] ? h[3..] : h; // UTF-8 BOM
        if (text.StartsWith("WEBVTT"u8))
        {
            return FileFormat.Vtt;
        }

        if (text.IndexOf((byte)0) < 0)
        {
            return claimed.Id is "txt" or "md" or "html" or "csv" or "srt" ? claimed : FileFormat.Unknown;
        }

        return FileFormat.Unknown;
    }

    private static FileFormat IsoBmff(ReadOnlySpan<byte> h, FileFormat claimed)
    {
        var boxSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(h), (uint)h.Length);
        var brands = new List<string>();
        for (var i = 8; i + 4 <= boxSize; i += i == 8 ? 8 : 4) // major brand, minor version, then compatible brands
        {
            brands.Add(Encoding.ASCII.GetString(h[i..(i + 4)]));
        }

        if (brands is ["avif" or "avis", ..] || (brands is ["mif1" or "msf1", ..] && brands.Contains("avif")))
        {
            return FileFormat.Avif;
        }

        if (brands is ["heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "mif1" or "msf1", ..])
        {
            return FileFormat.Heic;
        }

        return brands switch
        {
            ["qt  ", ..] => FileFormat.Mov,
            ["M4A " or "M4B " or "M4P ", ..] => FileFormat.M4a,
            _ when claimed == FileFormat.M4a || claimed == FileFormat.Mov => claimed, // generic brands (isom, mp42, ...) don't say whether there's video
            _ => FileFormat.Mp4,
        };
    }

    // ODF requires an uncompressed "mimetype" first entry, so its content sits right after the local header.
    private static FileFormat? OdfFromFirstEntry(ReadOnlySpan<byte> h)
    {
        if (h.Length < 38 || !h[30..38].SequenceEqual("mimetype"u8))
        {
            return null;
        }

        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(h[18..]), 128);
        var start = 38 + BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        if (start + size > h.Length)
        {
            return null;
        }

        return Encoding.ASCII.GetString(h.Slice(start, size)) switch
        {
            "application/vnd.oasis.opendocument.text" => FileFormat.Odt,
            "application/vnd.oasis.opendocument.spreadsheet" => FileFormat.Ods,
            "application/vnd.oasis.opendocument.presentation" => FileFormat.Odp,
            _ => null,
        };
    }

    // shortcut: ZipArchive loads the whole central directory; a ZIP with millions of entries costs memory in the
    // caller's process. Cap by the EOCD entry count if sniffing ever runs on untrusted bulk input.
    private static async Task<FileFormat> InspectZipAsync(Stream file, string extension, CancellationToken ct)
    {
        try
        {
            await using var zip = await ZipArchive.CreateAsync(file, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, ct)
                .ConfigureAwait(false);
            foreach (var entry in zip.Entries)
            {
                switch (entry.FullName)
                {
                    case "word/document.xml": return FileFormat.Docx;
                    case "xl/workbook.xml": return FileFormat.Xlsx;
                    case "ppt/presentation.xml": return FileFormat.Pptx;
                    // Pages, Numbers and Keynote share this layout; only the extension tells them apart.
                    case "Index/Document.iwa" when FileFormat.FromExtension(extension) == FileFormat.Pages: return FileFormat.Pages;
                }
            }
        }
        catch (InvalidDataException)
        {
            // Damaged central directory: still a ZIP as far as the header goes.
        }

        return FileFormat.Zip;
    }
}
