using ImageMagick;

using Morph.Core.Jobs;
using Morph.Core.Packs;
using Morph.Engines.Ai;

namespace Morph.Engines.Tests;

/// <summary>
/// Remove background (IT-4) in-process. Needs the BiRefNet-lite model: the installed bg-model pack, or MORPH_BG_MODEL pointing
/// at a model.onnx. The fixture is the gen-fixtures picture: a red box and a blue disc on white.
/// </summary>
public sealed class BackgroundEngineTests : IDisposable
{
    internal static readonly string? Model = Environment.GetEnvironmentVariable("MORPH_BG_MODEL") is { Length: > 0 } path && File.Exists(path)
        ? path : PackStore.Installed(PackStore.DefaultRoot, BackgroundEngine.PackId, "model.onnx");

    private readonly string _dir = Directory.CreateTempSubdirectory("morph-bg-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Cuts_out_the_shapes_and_clears_the_white()
    {
        Assert.SkipWhen(Model is null, "needs the BiRefNet-lite model: the bg-model pack or MORPH_BG_MODEL");
        var ct = TestContext.Current.CancellationToken;
        var input = Path.Combine(_dir, "sample.png");
        File.Copy(Path.Combine(GoldenTests.Fixtures, "sample.png"), input);
        var namer = new OutputNamer(_dir);

        await new BackgroundEngine(Model).RunAsync(new JobRequest(Guid.NewGuid(), "remove-bg", "ai", [input], _dir), namer,
            new SyncProgress<JobProgress>(_ => { }), ct);

        AssertCutOut(Assert.Single(namer.CommitAll()));
    }

    [Fact]
    public async Task Without_the_pack_it_says_so()
    {
        var missing = Path.Combine(_dir, "nothing.onnx");
        var ex = await Assert.ThrowsAsync<EngineException>(() => new BackgroundEngine(missing).RunAsync(
            new JobRequest(Guid.NewGuid(), "remove-bg", "ai", [Path.Combine(GoldenTests.Fixtures, "sample.png")], _dir), new OutputNamer(_dir),
            new SyncProgress<JobProgress>(_ => { }), TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.png"));
        Assert.Equal("Remove background needs Morph's background pack.", ex.UserMessage);
    }

    // sample-nobg.png, 160×120 like the fixture: the white is see-through, the red box and the blue disc stay solid.
    internal static void AssertCutOut(string output)
    {
        Assert.Equal("sample-nobg.png", Path.GetFileName(output));
        using var image = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, image.Format);
        Assert.Equal((160u, 120u), (image.Width, image.Height));
        Assert.True(image.HasAlpha);
        using var pixels = image.GetPixels();
        byte Alpha(int x, int y) => pixels.GetPixel(x, y).GetChannel(image.ChannelCount - 1); // Q8: 0..255
        Assert.True(Alpha(2, 2) < 40, $"corner alpha {Alpha(2, 2)}");
        Assert.True(Alpha(80, 5) < 40, $"top alpha {Alpha(80, 5)}"); // the black line runs into the top-right corner
        Assert.True(Alpha(150, 110) < 40, $"corner alpha {Alpha(150, 110)}");
        Assert.True(Alpha(40, 35) > 215, $"red box alpha {Alpha(40, 35)}");
        Assert.True(Alpha(115, 80) > 215, $"blue disc alpha {Alpha(115, 80)}");
    }
}
