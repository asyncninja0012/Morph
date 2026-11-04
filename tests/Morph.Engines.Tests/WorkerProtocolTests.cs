using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using ImageMagick;

using Morph.Core.Jobs;

namespace Morph.Engines.Tests;

/// <summary>App ↔ Worker over stdin/stdout with the real Morph.Worker.exe in its Job Object, like production.</summary>
public sealed class WorkerProtocolTests : IDisposable
{
    private static readonly string WorkerPath = Path.Combine(AppContext.BaseDirectory, "Morph.Worker.exe");
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-worker-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Converts_and_reports_progress_then_outputs()
    {
        var a = await Image("a.png");
        var b = await Image("b.png");
        var progress = new ConcurrentQueue<JobProgress>();

        var result = await RunAsync(Request("convert.jpg", "imaging", [a, b]), progress);

        Assert.Equal(JobStatus.Succeeded, result.Status);
        Assert.Equal([Path.Combine(_dir, "a.jpg"), Path.Combine(_dir, "b.jpg")], result.Outputs);
        Assert.Equal([0.5, 1.0], progress.Select(p => p.Fraction));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(90)]
    public async Task Parameters_reach_the_engine(int quality)
    {
        var input = await Image("photo.png");

        var result = await RunAsync(Request("convert.jpg", "imaging", [input], new JsonObject { ["quality"] = quality }));

        using var output = new MagickImage(result.Outputs.Single());
        Assert.Equal((uint)quality, output.Quality); // Magick estimates JPEG quality from the quantisation tables
    }

    [Fact]
    public async Task Missing_input_fails_with_its_name_and_writes_nothing()
    {
        var result = await RunAsync(Request("convert.png", "imaging", [Path.Combine(_dir, "gone.jpg")]));

        Assert.Equal(JobStatus.Failed, result.Status);
        Assert.Contains("gone.jpg", result.UserMessage, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    [Fact]
    public async Task Unknown_engine_fails_with_a_user_message()
    {
        var input = await Image("photo.png");

        var result = await RunAsync(Request("convert.jpg", "nope", [input]));

        Assert.Equal(JobStatus.Failed, result.Status);
        Assert.Equal("This tool isn't available.", result.UserMessage);
    }

    [Fact]
    public async Task Failure_mid_batch_leaves_no_outputs()
    {
        var good = await Image("good.png");
        var bad = Path.Combine(_dir, "bad.png");
        await File.WriteAllTextAsync(bad, "not an image", TestContext.Current.CancellationToken);

        var result = await RunAsync(Request("convert.jpg", "imaging", [good, bad]));

        Assert.Equal(JobStatus.Failed, result.Status);
        Assert.Equal(["bad.png", "good.png"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    private JobRequest Request(string tool, string engine, string[] inputs, JsonObject? parameters = null) =>
        new(Guid.NewGuid(), tool, engine, inputs, _dir, parameters);

    private static Task<JobResult> RunAsync(JobRequest request, ConcurrentQueue<JobProgress>? progress = null) =>
        WorkerClient.RunAsync(WorkerPath, request, new SyncProgress<JobProgress>(p => progress?.Enqueue(p)), TestContext.Current.CancellationToken);

    private async Task<string> Image(string name)
    {
        var path = Path.Combine(_dir, name);
        using var image = new MagickImage(MagickColors.SteelBlue, 64, 48);
        await image.WriteAsync(path, MagickFormat.Png, TestContext.Current.CancellationToken);
        return path;
    }
}
