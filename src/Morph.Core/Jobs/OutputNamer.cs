namespace Morph.Core.Jobs;

/// <param name="Path">The output's final name. <see cref="OutputNamer.Reserve"/> has already created it empty; the engine overwrites it.</param>
public sealed record PendingOutput(string Path);

/// <summary>
/// Output naming for one job (JB-3). Each output appears under its final name as soon as it is reserved and fills in while
/// the engine writes (developer's call, replaces JB-4's temp + rename). Never overwrites anything: a clash becomes
/// "photo (2).png". A failed or cancelled job deletes its outputs; a killed Worker can leave a partial file behind.
/// </summary>
public sealed class OutputNamer(string outputDirectory)
{
    private readonly List<string> _reserved = [];

    /// <summary>photo.jpg + ".png" → photo.png, or the first free "photo (n).png", created empty so nothing else takes it.</summary>
    public PendingOutput Reserve(string input, string extension, string suffix = "")
    {
        var wanted = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) + suffix + extension);
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? wanted : Numbered(wanted, n);
            try
            {
                // CreateNew is atomic: it fails rather than take a file another writer created a moment ago.
                new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();
                _reserved.Add(candidate);
                return new PendingOutput(candidate);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && (File.Exists(candidate) || Directory.Exists(candidate)))
            {
                // Taken (by a file or a folder); try the next number.
            }
        }
    }

    /// <returns>The outputs, in reservation order.</returns>
    public IReadOnlyList<string> CommitAll()
    {
        IReadOnlyList<string> outputs = [.. _reserved];
        _reserved.Clear();
        return outputs;
    }

    /// <summary>Deletes one output the engine decided not to make (e.g. a file Compress couldn't make smaller).</summary>
    public void Discard(PendingOutput output)
    {
        File.Delete(output.Path);
        _reserved.Remove(output.Path);
    }

    /// <summary>Deletes every output reserved so far (failure or cancel).</summary>
    public void AbortAll()
    {
        foreach (var path in _reserved)
        {
            File.Delete(path);
        }

        _reserved.Clear();
    }

    private static string Numbered(string path, int n) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({n}){Path.GetExtension(path)}");
}
