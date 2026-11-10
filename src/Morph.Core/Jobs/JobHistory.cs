namespace Morph.Core.Jobs;

/// <summary>
/// The tray's job history (JB-6): the last <see cref="Capacity"/> drops, newest first, each with its inputs, outputs and
/// status, and Undo (JB-5): a drop's outputs can be recycled for <see cref="UndoWindow"/> after it ends. Not thread-safe:
/// use it from one thread (the UI's).
/// </summary>
public sealed class JobHistory(TimeProvider time)
{
    public const int Capacity = 50;

    public static readonly TimeSpan UndoWindow = TimeSpan.FromMinutes(10);

    private readonly List<JobHistoryEntry> _entries = [];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<JobHistoryEntry> Entries => _entries;

    /// <summary>Call right after enqueueing, before any update for these jobs is applied.</summary>
    public JobHistoryEntry Add(string tool, IReadOnlyList<string> inputs, IReadOnlyList<(JobRequest Request, JobHandle Handle)> jobs)
    {
        var entry = new JobHistoryEntry(tool, inputs, new JobGroup(jobs), time.GetLocalNow());
        _entries.Insert(0, entry);
        if (_entries.Count > Capacity)
        {
            _entries.RemoveAt(Capacity); // shortcut: a running drop that falls off can't be cancelled from here; 50 at once is unlikely
        }

        return entry;
    }

    /// <returns>True when an entry changed.</returns>
    public bool Apply(JobUpdate update)
    {
        foreach (var entry in _entries)
        {
            if (entry.Group.Apply(update))
            {
                if (entry.Group.Status != JobStatus.Running)
                {
                    entry.Finished = time.GetLocalNow();
                }

                return true;
            }
        }

        return false;
    }

    public bool CanUndo(JobHistoryEntry entry) =>
        !entry.Undone && entry.Group.Outputs.Count > 0 && entry.Finished is { } done && time.GetLocalNow() - done <= UndoWindow;

    /// <summary>Marks the entry undone and returns the outputs to recycle; empty when it can't be undone (any more).</summary>
    public IReadOnlyList<string> TakeUndo(JobHistoryEntry entry)
    {
        if (!CanUndo(entry))
        {
            return [];
        }

        entry.Undone = true;
        return entry.Group.Outputs;
    }
}

/// <summary>One drop in <see cref="JobHistory"/>.</summary>
/// <param name="Tool">The wedge's label, e.g. "PNG" or "Compress".</param>
public sealed class JobHistoryEntry(string tool, IReadOnlyList<string> inputs, JobGroup group, DateTimeOffset started)
{
    public string Tool { get; } = tool;

    public IReadOnlyList<string> Inputs { get; } = inputs;

    public JobGroup Group { get; } = group;

    public DateTimeOffset Started { get; } = started;

    public DateTimeOffset? Finished { get; internal set; }

    /// <summary>Its outputs went to the Recycle Bin.</summary>
    public bool Undone { get; internal set; }

    /// <summary>Why recycling the outputs failed, set by whoever recycles them.</summary>
    public string? UndoError { get; set; }
}
