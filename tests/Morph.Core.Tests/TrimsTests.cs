using System.Text.Json.Nodes;

using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class TrimsTests
{
    [Fact]
    public void Segments_round_trip_through_parameters()
    {
        var (segments, exact) = Trims.Parse(JsonNode.Parse(Trims.ToParameters([new(0.5, 2.25), new(2.25, 9.1234)], exact: true).ToJsonString())!.AsObject());

        Assert.True(exact);
        Assert.Equal([new TrimSegment(0.5, 2.25), new TrimSegment(2.25, 9.123)], segments);
    }

    [Fact]
    public void Mode_defaults_to_fast() =>
        Assert.False(Trims.Parse(JsonNode.Parse("""{"segments":[{"start":0,"end":1}]}""")!.AsObject()).Exact);

    [Fact]
    public void Nothing_marked_asks_for_a_part() =>
        Assert.Equal("Mark the part to keep first.", Assert.Throws<EngineException>(() => Trims.Parse(new JsonObject())).Message);

    [Theory]
    [InlineData("""{"segments":[{"start":2,"end":1}]}""")]
    [InlineData("""{"segments":[{"start":-1,"end":1}]}""")]
    [InlineData("""{"segments":[{"start":0}]}""")]
    [InlineData("""{"segments":[{"start":0,"end":1}],"mode":"slow"}""")]
    public void A_bad_trim_is_refused(string json) =>
        Assert.Equal("Morph couldn't read where to cut.", Assert.Throws<EngineException>(() => Trims.Parse(JsonNode.Parse(json)!.AsObject())).Message);
}
