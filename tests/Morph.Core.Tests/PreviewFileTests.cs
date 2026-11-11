using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class PreviewFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("morph-preview-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task A_preview_round_trips()
    {
        var path = Path.Combine(_folder, "a.preview");
        var preview = new PreviewImage(2, 1, 595.5, 842, [1, 2, 3, 255, 4, 5, 6, 128], 12, 61.25);

        await PreviewFile.WriteAsync(path, preview, TestContext.Current.CancellationToken);
        var read = await PreviewFile.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal((2, 1, 595.5, 842.0, 12, 61.25), (read.Width, read.Height, read.SourceWidth, read.SourceHeight, read.Pages, read.Duration));
        Assert.Equal(preview.Bgra, read.Bgra);
    }

    [Theory]
    [InlineData(2, 1, 7)]      // pixels missing
    [InlineData(5000, 1, 20000)] // over MaxEdge
    [InlineData(0, 1, 0)]
    public async Task A_bad_preview_is_refused(int width, int height, int pixelBytes)
    {
        var path = Path.Combine(_folder, "bad.preview");
        await PreviewFile.WriteAsync(path, new PreviewImage(width, height, 10, 10, new byte[pixelBytes]), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => PreviewFile.ReadAsync(path, TestContext.Current.CancellationToken));
    }
}
