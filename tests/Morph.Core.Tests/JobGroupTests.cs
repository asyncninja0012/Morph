using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class JobGroupTests
{
    private readonly List<string> _cancelled = [];

    [Fact]
    public void Progress_is_weighted_by_input_count()
    {
        var (a, b) = (Request(3), Request(1));
        var group = Group(a, b);

        Assert.True(group.Apply(new JobUpdate(a, JobStatus.Running, new JobProgress(0.5, null))));
        Assert.Equal(1.5 / 4, group.Fraction);
        Assert.True(group.Apply(new JobUpdate(b, JobStatus.Running, new JobProgress(double.NaN, null))));
        Assert.Equal(1.5 / 4, group.Fraction); // unknown counts as nothing done
        Assert.Equal(JobStatus.Running, group.Status);
    }

    [Fact]
    public void Indeterminate_when_every_job_is()
    {
        var a = Request(1);
        var group = Group(a);

        group.Apply(new JobUpdate(a, JobStatus.Running, new JobProgress(double.NaN, null)));

        Assert.True(double.IsNaN(group.Fraction));
    }

    [Fact]
    public void Ends_when_every_job_has_a_result_and_collects_outputs()
    {
        var (a, b) = (Request(1), Request(1));
        var group = Group(a, b);

        group.Apply(Done(a, JobStatus.Succeeded, "a.png"));
        Assert.Equal(JobStatus.Running, group.Status);
        group.Apply(Done(b, JobStatus.Succeeded, "b.png"));

        Assert.Equal(JobStatus.Succeeded, group.Status);
        Assert.Equal(["a.png", "b.png"], group.Outputs);
        Assert.Equal(1, group.Fraction);
        Assert.Null(group.UserMessage);
    }

    [Fact]
    public void Any_failure_fails_the_group_but_keeps_other_outputs()
    {
        var (a, b) = (Request(1), Request(1));
        var group = Group(a, b);

        group.Apply(Done(a, JobStatus.Succeeded, "a.png"));
        group.Apply(new JobUpdate(b, JobStatus.Failed, Result: new JobResult(b.Id, JobStatus.Failed, [], "b.jpg was moved or deleted.", "trace", TimeSpan.Zero)));

        Assert.Equal(JobStatus.Failed, group.Status);
        Assert.Equal("b.jpg was moved or deleted.", group.UserMessage);
        Assert.Equal("trace", group.Details);
        Assert.Equal(["a.png"], group.Outputs);
    }

    [Fact]
    public void Cancelled_only_when_every_job_was()
    {
        var (a, b) = (Request(1), Request(1));
        var group = Group(a, b);

        group.Cancel();
        group.Apply(Done(a, JobStatus.Cancelled));
        group.Apply(Done(b, JobStatus.Cancelled));

        Assert.Equal(["cancel", "cancel"], _cancelled);
        Assert.Equal(JobStatus.Cancelled, group.Status);
    }

    [Fact]
    public void Ignores_other_jobs_and_updates_after_the_result()
    {
        var a = Request(1);
        var group = Group(a);

        Assert.False(group.Apply(new JobUpdate(Request(1), JobStatus.Running, new JobProgress(0.5, null))));
        Assert.False(group.Apply(new JobUpdate(a, JobStatus.Running)));
        group.Apply(Done(a, JobStatus.Succeeded));
        Assert.False(group.Apply(new JobUpdate(a, JobStatus.Running, new JobProgress(0.1, null))));
        Assert.Equal(1, group.Fraction);
    }

    private JobGroup Group(params JobRequest[] requests) =>
        new(requests.Select(r => (r, new JobHandle(r.Id, Task.FromResult<JobResult>(null!), () => _cancelled.Add("cancel")))));

    private static JobRequest Request(int inputs) =>
        new(Guid.NewGuid(), "convert.png", "imaging", [.. Enumerable.Range(0, inputs).Select(i => $"{i}.jpg")], "out");

    private static JobUpdate Done(JobRequest r, JobStatus status, params string[] outputs) =>
        new(r, status, Result: new JobResult(r.Id, status, outputs, null, null, TimeSpan.Zero));
}
