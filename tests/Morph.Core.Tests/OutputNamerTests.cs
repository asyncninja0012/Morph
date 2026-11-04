using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class OutputNamerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-namer-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Commit_renames_temp_to_the_wanted_name_in_the_output_folder()
    {
        var namer = new OutputNamer(_dir);
        var p = namer.Reserve(@"C:\elsewhere\photo.jpg", ".png");
        Assert.Equal(_dir, Path.GetDirectoryName(p.TempPath));
        File.WriteAllText(p.TempPath, "x");

        var outputs = namer.CommitAll();

        Assert.Equal([Path.Combine(_dir, "photo.png")], outputs);
        Assert.False(File.Exists(p.TempPath));
    }

    [Fact]
    public void Clashes_get_numbered_and_never_overwrite()
    {
        File.WriteAllText(Path.Combine(_dir, "photo.png"), "original");
        var namer = new OutputNamer(_dir);
        var a = namer.Reserve("photo.jpg", ".png");
        var b = namer.Reserve("photo.jpeg", ".png");
        File.WriteAllText(a.TempPath, "a");
        File.WriteAllText(b.TempPath, "b");

        var outputs = namer.CommitAll();

        Assert.Equal([Path.Combine(_dir, "photo (2).png"), Path.Combine(_dir, "photo (3).png")], outputs);
        Assert.Equal("original", File.ReadAllText(Path.Combine(_dir, "photo.png")));
    }

    [Fact]
    public void Abort_deletes_temp_files()
    {
        var namer = new OutputNamer(_dir);
        var p = namer.Reserve("photo.jpg", ".png");
        File.WriteAllText(p.TempPath, "x");
        namer.Reserve("unwritten.jpg", ".png");

        namer.AbortAll();

        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }
}
