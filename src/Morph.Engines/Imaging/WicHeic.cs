using ImageMagick;

using Morph.Core.Jobs;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Morph.Engines.Imaging;

/// <summary>
/// HEIC output through Windows (PLAN §5): there is no LGPL-compatible HEVC encoder to bundle, so this uses the
/// HEIF + HEVC Store extensions. The App hides HEIC output when its probe finds them missing.
/// </summary>
internal static class WicHeic
{
    public static async Task WriteAsync(MagickImage image, string path, int? quality, CancellationToken ct)
    {
        using var pixels = image.GetPixelsUnsafe();
        var bgra = pixels.ToByteArray(PixelMapping.BGRA)
            ?? throw new EngineException("Morph couldn't read this image's pixels.");
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            BitmapPropertySet? options = quality is { } q
                ? new() { ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(q, 1, 100) / 100f, Windows.Foundation.PropertyType.Single) }
                : null;
            var encoder = options is null
                ? await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream)
                : await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream, options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, image.Width, image.Height, 96, 96, bgra);
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await stream.GetInputStreamAt(0).AsStreamForRead().CopyToAsync(output, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or IOException))
        {
            throw new EngineException("This PC can't save HEIC. Install HEVC Video Extensions from the Microsoft Store.", ex.ToString());
        }
    }
}
