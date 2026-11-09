using System.Text.Json;
using System.Text.Json.Serialization;

namespace Morph.Engines.Media;

/// <summary>An H.264 encoder ffmpeg can actually use on this machine, with its quality settings.</summary>
/// <param name="Args">Default quality (constant quality 23), used by conversions.</param>
public sealed record VideoEncoder(string Name, IReadOnlyList<string> Args)
{
    /// <summary>Constant quality on x264's CRF-like scale: 23 is the default, higher is smaller (Compress presets, IT-1).</summary>
    public string[] Quality(int cq) => Name switch
    {
        "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", $"{cq}", "-b:v", "0"],
        "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "medium", "-global_quality", $"{cq}"],
        "h264_amf" => ["-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", $"{cq - 1}", "-qp_p", $"{cq + 1}"],
        // Media Foundation's 0–100 quality, higher is better: 23 → 70 as above, about 4 points per step.
        _ => [.. MediaFoundation, "-rate_control", "quality", "-quality", $"{Math.Clamp(70 - (cq - 23) * 4, 10, 100)}"],
    };

    /// <summary>Average bit rate, for "fit under X MB".</summary>
    public string[] Bitrate(int kbps) => Name switch
    {
        "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", .. Rate(kbps)],
        "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "medium", .. Rate(kbps)],
        "h264_amf" => ["-c:v", "h264_amf", "-quality", "balanced", "-rc", "vbr_peak", .. Rate(kbps)],
        _ => [.. MediaFoundation, "-rate_control", "cbr", "-b:v", $"{kbps}k"],
    };

    private string[] MediaFoundation => Name == "h264_mf hardware" ? ["-c:v", "h264_mf", "-hw_encoding", "1"] : ["-c:v", "h264_mf"];

    private static string[] Rate(int kbps) => ["-b:v", $"{kbps}k", "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k"];
}

/// <summary>
/// Finds the first working H.264 encoder by test-encoding one frame with each (PLAN §5): NVENC → QSV → AMF →
/// Media Foundation hardware → Media Foundation software. Never libx264 (GPL). The answer is cached on disk.
/// </summary>
public sealed class EncoderProbe(Ffmpeg ffmpeg, string cacheDirectory)
{
    // Constant-quality settings at roughly "visually lossless for sharing"; Compress uses VideoEncoder.Quality/Bitrate.
    internal static readonly VideoEncoder[] H264Candidates =
    [
        new("h264_nvenc", ["-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "23", "-b:v", "0"]),
        new("h264_qsv", ["-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "23"]),
        new("h264_amf", ["-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "22", "-qp_p", "24"]),
        new("h264_mf hardware", ["-c:v", "h264_mf", "-hw_encoding", "1", "-rate_control", "quality", "-quality", "70"]),
        new("h264_mf", ["-c:v", "h264_mf", "-rate_control", "quality", "-quality", "70"]),
    ];

    public static EncoderProbe Default { get; } = new(Ffmpeg.Bundled,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morph", "cache"));

    /// <returns>The encoder, or null when none works (e.g. Windows N without the Media Feature Pack).</returns>
    public async Task<VideoEncoder?> H264Async(CancellationToken ct)
    {
        // shortcut: cache keyed only by the ffmpeg build; a GPU or driver change needs the cache file deleted. Re-probe on
        // encode failure once real users hit it.
        var ffmpegFile = new FileInfo(ffmpeg.FfmpegPath);
        var key = $"{ffmpegFile.Length}-{ffmpegFile.LastWriteTimeUtc.Ticks}";
        var cachePath = Path.Combine(cacheDirectory, "encoders.json");
        try
        {
            await using var read = File.OpenRead(cachePath);
            if (await JsonSerializer.DeserializeAsync(read, ProbeJson.Default.EncoderCache, ct).ConfigureAwait(false) is { } cached && cached.Key == key)
            {
                return H264Candidates.FirstOrDefault(c => c.Name == cached.H264);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // No cache yet, or unreadable: probe.
        }

        VideoEncoder? found = null;
        foreach (var candidate in H264Candidates)
        {
            if (await WorksAsync(candidate, ct).ConfigureAwait(false))
            {
                found = candidate;
                break;
            }
        }

        Directory.CreateDirectory(cacheDirectory);
        await using (var write = File.Create(cachePath))
        {
            await JsonSerializer.SerializeAsync(write, new EncoderCache(key, found?.Name), ProbeJson.Default.EncoderCache, ct).ConfigureAwait(false);
        }

        return found;
    }

    private async Task<bool> WorksAsync(VideoEncoder encoder, CancellationToken ct)
    {
        string[] args =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.1",
            "-frames:v", "1", "-pix_fmt", "nv12", .. encoder.Args, "-f", "null", "-",
        ];
        var (exit, _, _) = await Ffmpeg.RunAsync(ffmpeg.FfmpegPath, args, onStdoutLine: null, ct).ConfigureAwait(false);
        return exit == 0;
    }
}

internal sealed record EncoderCache(string Key, string? H264);

[JsonSerializable(typeof(EncoderCache))]
internal sealed partial class ProbeJson : JsonSerializerContext;
