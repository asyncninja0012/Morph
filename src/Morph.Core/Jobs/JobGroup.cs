namespace Morph.Core.Jobs;

/// <summary>
/// The jobs from one drop, shown as one toast (JB-2): progress is weighted by input count, cancel stops them all,
/// and the group ends when every job has a result. Not thread-safe: feed <see cref="Apply"/> from one thread.
/// </summary>
public sealed class JobGroup
{
    private readonly Dictionary<Guid, Entry> _jobs;

    public JobGroup(IEnumerable<(JobRequest Request, JobHandle Handle)> jobs) =>
        _jobs = jobs.ToDictionary(j => j.Request.Id, j => new Entry(j.Request.Inputs.Count, j.Handle));

    /// <summary>0..1 weighted by input count, or NaN when every job reports an unknown fraction (indeterminate bar).</summary>
    public double Fraction { get; private set; }

    /// <summary>Running until every job has a result; then Failed if any failed, Cancelled if all were cancelled, else Succeeded.</summary>
    public JobStatus Status { get; private set; } = JobStatus.Running;

    public IReadOnlyList<string> Outputs { get; private set; } = [];

    /// <summary>The first failure's plain-language reason.</summary>
    public string? UserMessage { get; private set; }

    /// <summary>Every failure's details, for "Copy details".</summary>
    public string? Details { get; private set; }

    /// <returns>True when the update belonged to this group and its state changed.</returns>
    public bool Apply(JobUpdate update)
    {
        if (!_jobs.TryGetValue(update.Request.Id, out var job) || job.Result is not null)
        {
            return false;
        }

        if (update.Result is { } result)
        {
            job.Result = result;
            job.Fraction = 1;
        }
        else if (update.Progress is { } p)
        {
            job.Fraction = p.Fraction;
        }
        else
        {
            return false; // Queued/Running carry nothing to show
        }

        Recompute();
        return true;
    }

    public void Cancel()
    {
        foreach (var job in _jobs.Values)
        {
            job.Handle.Cancel();
        }
    }

    private void Recompute()
    {
        var jobs = _jobs.Values;
        var total = jobs.Sum(j => j.Inputs);
        Fraction = jobs.All(j => double.IsNaN(j.Fraction))
            ? double.NaN
            : jobs.Sum(j => double.IsNaN(j.Fraction) ? 0 : j.Fraction * j.Inputs) / Math.Max(1, total);

        if (jobs.Any(j => j.Result is null))
        {
            return;
        }

        var results = jobs.Select(j => j.Result!).ToList();
        Outputs = [.. results.SelectMany(r => r.Outputs)];
        var failed = results.Where(r => r.Status == JobStatus.Failed).ToList();
        UserMessage = failed.FirstOrDefault()?.UserMessage;
        Details = failed.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, failed.Select(r => r.Details));
        Status = failed.Count > 0 ? JobStatus.Failed
            : results.All(r => r.Status == JobStatus.Cancelled) ? JobStatus.Cancelled
            : JobStatus.Succeeded;
    }

    private sealed class Entry(int inputs, JobHandle handle)
    {
        public int Inputs { get; } = Math.Max(1, inputs);

        public JobHandle Handle { get; } = handle;

        public double Fraction { get; set; }

        public JobResult? Result { get; set; }
    }
}
