using ImageMagick;

using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Imaging;

/// <summary>
/// Image ↔ image conversion (CV-2) with Magick.NET; HEIC output through Windows (<see cref="WicHeic"/>).
/// Tool ids: convert.png/jpg/webp/heic/avif/tiff/gif/bmp.
/// </summary>
public sealed class ImagingEngine : IEngine
{
    private static readonly Dictionary<string, MagickFormat> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = MagickFormat.Png,
        ["jpg"] = MagickFormat.Jpeg,
        ["webp"] = MagickFormat.WebP,
        ["gif"] = MagickFormat.Gif,
        ["bmp"] = MagickFormat.Bmp,
        ["tiff"] = MagickFormat.Tiff,
        ["avif"] = MagickFormat.Avif,
        ["heic"] = MagickFormat.Heic, // written by WIC, not Magick
    };

    // HEIC/AVIF decode uses libheif/libde265 inside Magick.Native (LGPL, replaceable DLL; PLAN R5).
    private static readonly Dictionary<string, MagickFormat> Readable = new()
    {
        ["jpg"] = MagickFormat.Jpeg,
        ["png"] = MagickFormat.Png,
        ["gif"] = MagickFormat.Gif,
        ["bmp"] = MagickFormat.Bmp,
        ["webp"] = MagickFormat.WebP,
        ["tiff"] = MagickFormat.Tiff,
        ["heic"] = MagickFormat.Heic,
        ["avif"] = MagickFormat.Avif,
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
            using var image = await ReadAsync(input, ct).ConfigureAwait(false);
            if (outFormat is MagickFormat.Jpeg or MagickFormat.Bmp or MagickFormat.Heic)
            {
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }

            var quality = job.Parameters?["quality"]?.GetValue<int>();
            if (quality is { } q)
            {
                image.Quality = (uint)Math.Clamp(q, 1, 100); // lossy outputs only: PNG reads it as zlib level + filter
            }

            var pending = namer.Reserve(input, "." + target);
            if (outFormat == MagickFormat.Heic)
            {
                await WicHeic.WriteAsync(image, pending.Path, quality, ct).ConfigureAwait(false);
            }
            else
            {
                await using var output = new FileStream(pending.Path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                await image.WriteAsync(output, outFormat, ct).ConfigureAwait(false);
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }
    }

    /// <summary>Reads the first frame of an image, upright. Also used to put images into PDFs.</summary>
    internal static async Task<MagickImage> ReadAsync(string input, CancellationToken ct)
    {
        var sniffed = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);

        // The format comes from magic bytes, never from Magick's own detection, so a hostile file can't reach
        // script-like coders (MVG, MSL, SVG, PS).
        if (!Readable.TryGetValue(sniffed.Id, out var inFormat))
        {
            throw new EngineException($"{Path.GetFileName(input)} isn't an image Morph can read.");
        }

        await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        var image = new MagickImage();
        try
        {
            await image.ReadAsync(source, new MagickReadSettings { Format = inFormat }, ct).ConfigureAwait(false);
            image.AutoOrient(); // phone photos store rotation in EXIF; formats without EXIF would show them sideways
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
