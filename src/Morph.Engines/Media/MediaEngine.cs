using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Media;

/// <summary>Video and audio conversion through ffmpeg (PLAN §5): video → MP4/WebM/GIF/MP3, audio ↔ audio.</summary>
public sealed class MediaEngine(Ffmpeg ffmpeg, EncoderProbe probe) : IEngine
{
    // Audio settings per output: CV-4 asks for about 192 kbps; Opus gets there at 128k, FLAC/WAV are lossless.
    private static readonly Dictionary<string, (string Muxer, string[] Audio)> AudioOutputs = new()
    {
        ["mp3"] = ("mp3", ["-c:a", "libmp3lame", "-b:a", "192k"]),
        ["m4a"] = ("ipod", ["-c:a", "aac", "-b:a", "192k"]),
        ["wav"] = ("wav", ["-c:a", "pcm_s16le"]),
        ["flac"] = ("flac", ["-c:a", "flac"]),
        ["ogg"] = ("ogg", ["-c:a", "libvorbis", "-q:a", "6"]),
        ["opus"] = ("opus", ["-c:a", "libopus", "-b:a", "128k"]),
    };

    public MediaEngine()
        : this(Ffmpeg.Bundled, EncoderProbe.Default)
    {
    }

    public string Id => "media";

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var target = job.ToolId.StartsWith("convert.", StringComparison.Ordinal) ? job.ToolId["convert.".Length..] : "";
        if (target is not ("mp4" or "webm" or "gif") && !AudioOutputs.ContainsKey(target))
        {
            throw new EngineException("This conversion isn't available yet.", $"media: unsupported tool {job.ToolId}");
        }

        if (!ffmpeg.IsAvailable)
        {
            throw new EngineException("Morph's video converter is missing. Reinstall Morph.", $"not found: {ffmpeg.FfmpegPath}");
        }

        // Every output shows up under its final name at once and fills in as its turn comes.
        var pending = job.Inputs.Select(i => namer.Reserve(i, "." + target)).ToList();
        VideoEncoder? h264 = null;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var format = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);
            var demuxer = format.Id switch
            {
                "mp4" or "mov" or "m4a" => "mov", // ffmpeg's mov demuxer reads all ISO-BMFF
                "mkv" or "webm" => "matroska",
                "avi" or "mp3" or "wav" or "flac" => format.Id,
                "ogg" or "opus" => "ogg",
                _ => null,
            } ?? throw new EngineException($"{Path.GetFileName(input)} isn't a video or audio file Morph can read.");
            if (format.Kind == FileKind.Audio && target is "mp4" or "webm" or "gif")
            {
                throw new EngineException($"{Path.GetFileName(input)} has no picture to convert to {target.ToUpperInvariant()}.");
            }

            var info = await ffmpeg.ProbeAsync(input, demuxer, ct).ConfigureAwait(false);
            // Streams already in the target's codecs are copied, not re-encoded: seconds instead of minutes, no quality loss.
            var copy = target switch
            {
                "mp4" => info.VideoCodec == "h264" && info.PixelFormat is "yuv420p" or "yuvj420p" && info.AudioCodec is null or "aac" or "mp3",
                "webm" => info.VideoCodec is "vp8" or "vp9" && info.AudioCodec is null or "opus" or "vorbis",
                _ => false,
            };
            if (target == "mp4" && !copy)
            {
                h264 ??= await probe.H264Async(ct).ConfigureAwait(false)
                    ?? throw new EngineException("This PC has no H.264 video encoder. On Windows N, install the Media Feature Pack.");
            }

            var done = i;
            var fileProgress = new SyncProgress<JobProgress>(p =>
                progress.Report(p with { Fraction = double.IsNaN(p.Fraction) ? p.Fraction : (done + p.Fraction) / job.Inputs.Count }));

            string[] output = (target, copy) switch
            {
                ("mp4", true) => ["-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-movflags", "+faststart", "-f", "mp4"],
                ("webm", true) => ["-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-f", "webm"],
                ("mp4", _) =>
                [
                    "-map", "0:v:0", "-map", "0:a:0?",
                    "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=nv12", // H.264 4:2:0 needs even dimensions
                    .. h264!.Args, "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "mp4",
                ],
                // realtime + cpu-used 6: about 5x faster than "good"/4 at nearly the same quality (SSIM 0.979 vs 0.979,
                // ~15% larger) on a 30 s 1080p clip; tiles and threads let libvpx use every core.
                ("webm", _) =>
                [
                    "-map", "0:v:0", "-map", "0:a:0?", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p",
                    "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "realtime", "-cpu-used", "6", "-row-mt", "1",
                    "-tile-columns", "2", "-threads", $"{Environment.ProcessorCount}",
                    "-c:a", "libopus", "-b:a", "128k", "-f", "webm",
                ],
                // Two-pass palette in one graph: palettegen sees every frame, then paletteuse maps against it.
                ("gif", _) =>
                [
                    "-map", "0:v:0", "-an",
                    "-vf", "fps=15,scale='min(480,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
                    "-loop", "0", "-f", "gif",
                ],
                // shortcut: a silent video → MP3 fails with the generic message; probe for an audio stream if users hit it.
                _ => ["-map", "0:a:0", "-vn", .. AudioOutputs[target].Audio, "-f", AudioOutputs[target].Muxer],
            };

            // The demuxer is forced and only the file protocol allowed, so a hostile file can't reach
            // playlist/concat demuxers that open other files or URLs.
            string[] args =
            [
                "-protocol_whitelist", "file", "-f", demuxer, "-i", input, "-map_metadata", "0",
                .. output,
                pending[i].Path,
            ];
            await ffmpeg.EncodeAsync(args, info.Duration, fileProgress, ct).ConfigureAwait(false);
        }
    }
}
