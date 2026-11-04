using System.Diagnostics;

namespace Morph.Core.Jobs;

/// <param name="Progress">Set on progress updates while <see cref="JobStatus.Running"/>.</param>
/// <param name="Result">Set once, on the final update.</param>
public sealed record JobUpdate(JobRequest Request, JobStatus Status, JobProgress? Progress = null, JobResult? Result = null);

/// <param name="Cancel">Safe to call at any time; a queued job never starts, a running one is asked to stop.</param>
public sealed record JobHandle(Guid Id, Task<JobResult> Completion, Action Cancel);

/// <summary>
/// Runs jobs in parallel up to (logical cores − 1), with at most 2 media jobs at once (JB-1). Every job ends in a
/// <see cref="JobResult"/>: a runner exception becomes <see cref="JobStatus.Failed"/>, never an unobserved fault.
/// </summary>
public sealed class JobQueue
{
    public const string MediaEngine = "media";

    private readonly Func<JobRequest, IProgress<JobProgress>, CancellationToken, Task<JobResult>> _run;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _mediaSlots;

    /// <param name="run">Runs one job, normally <see cref="WorkerClient.RunAsync"/>.</param>
    public JobQueue(Func<JobRequest, IProgress<JobProgress>, CancellationToken, Task<JobResult>> run,
        int? maxParallel = null, int maxMedia = 2)
    {
        _run = run;
        var parallel = maxParallel ?? Math.Max(1, Environment.ProcessorCount - 1);
        _slots = new SemaphoreSlim(parallel, parallel);
        _mediaSlots = new SemaphoreSlim(maxMedia, maxMedia);
    }

    /// <summary>Raised on a thread-pool thread for every state change and progress report; UI subscribers must marshal.</summary>
    public event Action<JobUpdate>? Updated;

    public JobHandle Enqueue(JobRequest request)
    {
        // Not disposed: no timer or wait handle is ever created on it, and Cancel may be called after the job ends.
        var cts = new CancellationTokenSource();
        Updated?.Invoke(new JobUpdate(request, JobStatus.Queued));
        return new JobHandle(request.Id, Task.Run(() => RunAsync(request, cts.Token), CancellationToken.None), cts.Cancel);
    }

    private async Task<JobResult> RunAsync(JobRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        // Media slot first, so a media job waiting its turn never holds a general slot that image jobs could use.
        SemaphoreSlim[] needed = request.Engine == MediaEngine ? [_mediaSlots, _slots] : [_slots];
        var held = new List<SemaphoreSlim>(2);
        JobResult result;
        try
        {
            foreach (var slot in needed)
            {
                await slot.WaitAsync(ct).ConfigureAwait(false);
                held.Add(slot);
            }

            Updated?.Invoke(new JobUpdate(request, JobStatus.Running));
            var progress = new SyncProgress<JobProgress>(p => Updated?.Invoke(new JobUpdate(request, JobStatus.Running, p)));
            result = await _run(request, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new JobResult(request.Id, JobStatus.Cancelled, [], null, null, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex)
        {
            result = new JobResult(request.Id, JobStatus.Failed, [], "Morph couldn't run this job.", ex.ToString(),
                Stopwatch.GetElapsedTime(started));
        }
        finally
        {
            held.ForEach(s => s.Release());
        }

        Updated?.Invoke(new JobUpdate(request, result.Status, Result: result));
        return result;
    }
}
