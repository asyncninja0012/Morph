using System.Globalization;
using System.Text;

using ImageMagick;

using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Media;

/// <summary>
/// Video and audio conversion through ffmpeg (PLAN §5): video → MP4/WebM/GIF/MP3, audio ↔ audio. Compress and resize
/// (IT-1, IT-2): MKV and WebM stay themselves, other video becomes H.264 MP4, WAV/FLAC become M4A, lossy audio keeps its format.
/// Instant tools (IT-3): rotate, strip-metadata, extract-audio, mute, make-gif, and merge in the order given. Crop (ED-1) re-encodes
/// like rotate; trim (ED-4) cuts parts by copying streams (fast) or re-encoding (exact). Preview is one frame, or a filmstrip or
/// waveform with the length, for the editors; playback is the trim editor's small copy to play.
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
        if (!ffmpeg.IsAvailable)
        {
            throw new EngineException("Morph's video converter is missing. Reinstall Morph.", $"not found: {ffmpeg.FfmpegPath}");
        }

        switch (job.ToolId)
        {
            case "compress" or "resize":
                await ShrinkAsync(job, namer, progress, ct).ConfigureAwait(false);
                return;
            case PreviewFile.ToolId:
                await PreviewAsync(job, namer, ct).ConfigureAwait(false);
                return;
            case "rotate" or "strip-metadata" or "extract-audio" or "mute" or "crop":
                await EditAsync(job, namer, progress, ct).ConfigureAwait(false);
                return;
            case "merge":
                await MergeAsync(job, namer, progress, ct).ConfigureAwait(false);
                return;
            case "trim":
                await TrimAsync(job, namer, progress, ct).ConfigureAwait(false);
                return;
            case "speed":
                await SpeedAsync(job, namer, progress, ct).ConfigureAwait(false);
                return;
            case "frames":
                await FramesAsync(job, namer, progress, ct).ConfigureAwait(false);
                return;
            case PreviewFile.PlaybackToolId:
                await PlaybackAsync(job, namer, progress, ct).ConfigureAwait(false);
                return;
        }

        var target = job.ToolId == "make-gif" ? "gif" : job.ToolId.StartsWith("convert.", StringComparison.Ordinal) ? job.ToolId["convert.".Length..] : "";
        if (target is not ("gif" or "mkv") && !VideoOutputs.ContainsKey(target) && !AudioOutputs.ContainsKey(target))
        {
            throw new EngineException("This conversion isn't available yet.", $"media: unsupported tool {job.ToolId}");
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
    private Task EncodeAsync(string input, string demuxer, string[] output, string path, MediaInfo info, IProgress<JobProgress> progress, CancellationToken ct,
        bool keepMetadata = true) =>
        ffmpeg.EncodeAsync(["-protocol_whitelist", "file", "-f", demuxer, "-i", input, .. keepMetadata ? ["-map_metadata", "0"] : Array.Empty<string>(), .. output, path],
            info.Duration, progress, ct);

    // The muxer that writes a file back in its own format, for the tools that copy streams.
    private static string SameMuxer(FileFormat format) => format.Id switch
    {
        "mkv" => "matroska",
        "wmv" => "asf",
        "m4a" => "ipod",
        _ => format.Id, // mp4, mov, webm, avi, mp3, wav, flac, ogg, opus
    };

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
                    Preset.Small => Presets.CompressSize(info.Width, info.Height, job.Parameters, 1280, 720),
                    Preset.Balanced => Presets.CompressSize(info.Width, info.Height, job.Parameters, 1920, 1080),
                    _ => Presets.CompressSize(info.Width, info.Height, job.Parameters, int.MaxValue),
                };
            }
            else if ((size = Presets.Resize(info.Width, info.Height, job.Parameters)) is null)
            {
                skipped = $"{name} is already that size.";
                continue;
            }

            var pending = namer.Reserve(input, "." + container, compress ? "-compressed" : "-resized");
            string? failure;
            if (audioOnly && limit is { } audioLimit)
            {
                failure = await FitAudioAsync(input, demuxer, info, container, audioLimit, pending.Path, fileProgress, ct).ConfigureAwait(false);
            }
            else if (audioOnly)
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

    // Rotate re-encodes (MKV/WebM stay, other video → MP4) and copies the sound when the container holds it. Strip info, extract
    // audio and mute copy streams, so they take seconds and lose nothing. A file with nothing to do is skipped; the job fails
    // only when every file was.
    private async Task EditAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var (degrees, flip) = Presets.Rotate(job.Parameters);
        string[] turn = degrees switch { 90 => ["transpose=clock"], 180 => ["hflip", "vflip"], 270 => ["transpose=cclock"], _ => [] };
        string[] mirror = flip switch { "horizontal" => ["hflip"], "vertical" => ["vflip"], _ => [] };
        var transform = string.Join(",", [.. turn, .. mirror]);
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

            if (job.ToolId is "rotate" or "mute" or "crop" && info.VideoCodec is null)
            {
                throw new EngineException($"{name} has no picture.");
            }

            if (job.ToolId is "extract-audio" or "mute" && info.AudioCodec is null)
            {
                skipped = job.ToolId == "mute" ? $"{name} is already silent." : $"{name} has no sound.";
                continue;
            }

            string? crop = null;
            if (job.ToolId == "crop" && (crop = CropFilter(job.Parameters, info, name)) is null)
            {
                skipped = $"{name} is already that size.";
                continue;
            }

            // 0:V leaves out cover art: strip info drops it (a picture can carry its own EXIF), mute has no use for it.
            var (extension, suffix, keepMetadata, output) = job.ToolId switch
            {
                "rotate" => await RotateArgsAsync(format, info, transform, "-rotated", ct).ConfigureAwait(false),
                "crop" => await RotateArgsAsync(format, info, crop!, "-cropped", ct).ConfigureAwait(false),
                "strip-metadata" =>
                (
                    format.Id, "-stripped", false,
                    ["-map", "0:V?", "-map", "0:a?", "-map", "0:s?", "-c", "copy", "-map_metadata", "-1", "-map_chapters", "-1",
                        "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", "-f", SameMuxer(format)]
                ),
                "mute" => (format.Id, "-muted", true, ["-map", "0:V", "-map", "0:s?", "-an", "-c", "copy", "-f", SameMuxer(format)]),
                _ => ExtractArgs(info.AudioCodec!),
            };

            var pending = namer.Reserve(input, "." + extension, suffix);
            await EncodeAsync(input, demuxer, output, pending.Path, info, fileProgress, ct, keepMetadata).ConfigureAwait(false);
            made++;
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    private async Task<(string, string, bool, string[])> RotateArgsAsync(FileFormat format, MediaInfo info, string transform, string suffix, CancellationToken ct)
    {
        var container = format.Id is "mkv" or "webm" ? format.Id : "mp4";
        var video = await VideoArgsAsync(container, null, 23, kbps: null, ct, transform).ConfigureAwait(false);
        string[] audio = CopyAudio(container, info.AudioCodec) ? ["-c:a", "copy"] : VideoOutputs[Muxer(container)].Audio;
        return (container, suffix, true, Video(video, audio, container));
    }

    // The editor's box on the upright picture, on even pixels (4:2:0); null when it keeps the whole frame.
    private static string? CropFilter(System.Text.Json.Nodes.JsonObject? parameters, MediaInfo info, string name)
    {
        var (width, height) = info.ShownSize;
        var (x, y, w, h) = Presets.Crop(parameters, width, height, name);
        var (left, top) = ((int)x / 2 * 2, (int)y / 2 * 2);
        var (cw, ch) = (Math.Max(2, (int)Math.Round(w) / 2 * 2), Math.Max(2, (int)Math.Round(h) / 2 * 2));
        return cw >= width / 2 * 2 && ch >= height / 2 * 2 ? null : FormattableString.Invariant($"crop={cw}:{ch}:{left}:{top}");
    }

    // Editors: one frame 1 s in (or halfway through a shorter clip), past most fade-ins, upright as players show it.
    // ffmpeg writes it as a PNG into the preview folder, which Magick then reads; the PNG is deleted afterwards.
    private async Task PreviewAsync(JobRequest job, OutputNamer namer, CancellationToken ct)
    {
        var input = job.Inputs[0];
        var format = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);
        var demuxer = Demuxer(input, format);
        var info = await ffmpeg.ProbeAsync(input, demuxer, ct).ConfigureAwait(false);
        if (job.Parameters?["strip"] is { } strip)
        {
            await StripAsync(job, input, demuxer, info, double.TryParse(strip.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var shape) ? shape : 8, namer, ct).ConfigureAwait(false);
            return;
        }

        if (info.VideoCodec is null)
        {
            throw new EngineException($"{Path.GetFileName(input)} has no picture.");
        }

        var at = info.Duration is { } d ? Math.Min(1, d / 2) : 0;
        var frame = namer.Reserve(input, ".png", "-frame");
        try
        {
            await EncodeAsync(input, demuxer, ["-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-map", "0:V:0", "-frames:v", "1", "-c:v", "png", "-f", "image2"],
                frame.Path, info, new SyncProgress<JobProgress>(_ => { }), ct, keepMetadata: false).ConfigureAwait(false);
            using var image = new MagickImage(frame.Path, new MagickReadSettings { Format = MagickFormat.Png });
            await Preview.WriteAsync(image, image.Width, image.Height, job, namer, ct).ConfigureAwait(false);
        }
        finally
        {
            namer.Discard(frame);
        }
    }

    // The trim editor's timeline: for video, as many frames side by side (each one fast input seek, 90 px high) as fill a strip of
    // `shape` (width ÷ height) without squashing them; for sound, the waveform. The preview carries the length and, for video,
    // the shown size.
    private async Task StripAsync(JobRequest job, string input, string demuxer, MediaInfo info, double shape, OutputNamer namer, CancellationToken ct)
    {
        var name = Path.GetFileName(input);
        if (info.Duration is not { } seconds)
        {
            throw new EngineException($"Morph can't tell how long {name} is.");
        }

        if (info.VideoCodec is null && info.AudioCodec is null)
        {
            throw new EngineException($"{name} has no picture or sound.");
        }

        var picture = namer.Reserve(input, ".png", "-strip");
        try
        {
            string[] args;
            if (info.VideoCodec is null)
            {
                args = ["-protocol_whitelist", "file", "-f", demuxer, "-i", input, "-filter_complex", "[0:a:0]aformat=channel_layouts=mono,showwavespic=s=1600x120:colors=0xF28C28"];
            }
            else
            {
                var (w, h) = info.ShownSize;
                var count = (int)Math.Clamp(Math.Round(shape / (h > 0 ? (double)w / h : 16.0 / 9)), 1, 24);
                var inputs = new List<string>();
                var graph = new StringBuilder();
                for (var i = 0; i < count; i++)
                {
                    inputs.AddRange(["-ss", Seconds(seconds * i / count), "-protocol_whitelist", "file", "-f", demuxer, "-i", input]);
                    graph.Append(CultureInfo.InvariantCulture, $"[{i}:V:0]scale=-2:90,setsar=1,format=rgb24[t{i}];");
                }

                graph.Append(count == 1 ? "[t0]null" : $"{string.Concat(Enumerable.Range(0, count).Select(i => $"[t{i}]"))}hstack=inputs={count}");
                args = [.. inputs, "-filter_complex", graph.ToString()];
            }

            await ffmpeg.EncodeAsync([.. args, "-frames:v", "1", "-c:v", "png", "-f", "image2", picture.Path], null, new SyncProgress<JobProgress>(_ => { }), ct).ConfigureAwait(false);
            using var image = new MagickImage(picture.Path, new MagickReadSettings { Format = MagickFormat.Png });
            var (width, height) = info.VideoCodec is null ? ((int)image.Width, (int)image.Height) : info.ShownSize;
            await Preview.WriteAsync(image, width, height, job, namer, ct, duration: seconds, frameRate: FrameRate(info) ?? 0).ConfigureAwait(false);
        }
        finally
        {
            namer.Discard(picture);
        }
    }

    // A copy the App can play without decoding the dropped file: Morph's own encoder writes it, at most 960×540, a keyframe
    // every 15 frames so seeking lands close, AAC stereo sound; M4A for a sound-only file.
    private async Task PlaybackAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var input = job.Inputs[0];
        var format = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);
        var demuxer = Demuxer(input, format);
        var info = await ffmpeg.ProbeAsync(input, demuxer, ct).ConfigureAwait(false);
        string[] sound = ["-c:a", "aac", "-b:a", "128k", "-ac", "2"];
        if (info.VideoCodec is null)
        {
            if (info.AudioCodec is null)
            {
                throw new EngineException($"{Path.GetFileName(input)} has no picture or sound.");
            }

            await EncodeAsync(input, demuxer, ["-map", "0:a:0", "-vn", .. sound, "-f", "ipod"], namer.Reserve(input, ".m4a", "-playback").Path, info, progress, ct, keepMetadata: false)
                .ConfigureAwait(false);
            return;
        }

        var h264 = await probe.H264Async(ct).ConfigureAwait(false)
            ?? throw new EngineException("This PC has no H.264 video encoder. On Windows N, install the Media Feature Pack.");
        string[] output =
        [
            "-map", "0:V:0", "-map", "0:a:0?",
            "-vf", "scale=w='min(960,iw)':h='min(540,ih)':force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2,format=nv12",
            .. h264.Quality(30), "-g", "15", .. sound, "-movflags", "+faststart", "-f", "mp4",
        ];
        await EncodeAsync(input, demuxer, output, namer.Reserve(input, ".mp4", "-playback").Path, info, progress, ct, keepMetadata: false).ConfigureAwait(false);
    }

    // Trim (ED-4): each segment becomes a file (name-trimmed.ext, or name-part1.ext, name-part2.ext, ...). Fast copies the streams
    // in the file's own format: ffmpeg starts each part at the keyframe at or before its start. Exact re-encodes like rotate
    // (MKV/WebM stay, other video → MP4, sound re-encoded; a sound file keeps its format). Segments past the end are dropped and the last one is cut at
    // the end, so "Same for all" works on shorter files.
    private async Task TrimAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var (segments, exact) = Trims.Parse(job.Parameters);
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            var format = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);
            var demuxer = Demuxer(input, format);
            var info = await ffmpeg.ProbeAsync(input, demuxer, ct).ConfigureAwait(false);
            var length = info.Duration;
            var parts = segments.Where(s => length is not { } d || s.Start < d - 0.01).Select(s => length is { } d && s.End > d ? s with { End = d } : s).ToList();
            if (parts.Count == 0)
            {
                skipped = $"{name} is shorter than where you cut.";
                continue;
            }

            if (parts is [var only] && only.Start <= 0.01 && length is { } full && only.End >= full - 0.01)
            {
                skipped = $"{name} is already that length.";
                continue;
            }

            var (extension, output) = exact
                ? await ExactTrimArgsAsync(format, info, ct).ConfigureAwait(false)
                : (format.Id, ["-map", "0:V?", "-map", "0:a?", "-map", "0:s?", "-c", "copy", "-avoid_negative_ts", "make_zero", "-f", SameMuxer(format)]);
            for (var k = 0; k < parts.Count; k++)
            {
                var (done, part) = (i + (double)k / parts.Count, parts[k]);
                var partProgress = new SyncProgress<JobProgress>(p =>
                    progress.Report(p with { Fraction = double.IsNaN(p.Fraction) ? p.Fraction : (done + p.Fraction / parts.Count) / job.Inputs.Count }));
                var seconds = part.End - part.Start;
                var pending = namer.Reserve(input, "." + extension, parts.Count == 1 ? "-trimmed" : $"-part{k + 1}");
                await ffmpeg.EncodeAsync(
                    ["-ss", Seconds(part.Start), "-t", Seconds(seconds), "-protocol_whitelist", "file", "-f", demuxer, "-i", input, "-map_metadata", "0", .. output, pending.Path],
                    seconds, partProgress, ct).ConfigureAwait(false);
            }

            made++;
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    private async Task<(string Extension, string[] Output)> ExactTrimArgsAsync(FileFormat format, MediaInfo info, CancellationToken ct)
    {
        if (info.VideoCodec is not null)
        {
            // Sound re-encoded too: copied sound starts at a packet before the cut, and the muxer shifts the picture to match.
            var container = format.Id is "mkv" or "webm" ? format.Id : "mp4";
            var video = await VideoArgsAsync(container, null, 23, kbps: null, ct).ConfigureAwait(false);
            return (container, Video(video, VideoOutputs[Muxer(container)].Audio, container));
        }

        var extension = AudioOutputs.ContainsKey(format.Id) ? format.Id : "m4a";
        return (extension, ["-map", "0:a:0", "-vn", .. AudioOutputs[extension].Audio, "-f", AudioOutputs[extension].Muxer]);
    }

    // Change speed (ED-7): re-encoded like an exact trim (MKV/WebM stay, other video → MP4, a sound file keeps its format), the picture
    // retimed (setpts) at the source frame rate (cfr: frames dropped when faster, repeated when slower), the sound by atempo, which keeps its pitch.
    private async Task SpeedAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var speed = Speeds.Parse(job.Parameters);
        if (speed == 1)
        {
            throw new EngineException(job.Inputs is [var one] ? $"{Path.GetFileName(one)} is already at that speed." : "These are already at that speed.");
        }

        var tempo = Speeds.Atempo(speed);
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var format = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);
            var demuxer = Demuxer(input, format);
            var info = await ffmpeg.ProbeAsync(input, demuxer, ct).ConfigureAwait(false);
            string extension;
            string[] output;
            if (info.VideoCodec is not null)
            {
                extension = format.Id is "mkv" or "webm" ? format.Id : "mp4";
                var video = await VideoArgsAsync(extension, null, 23, kbps: null, ct, filter: $"setpts=PTS/{Seconds(speed)}").ConfigureAwait(false);
                output = Video([.. video, "-fps_mode", "cfr"], [.. VideoOutputs[Muxer(extension)].Audio, "-af", tempo], extension);
            }
            else
            {
                extension = AudioOutputs.ContainsKey(format.Id) ? format.Id : "m4a";
                output = ["-map", "0:a:0", "-vn", .. AudioOutputs[extension].Audio, "-af", tempo, "-f", AudioOutputs[extension].Muxer];
            }

            var done = i;
            var fileProgress = new SyncProgress<JobProgress>(p =>
                progress.Report(p with { Fraction = double.IsNaN(p.Fraction) ? p.Fraction : (done + p.Fraction) / job.Inputs.Count }));
            var pending = namer.Reserve(input, "." + extension, Speeds.Suffix(speed));
            await ffmpeg.EncodeAsync(["-protocol_whitelist", "file", "-f", demuxer, "-i", input, "-map_metadata", "0", .. output, pending.Path],
                info.Duration / speed, fileProgress, ct).ConfigureAwait(false);
        }
    }

    // Save frames (ED-8): one full-size PNG per time, upright (ffmpeg's autorotate). Each time is a frame's start; the seek goes a quarter
    // frame earlier so rounding never lands on the next frame (an input seek decodes up to the first frame at or after it). Times past the
    // end are dropped (Same for all on a shorter video).
    private async Task FramesAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var times = Frames.Parse(job.Parameters);
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            var format = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);
            var demuxer = Demuxer(input, format);
            var info = await ffmpeg.ProbeAsync(input, demuxer, ct).ConfigureAwait(false);
            if (info.VideoCodec is null)
            {
                skipped = $"{name} has no picture.";
                continue;
            }

            var kept = times.Where(t => info.Duration is not { } d || t < d).ToList();
            if (kept.Count == 0)
            {
                skipped = $"{name} is shorter than the frames you picked.";
                continue;
            }

            var quarter = 0.25 / (FrameRate(info) ?? 30);
            for (var k = 0; k < kept.Count; k++)
            {
                progress.Report(new JobProgress((i + (double)k / kept.Count) / job.Inputs.Count, null));
                var pending = namer.Reserve(input, ".png", Frames.Suffix(kept[k]));
                await ffmpeg.EncodeAsync(
                    ["-ss", Seconds(Math.Max(0, kept[k] - quarter)), "-protocol_whitelist", "file", "-f", demuxer, "-i", input,
                        "-map", "0:V:0", "-frames:v", "1", "-c:v", "png", "-f", "image2", "-update", "1", pending.Path],
                    null, new SyncProgress<JobProgress>(_ => { }), ct).ConfigureAwait(false);
            }

            made++;
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    // ffprobe's average frame rate ("30000/1001", "25/1") as a number; null when unknown.
    private static double? FrameRate(MediaInfo info)
    {
        var parts = info.Fps?.Split('/');
        return parts is [var n, var d] && double.TryParse(n, CultureInfo.InvariantCulture, out var num) && double.TryParse(d, CultureInfo.InvariantCulture, out var den)
            && den > 0 && num / den is > 0 and < 1000 and var fps
            ? fps
            : null;
    }

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    // The sound as it is when a plain audio file can hold it (video.mp4 → video.m4a in seconds), else AAC in M4A.
    private static (string, string, bool, string[]) ExtractArgs(string codec)
    {
        (string Extension, string Muxer)? copy = codec switch
        {
            "aac" => ("m4a", "ipod"),
            "mp3" => ("mp3", "mp3"),
            "opus" => ("opus", "opus"),
            "vorbis" => ("ogg", "ogg"),
            "flac" => ("flac", "flac"),
            "pcm_s16le" => ("wav", "wav"),
            _ => null,
        };
        return copy is var (extension, muxer)
            ? (extension, "", true, ["-map", "0:a:0", "-vn", "-c:a", "copy", "-f", muxer])
            : ("m4a", "", true, ["-map", "0:a:0", "-vn", .. AudioOutputs["m4a"].Audio, "-f", "ipod"]);
    }

    // One file from all inputs in order (name-merged.ext after the first). Video: every clip scaled and letterboxed into the
    // first one's frame at its frame rate, silence where a clip has no sound; MKV/WebM stay, other video → MP4. Audio: the
    // first file's format. Each input gets its own forced demuxer and the file-only protocol, never the concat demuxer
    // (it opens whatever paths a file lists).
    private async Task MergeAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (job.Inputs.Count < 2)
        {
            throw new EngineException("Merge needs two or more videos, or two or more sound files.");
        }

        var inputs = new List<(string Path, string Demuxer, FileFormat Format, MediaInfo Info)>();
        foreach (var input in job.Inputs)
        {
            var format = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);
            var demuxer = Demuxer(input, format);
            inputs.Add((input, demuxer, format, await ffmpeg.ProbeAsync(input, demuxer, ct).ConfigureAwait(false)));
        }

        var video = inputs[0].Info.VideoCodec is not null;
        if (inputs.Any(i => (i.Info.VideoCodec is not null) != video))
        {
            throw new EngineException("Merge can't mix videos with sound-only files.");
        }

        var duration = inputs.All(i => i.Info.Duration is not null) ? inputs.Sum(i => i.Info.Duration) : null;
        var withSound = inputs.Any(i => i.Info.AudioCodec is not null);
        var graph = new StringBuilder();
        var joined = new StringBuilder();
        const string Sound = "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo";
        string[] output;
        string extension;
        if (video)
        {
            extension = inputs[0].Format.Id is "mkv" or "webm" ? inputs[0].Format.Id : "mp4";
            var (width, height) = (Math.Max(2, inputs[0].Info.Width / 2 * 2), Math.Max(2, inputs[0].Info.Height / 2 * 2));
            var fps = inputs[0].Info.Fps ?? "30";
            for (var i = 0; i < inputs.Count; i++)
            {
                graph.Append(CultureInfo.InvariantCulture,
                    $"[{i}:V:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format={PixelFormat(extension)}[v{i}];");
                joined.Append(CultureInfo.InvariantCulture, $"[v{i}]");
                if (withSound)
                {
                    if (inputs[i].Info.AudioCodec is not null)
                    {
                        graph.Append(CultureInfo.InvariantCulture, $"[{i}:a:0]{Sound}[a{i}];");
                    }
                    else
                    {
                        graph.Append(CultureInfo.InvariantCulture, $"anullsrc=r=48000:cl=stereo,atrim=duration={inputs[i].Info.Duration ?? 0},{Sound}[a{i}];");
                    }

                    joined.Append(CultureInfo.InvariantCulture, $"[a{i}]");
                }
            }

            graph.Append(CultureInfo.InvariantCulture, $"{joined}concat=n={inputs.Count}:v=1:a={(withSound ? 1 : 0)}[v]{(withSound ? "[a]" : "")}");
            output =
            [
                "-map", "[v]", .. withSound ? ["-map", "[a]"] : Array.Empty<string>(),
                .. await EncoderAsync(extension, 23, kbps: null, ct).ConfigureAwait(false),
                .. withSound ? VideoOutputs[Muxer(extension)].Audio : [],
                .. VideoOutputs[Muxer(extension)].Muxer,
            ];
        }
        else
        {
            extension = inputs[0].Format.Id;
            for (var i = 0; i < inputs.Count; i++)
            {
                graph.Append(CultureInfo.InvariantCulture, $"[{i}:a:0]{Sound}[a{i}];");
                joined.Append(CultureInfo.InvariantCulture, $"[a{i}]");
            }

            graph.Append(CultureInfo.InvariantCulture, $"{joined}concat=n={inputs.Count}:v=0:a=1[a]");
            output = ["-map", "[a]", .. AudioOutputs[extension].Audio, "-f", AudioOutputs[extension].Muxer];
        }

        var pending = namer.Reserve(inputs[0].Path, "." + extension, "-merged");
        await ffmpeg.EncodeAsync(
            [.. inputs.SelectMany(i => new[] { "-protocol_whitelist", "file", "-f", i.Demuxer, "-i", i.Path }), "-filter_complex", graph.ToString(), .. output, pending.Path],
            duration, progress, ct).ConfigureAwait(false);
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

    // Constant bit rate makes audio size predictable: bytes ≈ kbps × seconds / 8. One retry if the container overhead tips it over.
    private async Task<string?> FitAudioAsync(string input, string demuxer, MediaInfo info, string container, long limit, string path,
        IProgress<JobProgress> progress, CancellationToken ct)
    {
        var name = Path.GetFileName(input);
        if (info.Duration is not { } seconds)
        {
            return $"Morph can't tell how long {name} is, so it can't aim for a size.";
        }

        var kbps = limit * 8 * 0.95 / seconds / 1000;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (kbps < 24)
            {
                return $"{name} is too long to fit under {Presets.Megabytes(limit)} MB and still sound right.";
            }

            var args = AudioArgs(container, Preset.High, null);
            args[Array.IndexOf(args, "-b:a") + 1] = $"{(int)kbps}k";
            await EncodeAsync(input, demuxer, args, path, info, progress, ct).ConfigureAwait(false);
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
    // filter (rotate's transpose) runs before the scale.
    private async Task<string[]> VideoArgsAsync(string container, (int Width, int Height)? size, int quality, int? kbps, CancellationToken ct, string? filter = null)
    {
        var scale = size is var (w, h) ? $"scale={w / 2 * 2}:{h / 2 * 2}" : "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        return ["-vf", $"{(filter is null ? "" : filter + ",")}{scale},format={PixelFormat(container)}", .. await EncoderAsync(container, quality, kbps, ct).ConfigureAwait(false)];
    }

    private static string PixelFormat(string container) => container == "webm" ? "yuv420p" : "nv12";

    private async Task<string[]> EncoderAsync(string container, int quality, int? kbps, CancellationToken ct)
    {
        if (container == "webm")
        {
            string[] rate = kbps is { } k ? ["-b:v", $"{k}k"] : ["-crf", $"{quality + 9}", "-b:v", "0"]; // VP9's CRF runs about 9 above x264's for the same look
            return
            [
                "-c:v", "libvpx-vp9", .. rate, "-deadline", "realtime", "-cpu-used", "6", "-row-mt", "1",
                "-tile-columns", "2", "-threads", $"{Environment.ProcessorCount}",
            ];
        }

        var h264 = await probe.H264Async(ct).ConfigureAwait(false)
            ?? throw new EngineException("This PC has no H.264 video encoder. On Windows N, install the Media Feature Pack.");
        return kbps is { } bitrate ? h264.Bitrate(bitrate) : h264.Quality(quality);
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
