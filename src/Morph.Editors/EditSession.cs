using System.Text.Json.Nodes;

namespace Morph.Editors;

public sealed record EditedFile(string Path, JsonObject Parameters);

/// <summary>
/// Which file the shell shows for a drop (ED-6). Same for all: the first file is edited and its parameters go to every file.
/// One by one: each file is edited in turn. A file that can't be shown is skipped.
/// </summary>
public sealed class EditSession(IReadOnlyList<string> files)
{
    private readonly List<string> _pending = [.. files];
    private readonly List<EditedFile> _done = [];

    /// <summary>One by one instead of same for all; can change only until the first file is done.</summary>
    public bool StepThrough
    {
        get;
        set => field = !Started ? value : throw new InvalidOperationException("The mode is fixed once a file is done.");
    }

    public string Current => _pending[0];

    /// <summary>1-based position of <see cref="Current"/> among the files dropped.</summary>
    public int Number => files.Count - _pending.Count + 1;

    public int Count => files.Count;

    public bool Started => _pending.Count < files.Count;

    /// <summary>Done on the current file ends the session (Done rather than Next).</summary>
    public bool IsLast => !StepThrough || _pending.Count == 1;

    /// <summary>Records the current file's parameters; returns every file's when the session is over, else null and moves on.</summary>
    public IReadOnlyList<EditedFile>? Done(JsonObject parameters)
    {
        if (!StepThrough)
        {
            _done.AddRange(_pending.Select(f => new EditedFile(f, parameters)));
            _pending.Clear();
            return _done;
        }

        _done.Add(new(_pending[0], parameters));
        _pending.RemoveAt(0);
        return _pending.Count == 0 ? _done : null;
    }

    /// <summary>Leaves the current file out; returns the files done so far when none are left (empty if nothing was done), else null.</summary>
    public IReadOnlyList<EditedFile>? Skip()
    {
        _pending.RemoveAt(0);
        return _pending.Count == 0 ? _done : null;
    }
}
