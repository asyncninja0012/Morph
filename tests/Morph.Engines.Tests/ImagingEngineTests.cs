using ImageMagick;

using Morph.Core.Capabilities;
using Morph.Core.Jobs;
using Morph.Engines.Imaging;

namespace Morph.Engines.Tests;

public sealed class ImagingEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-imaging-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Jpg_to_png_writes_a_png_next_to_the_original_and_leaves_it_untouched()
    {
        var input = Path.Combine(_dir, "photo.jpg");
        using (var image = new MagickImage(MagickColors.Red, 40, 30))
        {
            await image.WriteAsync(input, MagickFormat.Jpeg, TestContext.Current.CancellationToken);
        }

        var before = await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken);
        var namer = new OutputNamer(_dir);

        await new ImagingEngine().RunAsync(Job("convert.png", input), namer, new Progress<JobProgress>(), TestContext.Current.CancellationToken);
        var outputs = namer.CommitAll();

        Assert.Equal([Path.Combine(_dir, "photo.png")], outputs);
        using var result = new MagickImage(outputs[0]);
        Assert.Equal(MagickFormat.Png, result.Format);
        Assert.Equal((40u, 30u), (result.Width, result.Height));
        Assert.Equal(before, await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken));
        Assert.Equal(2, Directory.GetFiles(_dir).Length); // no temp file left
    }

    [Fact]
    public async Task Non_image_input_fails_with_a_user_message()
    {
        var input = Path.Combine(_dir, "notes.jpg");
        await File.WriteAllTextAsync(input, "push graphic-context viewbox 0 0 1 1", TestContext.Current.CancellationToken); // MVG script

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            new ImagingEngine().RunAsync(Job("convert.png", input), new OutputNamer(_dir), new Progress<JobProgress>(), TestContext.Current.CancellationToken));

        Assert.Contains("notes.jpg", ex.UserMessage, StringComparison.Ordinal);
    }

    // Every format pair, HEIC included, runs through the Worker in GoldenTests.
    [Fact]
    public async Task Probe_answers_without_throwing() =>
        await SystemCodecs.CanEncodeHeicAsync();

    private JobRequest Job(string tool, string input) => new(Guid.NewGuid(), tool, "imaging", [input], _dir);
}
