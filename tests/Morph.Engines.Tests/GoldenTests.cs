using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;

using ImageMagick;

using Morph.Core.Capabilities;
using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Engines;
using Morph.Engines.Documents;
using Morph.Engines.Media;
using Morph.Engines.Sheets;
using Morph.Engines.Subtitles;

using PdfSharp.Pdf.IO;

using SharpReader = PdfSharp.Pdf.IO.PdfReader;

using WinOcr = Windows.Media.Ocr.OcrEngine;

namespace Morph.Engines.Tests;

/// <summary>
/// One case per conversion row in capabilities.json (PLAN §8), so a new row without a fixture fails. Each runs the real
/// Morph.Worker.exe on tests/fixtures/sample.&lt;in&gt; with the tool's defaults, like a drop does, and checks the output:
/// sniffed format; images keep size and look (dHash, Hamming ≤ 6); media keep duration, frame size and get the target codecs;
/// images → PDF render back to the same look.
/// </summary>
public sealed class GoldenTests : IDisposable
{
    private static readonly CapabilityMatrix Matrix = CapabilityMatrix.LoadDefault(_ => true);
    private static readonly string WorkerPath = Path.Combine(AppContext.BaseDirectory, "Morph.Worker.exe");
    private static readonly string Fixtures = Path.Combine(RepoRoot(), "tests", "fixtures");

    private static readonly Dictionary<string, string[]> Codecs = new()
    {
        ["mp4"] = ["h264", "aac"],
        ["webm"] = ["vp9", "opus"],
        ["avi"] = ["mpeg4", "mp3"],
        ["gif"] = ["gif"],
        ["mp3"] = ["mp3"],
        ["m4a"] = ["aac"],
        ["wav"] = ["pcm_s16le"],
        ["flac"] = ["flac"],
        ["ogg"] = ["vorbis"],
        ["opus"] = ["opus"],
    };

    private readonly string _dir = Directory.CreateTempSubdirectory("morph-golden-").FullName;
    private readonly Ffmpeg _ffmpeg = Ffmpeg.Bundled;

    public static TheoryData<string, string, string> Rows()
    {
        var rows = new TheoryData<string, string, string>();
        foreach (var r in Matrix.Rows.Where(r => r.Tool == "convert"))
        {
            rows.Add(r.In, r.Out!, r.Engine);
        }

        return rows;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A just-written output can still be open in a virus scan or the thumbnailer; it's a temp folder.
        }
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Row_converts(string from, string to, string engine)
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(Fixtures, $"sample.{from}");
        Assert.SkipWhen(from == "heic" && !File.Exists(fixture), "no HEIC fixture: gen-fixtures needs HEVC Video Extensions");
        Assert.True(File.Exists(fixture), $"{fixture} is missing. Run: dotnet run build/gen-fixtures.cs");
        Assert.SkipWhen(to == "heic" && !await SystemCodecs.CanEncodeHeicAsync(), "HEIC output needs HEVC Video Extensions");
        if (engine == "media")
        {
            Assert.SkipUnless(_ffmpeg.IsAvailable, "ffmpeg not fetched: run ./build/fetch-binaries.ps1");
            Assert.SkipWhen((to == "mp4" || (to == "mkv" && from == "wmv")) && await new EncoderProbe(_ffmpeg, _dir).H264Async(ct) is null, "no H.264 encoder on this machine");
        }

        var input = Path.Combine(_dir, Path.GetFileName(fixture));
        File.Copy(fixture, input);
        Assert.Equal(from, (await FileSniffer.Instance.DetectAsync(input, ct)).Id);

        var result = await RunAsync($"convert.{to}", engine, [input], ct);
        if (from == "pdf" && engine == "pdf")
        {
            await AssertPdfPagesAsync(result.Outputs, to, ct);
            return;
        }

        var output = Assert.Single(result.Outputs);
        Assert.Equal(to, (await FileSniffer.Instance.DetectAsync(output, ct)).Id);
        if (engine == "document")
        {
            await AssertSameDocumentAsync(input, output, from, to, ct);
        }
        else if (engine == "sheet")
        {
            var grid = to == "csv" ? Csv.Parse(await TextFiles.ReadAsync(output, ct)) : Assert.Single(SheetEngine.ReadXlsx(output, ct)).Rows;
            Assert.Equal(SampleGrid, grid);
        }
        else if (engine == "subtitle")
        {
            Assert.Equal(SampleCues, await SubtitleEngine.ReadAsync(output, ct));
        }
        else if (engine == "archive")
        {
            Assert.Equal(SampleFiles, await ArchivedFilesAsync(output, ct));
        }
        else if (to == "pdf")
        {
            var pages = await RenderAsync(output, ct);
            AssertLooksTheSame(input, Assert.Single(pages), sameSize: false);
        }
        else if (FileFormat.FromExtension(from).Kind == FileKind.Image)
        {
            AssertLooksTheSame(input, output);
        }
        else
        {
            await AssertSameMediaAsync(input, output, from, to, ct);
        }
    }

    public static TheoryData<string, string, string> ToolRows()
    {
        var rows = new TheoryData<string, string, string>();
        foreach (var r in Matrix.Rows.Where(r => r.Tool is "compress" or "resize"))
        {
            rows.Add(r.In, r.Tool, r.Engine);
        }

        return rows;
    }

    // Compress runs the Small preset (the fixtures are tiny, so Balanced may already be beaten) and must shrink the file;
    // resize runs the wedge default (50%). Both keep the format (other video → MP4, WAV/FLAC → M4A) and the look or duration.
    [Theory]
    [MemberData(nameof(ToolRows))]
    public async Task Tool_row_runs(string from, string tool, string engine)
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(Fixtures, $"sample.{(from == "pdf" ? "jpg" : from)}");
        Assert.True(File.Exists(fixture), $"{fixture} is missing. Run: dotnet run build/gen-fixtures.cs");
        if (engine == "media")
        {
            Assert.SkipUnless(_ffmpeg.IsAvailable, "ffmpeg not fetched: run ./build/fetch-binaries.ps1");
            Assert.SkipWhen(FileFormat.FromExtension(from).Kind == FileKind.Video && from != "webm" && await new EncoderProbe(_ffmpeg, _dir).H264Async(ct) is null,
                "no H.264 encoder on this machine");
        }

        var input = Path.Combine(_dir, Path.GetFileName(fixture));
        File.Copy(fixture, input);
        if (from == "pdf")
        {
            input = Assert.Single((await RunAsync("convert.pdf", "pdf", [input], ct)).Outputs); // sample.pdf has no photo to shrink
        }

        var parameters = Matrix.Tool(tool)!.ParametersFor(tool == "compress" ? "small" : null);
        var output = Assert.Single((await RunAsync(tool, engine, [input], ct, parameters)).Outputs);
        var to = from switch
        {
            "mov" or "avi" or "wmv" => "mp4",
            "wav" or "flac" => "m4a",
            _ => from,
        };
        Assert.Equal(to, (await FileSniffer.Instance.DetectAsync(output, ct)).Id);
        Assert.EndsWith(tool == "compress" ? "-compressed" : "-resized", Path.GetFileNameWithoutExtension(output), StringComparison.Ordinal);
        if (tool == "compress")
        {
            Assert.True(new FileInfo(output).Length < new FileInfo(input).Length, $"{new FileInfo(output).Length} >= {new FileInfo(input).Length}");
        }

        if (from == "pdf")
        {
            AssertLooksTheSame(fixture, Assert.Single(await RenderAsync(output, ct)), sameSize: false);
        }
        else if (engine == "imaging")
        {
            using var a = new MagickImage(input);
            using var b = new MagickImage(output);
            Assert.Equal(tool == "resize" ? (a.Width / 2, a.Height / 2) : (a.Width, a.Height), (b.Width, b.Height));
            AssertLooksTheSame(input, output, sameSize: false);
        }
        else
        {
            var (inDuration, inSize, _) = await ProbeAsync(input, ct);
            var (outDuration, outSize, _) = await ProbeAsync(output, ct);
            Assert.InRange(outDuration, inDuration - 0.1, inDuration + 0.1);
            Assert.Equal(tool == "resize" ? "160x120" : inSize, outSize);
        }
    }

    public static TheoryData<string, string, string> InstantRows()
    {
        var rows = new TheoryData<string, string, string>();
        foreach (var r in Matrix.Rows.Where(r => r.Tool is "rotate" or "strip-metadata" or "ocr" or "extract-audio" or "mute" or "make-gif"))
        {
            rows.Add(r.In, r.Tool, r.Engine);
        }

        return rows;
    }

    // IT-3 with each tool's wedge default. Rotate turns a quarter right (width and height swap); strip info removes an Artist /
    // title / Author planted first; OCR reads sample.pdf's "Morph sample page 1", for images from a rendered page; extract audio
    // copies the sound into a matching audio file; mute leaves no sound; make GIF is the convert path.
    [Theory]
    [MemberData(nameof(InstantRows))]
    public async Task Instant_tool_row_runs(string from, string tool, string engine)
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(Fixtures, $"sample.{from}");
        Assert.True(File.Exists(fixture), $"{fixture} is missing. Run: dotnet run build/gen-fixtures.cs");
        if (engine == "media")
        {
            Assert.SkipUnless(_ffmpeg.IsAvailable, "ffmpeg not fetched: run ./build/fetch-binaries.ps1");
            Assert.SkipWhen(tool == "rotate" && from != "webm" && await new EncoderProbe(_ffmpeg, _dir).H264Async(ct) is null, "no H.264 encoder on this machine");
        }

        Assert.SkipWhen(tool == "ocr" && WinOcr.TryCreateFromUserProfileLanguages() is null, "no Windows OCR language installed");
        Assert.SkipWhen(tool == "ocr" && from == "heic", "Magick can't write a HEIC with text to read");

        var input = Path.Combine(_dir, Path.GetFileName(fixture));
        if (tool == "ocr" && from != "pdf")
        {
            var pdf = Path.Combine(_dir, "page.pdf");
            File.Copy(Path.Combine(Fixtures, "sample.pdf"), pdf);
            using var page = new MagickImage((await RenderAsync(pdf, ct))[0]);
            await page.WriteAsync(input, ct);
        }
        else if (tool == "strip-metadata")
        {
            await PlantSecretAsync(fixture, input, from, engine, ct);
        }
        else
        {
            File.Copy(fixture, input);
        }

        var output = Assert.Single((await RunAsync(tool, engine, [input], ct)).Outputs);
        switch (tool)
        {
            case "ocr":
                Assert.Equal(Path.Combine(_dir, "sample.txt"), output);
                Assert.Contains("sample page 1", await File.ReadAllTextAsync(output, ct), StringComparison.OrdinalIgnoreCase);
                break;
            case "rotate" when engine == "imaging":
                using (var a = new MagickImage(input))
                using (var b = new MagickImage(output))
                {
                    Assert.Equal((a.Height, a.Width), (b.Width, b.Height));
                    a.Rotate(90);
                    var distance = BitOperations.PopCount(DHash(a) ^ DHash(b));
                    Assert.True(distance <= 6, $"dHash distance {distance}");
                }

                break;
            case "rotate":
                Assert.EndsWith("-rotated", Path.GetFileNameWithoutExtension(output), StringComparison.Ordinal);
                Assert.Equal("240x320", (await ProbeAsync(output, ct)).Size);
                break;
            case "strip-metadata":
                Assert.Equal(from, (await FileSniffer.Instance.DetectAsync(output, ct)).Id);
                Assert.False(await HasSecretAsync(output, engine, ct), "the planted metadata is still there");
                break;
            case "extract-audio":
                Assert.Equal(FileKind.Audio, (await FileSniffer.Instance.DetectAsync(output, ct)).Kind);
                Assert.Equal(from switch { "mov" or "avi" => ".wav", "mkv" => ".flac", "webm" => ".ogg", _ => ".m4a" }, Path.GetExtension(output));
                Assert.InRange((await ProbeAsync(output, ct)).Duration, 0.9, 1.1);
                break;
            case "mute":
                Assert.Equal(from, (await FileSniffer.Instance.DetectAsync(output, ct)).Id);
                Assert.Single((await ProbeAsync(output, ct)).Codecs);
                break;
            default: // make-gif
                Assert.Equal("gif", (await FileSniffer.Instance.DetectAsync(output, ct)).Id);
                break;
        }
    }

    // A copy of the fixture with an author/title that strip info has to remove.
    private async Task PlantSecretAsync(string fixture, string input, string from, string engine, CancellationToken ct)
    {
        if (engine == "imaging")
        {
            using var image = new MagickImage(fixture);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Secret");
            image.SetProfile(exif);
            image.Comment = "Secret"; // Magick writes no EXIF into a TIFF, but it does write the comment
            await image.WriteAsync(input, ct);
        }
        else if (engine == "pdf")
        {
            using var document = SharpReader.Open(fixture, PdfDocumentOpenMode.Modify);
            document.Info.Author = "Secret";
            document.Save(input);
        }
        else
        {
            var (exit, _, stderr) = await Ffmpeg.RunAsync(_ffmpeg.FfmpegPath,
                ["-v", "error", "-i", fixture, "-map", "0", "-c", "copy", "-metadata", "title=Secret", "-f", from switch { "mkv" => "matroska", "wmv" => "asf", "m4a" => "ipod", _ => from }, input],
                null, ct);
            Assert.True(exit == 0, stderr);
        }

        Assert.True(await HasSecretAsync(input, engine, ct), $"couldn't plant metadata in {from}");
    }

    private async Task<bool> HasSecretAsync(string path, string engine, CancellationToken ct)
    {
        if (engine == "imaging")
        {
            using var image = new MagickImage(path);
            return image.GetExifProfile()?.GetValue(ExifTag.Artist)?.Value == "Secret" || image.Comment == "Secret";
        }

        if (engine == "pdf")
        {
            using var document = SharpReader.Open(path, PdfDocumentOpenMode.Import);
            return document.Info.Author == "Secret";
        }

        var (exit, stdout, stderr) = await Ffmpeg.RunAsync(_ffmpeg.FfprobePath, ["-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "json", path], null, ct);
        Assert.True(exit == 0, stderr);
        return stdout.Contains("Secret", StringComparison.Ordinal);
    }

    public static TheoryData<string, string> CropRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var r in Matrix.Rows.Where(r => r.Tool == "crop"))
        {
            rows.Add(r.In, r.Engine);
        }

        return rows;
    }

    // ED-1: the editor's preview gives the file's own size (pixels; PDF points) and fits the size asked for; crop keeps exactly
    // the box (images: the fixture's red rectangle at 10,10 60×50; PDF: 60×50 pt, which renders at 150 DPI as 125×104).
    [Theory]
    [MemberData(nameof(CropRows))]
    public async Task Crop_row_previews_and_crops(string from, string engine)
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(Fixtures, $"sample.{from}");
        Assert.True(File.Exists(fixture), $"{fixture} is missing. Run: dotnet run build/gen-fixtures.cs");
        if (engine == "media")
        {
            Assert.SkipUnless(_ffmpeg.IsAvailable, "ffmpeg not fetched: run ./build/fetch-binaries.ps1");
            Assert.SkipWhen(from != "webm" && await new EncoderProbe(_ffmpeg, _dir).H264Async(ct) is null, "no H.264 encoder on this machine");
        }

        var input = Path.Combine(_dir, Path.GetFileName(fixture));
        File.Copy(fixture, input);

        var previewPath = Assert.Single((await RunAsync(PreviewFile.ToolId, engine, [input], ct, new() { ["maxEdge"] = 64 })).Outputs);
        var preview = await PreviewFile.ReadAsync(previewPath, ct);
        Assert.Equal(engine switch { "pdf" => (216.0, 144.0), "media" => (320.0, 240.0), _ => (160.0, 120.0) }, (preview.SourceWidth, preview.SourceHeight));
        Assert.Equal(64, Math.Max(preview.Width, preview.Height));
        File.Delete(previewPath);

        var output = Assert.Single((await RunAsync("crop", engine, [input], ct, new() { ["x"] = 10, ["y"] = 10, ["w"] = 60, ["h"] = 50 })).Outputs);
        Assert.Equal(Path.GetFileNameWithoutExtension(input) + "-cropped", Path.GetFileNameWithoutExtension(output));
        switch (engine)
        {
            case "imaging":
                using (var image = new MagickImage(output))
                {
                    Assert.Equal((60u, 50u), (image.Width, image.Height));
                    var centre = image.GetPixels().GetPixel(30, 25)!.ToColor()!;
                    Assert.True(centre.R > 200 && centre.G < 60 && centre.B < 60, $"centre is {centre}, not red");
                }

                break;
            case "media":
                Assert.Equal("60x50", (await ProbeAsync(output, ct)).Size);
                break;
            default:
                foreach (var page in await RenderAsync(output, ct))
                {
                    using var image = new MagickImage(page);
                    Assert.Equal((125u, 104u), (image.Width, image.Height));
                }

                break;
        }
    }

    [Fact]
    public async Task Crop_of_the_whole_picture_is_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = Path.Combine(_dir, "sample.png");
        File.Copy(Path.Combine(Fixtures, "sample.png"), input);

        var result = await WorkerClient.RunAsync(WorkerPath, new JobRequest(Guid.NewGuid(), "crop", "imaging", [input], _dir,
            new() { ["x"] = 0, ["y"] = 0, ["w"] = 160, ["h"] = 120 }), null, ct);

        Assert.Equal((JobStatus.Failed, "sample.png is already that size."), (result.Status, result.UserMessage));
        Assert.Equal([input], Directory.GetFiles(_dir));
    }

    public static TheoryData<string, string> AnnotateRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var r in Matrix.Rows.Where(r => r.Tool == "annotate"))
        {
            rows.Add(r.In, r.Engine);
        }

        return rows;
    }

    // ED-2: every kind of mark is drawn where the editor put it, in its colour (green: nothing in the fixtures is green). Pictures:
    // a box edge at x 20, text near 75,62, a pen line through 40,20 and pixelate over the top of the blue circle; PDF (pixelate isn't offered): the same
    // box on both pages (page 5 doesn't exist and is skipped), rendered at 150 DPI.
    [Theory]
    [MemberData(nameof(AnnotateRows))]
    public async Task Annotate_row_draws_the_marks(string from, string engine)
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(Fixtures, $"sample.{from}");
        Assert.True(File.Exists(fixture), $"{fixture} is missing. Run: dotnet run build/gen-fixtures.cs");
        var input = Path.Combine(_dir, Path.GetFileName(fixture));
        File.Copy(fixture, input);
        var pdf = engine == "pdf";
        const string Green = "#2FB344";
        List<AnnotationShape> shapes =
        [
            new("rect", [(20, 70), (60, 100)], Green, 6),
            new("arrow", [(30, 110), (60, 80)], Green, 2),
            new("highlight", [(100, 10), (120, 12), (140, 10)], "#FFD60A", 3),
            new("text", [(75, 62)], Green, 4, "Hi"),
            new("pen", [(10, 30), (40, 20), (70, 30)], Green, 6),
        ];
        if (pdf)
        {
            shapes.Add(new("rect", [(20, 70), (60, 100)], Green, 6, Page: 2));
            shapes.Add(new("rect", [(20, 70), (60, 100)], Green, 6, Page: 5));
            var second = await PreviewFile.ReadAsync(Assert.Single((await RunAsync(PreviewFile.ToolId, engine, [input], ct, new() { ["maxEdge"] = 64, ["page"] = 2 })).Outputs), ct);
            Assert.Equal(2, second.Pages);
        }
        else
        {
            shapes.Add(new("pixelate", [(90, 40), (140, 100)], Green, 1));
        }

        var output = Assert.Single((await RunAsync("annotate", engine, [input], ct, Annotations.ToParameters(shapes))).Outputs);
        Assert.Equal(Path.GetFileNameWithoutExtension(input) + "-annotated", Path.GetFileNameWithoutExtension(output));
        static bool IsGreen(IMagickColor<byte> c) => c.G > 140 && c.R < 110 && c.B < 120;
        var images = pdf ? (await RenderAsync(output, ct)).Select(p => new MagickImage(p)).ToList() : [new MagickImage(output)];
        try
        {
            Assert.Equal(pdf ? 2 : 1, images.Count);
            var scale = pdf ? 150.0 / 72 : 1;
            foreach (var image in images)
            {
                var pixels = image.GetPixels();
                Assert.True(IsGreen(pixels.GetPixel((int)(20 * scale), (int)(85 * scale))!.ToColor()!), $"no box edge on {from}");
            }

            var first = images[0].GetPixels();
            var text = 0;
            for (var x = (int)(75 * scale); x < (int)(100 * scale); x++)
            {
                for (var y = (int)(62 * scale); y < (int)(90 * scale); y++)
                {
                    text += IsGreen(first.GetPixel(x, y)!.ToColor()!) ? 1 : 0;
                }
            }

            Assert.True(text > 10, $"no text on {from}");
            Assert.True(IsGreen(first.GetPixel((int)(40 * scale), (int)(20 * scale))!.ToColor()!), $"no pen line on {from}");
            if (from is "png" or "bmp" or "tiff")
            {
                // The circle's top edge (white above y 50, blue below) falls inside one pixel cell.
                Assert.Equal(first.GetPixel(115, 47)!.ToColor()!.ToString(), first.GetPixel(115, 51)!.ToColor()!.ToString());
            }
        }
        finally
        {
            images.ForEach(i => i.Dispose());
        }
    }

    public static TheoryData<string, string> PasswordRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var r in Matrix.Rows.Where(r => r.Tool == "protect"))
        {
            Assert.Contains(Matrix.Rows, u => u.Tool == "unlock" && u.In == r.In && u.Engine == r.Engine);
            rows.Add(r.In, r.Engine);
        }

        return rows;
    }

    // IT-5: protect locks a copy, a wrong password fails plainly, unlock with the right one gives the content back.
    [Theory]
    [MemberData(nameof(PasswordRows))]
    public async Task Protect_and_unlock_row_round_trips(string from, string engine)
    {
        var ct = TestContext.Current.CancellationToken;
        var input = Path.Combine(_dir, $"sample.{from}");
        File.Copy(Path.Combine(Fixtures, $"sample.{from}"), input);
        const string Password = "Pässwörd 1";

        var locked = Assert.Single((await RunAsync("protect", engine, [input], ct, new() { ["password"] = Password })).Outputs);
        Assert.Equal(Path.Combine(_dir, $"sample-protected.{from}"), locked);
        Assert.True(IsLocked(locked), "the copy opens without the password");
        Assert.False(IsLocked(input), "the original changed");

        Assert.Equal($"That password doesn't open sample-protected.{from}.", (await TryRunAsync("unlock", engine, [locked], "wrong", ct)).UserMessage);
        Assert.Equal($"sample.{from} isn't password-protected.", (await TryRunAsync("unlock", engine, [input], Password, ct)).UserMessage);
        Assert.Equal($"sample-protected.{from} is already password-protected.", (await TryRunAsync("protect", engine, [locked], Password, ct)).UserMessage);

        var unlocked = Assert.Single((await RunAsync("unlock", engine, [locked], ct, new() { ["password"] = Password })).Outputs);
        Assert.Equal(Path.Combine(_dir, $"sample-protected-unlocked.{from}"), unlocked);
        Assert.False(IsLocked(unlocked));
        Assert.Equal(Contents(input), Contents(unlocked));

        // A ZIP is ZipCrypto by default (File Explorer can extract it), AES-256 when asked for.
        if (from == "zip")
        {
            Assert.Equal(0, AesKeySize(locked));
            var strong = Assert.Single((await RunAsync("protect", engine, [input], ct, new() { ["password"] = Password, ["strong"] = true })).Outputs);
            Assert.Equal(256, AesKeySize(strong));
            var back = Assert.Single((await RunAsync("unlock", engine, [strong], ct, new() { ["password"] = Password })).Outputs);
            Assert.Equal(Contents(input), Contents(back));
        }
    }

    private static int AesKeySize(string zip)
    {
        using var file = new ICSharpCode.SharpZipLib.Zip.ZipFile(zip);
        return file.Cast<ICSharpCode.SharpZipLib.Zip.ZipEntry>().Where(e => e.IsFile).Select(e => e.AESKeySize).Distinct().Single();
    }

    // Other apps' files: a PDF whose password only opens it (the owner password differs), and a ZIP with old ZipCrypto.
    [Fact]
    public async Task Unlock_takes_an_open_only_pdf_password_and_old_zip_encryption()
    {
        var ct = TestContext.Current.CancellationToken;
        var pdf = Path.Combine(_dir, "open-only.pdf");
        using (var document = SharpReader.Open(Path.Combine(Fixtures, "sample.pdf"), PdfDocumentOpenMode.Modify))
        {
            document.SecuritySettings.UserPassword = "user";
            document.SecuritySettings.OwnerPassword = "owner";
            document.Save(pdf);
        }

        var zip = Path.Combine(_dir, "old.zip");
        using (var output = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(File.Create(zip)) { Password = "user" })
        {
            output.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry("notes/a.txt"));
            output.Write("hello"u8);
        }

        var outputs = new List<string>();
        foreach (var (file, engine) in new[] { (pdf, "pdf"), (zip, "archive") })
        {
            outputs.Add(Assert.Single((await RunAsync("unlock", engine, [file], ct, new() { ["password"] = "user" })).Outputs));
        }

        Assert.All(outputs, o => Assert.False(IsLocked(o)));
        Assert.Equal(Contents(Path.Combine(Fixtures, "sample.pdf")), Contents(outputs[0]));
        Assert.Equal(["notes/a.txt:hello"], Contents(outputs[1]));
    }

    private async Task<JobResult> TryRunAsync(string tool, string engine, string[] inputs, string password, CancellationToken ct)
    {
        var result = await WorkerClient.RunAsync(WorkerPath, new JobRequest(Guid.NewGuid(), tool, engine, inputs, _dir, new() { ["password"] = password }),
            new SyncProgress<JobProgress>(_ => { }), ct);
        Assert.Equal(JobStatus.Failed, result.Status);
        return result;
    }

    private static bool IsLocked(string path)
    {
        if (path.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(path);
            return zip.Cast<ICSharpCode.SharpZipLib.Zip.ZipEntry>().Any(e => e.IsCrypted);
        }

        var asked = false;
        try
        {
            SharpReader.Open(path, PdfDocumentOpenMode.Import, args =>
            {
                asked = true;
                args.Abort = true;
            })?.Dispose();
        }
        catch when (asked)
        {
            // the aborted prompt
        }

        return asked;
    }

    // What a reader sees: each page's text for a PDF, "name:text" per file for a ZIP.
    private static List<string> Contents(string path)
    {
        if (path.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = ZipFile.OpenRead(path);
            return [.. zip.Entries.Select(e => e.FullName + ":" + (e.FullName.EndsWith('/') ? "" : new StreamReader(e.Open()).ReadToEnd()))];
        }

        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        return [.. document.GetPages().Select(p => p.Text)];
    }

    [Fact]
    public async Task Merge_joins_videos_and_sounds_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipUnless(_ffmpeg.IsAvailable, "ffmpeg not fetched: run ./build/fetch-binaries.ps1");
        Assert.SkipWhen(await new EncoderProbe(_ffmpeg, _dir).H264Async(ct) is null, "no H.264 encoder on this machine");
        var files = new[] { "mov", "webm", "mp3", "wav" }.Select(f => Path.Combine(_dir, $"sample.{f}")).ToArray();
        foreach (var file in files)
        {
            File.Copy(Path.Combine(Fixtures, Path.GetFileName(file)), file);
        }

        // Different codecs and containers; the first file sets the frame and the format.
        var video = Assert.Single((await RunAsync("merge", "media", files[..2], ct)).Outputs);
        Assert.Equal(Path.Combine(_dir, "sample-merged.mp4"), video);
        var (duration, size, codecs) = await ProbeAsync(video, ct);
        Assert.InRange(duration, 1.9, 2.1);
        Assert.Equal("320x240", size);
        Assert.Equal(["h264", "aac"], codecs);

        var sound = Assert.Single((await RunAsync("merge", "media", files[2..], ct)).Outputs);
        Assert.Equal(Path.Combine(_dir, "sample-merged.mp3"), sound);
        Assert.InRange((await ProbeAsync(sound, ct)).Duration, 1.9, 2.1);
    }

    [Fact]
    public async Task Merge_puts_pdfs_and_images_into_one_pdf_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var inputs = new[] { "png", "pdf", "jpg" }.Select(f => Path.Combine(_dir, $"sample.{f}")).ToArray();
        foreach (var input in inputs)
        {
            Assert.True(File.Exists(Path.Combine(Fixtures, Path.GetFileName(input))), "Run: dotnet run build/gen-fixtures.cs");
            File.Copy(Path.Combine(Fixtures, Path.GetFileName(input)), input);
        }

        var merged = Assert.Single((await RunAsync("merge", "pdf", inputs, ct)).Outputs);
        Assert.Equal(Path.Combine(_dir, "sample-merged.pdf"), merged);

        var pages = await RenderAsync(merged, ct);
        Assert.Equal(4, pages.Count);
        AssertLooksTheSame(inputs[0], pages[0], sameSize: false);
        await AssertPdfPagesAsync(pages.Skip(1).Take(2).ToList(), "png", ct, checkNames: false);
        AssertLooksTheSame(inputs[2], pages[3], sameSize: false);
    }

    private async Task<JobResult> RunAsync(string tool, string engine, string[] inputs, CancellationToken ct, JsonObject? parameters = null)
    {
        var result = await WorkerClient.RunAsync(WorkerPath,
            new JobRequest(Guid.NewGuid(), tool, engine, inputs, _dir, parameters ?? Matrix.Tool(tool)?.Defaults?.DeepClone().AsObject()),
            new SyncProgress<JobProgress>(_ => { }), ct);
        Assert.True(result.Status == JobStatus.Succeeded, $"{result.UserMessage} {result.Details}");
        return result;
    }

    // Each page of a PDF as a PNG, through the PDFium path that golden-tests itself above.
    private async Task<IReadOnlyList<string>> RenderAsync(string pdf, CancellationToken ct) =>
        (await RunAsync("convert.png", "pdf", [pdf], ct)).Outputs;

    // sample.pdf: two 216×144 pt pages, each with a red box (10,80)-(70,130) pt and "Morph sample page N".
    private async Task AssertPdfPagesAsync(IReadOnlyList<string> outputs, string to, CancellationToken ct, bool checkNames = true)
    {
        if (to == "txt")
        {
            var text = await File.ReadAllTextAsync(Assert.Single(outputs), ct);
            Assert.Contains("Morph sample page 1", text, StringComparison.Ordinal);
            Assert.Contains("Morph sample page 2", text, StringComparison.Ordinal);
            return;
        }

        if (checkNames)
        {
            Assert.Equal([Path.Combine(_dir, $"sample-1.{to}"), Path.Combine(_dir, $"sample-2.{to}")], outputs);
        }

        Assert.Equal(2, outputs.Count);
        foreach (var page in outputs)
        {
            Assert.Equal(to, (await FileSniffer.Instance.DetectAsync(page, ct)).Id);
            using var image = new MagickImage(page);
            Assert.Equal((450u, 300u), (image.Width, image.Height)); // 3×2 inches at 150 DPI
            var box = image.GetPixels().GetPixel(80, 80)!.ToColor()!;
            Assert.True(box.R > 200 && box.G < 60 && box.B < 60, $"expected red at (80,80), got {box}");
        }
    }

    // sameSize false: a PDF page rendered back to pixels, so only the aspect ratio has to match.
    private static void AssertLooksTheSame(string input, string output, bool sameSize = true)
    {
        using var a = new MagickImage(input);
        using var b = new MagickImage(output);
        if (sameSize)
        {
            Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        }
        else
        {
            Assert.Equal((double)a.Width / a.Height, (double)b.Width / b.Height, 0.02);
        }

        var distance = BitOperations.PopCount(DHash(a) ^ DHash(b));
        Assert.True(distance <= 6, $"dHash distance {distance}");
    }

    // 9×8 greyscale; each bit says whether a pixel is brighter than its right neighbour.
    private static ulong DHash(MagickImage image)
    {
        using var small = (MagickImage)image.Clone();
        small.Alpha(AlphaOption.Remove);
        small.Grayscale();
        small.Resize(new MagickGeometry(9, 8) { IgnoreAspectRatio = true });
        var pixels = small.GetPixels();
        ulong hash = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                hash = (hash << 1) | (pixels.GetPixel(x, y)!.GetChannel(0) > pixels.GetPixel(x + 1, y)!.GetChannel(0) ? 1UL : 0);
            }
        }

        return hash;
    }

    // gen-fixtures writes the same grid, cues and files in each format; these are written out by hand, not read from a fixture.
    private static readonly List<List<string>> SampleGrid =
    [
        ["Name", "Amount", "Date", "Note"],
        ["Apple", "1.5", "2023-03-15", "red, sweet"],
        ["Banana", "20", "2024-01-01", "says \"hi\""],
        ["Café", "-3", "", "line1\nline2"],
    ];

    private static readonly List<Cue> SampleCues =
    [
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), "Hello <i>world</i>"),
        new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "Fish & chips\nsecond line"),
    ];

    private static readonly Dictionary<string, string> SampleFiles = new() { ["readme.txt"] = "hello", ["docs/notes.md"] = "# notes\n" };

    // Read with the framework's own readers, not the engine: file name → text, folders left out.
    private static async Task<Dictionary<string, string>> ArchivedFilesAsync(string path, CancellationToken ct)
    {
        var files = new Dictionary<string, string>();
        if (path.EndsWith(".zip", StringComparison.Ordinal))
        {
            await using var zip = await ZipFile.OpenReadAsync(path, ct);
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                using var reader = new StreamReader(await entry.OpenAsync(ct));
                files[entry.FullName] = await reader.ReadToEndAsync(ct);
            }

            return files;
        }

        await using var file = File.OpenRead(path);
        await using var stream = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
        await using var tar = new TarReader(stream);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType == TarEntryType.RegularFile)
            {
                using var reader = new StreamReader(entry.DataStream!);
                files[entry.Name] = await reader.ReadToEndAsync(ct);
            }
        }

        return files;
    }

    // The sample documents hold the same content (gen-fixtures); TXT keeps only the words.
    private async Task AssertSameDocumentAsync(string input, string output, string from, string to, CancellationToken ct)
    {
        var actual = to == "pdf" ? [] : await DocumentEngine.ReadAsync(output, ct);
        if (from == "pdf")
        {
            // sample.pdf is two lines of text; the sample document's round trip through PDF is in DocumentEngineTests.
            Assert.Equal(["Morph sample page 1", "Morph sample page 2"], actual.Select(b => Runs.Plain(((Paragraph)b).Runs)));
            return;
        }

        if (from == "txt" || to is "txt" or "pdf")
        {
            // A PDF keeps the words; read them back through the PDFium PDF -> TXT path that golden-tests itself.
            var text = to == "pdf" ? await File.ReadAllTextAsync(Assert.Single((await RunAsync("convert.txt", "pdf", [output], ct)).Outputs), ct) : Runs.Plain(actual);
            foreach (var phrase in new[] { "Morph sample", "Plain text with bold", "First item", "Nested item", "Two", "Cell B2", "var x = 1;" })
            {
                Assert.Contains(phrase, text, StringComparison.Ordinal);
            }

            return;
        }

        Assert.Equal(Shape(await DocumentEngine.ReadAsync(input, ct)), Shape(actual));
        Assert.Equal(SampleShape.ReplaceLineEndings("\n"), Shape(actual));
    }

    private const string SampleShape = """
        H1 Morph sample
        P Plain text with **bold**, *italic*, `code` and a [link](https://example.com/).
        UL[P First item; P Second item UL[P Nested item]]
        OL[P One; P Two]
        T Name | Value / Cell A2 | Cell B2
        C var x = 1;
        """;

    private static string Shape(IEnumerable<Block> blocks) => string.Join('\n', blocks.Select(Shape));

    private static string Shape(Block block) => block switch
    {
        Heading h => $"H{h.Level} {Shape(h.Runs)}",
        Paragraph p => "P " + Shape(p.Runs),
        ListBlock l => (l.Ordered ? "OL[" : "UL[") + string.Join("; ", l.Items.Select(i => string.Join(" ", i.Select(Shape)))) + "]",
        Table t => "T " + string.Join(" / ", t.Rows.Select(r => string.Join(" | ", r.Select(Runs.Plain)))),
        CodeBlock c => "C " + c.Text.Trim(),
        _ => block.ToString(),
    };

    private static string Shape(IEnumerable<Run> runs) => string.Concat(runs.Select(r =>
    {
        var s = r.Code ? $"`{r.Text}`" : r.Text;
        s = r.Italic ? $"*{s}*" : s;
        s = r.Bold ? $"**{s}**" : s;
        return r.Link is { } link ? $"[{s}]({link})" : s;
    }));

    private async Task AssertSameMediaAsync(string input, string output, string from, string to, CancellationToken ct)
    {
        var (inDuration, inSize, inCodecs) = await ProbeAsync(input, ct);
        var (outDuration, outSize, codecs) = await ProbeAsync(output, ct);

        // MKV holds every fixture's codecs except WMV's, so it copies them; WMV gets H.264 + AAC.
        Assert.Equal(to != "mkv" ? Codecs[to] : from == "wmv" ? ["h264", "aac"] : inCodecs, codecs);
        // ±0.1 s: one GIF frame at 15 fps is 0.067 s, and lossy audio adds up to a codec frame of padding.
        Assert.InRange(outDuration, inDuration - 0.1, inDuration + 0.1);
        if (inSize is not null && outSize is not null)
        {
            Assert.Equal(inSize, outSize);
        }
    }

    private async Task<(double Duration, string? Size, string[] Codecs)> ProbeAsync(string path, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await Ffmpeg.RunAsync(_ffmpeg.FfprobePath,
            ["-v", "error", "-show_entries", "stream=codec_name,width,height,duration:format=duration", "-of", "csv=p=0", path], null, ct);
        Assert.True(exit == 0, stderr);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var streams = lines[..^1].Select(l => l.Split(',')).ToList();
        var size = streams.Where(s => s.Length >= 3 && s[1].Length > 0).Select(s => $"{s[1]}x{s[2]}").FirstOrDefault();
        // The shortest stream, not the container: ASF (WMV) adds its preroll to the container duration. GIF streams have none.
        var durations = streams.Select(s => double.TryParse(s[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
            .Where(d => !double.IsNaN(d)).ToList();
        var duration = durations.Count > 0 ? durations.Min() : double.Parse(lines[^1], CultureInfo.InvariantCulture);
        return (duration, size, [.. streams.Select(s => s[0])]);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Morph.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Morph.slnx not found above the test binaries.");
    }
}
