using System.Globalization;
using System.Numerics;

using ImageMagick;

using Morph.Core.Capabilities;
using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Engines.Documents;
using Morph.Engines.Media;

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

    private async Task<JobResult> RunAsync(string tool, string engine, string[] inputs, CancellationToken ct)
    {
        var result = await WorkerClient.RunAsync(WorkerPath,
            new JobRequest(Guid.NewGuid(), tool, engine, inputs, _dir, Matrix.Tool(tool)?.Defaults?.DeepClone().AsObject()),
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
