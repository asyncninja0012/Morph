using System.Globalization;
using System.Numerics;

using ImageMagick;

using Morph.Core.Capabilities;
using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Engines.Media;

namespace Morph.Engines.Tests;

/// <summary>
/// One case per conversion row in capabilities.json (PLAN §8), so a new row without a fixture fails. Each runs the real
/// Morph.Worker.exe on tests/fixtures/sample.&lt;in&gt; with the tool's defaults, like a drop does, and checks the output:
/// sniffed format; images keep size and look (dHash, Hamming ≤ 6); media keep duration, frame size and get the target codecs.
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
            Assert.SkipWhen(to == "mp4" && await new EncoderProbe(_ffmpeg, _dir).H264Async(ct) is null, "no H.264 encoder on this machine");
        }

        var input = Path.Combine(_dir, Path.GetFileName(fixture));
        File.Copy(fixture, input);
        Assert.Equal(from, (await FileSniffer.Instance.DetectAsync(input, ct)).Id);

        var tool = $"convert.{to}";
        var result = await WorkerClient.RunAsync(WorkerPath,
            new JobRequest(Guid.NewGuid(), tool, engine, [input], _dir, Matrix.Tool(tool)?.Defaults?.DeepClone().AsObject()),
            new SyncProgress<JobProgress>(_ => { }), ct);

        Assert.True(result.Status == JobStatus.Succeeded, $"{result.UserMessage} {result.Details}");
        var output = Assert.Single(result.Outputs);
        Assert.Equal(to, (await FileSniffer.Instance.DetectAsync(output, ct)).Id);
        if (FileFormat.FromExtension(from).Kind == FileKind.Image)
        {
            AssertLooksTheSame(input, output);
        }
        else
        {
            await AssertSameMediaAsync(input, output, to, ct);
        }
    }

    private static void AssertLooksTheSame(string input, string output)
    {
        using var a = new MagickImage(input);
        using var b = new MagickImage(output);
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
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

    private async Task AssertSameMediaAsync(string input, string output, string to, CancellationToken ct)
    {
        var (inDuration, inSize, _) = await ProbeAsync(input, ct);
        var (outDuration, outSize, codecs) = await ProbeAsync(output, ct);

        Assert.Equal(Codecs[to], codecs);
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
            ["-v", "error", "-show_entries", "stream=codec_name,width,height:format=duration", "-of", "csv=p=0", path], null, ct);
        Assert.True(exit == 0, stderr);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var streams = lines[..^1].Select(l => l.Split(',')).ToList();
        var size = streams.Where(s => s.Length >= 3 && s[1].Length > 0).Select(s => $"{s[1]}x{s[2]}").FirstOrDefault();
        return (double.Parse(lines[^1], CultureInfo.InvariantCulture), size, [.. streams.Select(s => s[0])]);
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
