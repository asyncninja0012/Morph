using System.Globalization;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace Morph.Core.Logging;

/// <summary>
/// Plain-text daily log files, <c>morph-yyyyMMdd.log</c>, kept for <paramref name="keepDays"/> days. Callers only queue a
/// line; one background task does the I/O, so logging is safe on the UI thread and in hooks. Also echoes to
/// <paramref name="echo"/> (the console when Morph is started from a terminal).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Channel<(DateTime Time, string Line)> _lines = Channel.CreateUnbounded<(DateTime, string)>(new() { SingleReader = true });
    private readonly LogLevel _minimum;
    private readonly TextWriter? _echo;
    private readonly Task _writer;

    public FileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information, int keepDays = 7, TextWriter? echo = null)
    {
        Directory = directory;
        _minimum = minimum;
        _echo = echo;
        _writer = Task.Run(() => WriteAsync(keepDays));
    }

    public string Directory { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Flushes what is queued (waits up to 2 s) and stops.</summary>
    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private void Enqueue(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {Level(level)} {category}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        _lines.Writer.TryWrite((now, line));
    }

    private async Task WriteAsync(int keepDays)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var old in new DirectoryInfo(Directory).EnumerateFiles("morph-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-keepDays)))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _echo?.WriteLine($"Logging to {Directory} failed: {ex.Message}");
        }

        await foreach (var (time, line) in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _echo?.WriteLine(line);
            try
            {
                var path = Path.Combine(Directory, $"morph-{time.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
                await File.AppendAllTextAsync(path, line + Environment.NewLine).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disk full or folder gone: the line is lost, the app goes on.
            }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "FATAL",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Enqueue(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
