using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class OutputNamerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-namer-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Reserve_creates_the_final_name_at_once_in_the_output_folder()
    {
        var namer = new OutputNamer(_dir);

        var p = namer.Reserve(@"C:\elsewhere\photo.jpg", ".png");

        Assert.Equal(Path.Combine(_dir, "photo.png"), p.Path);
        Assert.Equal(0, new FileInfo(p.Path).Length);
        File.WriteAllText(p.Path, "x"); // the engine overwrites the placeholder
        Assert.Equal([p.Path], namer.CommitAll());
    }

    [Fact]
    public void Clashes_get_numbered_and_never_overwrite()
    {
        File.WriteAllText(Path.Combine(_dir, "photo.png"), "original");
        Directory.CreateDirectory(Path.Combine(_dir, "photo (2).png"));
        var namer = new OutputNamer(_dir);

        var a = namer.Reserve("photo.jpg", ".png");
        var b = namer.Reserve("photo.jpeg", ".png");

        Assert.Equal([Path.Combine(_dir, "photo (3).png"), Path.Combine(_dir, "photo (4).png")], [a.Path, b.Path]);
        Assert.Equal("original", File.ReadAllText(Path.Combine(_dir, "photo.png")));
    }

    [Fact]
    public void Abort_deletes_only_what_it_reserved()
    {
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "x");
        var namer = new OutputNamer(_dir);
        File.WriteAllText(namer.Reserve("photo.jpg", ".png").Path, "partial");
        namer.Reserve("unwritten.jpg", ".png");

        namer.AbortAll();

        Assert.Equal([Path.Combine(_dir, "keep.txt")], Directory.GetFileSystemEntries(_dir));
    }
}
