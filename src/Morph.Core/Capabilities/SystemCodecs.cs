using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Morph.Core.Capabilities;

/// <summary>Probes Windows codecs that come from Store extensions (CV-6). Synthetic input only, so safe in the App process.</summary>
public static class SystemCodecs
{
    /// <summary>Requirement id in capabilities.json for HEIC output.</summary>
    public const string HevcExtension = "hevc-ext";

    /// <summary>
    /// True when WIC can write HEIC: needs the HEIF Image Extensions and an HEVC encoder (HEVC Video Extensions).
    /// The encoder can be listed while HEVC is missing, so this encodes a 16×16 image rather than checking the list.
    /// </summary>
    public static async Task<bool> CanEncodeHeicAsync()
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 16, 16, 96, 96, new byte[16 * 16 * 4]);
            await encoder.FlushAsync();
            return stream.Size > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // WIC reports a missing codec as a COMException with a WINCODEC_ERR_* or MF HRESULT
        }
    }
}
