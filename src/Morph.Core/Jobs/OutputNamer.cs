namespace Morph.Core.Jobs;

/// <param name="TempPath">Where the engine writes. Same folder as the output, so the commit is a rename.</param>
/// <param name="FinalPath">The name wanted; the commit picks "name (2).ext" if it is taken by then.</param>
public sealed record PendingOutput(string TempPath, string FinalPath);

/// <summary>
/// Output naming and temp+rename for one job (JB-3/JB-4). Never overwrites anything: a clash becomes "photo (2).png".
/// </summary>
public sealed class OutputNamer(string outputDirectory)
{
    private readonly List<PendingOutput> _pending = [];

    /// <summary>photo.jpg + ".png" → photo.png. Nothing is created on disk until the engine writes the temp path.</summary>
    public PendingOutput Reserve(string input, string extension, string suffix = "")
    {
        // Not hidden: Magick and ffmpeg open with CREATE_ALWAYS, which fails with access denied on a hidden file.
        var temp = Path.Combine(outputDirectory, $".morph-{Guid.NewGuid():N}.tmp");
        var final = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) + suffix + extension);
        var pending = new PendingOutput(temp, final);
        _pending.Add(pending);
        return pending;
    }

    /// <summary>Renames every temp file to a free final name, in reservation order.</summary>
    /// <returns>The final paths.</returns>
    public IReadOnlyList<string> CommitAll()
    {
        var outputs = new List<string>(_pending.Count);
        foreach (var p in _pending)
        {
            for (var n = 1; ; n++)
            {
                var candidate = n == 1 ? p.FinalPath : Numbered(p.FinalPath, n);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    File.Move(p.TempPath, candidate, overwrite: false);
                    outputs.Add(candidate);
                    break;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Lost a race with another writer; try the next number.
                }
            }
        }

        _pending.Clear();
        return outputs;
    }

    /// <summary>Deletes every temp file written so far (failure or cancel).</summary>
    public void AbortAll()
    {
        foreach (var p in _pending)
        {
            File.Delete(p.TempPath);
        }

        _pending.Clear();
    }

    private static string Numbered(string path, int n) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({n}){Path.GetExtension(path)}");
}
