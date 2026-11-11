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

    [Theory]
    [InlineData(@"C:\Users\me", true)]
    [InlineData(@"C:\a;b", false)]
    public void Terminal_avoids_folders_it_would_split(string folder, bool windowsTerminal) =>
        Assert.Equal(windowsTerminal, FileActions.TerminalCanOpen(folder));
}
