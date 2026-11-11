using System.Text.Json.Nodes;

using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class RedactionsTests
{
    [Fact]
    public void Boxes_round_trip_through_parameters()
    {
        var parsed = Redactions.Parse(JsonNode.Parse(Redactions.ToParameters([new RedactBox(2, 1.234, 5, 30, 40.5)]).ToJsonString())!.AsObject());

        Assert.Equal([new RedactBox(2, 1.23, 5, 30, 40.5)], parsed);
    }

    [Fact]
    public void No_boxes_asks_for_one() =>
        Assert.Equal("Draw a box over what to hide first.", Assert.Throws<EngineException>(() => Redactions.Parse(new JsonObject { ["boxes"] = new JsonArray() })).Message);

    [Theory]
    [InlineData("""{"x":0,"y":0,"w":0,"h":5}""")]
    [InlineData("""{"x":0,"y":0,"w":5,"h":-1}""")]
    [InlineData("""{"x":"a","y":0,"w":5,"h":5}""")]
    [InlineData("""{"x":0,"y":0,"w":5,"h":5,"page":0}""")]
    [InlineData("""{"x":0,"y":0,"w":5,"h":5,"page":1.5}""")]
    public void A_bad_box_is_refused(string box) =>
        Assert.Equal("Morph couldn't read the boxes to hide.",
            Assert.Throws<EngineException>(() => Redactions.Parse(JsonNode.Parse($$"""{"boxes":[{{box}}]}""")!.AsObject())).Message);
}
