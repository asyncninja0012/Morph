using Morph.Editors.Frames;

namespace Morph.Editors.Tests;

public sealed class FrameMathTests
{
    [Fact]
    public void Frames_land_on_their_start()
    {
        const double ntsc = 30000 / 1001.0;
        Assert.Equal(12, FrameMath.At(FrameMath.Start(12, ntsc), ntsc)); // k / fps reads back as k, not k − 1
        Assert.Equal(0, FrameMath.At(-1, 30));
        Assert.Equal(14, FrameMath.At(0.499, 30));
        Assert.Equal((29, 29), (FrameMath.Last(1, 30), FrameMath.Last(0.99, 30)));
        Assert.Equal(0, FrameMath.Last(0, 30));
        Assert.Equal(("0:00.067", "1:02:03.000"), (FrameMath.Format(2 / 30.0), FrameMath.Format(3723)));
    }
}
