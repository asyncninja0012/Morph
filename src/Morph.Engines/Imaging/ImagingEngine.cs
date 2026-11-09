using System.Text.Json.Nodes;

using ImageMagick;

using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Imaging;

/// <summary>
/// Image ↔ image conversion (CV-2) with Magick.NET; HEIC output through Windows (<see cref="WicHeic"/>).
/// Tool ids: convert.png/jpg/webp/heic/avif/tiff/gif/bmp; compress, resize, rotate and strip-metadata (IT-1..3) keep the format.
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

        if (job.ToolId is "rotate" or "strip-metadata")
        {
            await EditAsync(job, namer, progress, ct).ConfigureAwait(false);
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
        var limit = compress ? Presets.TargetBytes(job.Parameters) : null;
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            var original = new FileInfo(input).Length;
            if (limit is { } under && original <= under)
            {
                skipped = $"{name} is already under {Presets.Megabytes(under)} MB.";
                continue;
            }

            var format = Targets[(await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id];
            using var image = await ReadAsync(input, ct).ConfigureAwait(false);
            byte[]? bytes;
            if (!compress)
            {
                if (Presets.Resize((int)image.Width, (int)image.Height, job.Parameters) is not var (width, height))
                {
                    skipped = $"{name} is already that size.";
                    continue;
                }

                image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
                bytes = image.ToByteArray(format);
            }
            else if (limit is { } target)
            {
                bytes = FitUnder(image, format, job.Parameters, target, ct);
                skipped = bytes is null ? $"Morph couldn't get {name} under {Presets.Megabytes(target)} MB." : skipped;
            }
            else
            {
                // Small and Balanced also cap the long edge: a 12 MP phone photo is mostly pixels no email reader sees.
                var (edge, level) = Presets.Compress(job.Parameters) switch
                {
                    Preset.Small => (1600, format == MagickFormat.Png ? 128u : 65u),
                    Preset.Balanced => (2560, format == MagickFormat.Png ? 256u : 78u),
                    _ => (int.MaxValue, format == MagickFormat.Png ? 0u : 88u),
                };
                bytes = Encode(image, format, level, Presets.CompressSize((int)image.Width, (int)image.Height, job.Parameters, edge));
                if (bytes.Length >= original)
                {
                    bytes = null;
                    skipped = $"{name} is already as small as Morph can make it at this setting.";
                }
            }

            if (bytes is not null)
            {
                var pending = namer.Reserve(input, Path.GetExtension(input), compress ? "-compressed" : "-resized");
                await File.WriteAllBytesAsync(pending.Path, bytes, ct).ConfigureAwait(false);
                made++;
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    // Rotate turns the upright picture (EXIF orientation already applied). Strip info drops EXIF (GPS, camera, dates), XMP, IPTC
    // and comments but keeps the colour profile. shortcut: lossy formats re-encode (JPEG at the quality Magick reads from the file,
    // WebP/AVIF at Magick's defaults); a lossless JPEG path (jpegtran-style) if users notice.
    private static async Task EditAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var rotate = job.ToolId == "rotate";
        var (degrees, flip) = Presets.Rotate(job.Parameters);
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            var format = Targets[(await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id];
            using var image = await ReadAsync(input, ct).ConfigureAwait(false);
            if (rotate)
            {
                image.Rotate(degrees);
                if (flip == "horizontal")
                {
                    image.Flop();
                }
                else if (flip == "vertical")
                {
                    image.Flip();
                }
            }
            else
            {
                foreach (var profile in new[] { "exif", "xmp", "iptc", "8bim" })
                {
                    image.RemoveProfile(profile);
                }

                image.RemoveAttribute("comment");
                image.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time"); // Magick's own date:create/modify text
            }

            var pending = namer.Reserve(input, Path.GetExtension(input), rotate ? "-rotated" : "-stripped");
            await File.WriteAllBytesAsync(pending.Path, image.ToByteArray(format), ct).ConfigureAwait(false);
            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }
    }

    // Steps quality down at full size (or the size the Compress window chose), then shrinks the picture at the lowest quality.
    private static byte[]? FitUnder(MagickImage image, MagickFormat format, JsonObject? parameters, long limit, CancellationToken ct)
    {
        uint[] levels = format == MagickFormat.Png ? [256, 128, 64, 32] : [85, 75, 65, 55, 45, 35];
        var (width, height) = Presets.CompressSize((int)image.Width, (int)image.Height, parameters, int.MaxValue) ?? ((int)image.Width, (int)image.Height);
        foreach (var level in levels)
        {
            ct.ThrowIfCancellationRequested();
            if (Encode(image, format, level, (width, height)) is { } bytes && bytes.Length <= limit)
            {
                return bytes;
            }
        }

        for (var scale = 0.75; Math.Max(width, height) * scale >= 160; scale *= 0.75)
        {
            ct.ThrowIfCancellationRequested();
            var size = (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
            if (Encode(image, format, levels[^1], size) is { } bytes && bytes.Length <= limit)
            {
                return bytes;
            }
        }

        return null;
    }

    /// <param name="level">JPEG-style quality, or for PNG the colour count (0 = lossless; TinyPNG-style tools quantize to 256).</param>
    private static byte[] Encode(MagickImage image, MagickFormat format, uint level, (int Width, int Height)? size)
    {
        using var copy = (MagickImage)image.Clone();
        if (size is var (width, height))
        {
            copy.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        }

        if (format == MagickFormat.Png)
        {
            if (level > 0)
            {
                copy.Quantize(new QuantizeSettings { Colors = level, DitherMethod = DitherMethod.FloydSteinberg });
            }

            copy.Quality = 95; // zlib level 9, adaptive filtering
        }
        else
        {
            // AVIF's scale sits lower: Magick writes it at 50 unless told otherwise, about what JPEG looks like at 78.
            copy.Quality = format == MagickFormat.Avif ? Math.Max(1, level - 35) : level;
        }

        return copy.ToByteArray(format);
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
