using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class SpeedsTests
{
    [Theory]
    [InlineData(4, "atempo=2,atempo=2")]
    [InlineData(1.5, "atempo=1.5")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    [InlineData(1, "atempo=1")]
    public void Tempo_steps_stay_between_half_and_double(double speed, string filter) => Assert.Equal(filter, Speeds.Atempo(speed));

    [Fact]
    public void Only_the_stops_are_read()
    {
        Assert.Equal(0.25, Speeds.Parse(Speeds.ToParameters(0.25)));
        Assert.Throws<EngineException>(() => Speeds.Parse(Speeds.ToParameters(3)));
        Assert.Throws<EngineException>(() => Speeds.Parse(new() { ["speed"] = "fast" }));
        Assert.Throws<EngineException>(() => Speeds.Parse(null));
        Assert.Equal(("-0.25x", "1.5×"), (Speeds.Suffix(0.25), Speeds.Label(1.5)));
    }
}
