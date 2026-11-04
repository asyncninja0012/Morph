using System.Diagnostics;

using Morph.Core.Jobs;
using Morph.Engines.Media;

namespace Morph.Engines.Tests;

public sealed class MediaEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-media-").FullName;
    private readonly Ffmpeg _ffmpeg = Ffmpeg.Bundled;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("out_time_us=1500000", 3.0, 0.5)]
    [InlineData("out_time_us=9000000", 3.0, 1.0)]
    [InlineData("progress=end", 3.0, 1.0)]
    public void Progress_lines_become_fractions(string line, double duration, double expected) =>
        Assert.Equal(expected, Ffmpeg.ParseProgress(line, duration));

    [Fact]
    public void Progress_without_a_duration_is_indeterminate_and_other_keys_are_ignored()
    {
        Assert.True(double.IsNaN(Ffmpeg.ParseProgress("out_time_us=1000", null)!.Value));
        Assert.Null(Ffmpeg.ParseProgress("out_time_us=N/A", 3));
        Assert.Null(Ffmpeg.ParseProgress("frame=12", 3));
    }

    [Fact]
    public async Task Mov_to_mp4_writes_h264_aac_with_the_same_duration()
    {
        var encoder = await RequireEncoderAsync();
        var input = await MakeMovAsync("clip.mov", seconds: 2);
        var namer = new OutputNamer(_dir);
        var reports = new List<JobProgress>();

        await Engine().RunAsync(Job(input), namer, new SyncProgress<JobProgress>(reports.Add), TestContext.Current.CancellationToken);
        var outputs = namer.CommitAll();

        Assert.Equal([Path.Combine(_dir, "clip.mp4")], outputs);
        var (_, streams, _) = await Ffmpeg.RunAsync(_ffmpeg.FfprobePath,
            ["-v", "error", "-show_entries", "stream=codec_name", "-of", "csv=p=0", outputs[0]], null, TestContext.Current.CancellationToken);
        Assert.Equal(["h264", "aac"], streams.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var duration = await _ffmpeg.DurationAsync(outputs[0], "mov", TestContext.Current.CancellationToken);
        Assert.InRange(duration!.Value, 1.9, 2.1);
        Assert.Equal(1.0, reports[^1].Fraction);
        TestContext.Current.TestOutputHelper?.WriteLine($"encoder: {encoder.Name}");
    }

    [Fact]
    public async Task Cancel_kills_ffmpeg_and_leaves_no_temp_file()
    {
        await RequireEncoderAsync();
        var input = await MakeMovAsync("long.mov", seconds: 120);
        var namer = new OutputNamer(_dir);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine().RunAsync(Job(input), namer, new SyncProgress<JobProgress>(_ => cts.Cancel()), cts.Token));
        namer.AbortAll();

        Assert.DoesNotContain(Process.GetProcessesByName("ffmpeg"), p => SafePath(p) == _ffmpeg.FfmpegPath);
        Assert.Equal([input], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Non_video_input_fails_with_a_user_message()
    {
        await RequireEncoderAsync();
        var input = Path.Combine(_dir, "playlist.mov");
        await File.WriteAllTextAsync(input, "#EXTM3U\nhttp://example.invalid/a.ts\n", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            Engine().RunAsync(Job(input), new OutputNamer(_dir), new SyncProgress<JobProgress>(_ => { }), TestContext.Current.CancellationToken));

        Assert.Contains("playlist.mov", ex.UserMessage, StringComparison.Ordinal);
    }

    private MediaEngine Engine() => new(_ffmpeg, new EncoderProbe(_ffmpeg, Path.Combine(_dir, "..", "morph-test-encoder-cache")));

    private JobRequest Job(string input) => new(Guid.NewGuid(), "convert.mp4", "media", [input], _dir);

    private async Task<VideoEncoder> RequireEncoderAsync()
    {
        Assert.SkipUnless(_ffmpeg.IsAvailable, "ffmpeg not fetched: run ./build/fetch-binaries.ps1");
        var encoder = await new EncoderProbe(_ffmpeg, Path.Combine(_dir, "..", "morph-test-encoder-cache")).H264Async(TestContext.Current.CancellationToken);
        Assert.SkipWhen(encoder is null, "no H.264 encoder on this machine (no GPU encoder or Media Foundation)");
        return encoder!;
    }

    // shortcut: fixtures made in-test until build/gen-fixtures.cs lands (task 1.8).
    private async Task<string> MakeMovAsync(string name, int seconds)
    {
        var path = Path.Combine(_dir, name);
        var (exit, _, stderr) = await Ffmpeg.RunAsync(_ffmpeg.FfmpegPath,
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc2=d={seconds}:s=640x360:r=25", "-f", "lavfi", "-i", $"sine=d={seconds}",
            "-c:v", "mpeg4", "-q:v", "5", "-c:a", "pcm_s16le", path,
        ], null, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, stderr);
        return path;
    }

    private static string? SafePath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
