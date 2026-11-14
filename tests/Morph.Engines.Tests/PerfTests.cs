using System.Diagnostics;
using System.Globalization;
using System.Text;

using ImageMagick;

using Morph.Core.Capabilities;
using Morph.Core.Jobs;
using Morph.Engines.Media;

using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Morph.Engines.Tests;

/// <summary>
/// PRD §7 speed targets (PLAN 5.1), timed through the real Morph.Worker.exe like a drop: 12 MP JPG → PNG ≤ 1.5 s,
/// 1-minute 1080p MOV → MP4 ≤ 20 s, 50-page PDF → JPGs ≤ 8 s. Opt-in (<c>MORPH_PERF=1</c>): the targets are for a
/// mid-range laptop, not a shared CI runner. Inputs are generated, so no fixtures are needed.
/// </summary>
[Trait("Category", "Perf")]
public sealed class PerfTests(ITestOutputHelper output) : IDisposable
{
    private static readonly CapabilityMatrix Matrix = CapabilityMatrix.LoadDefault(_ => true);
    private static readonly string WorkerPath = Path.Combine(AppContext.BaseDirectory, "Morph.Worker.exe");
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-perf-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // An output can still be open in a virus scan; it's a temp folder.
        }
    }

    [Fact]
    public async Task Jpg_12mp_to_png()
    {
        SkipUnlessPerf();
        var input = Path.Combine(_dir, "photo.jpg");
        using (var image = new MagickImage("plasma:", 4000, 3000)) // photo-like noise, so PNG compression does real work
        {
            image.Quality = 90;
            await image.WriteAsync(input, MagickFormat.Jpeg, TestContext.Current.CancellationToken);
        }

        await AssertUnderAsync("convert.png", "imaging", input, TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task Mov_1080p_1min_to_mp4()
    {
        SkipUnlessPerf();
        var ct = TestContext.Current.CancellationToken;
        var ffmpeg = Ffmpeg.Bundled;
        Assert.SkipUnless(ffmpeg.IsAvailable, "ffmpeg not fetched: run ./build/fetch-binaries.ps1");
        Assert.SkipWhen(await new EncoderProbe(ffmpeg, _dir).H264Async(ct) is null, "no H.264 encoder on this machine");

        // MPEG-4 Part 2, so the MP4 must be re-encoded (an H.264 MOV would only be stream-copied).
        var input = Path.Combine(_dir, "clip.mov");
        await ffmpeg.EncodeAsync(["-f", "lavfi", "-i", "testsrc2=d=60:s=1920x1080:r=30", "-f", "lavfi", "-i", "sine=d=60",
            "-c:v", "mpeg4", "-q:v", "3", "-c:a", "aac", input], 60, new SyncProgress<JobProgress>(_ => { }), ct);

        await AssertUnderAsync("convert.mp4", "media", input, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Pdf_50_pages_to_jpg()
    {
        SkipUnlessPerf();
        var ct = TestContext.Current.CancellationToken;

        // A text report: Morph's own Markdown → PDF, cut to exactly 50 pages.
        var md = new StringBuilder();
        for (var i = 1; i <= 400; i++)
        {
            md.Append(CultureInfo.InvariantCulture, $"## Section {i}\n\n");
            md.Append(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog, then files the quarterly report. ", 12))).Append("\n\n");
        }

        var mdPath = Path.Combine(_dir, "report.md");
        await File.WriteAllTextAsync(mdPath, md.ToString(), ct);
        var report = Assert.Single((await RunAsync("convert.pdf", "document", mdPath, ct)).Outputs);
        var input = Path.Combine(_dir, "fifty.pdf");
        using (var source = PdfReader.Open(report, PdfDocumentOpenMode.Import))
        using (var fifty = new PdfDocument())
        {
            Assert.True(source.PageCount >= 50, $"report.md made only {source.PageCount} pages");
            for (var p = 0; p < 50; p++)
            {
                fifty.AddPage(source.Pages[p]);
            }

            fifty.Save(input);
        }

        await AssertUnderAsync("convert.jpg", "pdf", input, TimeSpan.FromSeconds(8));
    }

    private static void SkipUnlessPerf() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MORPH_PERF") == "1", "speed targets: set MORPH_PERF=1 on the reference laptop");

    // Wall clock from starting the Worker to its result, as the user waits for it.
    private async Task AssertUnderAsync(string tool, string engine, string input, TimeSpan budget)
    {
        var started = Stopwatch.GetTimestamp();
        await RunAsync(tool, engine, input, TestContext.Current.CancellationToken);
        var took = Stopwatch.GetElapsedTime(started);
        output.WriteLine($"{Path.GetFileName(input)} {tool}: {took.TotalSeconds:F2} s (target {budget.TotalSeconds} s)");
        Assert.True(took <= budget, $"{tool} took {took.TotalSeconds:F2} s, target {budget.TotalSeconds} s");
    }

    private async Task<JobResult> RunAsync(string tool, string engine, string input, CancellationToken ct)
    {
        var result = await WorkerClient.RunAsync(WorkerPath, new JobRequest(Guid.NewGuid(), tool, engine, [input], _dir, Matrix.Tool(tool)?.Defaults?.DeepClone().AsObject()),
            new SyncProgress<JobProgress>(_ => { }), ct);
        Assert.True(result.Status == JobStatus.Succeeded, $"{result.UserMessage} {result.Details}");
        return result;
    }
}
