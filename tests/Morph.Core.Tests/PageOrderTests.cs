using System.Text.Json.Nodes;

using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class PageOrderTests
{
    [Fact]
    public void Order_round_trips_cut_into_files()
    {
        PagePick[] pages = [new(3, 90, SplitAfter: true), new(1), new(2, 270, SplitAfter: true), new(4, SplitAfter: true)]; // the last split is meaningless

        var files = PageOrder.Parse(JsonNode.Parse(PageOrder.ToParameters(pages).ToJsonString())!.AsObject());

        Assert.Equal(3, files.Count);
        Assert.Equal([new PagePick(3, 90)], files[0]);
        Assert.Equal([new PagePick(1), new PagePick(2, 270)], files[1]);
        Assert.Equal([new PagePick(4)], files[2]);
    }

    [Fact]
    public void Turns_are_kept_within_a_circle() =>
        Assert.Equal(270, PageOrder.Parse(JsonNode.Parse("""{"pages":[{"src":1,"rotate":-90}]}""")!.AsObject())[0][0].Rotate);

    [Fact]
    public void No_pages_asks_to_keep_one() =>
        Assert.Equal("Keep at least one page.", Assert.Throws<EngineException>(() => PageOrder.Parse(JsonNode.Parse("""{"pages":[]}""")!.AsObject())).Message);

    [Theory]
    [InlineData("""{"pages":[{"src":0}]}""")]
    [InlineData("""{"pages":[{"src":1,"rotate":45}]}""")]
    [InlineData("""{"pages":[{"src":"a"}]}""")]
    [InlineData("""{"pages":[{"src":1},{"src":2}],"splitAfter":[2]}""")]
    [InlineData("""{"pages":[{"src":1},{"src":2}],"splitAfter":[0]}""")]
    public void A_bad_order_is_refused(string json) =>
        Assert.Equal("Morph couldn't read the new page order.", Assert.Throws<EngineException>(() => PageOrder.Parse(JsonNode.Parse(json)!.AsObject())).Message);
}
