using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class FramesTests
{
    [Fact]
    public void Times_round_trip_in_order()
    {
        Assert.Equal([2.5, 0, 1 / 30.0], Frames.Parse(Frames.ToParameters([2.5, 0, 1 / 30.0])), new RoundedComparer());
        Assert.Equal(("-frame-12.5s", "-frame-0s", "-frame-0.033s"), (Frames.Suffix(12.5), Frames.Suffix(0), Frames.Suffix(1 / 30.0)));
    }

    [Fact]
    public void Bad_times_are_refused()
    {
        Assert.Throws<EngineException>(() => Frames.Parse(null));
        Assert.Throws<EngineException>(() => Frames.Parse(Frames.ToParameters([])));
        Assert.Throws<EngineException>(() => Frames.Parse(Frames.ToParameters([-1])));
        Assert.Throws<EngineException>(() => Frames.Parse(new() { ["times"] = new System.Text.Json.Nodes.JsonArray("1") }));
        Assert.Throws<EngineException>(() => Frames.Parse(Frames.ToParameters(Enumerable.Repeat(1.0, Frames.MaxFrames + 1))));
    }

    private sealed class RoundedComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-6;

        public int GetHashCode(double obj) => 0;
    }
}
