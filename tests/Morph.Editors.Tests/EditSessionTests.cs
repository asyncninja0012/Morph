using System.Text.Json.Nodes;

namespace Morph.Editors.Tests;

public sealed class EditSessionTests
{
    [Fact]
    public void Same_for_all_gives_the_first_files_parameters_to_every_file()
    {
        var session = new EditSession(["a.png", "b.png", "c.png"]);
        Assert.True(session.IsLast);

        JsonObject crop = new() { ["w"] = 10 };
        var done = session.Done(crop);

        Assert.NotNull(done);
        Assert.Equal(["a.png", "b.png", "c.png"], done.Select(f => f.Path));
        Assert.All(done, f => Assert.Same(crop, f.Parameters));
    }

    [Fact]
    public void One_by_one_edits_each_file_in_turn()
    {
        var session = new EditSession(["a.png", "b.png"]) { StepThrough = true };

        Assert.Equal(("a.png", 1, false), (session.Current, session.Number, session.IsLast));
        Assert.Null(session.Done(new() { ["w"] = 1 }));
        Assert.Equal(("b.png", 2, true), (session.Current, session.Number, session.IsLast));

        var done = session.Done(new() { ["w"] = 2 });

        Assert.NotNull(done);
        Assert.Equal([1, 2], done.Select(f => (int)f.Parameters["w"]!));
    }

    [Fact]
    public void The_mode_is_fixed_once_a_file_is_done()
    {
        var session = new EditSession(["a.png", "b.png", "c.png"]) { StepThrough = true };
        session.Done([]);

        Assert.Throws<InvalidOperationException>(() => session.StepThrough = false);
    }

    [Fact]
    public void A_skipped_file_is_left_out()
    {
        var session = new EditSession(["bad.png", "a.png", "b.png"]);

        Assert.Null(session.Skip());
        Assert.Equal("a.png", session.Current);
        var done = session.Done([]);

        Assert.Equal(["a.png", "b.png"], done!.Select(f => f.Path));
    }

    [Fact]
    public void Skipping_every_file_ends_with_nothing()
    {
        var session = new EditSession(["bad.png"]);

        Assert.Empty(session.Skip()!);
    }
}
