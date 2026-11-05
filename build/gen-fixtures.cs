#:package Magick.NET-Q8-x64

// Writes one sample file per input format in capabilities.json to tests/fixtures/ (git-ignored), for the golden tests.
// Run from the repo root after ./build/fetch-binaries.ps1:  dotnet run build/gen-fixtures.cs
// Images: 160×120 drawn shapes. Video: 1 s 320×240 testsrc2 at 25 fps with a sine tone. Audio: 1 s sine.
// HEIC is written through Windows (WIC) and is skipped when the HEVC Video Extensions are missing.

using System.Diagnostics;

using ImageMagick;
using ImageMagick.Drawing;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

var root = Directory.GetCurrentDirectory();
var ffmpeg = Path.Combine(root, "artifacts", "binaries", "ffmpeg.exe");
if (!File.Exists(Path.Combine(root, "Morph.slnx")) || !File.Exists(ffmpeg))
{
    Console.Error.WriteLine("Run from the repo root after ./build/fetch-binaries.ps1.");
    return 1;
}

var dir = Directory.CreateDirectory(Path.Combine(root, "tests", "fixtures")).FullName;

using (var image = new MagickImage(MagickColors.White, 160, 120))
{
    new Drawables()
        .FillColor(MagickColors.Red).Rectangle(10, 10, 70, 60)
        .FillColor(MagickColors.Blue).Circle(115, 80, 115, 110)
        .StrokeColor(MagickColors.Black).StrokeWidth(4).Line(0, 119, 159, 0)
        .Draw(image);
    foreach (var format in new[] { MagickFormat.Png, MagickFormat.Jpg, MagickFormat.WebP, MagickFormat.Gif, MagickFormat.Bmp, MagickFormat.Tiff, MagickFormat.Avif })
    {
        image.Write(Path.Combine(dir, $"sample.{format.ToString().ToLowerInvariant()}"), format);
    }

    try
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, image.Width, image.Height, 96, 96,
            image.GetPixelsUnsafe().ToByteArray(PixelMapping.BGRA)!);
        await encoder.FlushAsync();
        await using var file = File.Create(Path.Combine(dir, "sample.heic"));
        await stream.GetInputStreamAt(0).AsStreamForRead().CopyToAsync(file);
    }
    catch (Exception ex)
    {
        File.Delete(Path.Combine(dir, "sample.heic"));
        Console.WriteLine($"sample.heic skipped (needs HEVC Video Extensions): {ex.Message}");
    }
}

string[] video = ["-f", "lavfi", "-i", "testsrc2=d=1:s=320x240:r=25"];
string[] tone = ["-f", "lavfi", "-i", "sine=d=1"];
(string Name, string[] Args)[] media =
[
    ("mp4", [.. video, .. tone, "-c:v", "mpeg4", "-c:a", "aac"]),
    ("mov", [.. video, .. tone, "-c:v", "mpeg4", "-c:a", "pcm_s16le"]),
    ("mkv", [.. video, .. tone, "-c:v", "mpeg4", "-c:a", "flac"]),
    ("webm", [.. video, .. tone, "-c:v", "libvpx", "-c:a", "libvorbis"]),
    ("avi", [.. video, .. tone, "-c:v", "mpeg4", "-c:a", "pcm_s16le"]),
    ("wmv", [.. video, .. tone, "-c:v", "wmv2", "-c:a", "wmav2"]),
    ("mp3", [.. tone, "-c:a", "libmp3lame"]),
    ("m4a", [.. tone, "-c:a", "aac"]),
    ("wav", [.. tone, "-c:a", "pcm_s16le"]),
    ("flac", [.. tone, "-c:a", "flac"]),
    ("ogg", [.. tone, "-c:a", "libvorbis"]),
    ("opus", [.. tone, "-c:a", "libopus"]),
];
foreach (var (name, ffArgs) in media)
{
    var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true };
    foreach (var a in (string[])["-hide_banner", "-nostdin", "-loglevel", "error", "-y", .. ffArgs, Path.Combine(dir, $"sample.{name}")])
    {
        start.ArgumentList.Add(a);
    }

    using var process = Process.Start(start)!;
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine($"sample.{name} failed: {stderr}");
        return 1;
    }
}

Console.WriteLine($"Fixtures in {dir}");
return 0;
