using Microsoft.Extensions.Logging;

using Morph.Core.Logging;

namespace Morph.Core.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-log-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Writes_lines_at_or_above_the_minimum_with_category_and_exception()
    {
        var echo = new StringWriter();
        using (var provider = new FileLoggerProvider(_dir, LogLevel.Information, echo: echo))
        {
            var log = provider.CreateLogger("Jobs");
            log.LogDebug("hidden");
            log.LogInformation("convert.png succeeded in {Ms} ms", 12);
            log.LogError(new InvalidOperationException("boom"), "failed");
        }

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(_dir, "morph-*.log")));
        Assert.Contains("INFO  Jobs: convert.png succeeded in 12 ms", text, StringComparison.Ordinal);
        Assert.Contains("ERROR Jobs: failed", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", text, StringComparison.Ordinal);
        Assert.Equal(text, echo.ToString());
    }

    [Fact]
    public void Deletes_logs_older_than_the_kept_days()
    {
        var old = Path.Combine(_dir, "morph-20200101.log");
        var recent = Path.Combine(_dir, "morph-20990101.log");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-8));

        new FileLoggerProvider(_dir, keepDays: 7).Dispose();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
