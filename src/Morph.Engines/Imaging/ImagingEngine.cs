using ImageMagick;

using Morph.Core.Jobs;

namespace Morph.Engines.Imaging;

/// <summary>Image ↔ image conversion with Magick.NET (PLAN §5). Tool ids: convert.png/jpg/webp/gif/bmp.</summary>
public sealed class ImagingEngine : IEngine
{
    private static readonly Dictionary<string, MagickFormat> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = MagickFormat.Png,
        ["jpg"] = MagickFormat.Jpeg,
        ["webp"] = MagickFormat.WebP,
        ["gif"] = MagickFormat.Gif,
        ["bmp"] = MagickFormat.Bmp,
    };

    static ImagingEngine()
    {
        ResourceLimits.Memory = Math.Min(1UL << 30, (ulong)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4));
        ResourceLimits.Width = 30_000;
        ResourceLimits.Height = 30_000;
    }

    public string Id => "imaging";

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var target = job.ToolId.StartsWith("convert.", StringComparison.Ordinal) ? job.ToolId["convert.".Length..] : "";
        if (!Targets.TryGetValue(target, out var outFormat))
        {
            throw new EngineException("This conversion isn't available yet.", $"imaging: unsupported tool {job.ToolId}");
        }

        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous);

            // The format comes from magic bytes, never from Magick's own detection, so a hostile file can't reach
            // script-like coders (MVG, MSL, SVG, PS).
            var inFormat = Sniff(source) ?? throw new EngineException($"{Path.GetFileName(input)} isn't an image Morph can read.");
            using var image = new MagickImage();
            await image.ReadAsync(source, new MagickReadSettings { Format = inFormat }, ct).ConfigureAwait(false);
            image.AutoOrient(); // phone photos store rotation in EXIF; formats without EXIF would show them sideways
            if (outFormat is MagickFormat.Jpeg or MagickFormat.Bmp)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }

            var pending = namer.Reserve(input, "." + target);
            await using (var output = new FileStream(pending.TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await image.WriteAsync(output, outFormat, ct).ConfigureAwait(false);
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }
    }

    // shortcut: magic-byte check for the formats Phase 0 needs; FileSniffer (task 1.1) replaces it.
    internal static MagickFormat? Sniff(Stream stream)
    {
        Span<byte> h = stackalloc byte[12];
        var n = stream.ReadAtLeast(h, h.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        h = h[..n];
        return h switch
        {
            [0xFF, 0xD8, 0xFF, ..] => MagickFormat.Jpeg,
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => MagickFormat.Png,
            [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => MagickFormat.Gif,
            [(byte)'B', (byte)'M', ..] => MagickFormat.Bmp,
            [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P'] => MagickFormat.WebP,
            [(byte)'I', (byte)'I', 0x2A, 0x00, ..] or [(byte)'M', (byte)'M', 0x00, 0x2A, ..] => MagickFormat.Tiff,
            _ => null,
        };
    }
}
