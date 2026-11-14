using Morph.Core.Logging;

namespace Morph.Core.Tests;

public sealed class CrashReportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-crash-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_crash_is_saved_and_told_about_once()
    {
        Assert.Null(CrashReport.TakeUnseen(_dir));
        CrashReport.Write(_dir, new InvalidOperationException("first"), new Version(1, 0, 0), new DateTime(2026, 11, 14, 9, 0, 0));
        var second = CrashReport.Write(_dir, new InvalidOperationException("second"), new Version(1, 0, 0), new DateTime(2026, 11, 14, 10, 0, 0));

        Assert.Equal(second, CrashReport.Latest(_dir));
        Assert.Contains("second", File.ReadAllText(second), StringComparison.Ordinal);
        Assert.Equal(second, CrashReport.TakeUnseen(_dir));
        Assert.Null(CrashReport.TakeUnseen(_dir));
        Assert.Equal(second, CrashReport.Latest(_dir)); // the "seen" marker isn't a crash
    }

    [Fact]
    public void The_issue_link_is_prefilled_and_short_enough()
    {
        var crash = "System.InvalidOperationException: boom\n" + string.Concat(Enumerable.Repeat("   at Morph.App.Something() in App.cs:line 1\n", 400));

        var url = CrashReport.IssueUrl(new Version(1, 2, 3), crash);
        var text = Uri.UnescapeDataString(url.Query);

        Assert.StartsWith(CrashReport.NewIssue + "?title=Crash", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("Morph 1.2.3", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", text, StringComparison.Ordinal);
        Assert.Contains("shortened", text, StringComparison.Ordinal);
        Assert.True(url.AbsoluteUri.Length < 8000, $"{url.AbsoluteUri.Length} characters");
        Assert.StartsWith(CrashReport.NewIssue + "?title=Problem", CrashReport.IssueUrl(null, null).AbsoluteUri, StringComparison.Ordinal);
    }
}
