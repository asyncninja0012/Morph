using System.Text.Json.Nodes;

using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class AnnotationsTests
{
    [Fact]
    public void Shapes_round_trip_through_parameters()
    {
        AnnotationShape[] shapes =
        [
            new("arrow", [(1, 2), (30.556, 40)], "#E5383B", 4),
            new("text", [(5, 6)], "#111111", 2, "Look here", 3),
            new("highlight", [(0, 0), (10, 0), (20, 5)], "#FFD60A", 8),
        ];

        // Through JSON text, as the job request travels to the Worker.
        var parsed = Annotations.Parse(JsonNode.Parse(Annotations.ToParameters(shapes).ToJsonString())!.AsObject());

        Assert.Equal(3, parsed.Count);
        Assert.Equal(("arrow", "#E5383B", 4.0, 1), (parsed[0].Type, parsed[0].Color, parsed[0].Stroke, parsed[0].Page));
        Assert.Equal([(1.0, 2.0), (30.56, 40.0)], parsed[0].Points);
        Assert.Equal(("Look here", 3), (parsed[1].Text, parsed[1].Page));
        Assert.Equal(3, parsed[2].Points.Count);
    }

    [Fact]
    public void Nothing_drawn_asks_to_draw()
    {
        var ex = Assert.Throws<EngineException>(() => Annotations.Parse(new JsonObject { ["shapes"] = new JsonArray() }));

        Assert.Equal("Draw something first.", ex.Message);
    }

    [Theory]
    [InlineData("""{"type":"star","points":[[0,0],[1,1]],"color":"#000000","stroke":1}""")]
    [InlineData("""{"type":"rect","points":[[0,0]],"color":"#000000","stroke":1}""")]
    [InlineData("""{"type":"rect","points":[[0,0],[1,"x"]],"color":"#000000","stroke":1}""")]
    [InlineData("""{"type":"rect","points":[[0,0],[1,1]],"color":"red","stroke":1}""")]
    [InlineData("""{"type":"rect","points":[[0,0],[1,1]],"color":"#000000","stroke":0}""")]
    [InlineData("""{"type":"rect","points":[[0,0],[1,1]],"color":"#000000","stroke":1,"page":0}""")]
    [InlineData("""{"type":"text","points":[[0,0]],"color":"#000000","stroke":1,"text":" "}""")]
    public void A_bad_shape_is_refused(string shape)
    {
        var ex = Assert.Throws<EngineException>(() => Annotations.Parse(JsonNode.Parse($$"""{"shapes":[{{shape}}]}""")!.AsObject()));

        Assert.Equal("Morph couldn't read the drawing.", ex.Message);
    }

    [Fact]
    public void An_arrow_head_points_along_the_line()
    {
        var (end, head) = Annotations.Arrow((0, 0), (100, 0), 5);

        Assert.Equal((90.0, 0.0), end);
        Assert.Equal((100.0, 0.0), head[0]);
        Assert.Equal((80.0, 10.0), head[1]);
        Assert.Equal((80.0, -10.0), head[2]);
    }
}
