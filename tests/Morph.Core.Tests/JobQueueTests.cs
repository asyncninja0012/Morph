using System.Collections.Concurrent;

using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class JobQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Runs_at_most_max_parallel_and_at_most_two_media_jobs()
    {
        var runner = new GatedRunner();
        var queue = new JobQueue(runner.RunAsync, maxParallel: 3);
        var media = Enumerable.Range(0, 3).Select(_ => queue.Enqueue(Request("media"))).ToList();
        var images = Enumerable.Range(0, 3).Select(_ => queue.Enqueue(Request("imaging"))).ToList();

        await runner.WaitForRunningAsync(3);
        await Task.Delay(100, TestContext.Current.CancellationToken); // give an over-limit job the chance to start
        Assert.Equal(3, runner.Running.Count);
        Assert.Equal(2, runner.Running.Count(r => r.Engine == "media"));

        runner.ReleaseAll();
        var results = await Task.WhenAll(media.Concat(images).Select(h => h.Completion)).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal(JobStatus.Succeeded, r.Status));
        Assert.Equal(3, runner.MaxConcurrent);
        Assert.Equal(2, runner.MaxConcurrentMedia);
    }

    [Fact]
    public async Task Waiting_media_job_does_not_block_image_jobs()
    {
        var runner = new GatedRunner();
        var queue = new JobQueue(runner.RunAsync, maxParallel: 3);
        for (var i = 0; i < 3; i++)
        {
            queue.Enqueue(Request("media"));
        }

        queue.Enqueue(Request("imaging"));

        await runner.WaitForRunningAsync(3);
        Assert.Contains(runner.Running, r => r.Engine == "imaging");
        runner.ReleaseAll();
    }

    [Fact]
    public async Task Cancel_before_start_never_runs_the_job()
    {
        var runner = new GatedRunner();
        var queue = new JobQueue(runner.RunAsync, maxParallel: 1);
        var first = queue.Enqueue(Request("imaging"));
        var second = queue.Enqueue(Request("imaging"));
        await runner.WaitForRunningAsync(1);

        second.Cancel();
        var result = await second.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        runner.ReleaseAll();
        await first.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Cancelled, result.Status);
        Assert.DoesNotContain(runner.Started, r => r.Id == second.Id);
    }

    [Fact]
    public async Task Cancel_while_running_reaches_the_runner()
    {
        var runner = new GatedRunner();
        var handle = new JobQueue(runner.RunAsync).Enqueue(Request("media"));
        await runner.WaitForRunningAsync(1);

        handle.Cancel();
        var result = await handle.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Cancelled, result.Status);
        handle.Cancel(); // after completion: harmless
    }

    [Fact]
    public async Task Runner_exception_becomes_a_failed_result_and_frees_the_slot()
    {
        var calls = 0;
        var queue = new JobQueue((r, _, _) => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("worker missing")
            : Task.FromResult(new JobResult(r.Id, JobStatus.Succeeded, [], null, null, TimeSpan.Zero)), maxParallel: 1);

        var failed = await queue.Enqueue(Request("imaging")).Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var next = await queue.Enqueue(Request("imaging")).Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.NotNull(failed.UserMessage);
        Assert.Contains("worker missing", failed.Details, StringComparison.Ordinal);
        Assert.Equal(JobStatus.Succeeded, next.Status);
    }

    [Fact]
    public async Task Updates_go_queued_running_progress_then_result()
    {
        var updates = new ConcurrentQueue<JobUpdate>();
        var queue = new JobQueue((r, p, _) =>
        {
            p.Report(new JobProgress(0.5, "encoding"));
            return Task.FromResult(new JobResult(r.Id, JobStatus.Succeeded, ["out.png"], null, null, TimeSpan.Zero));
        });
        queue.Updated += updates.Enqueue;

        await queue.Enqueue(Request("imaging")).Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Collection(updates,
            u => Assert.Equal(JobStatus.Queued, u.Status),
            u => Assert.Equal((JobStatus.Running, (JobProgress?)null), (u.Status, u.Progress)),
            u => Assert.Equal(0.5, u.Progress?.Fraction),
            u => Assert.Equal(["out.png"], u.Result?.Outputs ?? []));
    }

    private static JobRequest Request(string engine) => new(Guid.NewGuid(), "convert.x", engine, ["in"], "out");

    /// <summary>Each job blocks until released (or cancelled) and records how many ran at once.</summary>
    private sealed class GatedRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();

        public List<JobRequest> Running { get; } = [];

        public ConcurrentBag<JobRequest> Started { get; } = [];

        public int MaxConcurrent { get; private set; }

        public int MaxConcurrentMedia { get; private set; }

        public async Task<JobResult> RunAsync(JobRequest request, IProgress<JobProgress> progress, CancellationToken ct)
        {
            Started.Add(request);
            lock (_lock)
            {
                Running.Add(request);
                MaxConcurrent = Math.Max(MaxConcurrent, Running.Count);
                MaxConcurrentMedia = Math.Max(MaxConcurrentMedia, Running.Count(r => r.Engine == "media"));
            }

            try
            {
                await _release.Task.WaitAsync(ct);
                return new JobResult(request.Id, JobStatus.Succeeded, [], null, null, TimeSpan.Zero);
            }
            finally
            {
                lock (_lock)
                {
                    Running.Remove(request);
                }
            }
        }

        public async Task WaitForRunningAsync(int count)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                lock (_lock)
                {
                    if (Running.Count >= count)
                    {
                        return;
                    }
                }

                Assert.True(DateTime.UtcNow < deadline, $"fewer than {count} jobs started");
                await Task.Delay(10);
            }
        }

        public void ReleaseAll() => _release.TrySetResult();
    }
}
