using Morph.Core.Actions;

namespace Morph.Core.Tests;

public sealed class FileActionsTests
{
    [Fact]
    public void Paths_are_quoted_one_per_line()
    {
        string[] paths = [@"C:\Users\me\My file.txt", @"\\server\share\b"];

        Assert.Equal("\"C:\\Users\\me\\My file.txt\"\r\n\"\\\\server\\share\\b\"", FileActions.PathText(paths, unix: false));
        Assert.Equal("\"C:/Users/me/My file.txt\"\r\n\"//server/share/b\"", FileActions.PathText(paths, unix: true));
    }

    [Fact]
    public void A_used_folder_moves_to_the_front()
    {
        string[] recent = [@"C:\a", @"D:\b\", @"E:\c", @"F:\d", @"G:\e", @"H:\f"];

        Assert.Equal([@"D:\b", @"C:\a", @"E:\c", @"F:\d", @"G:\e", @"H:\f"], FileActions.AddRecent(recent, @"D:\b"));
        Assert.Equal([@"Z:\new", @"C:\a", @"D:\b\", @"E:\c", @"F:\d", @"G:\e"], FileActions.AddRecent(recent, @"Z:\new"));
    }

    [Fact]
    public void Float_keeps_one_of_each_newest_last()
    {
        string[] shelf = [@"C:\a.txt", @"C:\b\", @"C:\c.png"];

        Assert.Equal([@"C:\a.txt", @"C:\c.png", @"c:\B", @"D:\d.pdf"], FileActions.AddToFloat(shelf, [@"c:\B", @"D:\d.pdf", @"D:\d.pdf"]));
        var full = FileActions.AddToFloat([.. Enumerable.Range(0, FileActions.FloatCount).Select(i => $@"C:\{i}")], [@"C:\new"]);
        Assert.Equal(FileActions.FloatCount, full.Count);
        Assert.Equal((@"C:\1", @"C:\new"), (full[0], full[^1]));
    }

    [Theory]
    [InlineData(@"C:\Users\me", true)]
    [InlineData(@"C:\a;b", false)]
    public void Terminal_avoids_folders_it_would_split(string folder, bool windowsTerminal) =>
        Assert.Equal(windowsTerminal, FileActions.TerminalCanOpen(folder));
}
