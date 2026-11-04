using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Morph.Core.Jobs;

namespace Morph.Engines.Media;

/// <param name="Duration">Seconds, or null when unknown.</param>
public sealed record MediaInfo(double? Duration, string? VideoCodec, string? PixelFormat, string? AudioCodec);

/// <summary>Runs the bundled LGPL ffmpeg.exe/ffprobe.exe (PLAN §5). Never a system copy: that could be a GPL build.</summary>
public sealed class Ffmpeg(string directory)
{
    public static Ffmpeg Bundled { get; } = new(AppContext.BaseDirectory);

    public string FfmpegPath { get; } = Path.Combine(directory, "ffmpeg.exe");

    public string FfprobePath { get; } = Path.Combine(directory, "ffprobe.exe");

    public bool IsAvailable => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    /// <summary>Duration (null when ffprobe can't tell; progress is then indeterminate) and the first video and audio codecs.</summary>
    public async Task<MediaInfo> ProbeAsync(string input, string demuxer, CancellationToken ct)
    {
        var (exit, stdout, _) = await RunAsync(FfprobePath,
            ["-v", "error", "-protocol_whitelist", "file", "-f", demuxer, "-show_entries", "format=duration:stream=codec_type,codec_name,pix_fmt",
                "-of", "json", input],
            onStdoutLine: null, ct).ConfigureAwait(false);
        if (exit != 0 || JsonNode.Parse(stdout) is not { } json)
        {
            return new MediaInfo(null, null, null, null);
        }

        var streams = json["streams"]?.AsArray() ?? [];
        var video = streams.FirstOrDefault(s => (string?)s?["codec_type"] == "video");
        var audio = streams.FirstOrDefault(s => (string?)s?["codec_type"] == "audio");
        var duration = double.TryParse((string?)json["format"]?["duration"], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : (double?)null;
        return new MediaInfo(duration, (string?)video?["codec_name"], (string?)video?["pix_fmt"], (string?)audio?["codec_name"]);
    }

    /// <summary>
    /// Runs ffmpeg with <paramref name="args"/> (inputs, filters, output) after the standard prefix, reporting
    /// <c>-progress</c> against <paramref name="durationSeconds"/>. Cancelling kills ffmpeg and waits for it to exit,
    /// so the caller can delete the temp output.
    /// </summary>
    public async Task EncodeAsync(IEnumerable<string> args, double? durationSeconds, IProgress<JobProgress> progress, CancellationToken ct)
    {
        string[] prefix = ["-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-progress", "pipe:1", "-y"];
        var (exit, _, stderr) = await RunAsync(FfmpegPath, [.. prefix, .. args], line =>
        {
            if (ParseProgress(line, durationSeconds) is { } fraction)
            {
                progress.Report(new JobProgress(fraction, "encoding"));
            }
        }, ct).ConfigureAwait(false);

        if (exit != 0)
        {
            throw new EngineException("Morph couldn't convert this file.", $"ffmpeg exit {exit}: {stderr.Trim()}");
        }
    }

    /// <summary>One <c>-progress</c> line → fraction done; <c>progress=end</c> → 1; NaN when the duration is unknown.</summary>
    internal static double? ParseProgress(string line, double? durationSeconds)
    {
        if (line == "progress=end")
        {
            return 1;
        }

        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)
            || !long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
        {
            return null; // other keys, and "N/A" before the first frame
        }

        return durationSeconds is > 0 and var d ? Math.Clamp(us / 1e6 / d, 0, 1) : double.NaN;
    }

    internal static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string exe, IReadOnlyList<string> args,
        Action<string>? onStdoutLine, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new EngineException("Morph couldn't start its video converter.", exe);
        using var kill = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = new StringBuilder();
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            if (onStdoutLine is null)
            {
                stdout.AppendLine(line);
            }
            else
            {
                onStdoutLine(line);
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return (process.ExitCode, stdout.ToString(), await stderr.ConfigureAwait(false));
    }
}
