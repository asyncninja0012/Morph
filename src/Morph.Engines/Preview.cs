using System.Text.Json.Nodes;

using ImageMagick;

using Morph.Core.Jobs;

namespace Morph.Engines;

/// <summary>
/// The "preview" job (ED-1..3): a downscaled picture of the file for an editor, written as a <see cref="PreviewFile"/>
/// into the job's (App temp) folder. Parameters: { "maxEdge": 2048, "page": 1 } (page: PDF only, 1-based).
/// </summary>
internal static class Preview
{
    public static async Task WriteAsync(MagickImage image, double sourceWidth, double sourceHeight, JobRequest job, OutputNamer namer, CancellationToken ct, int pages = 1, double duration = 0)
    {
        var maxEdge = (uint)Math.Clamp(job.Parameters?["maxEdge"]?.GetValue<int>() ?? 2048, 64, PreviewFile.MaxEdge);
        if (image.Width > maxEdge || image.Height > maxEdge)
        {
            image.Resize(new MagickGeometry(maxEdge, maxEdge)); // fits inside, aspect kept
        }

        if (!image.HasAlpha)
        {
            image.Alpha(AlphaOption.Set); // opaque
        }

        using var pixels = image.GetPixelsUnsafe();
        var bgra = pixels.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidOperationException("Magick gave no pixels.");
        var pending = namer.Reserve(job.Inputs[0], ".preview");
        await PreviewFile.WriteAsync(pending.Path, new PreviewImage((int)image.Width, (int)image.Height, sourceWidth, sourceHeight, bgra, pages, duration), ct).ConfigureAwait(false);
    }
}
