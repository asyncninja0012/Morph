namespace Morph.Core.Formats;

public enum FileKind { Unknown, Image, Video, Audio, Pdf, Document, Slides, Spreadsheet, Subtitle, Archive, Folder }

/// <summary>A detected file format (PLAN §2).</summary>
/// <param name="Id">Lower-case id, e.g. "jpg", "mp4"; "" when unknown.</param>
/// <param name="Extension">Canonical extension with dot, e.g. ".jpg"; "" for folders and unknown files.</param>
public sealed record FileFormat(string Id, FileKind Kind, string Extension, string MimeType)
{
    public static readonly FileFormat Unknown = new("", FileKind.Unknown, "", "application/octet-stream");
    public static readonly FileFormat Folder = new("folder", FileKind.Folder, "", "inode/directory");

    public static readonly FileFormat Jpg = Of("jpg", FileKind.Image, "image/jpeg");
    public static readonly FileFormat Png = Of("png", FileKind.Image, "image/png");
    public static readonly FileFormat Gif = Of("gif", FileKind.Image, "image/gif");
    public static readonly FileFormat Bmp = Of("bmp", FileKind.Image, "image/bmp");
    public static readonly FileFormat Webp = Of("webp", FileKind.Image, "image/webp");
    public static readonly FileFormat Tiff = Of("tiff", FileKind.Image, "image/tiff");
    public static readonly FileFormat Heic = Of("heic", FileKind.Image, "image/heic");
    public static readonly FileFormat Avif = Of("avif", FileKind.Image, "image/avif");

    public static readonly FileFormat Mp4 = Of("mp4", FileKind.Video, "video/mp4");
    public static readonly FileFormat Mov = Of("mov", FileKind.Video, "video/quicktime");
    public static readonly FileFormat Mkv = Of("mkv", FileKind.Video, "video/x-matroska");
    public static readonly FileFormat Webm = Of("webm", FileKind.Video, "video/webm");
    public static readonly FileFormat Avi = Of("avi", FileKind.Video, "video/x-msvideo");

    public static readonly FileFormat Mp3 = Of("mp3", FileKind.Audio, "audio/mpeg");
    public static readonly FileFormat M4a = Of("m4a", FileKind.Audio, "audio/mp4");
    public static readonly FileFormat Wav = Of("wav", FileKind.Audio, "audio/wav");
    public static readonly FileFormat Flac = Of("flac", FileKind.Audio, "audio/flac");
    public static readonly FileFormat Ogg = Of("ogg", FileKind.Audio, "audio/ogg");
    public static readonly FileFormat Opus = Of("opus", FileKind.Audio, "audio/opus");

    public static readonly FileFormat Pdf = Of("pdf", FileKind.Pdf, "application/pdf");

    public static readonly FileFormat Docx = Of("docx", FileKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    public static readonly FileFormat Xlsx = Of("xlsx", FileKind.Spreadsheet, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    public static readonly FileFormat Pptx = Of("pptx", FileKind.Slides, "application/vnd.openxmlformats-officedocument.presentationml.presentation");
    public static readonly FileFormat Odt = Of("odt", FileKind.Document, "application/vnd.oasis.opendocument.text");
    public static readonly FileFormat Ods = Of("ods", FileKind.Spreadsheet, "application/vnd.oasis.opendocument.spreadsheet");
    public static readonly FileFormat Odp = Of("odp", FileKind.Slides, "application/vnd.oasis.opendocument.presentation");
    public static readonly FileFormat Txt = Of("txt", FileKind.Document, "text/plain");
    public static readonly FileFormat Md = Of("md", FileKind.Document, "text/markdown");
    public static readonly FileFormat Html = Of("html", FileKind.Document, "text/html");
    public static readonly FileFormat Csv = Of("csv", FileKind.Spreadsheet, "text/csv");
    public static readonly FileFormat Srt = Of("srt", FileKind.Subtitle, "application/x-subrip");
    public static readonly FileFormat Vtt = Of("vtt", FileKind.Subtitle, "text/vtt");

    public static readonly FileFormat Zip = Of("zip", FileKind.Archive, "application/zip");
    public static readonly FileFormat Tar = Of("tar", FileKind.Archive, "application/x-tar");
    public static readonly FileFormat Gz = Of("gz", FileKind.Archive, "application/gzip");

    private static FileFormat Of(string id, FileKind kind, string mime) => new(id, kind, "." + id, mime);
}
