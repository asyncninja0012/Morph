using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Media;

/// <summary>
/// Video and audio conversion through ffmpeg (PLAN §5): video → MP4/WebM/GIF/MP3, audio ↔ audio. Compress and resize
/// (IT-1, IT-2): MKV and WebM stay themselves, other video becomes H.264 MP4, WAV/FLAC become M4A, lossy audio keeps its format.
/// </summary>
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

    // Audio inside each video container, and the muxer with its options.
    private static readonly Dictionary<string, (string[] Audio, string[] Muxer)> VideoOutputs = new()
    {
        ["mp4"] = (["-c:a", "aac", "-b:a", "192k"], ["-movflags", "+faststart", "-f", "mp4"]),
        ["webm"] = (["-c:a", "libopus", "-b:a", "128k"], ["-f", "webm"]),
        ["mkv"] = (["-c:a", "aac", "-b:a", "192k"], ["-f", "matroska"]),
        ["avi"] = (["-c:a", "libmp3lame", "-b:a", "192k"], ["-f", "avi"]),
    };

    public MediaEngine()
        : this(Ffmpeg.Bundled, EncoderProbe.Default)
    {
    }

    public string Id => "media";

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (job.ToolId is "compress" or "resize")
        {
            await ShrinkAsync(job, namer, progress, ct).ConfigureAwait(false);
            return;
        }

        var target = job.ToolId.StartsWith("convert.", StringComparison.Ordinal) ? job.ToolId["convert.".Length..] : "";
        if (target is not ("gif" or "mkv") && !VideoOutputs.ContainsKey(target) && !AudioOutputs.ContainsKey(target))
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
            var demuxer = Demuxer(input, format);
            if (format.Kind == FileKind.Audio && target is "mp4" or "webm" or "mkv" or "avi" or "gif")
            {
                throw new EngineException($"{Path.GetFileName(input)} has no picture to convert to {target.ToUpperInvariant()}.");
            }

            var info = await ffmpeg.ProbeAsync(input, demuxer, ct).ConfigureAwait(false);
            // A stream already in a codec the target holds is copied, not re-encoded: seconds instead of minutes, no loss.
            var copyVideo = target switch
            {
                "mp4" => info.VideoCodec == "h264" && info.PixelFormat is "yuv420p" or "yuvj420p",
                "webm" => info.VideoCodec is "vp8" or "vp9",
                "mkv" => info.VideoCodec is "h264" or "hevc" or "vp8" or "vp9" or "av1" or "mpeg4",
                "avi" => info.VideoCodec == "mpeg4",
                _ => false,
            };
            var copyAudio = target switch
            {
                "mp4" => info.AudioCodec is "aac" or "mp3",
                "webm" => info.AudioCodec is "opus" or "vorbis",
                "mkv" => info.AudioCodec is "aac" or "mp3" or "opus" or "vorbis" or "flac" or "ac3" or "eac3" or "pcm_s16le",
                "avi" => info.AudioCodec == "mp3",
                _ => false,
            };
            if (target is "mp4" or "mkv" && !copyVideo)
            {
                h264 ??= await probe.H264Async(ct).ConfigureAwait(false)
                    ?? throw new EngineException("This PC has no H.264 video encoder. On Windows N, install the Media Feature Pack.");
            }

            var done = i;
            var fileProgress = new SyncProgress<JobProgress>(p =>
                progress.Report(p with { Fraction = double.IsNaN(p.Fraction) ? p.Fraction : (done + p.Fraction) / job.Inputs.Count }));

            string[] output = target switch
            {
                "mp4" or "webm" or "mkv" or "avi" =>
                [
                    "-map", "0:v:0", "-map", "0:a:0?",
                    .. copyVideo ? ["-c:v", "copy"] : VideoArgs(target, h264),
                    .. copyAudio ? ["-c:a", "copy"] : VideoOutputs[target].Audio,
                    .. VideoOutputs[target].Muxer,
                ],
                // Two-pass palette in one graph: palettegen sees every frame, then paletteuse maps against it.
                "gif" =>
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
            await EncodeAsync(input, demuxer, output, pending[i].Path, info, fileProgress, ct).ConfigureAwait(false);
        }
    }

    // The demuxer is forced and only the file protocol allowed, so a hostile file can't reach
    // playlist/concat demuxers that open other files or URLs.
    private Task EncodeAsync(string input, string demuxer, string[] output, string path, MediaInfo info, IProgress<JobProgress> progress, CancellationToken ct) =>
        ffmpeg.EncodeAsync(["-protocol_whitelist", "file", "-f", demuxer, "-i", input, "-map_metadata", "0", .. output, path], info.Duration, progress, ct);

    private static string Demuxer(string input, FileFormat format) => format.Id switch
    {
        "mp4" or "mov" or "m4a" => "mov", // ffmpeg's mov demuxer reads all ISO-BMFF
        "mkv" or "webm" => "matroska",
        "avi" or "mp3" or "wav" or "flac" => format.Id,
        "wmv" => "asf",
        "ogg" or "opus" => "ogg",
        _ => null,
    } ?? throw new EngineException($"{Path.GetFileName(input)} isn't a video or audio file Morph can read.");

    // Compress (preset or "fit under X MB") and resize. A file already small enough is skipped; the job fails only when every file was.
    private async Task ShrinkAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (!ffmpeg.IsAvailable)
        {
            throw new EngineException("Morph's video converter is missing. Reinstall Morph.", $"not found: {ffmpeg.FfmpegPath}");
        }

        var compress = job.ToolId == "compress";
        var preset = Presets.Compress(job.Parameters);
        var limit = compress ? Presets.TargetBytes(job.Parameters) : null;
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            var format = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);
            var demuxer = Demuxer(input, format);
            var info = await ffmpeg.ProbeAsync(input, demuxer, ct).ConfigureAwait(false);
            var done = i;
            var fileProgress = new SyncProgress<JobProgress>(p =>
                progress.Report(p with { Fraction = double.IsNaN(p.Fraction) ? p.Fraction : (done + p.Fraction) / job.Inputs.Count }));

            var audioOnly = info.VideoCodec is null;
            if (audioOnly && !compress)
            {
                throw new EngineException($"{name} has no picture to resize.");
            }

            var container = audioOnly
                ? (format.Id is "mp3" or "ogg" or "opus" ? format.Id : "m4a") // WAV/FLAC, and a video file with only sound
                : format.Id is "mkv" or "webm" ? format.Id : "mp4";
            if (limit is { } bytes && new FileInfo(input).Length <= bytes)
            {
                skipped = $"{name} is already under {Presets.Megabytes(bytes)} MB.";
                continue;
            }

            (int Width, int Height)? size;
            if (compress)
            {
                // Small and Balanced also cap the frame at 720p/1080p: most of a 4K phone clip's size is detail no upload keeps.
                size = preset switch
                {
                    Preset.Small => Presets.Fit(info.Width, info.Height, 1280, 720),
                    Preset.Balanced => Presets.Fit(info.Width, info.Height, 1920, 1080),
                    _ => null,
                };
            }
            else if ((size = Presets.Resize(info.Width, info.Height, job.Parameters)) is null)
            {
                skipped = $"{name} is already that small.";
                continue;
            }

            var pending = namer.Reserve(input, "." + container, compress ? "-compressed" : "-resized");
            string? failure;
            if (audioOnly)
            {
                await EncodeAsync(input, demuxer, AudioArgs(container, preset, info.AudioKbps), pending.Path, info, fileProgress, ct).ConfigureAwait(false);
                failure = new FileInfo(pending.Path).Length >= new FileInfo(input).Length ? $"{name} is already as small as Morph can make it at this setting." : null;
            }
            else if (limit is { } target)
            {
                failure = await FitAsync(input, demuxer, info, container, size, target, pending.Path, fileProgress, ct).ConfigureAwait(false);
            }
            else
            {
                var quality = !compress ? 23 : preset switch { Preset.Small => 32, Preset.Balanced => 28, _ => 24 };
                var video = await VideoArgsAsync(container, size, quality, kbps: null, ct).ConfigureAwait(false);
                string[] audio = compress ? ShrinkAudio(container, preset) : CopyAudio(container, info.AudioCodec) ? ["-c:a", "copy"] : VideoOutputs[Muxer(container)].Audio;
                await EncodeAsync(input, demuxer, Video(video, audio, container), pending.Path, info, fileProgress, ct).ConfigureAwait(false);
                failure = compress && new FileInfo(pending.Path).Length >= new FileInfo(input).Length ? $"{name} is already as small as Morph can make it at this setting." : null;
            }

            if (failure is not null)
            {
                namer.Discard(pending);
                skipped = failure;
            }
            else
            {
                made++;
            }
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    // Aims the average bit rate at the size limit, then re-encodes lower if the encoder overshot (hardware rate control is loose).
    private async Task<string?> FitAsync(string input, string demuxer, MediaInfo info, string container, (int Width, int Height)? size, long limit, string path,
        IProgress<JobProgress> progress, CancellationToken ct)
    {
        var name = Path.GetFileName(input);
        if (info.Duration is not { } seconds)
        {
            return $"Morph can't tell how long {name} is, so it can't aim for a size.";
        }

        const int audioKbps = 64;
        var hasAudio = info.AudioCodec is not null;
        var kbps = limit * 8 * 0.92 / seconds / 1000 - (hasAudio ? audioKbps : 0); // 8% for the container and rate-control slack
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (kbps < 100)
            {
                return $"{name} is too long to fit under {Presets.Megabytes(limit)} MB and still be watchable.";
            }

            // Fewer bits look better on fewer pixels.
            var frame = kbps < 600 ? Presets.Fit(info.Width, info.Height, 854, 480) : kbps < 1500 ? Presets.Fit(info.Width, info.Height, 1280, 720) : null;
            var video = await VideoArgsAsync(container, frame ?? size, quality: 0, (int)kbps, ct).ConfigureAwait(false);
            string[] audio = container == "webm" ? ["-c:a", "libopus", "-b:a", $"{audioKbps}k"] : ["-c:a", "aac", "-b:a", $"{audioKbps}k"];
            await EncodeAsync(input, demuxer, Video(video, audio, container), path, info, progress, ct).ConfigureAwait(false);
            var written = new FileInfo(path).Length;
            if (written <= limit)
            {
                return null;
            }

            kbps *= limit * 0.95 / written;
        }

        return $"Morph couldn't get {name} under {Presets.Megabytes(limit)} MB.";
    }

    // Even dimensions: every encoder here wants 4:2:0, which needs them. kbps set = average bit rate, else constant quality.
    private async Task<string[]> VideoArgsAsync(string container, (int Width, int Height)? size, int quality, int? kbps, CancellationToken ct)
    {
        var scale = size is var (w, h) ? $"scale={w / 2 * 2}:{h / 2 * 2}" : "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        if (container == "webm")
        {
            string[] rate = kbps is { } k ? ["-b:v", $"{k}k"] : ["-crf", $"{quality + 9}", "-b:v", "0"]; // VP9's CRF runs about 9 above x264's for the same look
            return
            [
                "-vf", $"{scale},format=yuv420p", "-c:v", "libvpx-vp9", .. rate, "-deadline", "realtime", "-cpu-used", "6", "-row-mt", "1",
                "-tile-columns", "2", "-threads", $"{Environment.ProcessorCount}",
            ];
        }

        var h264 = await probe.H264Async(ct).ConfigureAwait(false)
            ?? throw new EngineException("This PC has no H.264 video encoder. On Windows N, install the Media Feature Pack.");
        return ["-vf", $"{scale},format=nv12", .. kbps is { } bitrate ? h264.Bitrate(bitrate) : h264.Quality(quality)];
    }

    private static string[] Video(string[] video, string[] audio, string container) =>
        ["-map", "0:v:0", "-map", "0:a:0?", .. video, .. audio, .. VideoOutputs[Muxer(container)].Muxer];

    private static string Muxer(string container) => container == "mkv" ? "mkv" : container == "webm" ? "webm" : "mp4";

    private static bool CopyAudio(string container, string? codec) => container switch
    {
        "mp4" => codec is "aac" or "mp3",
        "webm" => codec is "opus" or "vorbis",
        _ => codec is "aac" or "mp3" or "opus" or "vorbis" or "flac" or "ac3" or "eac3" or "pcm_s16le",
    };

    private static string[] ShrinkAudio(string container, Preset preset)
    {
        var kbps = preset switch { Preset.Small => 64, Preset.Balanced => 96, _ => 128 };
        return container == "webm" ? ["-c:a", "libopus", "-b:a", $"{kbps * 2 / 3}k"] : ["-c:a", "aac", "-b:a", $"{kbps}k"];
    }

    // Never above 80% of what the file has now, so a low-bit-rate MP3 still gets smaller.
    private static string[] AudioArgs(string container, Preset preset, double? sourceKbps)
    {
        var (codec, small, balanced, high) = container switch
        {
            "mp3" => ("libmp3lame", 96, 128, 192),
            "ogg" => ("libvorbis", 64, 96, 160),
            "opus" => ("libopus", 32, 64, 96),
            _ => ("aac", 64, 96, 160),
        };
        var kbps = preset switch { Preset.Small => small, Preset.Balanced => balanced, _ => high };
        if (sourceKbps is { } source)
        {
            kbps = (int)Math.Max(24, Math.Min(kbps, source * 0.8));
        }

        return ["-map", "0:a:0", "-vn", "-c:a", codec, "-b:a", $"{kbps}k", "-f", AudioOutputs[container].Muxer];
    }

    // Even dimensions: every encoder here wants 4:2:0, which needs them.
    private static string[] VideoArgs(string target, VideoEncoder? h264) => target switch
    {
        "mp4" or "mkv" => ["-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=nv12", .. h264!.Args],
        // realtime + cpu-used 6: about 5x faster than "good"/4 at nearly the same quality (SSIM 0.979 vs 0.979,
        // ~15% larger) on a 30 s 1080p clip; tiles and threads let libvpx use every core.
        "webm" =>
        [
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p",
            "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "realtime", "-cpu-used", "6", "-row-mt", "1",
            "-tile-columns", "2", "-threads", $"{Environment.ProcessorCount}",
        ],
        // MPEG-4 Part 2 (Xvid-style): what old players and TVs expect in an AVI. Larger than H.264 at this quality.
        _ =>
        [
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p",
            "-c:v", "mpeg4", "-q:v", "3", "-tag:v", "XVID", "-threads", $"{Environment.ProcessorCount}",
        ],
    };
}
