using Morph.Core.Jobs;
using Morph.Editors.Trim;

namespace Morph.Editors.Tests;

public sealed class TrimMathTests
{
    [Fact]
    public void Splits_make_parts_in_order()
    {
        var cut = TrimMath.AddSplit(TrimMath.AddSplit(TrimMath.Whole(10) with { In = 1, Out = 9 }, 6), 3);

        Assert.Equal([new TrimSegment(1, 3), new TrimSegment(3, 6), new TrimSegment(6, 9)], TrimMath.Segments(cut));
    }

    [Theory]
    [InlineData(1.05)] // too close to the start
    [InlineData(8.95)] // too close to the end
    [InlineData(5.05)] // too close to another split
    [InlineData(0.5)]  // outside what's kept
    public void A_split_that_would_leave_a_sliver_is_ignored(double at)
    {
        var cut = new TrimCut(1, 9, [5]);

        Assert.Same(cut, TrimMath.AddSplit(cut, at));
    }

    [Fact]
    public void Moving_the_ends_drops_the_splits_they_pass()
    {
        var cut = new TrimCut(0, 10, [2, 5, 8]);

        Assert.Equal([5.0, 8.0], TrimMath.SetIn(cut, 3).Splits);
        Assert.Equal([2.0, 5.0], TrimMath.SetOut(cut, 7, 10).Splits);
        Assert.Equal(9.9, TrimMath.SetIn(cut, 20).In);
        Assert.Equal(10, TrimMath.SetOut(cut, 20, 10).Out);
    }

    [Fact]
    public void A_split_stays_between_its_neighbours()
    {
        var cut = new TrimCut(0, 10, [2, 5, 8]);

        Assert.Equal([2, 7.9, 8], TrimMath.MoveSplit(cut, 1, 9).Splits);
        Assert.Equal([2, 2.1, 8], TrimMath.MoveSplit(cut, 1, 0).Splits);
        Assert.Equal(1, TrimMath.Nearest(cut, 4.9));
        Assert.Equal([2.0, 8.0], TrimMath.RemoveSplit(cut, 1).Splits);
        Assert.Null(TrimMath.Nearest(TrimMath.Whole(3), 1));
    }

    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(65.34, "1:05.3")]
    [InlineData(3725.06, "1:02:05.1")]
    public void Times_read_as_minutes_and_seconds(double seconds, string text) => Assert.Equal(text, TrimMath.Format(seconds));
}
