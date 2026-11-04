using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Media;

/// <summary>Video conversion through ffmpeg (PLAN §5). Phase 0: MOV/MP4 → MP4 (H.264 + AAC).</summary>
public sealed class MediaEngine(Ffmpeg ffmpeg, EncoderProbe probe) : IEngine
{
    public MediaEngine()
        : this(Ffmpeg.Bundled, EncoderProbe.Default)
    {
    }

    public string Id => "media";

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (job.ToolId != "convert.mp4")
        {
            throw new EngineException("This conversion isn't available yet.", $"media: unsupported tool {job.ToolId}");
        }

        if (!ffmpeg.IsAvailable)
        {
            throw new EngineException("Morph's video converter is missing. Reinstall Morph.", $"not found: {ffmpeg.FfmpegPath}");
        }

        var encoder = await probe.H264Async(ct).ConfigureAwait(false)
            ?? throw new EngineException("This PC has no H.264 video encoder. On Windows N, install the Media Feature Pack.");

        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var demuxer = (await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id switch
            {
                "mp4" or "mov" => "mov", // ffmpeg's mov demuxer reads all ISO-BMFF
                _ => null, // shortcut: MKV/WebM/AVI input lands with task 1.6
            } ?? throw new EngineException($"{Path.GetFileName(input)} isn't a video Morph can read.");
            var duration = await ffmpeg.DurationAsync(input, demuxer, ct).ConfigureAwait(false);
            var pending = namer.Reserve(input, ".mp4");
            var done = i;
            var fileProgress = new SyncProgress<JobProgress>(p =>
                progress.Report(p with { Fraction = double.IsNaN(p.Fraction) ? p.Fraction : (done + p.Fraction) / job.Inputs.Count }));

            // The demuxer is forced and only the file protocol allowed, so a hostile file can't reach
            // playlist/concat demuxers that open other files or URLs.
            string[] args =
            [
                "-protocol_whitelist", "file", "-f", demuxer, "-i", input,
                "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "0",
                "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=nv12", // H.264 4:2:0 needs even dimensions
                .. encoder.Args,
                "-c:a", "aac", "-b:a", "160k",
                "-movflags", "+faststart", "-f", "mp4", pending.TempPath,
            ];
            await ffmpeg.EncodeAsync(args, duration, fileProgress, ct).ConfigureAwait(false);
        }
    }
}
