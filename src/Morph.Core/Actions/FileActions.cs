namespace Morph.Core.Actions;

/// <summary>The Actions wheel's plain rules (FA-2), kept out of the App so they can be tested.</summary>
public static class FileActions
{
    /// <summary>How many recent Copy to / Move to folders are offered.</summary>
    public const int RecentCount = 6;

    /// <summary>
    /// Copy path: each path in double quotes, one per line (as Explorer's "Copy as path"); <paramref name="unix"/> turns the
    /// backslashes into forward slashes (C:/Users/me/a.txt), which Git Bash, PowerShell and most tools accept.
    /// </summary>
    public static string PathText(IEnumerable<string> paths, bool unix) =>
        string.Join("\r\n", paths.Select(p => $"\"{(unix ? p.Replace('\\', '/') : p)}\""));

    /// <summary>The recent folders with <paramref name="folder"/> first, no repeats, at most <see cref="RecentCount"/>.</summary>
    public static IReadOnlyList<string> AddRecent(IEnumerable<string> recent, string folder) =>
        [.. new[] { folder }.Concat(recent.Where(f => !string.Equals(Trim(f), Trim(folder), StringComparison.OrdinalIgnoreCase))).Take(RecentCount)];

    /// <summary>
    /// Windows Terminal reads ';' in its command line as "new tab", even inside quotes, so a folder with one opens in
    /// PowerShell instead.
    /// </summary>
    public static bool TerminalCanOpen(string folder) => !folder.Contains(';', StringComparison.Ordinal);

    private static string Trim(string folder) => folder.TrimEnd('\\', '/');
}
