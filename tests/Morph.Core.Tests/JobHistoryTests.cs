using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class JobHistoryTests
{
    private readonly Clock _clock = new();

    [Fact]
    public void Undo_returns_the_outputs_once_within_ten_minutes()
    {
        var history = new JobHistory(_clock);
        var (a, entry) = Add(history);

        Assert.False(history.CanUndo(entry)); // still running
        Assert.True(history.Apply(Done(a, JobStatus.Succeeded, "a.png")));
        _clock.Now += JobHistory.UndoWindow;

        Assert.Equal(["a.png"], history.TakeUndo(entry));
        Assert.True(entry.Undone);
        Assert.Empty(history.TakeUndo(entry));
    }

    [Fact]
    public void Undo_expires_and_needs_outputs()
    {
        var history = new JobHistory(_clock);
        var (a, late) = Add(history);
        var (b, empty) = Add(history);
        history.Apply(Done(a, JobStatus.Succeeded, "a.png"));
        history.Apply(Done(b, JobStatus.Cancelled));

        Assert.False(history.CanUndo(empty));
        _clock.Now += JobHistory.UndoWindow + TimeSpan.FromSeconds(1);
        Assert.False(history.CanUndo(late));
        Assert.Empty(history.TakeUndo(late));
    }

    [Fact]
    public void Keeps_the_newest_fifty()
    {
        var history = new JobHistory(_clock);
        var first = Add(history).Entry;
        JobHistoryEntry last = first;
        for (var i = 0; i < JobHistory.Capacity; i++)
        {
            last = Add(history).Entry;
        }

        Assert.Equal(JobHistory.Capacity, history.Entries.Count);
        Assert.Same(last, history.Entries[0]);
        Assert.DoesNotContain(first, history.Entries);
    }

    private static (JobRequest, JobHistoryEntry Entry) Add(JobHistory history)
    {
        var r = new JobRequest(Guid.NewGuid(), "convert.png", "imaging", ["a.jpg"], "out");
        return (r, history.Add("PNG", r.Inputs, [(r, new JobHandle(r.Id, Task.FromResult<JobResult>(null!), () => { }))]));
    }

    private static JobUpdate Done(JobRequest r, JobStatus status, params string[] outputs) =>
        new(r, status, Result: new JobResult(r.Id, status, outputs, null, null, TimeSpan.Zero));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
