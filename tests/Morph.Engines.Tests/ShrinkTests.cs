using System.Text.Json.Nodes;

using ImageMagick;

using Morph.Core.Jobs;
using Morph.Engines.Imaging;
using Morph.Engines.Media;
using Morph.Engines.Pdf;

namespace Morph.Engines.Tests;

// Each compress/resize row runs on the samples in GoldenTests; these cover the presets and the edges.
public sealed class ShrinkTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures");
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-shrink-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(4000, 3000, """{"scale":0.5}""", 2000, 1500)]
    [InlineData(4000, 3000, """{"long":1920,"short":1080}""", 1440, 1080)] // the short edge limits a 4:3 photo
    [InlineData(1080, 1920, """{"long":1920,"short":1080}""", null, null)] // portrait 1080p is already 1080p
    [InlineData(2160, 3840, """{"long":1280,"short":720}""", 720, 1280)]
    [InlineData(5000, 1000, """{"long":2048}""", 2048, 410)]
    [InlineData(1000, 800, """{"long":2048}""", null, null)] // never upscales
    public void Resize_presets_keep_the_aspect_ratio_and_never_upscale(int width, int height, string parameters, int? toWidth, int? toHeight)
    {
        var size = Presets.Resize(width, height, JsonNode.Parse(parameters)!.AsObject());

        Assert.Equal(toWidth is null ? null : (toWidth.Value, toHeight!.Value), size);
    }

    [Fact]
    public void Under_10_mb_means_decimal_megabytes() =>
        Assert.Equal(10_000_000, Presets.TargetBytes(new JsonObject { ["targetMb"] = 10 }));

    [Fact]
    public async Task Resize_skips_a_small_image_and_fails_only_when_all_are_small()
    {
        var small = await ImageAsync("small.png", 100, 100);
        var big = await ImageAsync("big.png", 3000, 2000);
        var parameters = new JsonObject { ["long"] = 2048 };

        var output = Assert.Single(await ConvertAsync(new ImagingEngine(), "resize", "imaging", [small, big], parameters));
        var ex = await Assert.ThrowsAsync<EngineException>(() => ConvertAsync(new ImagingEngine(), "resize", "imaging", [small], parameters));

        Assert.Equal("big-resized.png", Path.GetFileName(output));
        using var image = new MagickImage(output);
        Assert.Equal((2048u, 1365u), (image.Width, image.Height));
        Assert.Equal("small.png is already that small.", ex.UserMessage);
    }

    [Fact]
    public async Task Video_fits_under_the_size_limit()
    {
        var ffmpeg = Ffmpeg.Bundled;
        Assert.SkipUnless(ffmpeg.IsAvailable, "ffmpeg not fetched: run ./build/fetch-binaries.ps1");
        Assert.SkipWhen(await new EncoderProbe(ffmpeg, _dir).H264Async(TestContext.Current.CancellationToken) is null, "no H.264 encoder");
        var input = Path.Combine(_dir, "clip.mov");
        File.Copy(Path.Combine(Fixtures, "sample.mov"), input); // 174 KB, 1 s

        var output = Assert.Single(await ConvertAsync(new MediaEngine(), "compress", "media", [input], new JsonObject { ["targetMb"] = 0.08 }));
        var tooSmall = await Assert.ThrowsAsync<EngineException>(() =>
            ConvertAsync(new MediaEngine(), "compress", "media", [input], new JsonObject { ["targetMb"] = 0.01 }));
        var alreadyUnder = await Assert.ThrowsAsync<EngineException>(() =>
            ConvertAsync(new MediaEngine(), "compress", "media", [input], new JsonObject { ["targetMb"] = 10 }));

        Assert.Equal("clip-compressed.mp4", Path.GetFileName(output));
        Assert.InRange(new FileInfo(output).Length, 1, 80_000);
        Assert.StartsWith("clip.mov is too long to fit under 0.01 MB", tooSmall.UserMessage, StringComparison.Ordinal);
        Assert.Equal("clip.mov is already under 10 MB.", alreadyUnder.UserMessage);
    }

    [Fact]
    public async Task Pdf_photos_shrink_until_it_fits()
    {
        // A noisy 2400×1800 photo at JPEG 95 is big; Balanced caps it at 2000 px and quality 70.
        var photo = Path.Combine(_dir, "photo.jpg");
        using (var image = new MagickImage("plasma:", 2400, 1800))
        {
            image.Quality = 95;
            await image.WriteAsync(photo, MagickFormat.Jpeg, TestContext.Current.CancellationToken);
        }

        var pdf = Assert.Single(await ConvertAsync(new PdfEngine(), "convert.pdf", "pdf", [photo], null));
        var limit = new FileInfo(pdf).Length / 4;

        var output = Assert.Single(await ConvertAsync(new PdfEngine(), "compress", "pdf", [pdf], new JsonObject { ["targetMb"] = limit / 1e6 }));

        Assert.Equal("photo-compressed.pdf", Path.GetFileName(output));
        Assert.InRange(new FileInfo(output).Length, 1, limit);
        Assert.Single(await ConvertAsync(new PdfEngine(), "convert.png", "pdf", [output], null)); // still renders
    }

    private async Task<string> ImageAsync(string name, uint width, uint height)
    {
        var path = Path.Combine(_dir, name);
        using var image = new MagickImage("gradient:red-blue", width, height);
        await image.WriteAsync(path, MagickFormat.Png, TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<IReadOnlyList<string>> ConvertAsync(IEngine engine, string tool, string engineId, string[] inputs, JsonObject? parameters)
    {
        var namer = new OutputNamer(_dir);
        try
        {
            await engine.RunAsync(new JobRequest(Guid.NewGuid(), tool, engineId, inputs, _dir, parameters), namer, new Progress<JobProgress>(),
                TestContext.Current.CancellationToken);
        }
        catch
        {
            namer.AbortAll();
            throw;
        }

        return namer.CommitAll();
    }
}
