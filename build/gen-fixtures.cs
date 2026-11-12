#:package Magick.NET-Q8-x64
#:property PublishAot=false

// Writes one sample file per input format in capabilities.json to tests/fixtures/ (git-ignored), for the golden tests.
// Run from the repo root after ./build/fetch-binaries.ps1:  dotnet run build/gen-fixtures.cs
// Images: 160×120 drawn shapes. Video: 1 s 320×240 testsrc2 at 25 fps with a sine tone. Audio: 1 s sine.
// HEIC is written through Windows (WIC) and is skipped when the HEVC Video Extensions are missing.

using System.Diagnostics;

using ImageMagick;
using ImageMagick.Drawing;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

var root = Directory.GetCurrentDirectory();
var ffmpeg = Path.Combine(root, "artifacts", "binaries", "ffmpeg.exe");
if (!File.Exists(Path.Combine(root, "Morph.slnx")) || !File.Exists(ffmpeg))
{
    Console.Error.WriteLine("Run from the repo root after ./build/fetch-binaries.ps1.");
    return 1;
}

var dir = Directory.CreateDirectory(Path.Combine(root, "tests", "fixtures")).FullName;

using (var image = new MagickImage(MagickColors.White, 160, 120))
{
    new Drawables()
        .FillColor(MagickColors.Red).Rectangle(10, 10, 70, 60)
        .FillColor(MagickColors.Blue).Circle(115, 80, 115, 110)
        .StrokeColor(MagickColors.Black).StrokeWidth(4).Line(0, 119, 159, 0)
        .Draw(image);
    foreach (var format in new[] { MagickFormat.Png, MagickFormat.Jpg, MagickFormat.WebP, MagickFormat.Gif, MagickFormat.Bmp, MagickFormat.Tiff, MagickFormat.Avif })
    {
        image.Write(Path.Combine(dir, $"sample.{format.ToString().ToLowerInvariant()}"), format);
    }

    try
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, image.Width, image.Height, 96, 96,
            image.GetPixelsUnsafe().ToByteArray(PixelMapping.BGRA)!);
        await encoder.FlushAsync();
        await using var file = File.Create(Path.Combine(dir, "sample.heic"));
        await stream.GetInputStreamAt(0).AsStreamForRead().CopyToAsync(file);
    }
    catch (Exception ex)
    {
        File.Delete(Path.Combine(dir, "sample.heic"));
        Console.WriteLine($"sample.heic skipped (needs HEVC Video Extensions): {ex.Message}");
    }
}

string[] video = ["-f", "lavfi", "-i", "testsrc2=d=1:s=320x240:r=25"];
string[] tone = ["-f", "lavfi", "-i", "sine=d=1"];
(string Name, string[] Args)[] media =
[
    ("mp4", [.. video, .. tone, "-c:v", "mpeg4", "-c:a", "aac"]),
    ("mov", [.. video, .. tone, "-c:v", "mpeg4", "-c:a", "pcm_s16le"]),
    ("mkv", [.. video, .. tone, "-c:v", "mpeg4", "-c:a", "flac"]),
    ("webm", [.. video, .. tone, "-c:v", "libvpx", "-b:v", "2M", "-c:a", "libvorbis"]), // 2 Mb/s so Compress has something to shrink
    ("avi", [.. video, .. tone, "-c:v", "mpeg4", "-c:a", "pcm_s16le"]),
    ("wmv", [.. video, .. tone, "-c:v", "wmv2", "-c:a", "wmav2"]),
    ("mp3", [.. tone, "-c:a", "libmp3lame"]),
    ("m4a", [.. tone, "-c:a", "aac"]),
    ("wav", [.. tone, "-c:a", "pcm_s16le"]),
    ("flac", [.. tone, "-c:a", "flac"]),
    ("ogg", [.. tone, "-c:a", "libvorbis"]),
    ("opus", [.. tone, "-c:a", "libopus"]),
];
foreach (var (name, ffArgs) in media)
{
    var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true };
    foreach (var a in (string[])["-hide_banner", "-nostdin", "-loglevel", "error", "-y", .. ffArgs, Path.Combine(dir, $"sample.{name}")])
    {
        start.ArgumentList.Add(a);
    }

    using var process = Process.Start(start)!;
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine($"sample.{name} failed: {stderr}");
        return 1;
    }
}

// PDF: two 3×2 inch pages (216×144 pt), each with a red box top-left and the line "Morph sample page N" in Helvetica.
// Written by hand (no PDF library until task 2.2); xref offsets are byte positions, so the text is ASCII.
var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
var offsets = new List<int>();
void PdfObject(string body)
{
    offsets.Add(pdf.Length);
    pdf.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
}

string PageContent(int n) => $"1 0 0 rg 10 80 60 50 re f BT /F1 14 Tf 10 30 Td (Morph sample page {n}) Tj ET";
PdfObject("<< /Type /Catalog /Pages 2 0 R >>");
PdfObject("<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>");
for (var n = 1; n <= 2; n++)
{
    PdfObject($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 216 144] /Resources << /Font << /F1 7 0 R >> >> /Contents {n * 2 + 2} 0 R >>");
    PdfObject($"<< /Length {PageContent(n).Length} >>\nstream\n{PageContent(n)}\nendstream");
}

PdfObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
var xref = pdf.Length;
pdf.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
offsets.ForEach(o => pdf.Append($"{o:D10} 00000 n \n"));
pdf.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
File.WriteAllText(Path.Combine(dir, "sample.pdf"), pdf.ToString(), System.Text.Encoding.ASCII);

// Documents: the same content in each format: a heading, a paragraph with bold/italic/code/link, a bullet list with a nested item,
// a numbered list, a 2×2 table and a code block. The DOCX is written by hand, the way Word lays it out, not by Morph's writer.
File.WriteAllText(Path.Combine(dir, "sample.md"), """
    # Morph sample

    Plain text with **bold**, *italic*, `code` and a [link](https://example.com/).

    - First item
    - Second item
      - Nested item

    1. One
    2. Two

    | Name | Value |
    | --- | --- |
    | Cell A2 | Cell B2 |

    ```
    var x = 1;
    ```

    """);
File.WriteAllText(Path.Combine(dir, "sample.html"), """
    <!DOCTYPE html>
    <html><head><meta charset="utf-8"><title>Ignored title</title><style>p { color: red }</style><script>var hidden = 1;</script></head>
    <body>
    <h1>Morph sample</h1>
    <p>Plain text with <strong>bold</strong>, <em>italic</em>, <code>code</code> and a <a href="https://example.com/">link</a>.</p>
    <ul>
      <li>First item</li>
      <li>Second item
        <ul><li>Nested item</li></ul>
      </li>
    </ul>
    <ol><li>One</li><li>Two</li></ol>
    <table><tr><th>Name</th><th>Value</th></tr><tr><td>Cell A2</td><td>Cell B2</td></tr></table>
    <pre><code>var x = 1;</code></pre>
    </body></html>
    """);
File.WriteAllText(Path.Combine(dir, "sample.txt"), "Morph sample\r\n\r\nPlain text with bold, italic, code and a link.\r\n\r\nFirst item\r\nSecond item\r\nNested item\r\n\r\nOne\r\nTwo\r\n\r\nName\tValue\r\nCell A2\tCell B2\r\n\r\nvar x = 1;\r\n");

const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
string Para(string body, string? style = null, int? num = null, int level = 0) =>
    "<w:p>" + (style ?? (num is null ? null : "ListParagraph")) switch
    {
        null => "",
        var s => $"<w:pPr><w:pStyle w:val=\"{s}\"/>" + (num is null ? "" : $"<w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{num}\"/></w:numPr>") + "</w:pPr>",
    } + body + "</w:p>";
string R(string text, string props = "") => $"<w:r>{(props.Length > 0 ? $"<w:rPr>{props}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{text}</w:t></w:r>";
string Cell(string text, bool bold = false) => $"<w:tc><w:tcPr><w:tcW w:w=\"0\" w:type=\"auto\"/></w:tcPr>{Para(R(text, bold ? "<w:b/>" : ""))}</w:tc>";
string Level(int l, string format, string text) =>
    $"<w:lvl w:ilvl=\"{l}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{format}\"/><w:lvlText w:val=\"{text}\"/><w:pPr><w:ind w:left=\"{720 * (l + 1)}\" w:hanging=\"360\"/></w:pPr></w:lvl>";
var parts = new Dictionary<string, string>
{
    ["[Content_Types].xml"] = """
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/><Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/></Types>
        """,
    ["_rels/.rels"] = """
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
        """,
    ["word/_rels/document.xml.rels"] = """
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.com/" TargetMode="External"/></Relationships>
        """,
    ["word/styles.xml"] = $"""
        <w:styles xmlns:w="{W}"><w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style><w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:rPr><w:b/><w:sz w:val="32"/></w:rPr></w:style><w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/></w:style><w:style w:type="paragraph" w:styleId="HTMLPreformatted"><w:name w:val="HTML Preformatted"/><w:basedOn w:val="Normal"/><w:rPr><w:rFonts w:ascii="Courier New" w:hAnsi="Courier New"/></w:rPr></w:style></w:styles>
        """,
    ["word/numbering.xml"] = $"""
        <w:numbering xmlns:w="{W}"><w:abstractNum w:abstractNumId="0">{Level(0, "bullet", "•")}{Level(1, "bullet", "o")}</w:abstractNum><w:abstractNum w:abstractNumId="1">{Level(0, "decimal", "%1.")}</w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num><w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num></w:numbering>
        """,
    ["word/document.xml"] = $"""
        <w:document xmlns:w="{W}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><w:body>
        {Para(R("Morph sample"), "Heading1")}
        {Para(R("Plain text with ") + R("bold", "<w:b/>") + R(", ") + R("italic", "<w:i/>") + R(", ") + R("code", "<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/>") + R(" and a ") + "<w:hyperlink r:id=\"rId3\">" + R("link", "<w:rStyle w:val=\"Hyperlink\"/>") + "</w:hyperlink>" + R("."))}
        {Para(R("First item"), num: 1)}{Para(R("Second item"), num: 1)}{Para(R("Nested item"), num: 1, level: 1)}
        {Para("")}
        {Para(R("One"), num: 2)}{Para(R("Two"), num: 2)}
        <w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/></w:tblPr><w:tblGrid><w:gridCol w:w="4500"/><w:gridCol w:w="4500"/></w:tblGrid><w:tr>{Cell("Name", bold: true)}{Cell("Value", bold: true)}</w:tr><w:tr>{Cell("Cell A2")}{Cell("Cell B2")}</w:tr></w:tbl>
        {Para(R("var x = 1;"), "HTMLPreformatted")}
        <w:sectPr/></w:body></w:document>
        """,
};
File.Delete(Path.Combine(dir, "sample.docx"));
using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(dir, "sample.docx"), System.IO.Compression.ZipArchiveMode.Create))
{
    foreach (var (name, xml) in parts)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(xml);
    }
}

// Sheets: the same grid as CSV and as a hand-written XLSX (shared strings, numbers, date-formatted serials, a blank cell).
File.WriteAllText(Path.Combine(dir, "sample.csv"), "Name,Amount,Date,Note\r\nApple,1.5,2023-03-15,\"red, sweet\"\r\nBanana,20,2024-01-01,\"says \"\"hi\"\"\"\r\nCafé,-3,,\"line1\nline2\"\r\n",
    new System.Text.UTF8Encoding(false));
const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
string[] shared = ["Name", "Amount", "Date", "Note", "Apple", "red, sweet", "Banana", "says \"hi\"", "Café", "line1\nline2"];
static string C(string r, string v, string? t = null, int s = 0) => $"<c r=\"{r}\"{(t is null ? "" : $" t=\"{t}\"")}{(s == 0 ? "" : $" s=\"{s}\"")}><v>{v}</v></c>";
var xlsx = new Dictionary<string, string>
{
    ["[Content_Types].xml"] = """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""",
    ["_rels/.rels"] = """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""",
    ["xl/_rels/workbook.xml.rels"] = """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""",
    ["xl/workbook.xml"] = $"""<workbook xmlns="{S}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Fruit" sheetId="1" r:id="rId1"/></sheets></workbook>""",
    ["xl/styles.xml"] = $"""<styleSheet xmlns="{S}"><fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs></styleSheet>""",
    ["xl/sharedStrings.xml"] = $"""<sst xmlns="{S}">{string.Concat(shared.Select(t => $"<si><t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(t)}</t></si>"))}</sst>""",
    ["xl/worksheets/sheet1.xml"] = $"""
        <worksheet xmlns="{S}"><sheetData>
        <row r="1">{C("A1", "0", "s")}{C("B1", "1", "s")}{C("C1", "2", "s")}{C("D1", "3", "s")}</row>
        <row r="2">{C("A2", "4", "s")}{C("B2", "1.5")}{C("C2", "45000", s: 1)}{C("D2", "5", "s")}</row>
        <row r="3">{C("A3", "6", "s")}{C("B3", "20")}{C("C3", "45292", s: 1)}{C("D3", "7", "s")}</row>
        <row r="4">{C("A4", "8", "s")}{C("B4", "-3")}{C("D4", "9", "s")}</row>
        </sheetData></worksheet>
        """,
};
File.Delete(Path.Combine(dir, "sample.xlsx"));
using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(dir, "sample.xlsx"), System.IO.Compression.ZipArchiveMode.Create))
{
    foreach (var (name, xml) in xlsx)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(xml);
    }
}

// Subtitles: the same two cues; the VTT also has a NOTE, cue settings, a cue id, a short timestamp and an entity.
File.WriteAllText(Path.Combine(dir, "sample.srt"), "1\r\n00:00:01,000 --> 00:00:02,500\r\nHello <i>world</i>\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nFish & chips\r\nsecond line\r\n\r\n");
File.WriteAllText(Path.Combine(dir, "sample.vtt"), "WEBVTT\n\nNOTE a comment\n\n00:01.000 --> 00:02.500 align:start\nHello <i>world</i>\n\nintro\n00:00:03.000 --> 00:00:04.000\nFish &amp; chips\nsecond line\n");

// Archives: readme.txt, a docs folder and docs/notes.md, as ZIP, TAR and TAR.GZ ("sample.gz").
(string Name, string? Text)[] archived = [("readme.txt", "hello"), ("docs", null), ("docs/notes.md", "# notes\n")];
File.Delete(Path.Combine(dir, "sample.zip"));
using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(dir, "sample.zip"), System.IO.Compression.ZipArchiveMode.Create))
{
    foreach (var (name, text) in archived)
    {
        var entry = zip.CreateEntry(text is null ? name + "/" : name);
        if (text is not null)
        {
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }
    }
}

var tarBytes = new MemoryStream();
using (var tar = new System.Formats.Tar.TarWriter(tarBytes, leaveOpen: true))
{
    foreach (var (name, text) in archived)
    {
        var entry = new System.Formats.Tar.PaxTarEntry(text is null ? System.Formats.Tar.TarEntryType.Directory : System.Formats.Tar.TarEntryType.RegularFile, name);
        if (text is not null)
        {
            entry.DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)); // a directory can't take one, not even null
        }

        tar.WriteEntry(entry);
    }
}

File.WriteAllBytes(Path.Combine(dir, "sample.tar"), tarBytes.ToArray());
using (var gz = new System.IO.Compression.GZipStream(File.Create(Path.Combine(dir, "sample.gz")), System.IO.Compression.CompressionLevel.Optimal))
{
    gz.Write(tarBytes.ToArray());
}

// Office (PK-1): saved by the user's own Office through COM, so these exist only where Office is installed; the golden tests skip
// them elsewhere. PPTX/PPT/ODP: two title slides ("Morph slide one", "Morph slide two"); DOC/ODT from sample.docx; XLS/ODS from sample.xlsx.
// No .pages sample: only Apple's Pages writes one.
WithOffice("PowerPoint.Application", powerPoint =>
{
    var deck = powerPoint.Presentations.Add(WithWindow: 0);
    for (var i = 1; i <= 2; i++)
    {
        deck.Slides.Add(i, 1).Shapes[1].TextFrame.TextRange.Text = i == 1 ? "Morph slide one" : "Morph slide two"; // 1 = ppLayoutTitle
    }

    foreach (var (ext, format) in new[] { ("pptx", 24), ("ppt", 1), ("odp", 35) })
    {
        deck.SaveAs(Path.Combine(dir, $"sample.{ext}"), format);
    }

    deck.Close();
});
WithOffice("Word.Application", word =>
{
    var document = word.Documents.Open(Path.Combine(dir, "sample.docx"), ReadOnly: true, AddToRecentFiles: false);
    document.SaveAs2(Path.Combine(dir, "sample.doc"), 0); // wdFormatDocument
    document.SaveAs2(Path.Combine(dir, "sample.odt"), 23); // wdFormatOpenDocumentText
    document.Close(0);
});
WithOffice("Excel.Application", excel =>
{
    excel.DisplayAlerts = false;
    var book = excel.Workbooks.Open(Path.Combine(dir, "sample.xlsx"), ReadOnly: true, AddToMru: false);
    book.SaveAs(Path.Combine(dir, "sample.xls"), 56); // xlExcel8
    book.SaveAs(Path.Combine(dir, "sample.ods"), 60); // xlOpenDocumentSpreadsheet
    book.Close(false);
});

Console.WriteLine($"Fixtures in {dir}");
return 0;

// Runs the Office app, then quits it. An Office app keeps running after Quit while any COM wrapper of it is alive (also after
// this process ends), so the wrappers are collected once save has returned and nothing refers to them.
static void WithOffice(string progId, Action<dynamic> save)
{
    if (Type.GetTypeFromProgID(progId) is not { } type)
    {
        return;
    }

    Run(type, save);
    GC.Collect();
    GC.WaitForPendingFinalizers();

    static void Run(Type type, Action<dynamic> save)
    {
        dynamic app = Activator.CreateInstance(type)!;
        save(app);
        app.Quit();
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app);
    }
}
