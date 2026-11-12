using Morph.Core.Settings;

namespace Morph.Core.Tests;

public sealed class WheelPrefsTests
{
    private static readonly string[] Items = ["png", "jpg", "webp", "pdf"];

    private static Dictionary<string, int> Uses => new() { ["pdf"] = 5, ["webp"] = 2 };

    [Fact]
    public void Most_used_come_first_and_ties_keep_the_matrix_order() =>
        Assert.Equal(["pdf", "webp", "png", "jpg"], WheelPrefs.Default.Apply(Items, i => i, Uses));

    [Fact]
    public void Pinned_come_first_in_their_order_and_hidden_are_left_out() =>
        Assert.Equal(["jpg", "png", "pdf"], new WheelPrefs(["jpg", "png", "gone"], ["webp"]).Apply(Items, i => i, Uses));

    [Fact]
    public void Without_learning_the_order_stays() =>
        Assert.Equal(["png", "jpg", "webp", "pdf"], new WheelPrefs([], [], Learn: false).Apply(Items, i => i, Uses));

    [Fact]
    public void Survives_a_round_trip()
    {
        var prefs = new WheelPrefs(["a", "b"], ["c"], Learn: false);

        var back = WheelPrefs.From(prefs.ToJson());

        Assert.Equal(prefs.Pinned, back.Pinned);
        Assert.Equal(prefs.Hidden, back.Hidden);
        Assert.False(back.Learn);
        Assert.Equal(WheelPrefs.Default.Pinned, WheelPrefs.From(null).Pinned);
        Assert.Equal(new Dictionary<string, int> { ["x"] = 3 }, WheelPrefs.Usage(new() { ["x"] = 3 }));
    }
}
