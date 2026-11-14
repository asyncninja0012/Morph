using System.Globalization;

namespace Morph.Core.Logging;

/// <summary>
/// Crash files and "Report a problem" (PLAN 5.4). A crash is saved next to the logs as <c>crash-yyyyMMdd-HHmmss.txt</c> and never
/// sent anywhere: there is no upload server (D12). Reporting opens a pre-filled GitHub issue in the browser for the user to read,
/// edit and send; the log is attached by hand.
/// </summary>
public static class CrashReport
{
    public const string NewIssue = "https://github.com/asyncninja0012/Morph/issues/new";

    private const string Prefix = "crash-";
    private const string SeenFile = "crash-seen.txt";
    private const int MaxCrashChars = 2500; // keeps the issue URL well under browsers' and GitHub's ~8 KB limit

    /// <summary>Saves <paramref name="exception"/>; returns the file. Runs while the App is going down, so it's synchronous.</summary>
    public static string Write(string folder, Exception exception, Version? version, DateTime now)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Prefix}{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");
        File.WriteAllText(path, $"Morph {version?.ToString(3)} on {Environment.OSVersion.VersionString}, {now.ToString("u", CultureInfo.InvariantCulture)}\n\n{exception}\n");
        return path;
    }

    /// <summary>The newest crash file, or null.</summary>
    public static string? Latest(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, Prefix + "*.txt").Where(f => Path.GetFileName(f) != SeenFile).Order(StringComparer.Ordinal).LastOrDefault()
            : null;

    /// <summary>The newest crash file the user hasn't been told about yet, marking it told.</summary>
    public static string? TakeUnseen(string folder)
    {
        var latest = Latest(folder);
        var seen = Path.Combine(folder, SeenFile);
        if (latest is null || (File.Exists(seen) && File.ReadAllText(seen).Trim() == Path.GetFileName(latest)))
        {
            return null;
        }

        File.WriteAllText(seen, Path.GetFileName(latest));
        return latest;
    }

    /// <summary>A new-issue page with the version, Windows build and, if given, the crash (shortened), for the user to complete.</summary>
    public static Uri IssueUrl(Version? version, string? crash)
    {
        if (crash is { Length: > MaxCrashChars })
        {
            crash = crash[..MaxCrashChars] + "\n… (shortened; the whole file is in the logs folder)";
        }

        var body = $"""
            **What happened, and what did you expect?**


            **Steps to reproduce** (which file type, which wheel and wedge):


            Morph {version?.ToString(3)}, Windows {Environment.OSVersion.Version}

            Please attach the log: tray icon → Open logs folder, drag today's morph-*.log here. Logs and crash files can contain file names; remove anything private before you send.
            """;
        if (crash is not null)
        {
            body += $"\n\n**Crash**\n```\n{crash}\n```";
        }

        return new Uri($"{NewIssue}?title={Uri.EscapeDataString(crash is null ? "Problem: " : "Crash: ")}&body={Uri.EscapeDataString(body)}");
    }
}
