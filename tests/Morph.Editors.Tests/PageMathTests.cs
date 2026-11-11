using Morph.Core.Jobs;
using Morph.Editors.Pages;

namespace Morph.Editors.Tests;

public sealed class PageMathTests
{
    private static int[] Order(IReadOnlyList<PagePick> pages) => [.. pages.Select(p => p.Source)];

    [Theory]
    [InlineData(new[] { 0, 2 }, 5, new[] { 2, 4, 5, 1, 3 }, 3)] // to the end
    [InlineData(new[] { 3, 4 }, 0, new[] { 4, 5, 1, 2, 3 }, 0)] // to the front
    [InlineData(new[] { 1 }, 4, new[] { 1, 3, 4, 2, 5 }, 3)]    // forward past two
    [InlineData(new[] { 1 }, 2, new[] { 1, 2, 3, 4, 5 }, 1)]    // into its own gap: no change
    public void Selected_pages_move_together(int[] selected, int to, int[] expected, int firstMoved)
    {
        var (pages, moved) = PageMath.Move(PageMath.Original(5), selected, to);

        Assert.Equal(expected, Order(pages));
        Assert.Equal(firstMoved, moved[0]);
        Assert.Equal(selected.Length, moved.Count);
    }

    [Fact]
    public void Turning_wraps_round()
    {
        var pages = PageMath.Turn(PageMath.Turn(PageMath.Original(3), [1], -90), [1, 2], 180);

        Assert.Equal([0, 90, 180], pages.Select(p => p.Rotate));
    }

    [Fact]
    public void Delete_never_removes_every_page()
    {
        var pages = PageMath.Original(3);

        Assert.Equal([1, 3], Order(PageMath.Delete(pages, [1])));
        Assert.Same(pages, PageMath.Delete(pages, [0, 1, 2]));
        Assert.Equal([2, 3], Order(PageMath.KeepOnly(pages, [1, 2])));
    }

    [Fact]
    public void Splits_count_files_but_not_after_the_last_page()
    {
        var pages = PageMath.ToggleSplit(PageMath.ToggleSplit(PageMath.Original(4), 1), 3);

        Assert.Equal(2, PageMath.Files(pages));
        Assert.Equal(1, PageMath.Files(PageMath.ToggleSplit(pages, 1)));
    }
}
