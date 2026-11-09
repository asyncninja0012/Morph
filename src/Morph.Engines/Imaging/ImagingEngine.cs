using ImageMagick;

using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Imaging;

/// <summary>
/// Image ↔ image conversion (CV-2) with Magick.NET; HEIC output through Windows (<see cref="WicHeic"/>).
/// Tool ids: convert.png/jpg/webp/heic/avif/tiff/gif/bmp; compress and resize (IT-1, IT-2) keep the format.
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
        if (job.ToolId is "compress" or "resize")
        {
            await ShrinkAsync(job, namer, progress, ct).ConfigureAwait(false);
            return;
        }

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

    // A file already small enough is skipped; the job fails only when every file was.
    private static async Task ShrinkAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var compress = job.ToolId == "compress";
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            var format = Targets[(await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id];
            using var image = await ReadAsync(input, ct).ConfigureAwait(false);
            var size = compress ? Compress(image, format, Presets.Compress(job.Parameters)) : Presets.Resize((int)image.Width, (int)image.Height, job.Parameters);
            if (!compress && size is null)
            {
                skipped = $"{name} is already that small.";
                continue;
            }

            if (size is var (width, height))
            {
                image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
            }

            var pending = namer.Reserve(input, Path.GetExtension(input), compress ? "-compressed" : "-resized");
            await using (var output = new FileStream(pending.Path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await image.WriteAsync(output, format, ct).ConfigureAwait(false);
            }

            if (compress && new FileInfo(pending.Path).Length >= new FileInfo(input).Length)
            {
                namer.Discard(pending);
                skipped = $"{name} is already as small as Morph can make it at this setting.";
            }
            else
            {
                made++;
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    // Small and Balanced also cap the long edge: a 12 MP phone photo is mostly pixels no email reader sees.
    // PNG has no quality knob; Small and Balanced reduce it to 128/256 colours (what TinyPNG-style tools do), High stays lossless.
    private static (int Width, int Height)? Compress(MagickImage image, MagickFormat format, Preset preset)
    {
        var (edge, quality, colours) = preset switch
        {
            Preset.Small => (1600, 65u, 128u),
            Preset.Balanced => (2560, 78u, 256u),
            _ => (int.MaxValue, 88u, 0u),
        };
        if (format == MagickFormat.Png)
        {
            if (colours > 0)
            {
                image.Quantize(new QuantizeSettings { Colors = colours, DitherMethod = DitherMethod.FloydSteinberg });
            }

            image.Quality = 95; // zlib level 9, adaptive filtering
        }
        else
        {
            // AVIF's scale sits lower: Magick writes it at 50 unless told otherwise, about what JPEG looks like at 78.
            image.Quality = format == MagickFormat.Avif ? quality - 35 : quality;
        }

        return Presets.Fit((int)image.Width, (int)image.Height, edge);
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
