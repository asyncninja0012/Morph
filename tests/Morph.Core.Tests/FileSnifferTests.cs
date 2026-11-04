using System.IO.Compression;
using System.Text;

using Morph.Core.Formats;

namespace Morph.Core.Tests;

public sealed class FileSnifferTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-sniff-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string, byte[], string> Headers => new()
    {
        { "jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10], ".jpeg" },
        { "png", [0x89, .. "PNG"u8, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0], ".png" },
        { "gif", [.. "GIF89a"u8], ".gif" },
        { "bmp", [.. "BM"u8, .. new byte[12], 40, 0, 0, 0], ".bmp" },
        { "tiff", [.. "II"u8, 0x2A, 0], ".tif" },
        { "tiff", [.. "MM"u8, 0, 0x2A], ".tiff" },
        { "webp", [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8 "u8], ".webp" },
        { "wav", [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVEfmt "u8], ".wav" },
        { "avi", [.. "RIFF"u8, 0, 0, 0, 0, .. "AVI LIST"u8], ".avi" },
        { "heic", Ftyp("heic", "mif1", "heic"), ".heic" },
        { "heic", Ftyp("mif1", "mif1", "heic"), ".heif" },
        { "avif", Ftyp("avif", "mif1", "avif"), ".avif" },
        { "avif", Ftyp("mif1", "mif1", "avif"), ".avif" },
        { "mp4", Ftyp("isom", "isom", "mp41"), ".mp4" },
        { "mov", Ftyp("qt  ", "qt  "), ".mov" },
        { "mov", [0, 0, 0, 8, .. "wide"u8, 0, 0, 0, 8], ".mov" },
        { "m4a", Ftyp("M4A ", "M4A ", "mp42"), ".m4a" },
        { "m4a", Ftyp("isom", "isom"), ".m4a" }, // generic brand: extension decides within ISO-BMFF
        { "mkv", [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x82, 0x88, .. "matroska"u8], ".mkv" },
        { "webm", [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x82, 0x84, .. "webm"u8], ".webm" },
        { "mp3", [.. "ID3"u8, 4, 0], ".mp3" },
        { "mp3", [0xFF, 0xFB, 0x90, 0x64], ".mp3" },
        { "flac", [.. "fLaC"u8, 0], ".flac" },
        { "ogg", Ogg("\u0001vorbis\0\0"), ".ogg" },
        { "opus", Ogg("OpusHead"), ".opus" },
        { "pdf", [.. "%PDF-1.7\n"u8], ".pdf" },
        { "pdf", [.. "junk\r\n%PDF-1.4"u8], ".pdf" },
        { "zip", [.. "PK"u8, 5, 6, .. new byte[18]], ".zip" },
        { "gz", [0x1F, 0x8B, 8, 0], ".gz" },
        { "tar", Tar(), ".tar" },
        { "vtt", [0xEF, 0xBB, 0xBF, .. "WEBVTT\n\n"u8], ".txt" },
        { "srt", [.. "1\n00:00:01,000 --> 00:00:02,000\nHi\n"u8], ".srt" },
        { "csv", [.. "a,b\n1,2\n"u8], ".csv" },
        { "md", [.. "# Title\n"u8], ".markdown" },
        { "html", [.. "<!doctype html>"u8], ".htm" },
        { "txt", [], ".txt" },
        { "", [.. "a,b\n"u8], ".xyz" },
        { "", [0, 1, 2, 3, 0, 0], ".csv" },
        { "", [.. "BMW owners\n"u8], ".bmp" },
    };

    [Theory]
    [MemberData(nameof(Headers))]
    public void Detects_format_from_header(string expected, byte[] header, string extension) =>
        Assert.Equal(expected, FileSniffer.Detect(header, extension).Id);

    [Fact]
    public async Task Magic_bytes_win_over_a_wrong_extension()
    {
        var path = Write("photo.jpg", [0x89, .. "PNG"u8, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]);

        var format = await FileSniffer.Instance.DetectAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(FileFormat.Png, format);
    }

    [Fact]
    public async Task Folder_is_detected() =>
        Assert.Equal(FileFormat.Folder, await FileSniffer.Instance.DetectAsync(_dir, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("word/document.xml", "docx")]
    [InlineData("xl/workbook.xml", "xlsx")]
    [InlineData("ppt/presentation.xml", "pptx")]
    [InlineData("readme.txt", "zip")]
    public async Task Zip_entries_tell_office_files_from_archives(string entry, string expected)
    {
        var path = Path.Combine(_dir, "file.zip"); // extension deliberately wrong for the Office cases
        await using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
        {
            zip.CreateEntry("[Content_Types].xml");
            zip.CreateEntry("_rels/.rels");
            zip.CreateEntry(entry);
        }

        var format = await FileSniffer.Instance.DetectAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(expected, format.Id);
    }

    [Theory]
    [InlineData("application/vnd.oasis.opendocument.text", "odt")]
    [InlineData("application/vnd.oasis.opendocument.spreadsheet", "ods")]
    [InlineData("application/vnd.oasis.opendocument.presentation", "odp")]
    public async Task Odf_is_read_from_the_stored_mimetype_entry(string mime, string expected)
    {
        var path = Path.Combine(_dir, "file.bin");
        await using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
        {
            await using (var s = await zip.CreateEntry("mimetype", CompressionLevel.NoCompression).OpenAsync(TestContext.Current.CancellationToken))
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes(mime), TestContext.Current.CancellationToken);
            }

            zip.CreateEntry("content.xml");
        }

        var format = await FileSniffer.Instance.DetectAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(expected, format.Id);
    }

    [Fact]
    public async Task Damaged_zip_is_still_a_zip()
    {
        var path = Write("broken.docx", [.. "PK"u8, 3, 4, .. new byte[100]]);

        Assert.Equal(FileFormat.Zip, await FileSniffer.Instance.DetectAsync(path, TestContext.Current.CancellationToken));
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Ftyp(string major, params string[] compatible)
    {
        var size = 16 + (4 * compatible.Length);
        return [0, 0, 0, (byte)size, .. "ftyp"u8, .. Encoding.ASCII.GetBytes(major), 0, 0, 0, 0,
            .. compatible.SelectMany(Encoding.ASCII.GetBytes), 0, 0, 0, 8, .. "free"u8];
    }

    private static byte[] Ogg(string firstPacket) => [.. "OggS"u8, .. new byte[24], .. Encoding.ASCII.GetBytes(firstPacket)];

    private static byte[] Tar()
    {
        var block = new byte[512];
        "file.txt"u8.CopyTo(block);
        "ustar\0"u8.CopyTo(block.AsSpan(257));
        return block;
    }
}
