using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Morph.Core.Jobs;

/// <summary>Runs one job in a fresh Morph.Worker.exe inside a Job Object (PLAN §2, §5): untrusted files are never parsed in the App process.</summary>
public static class WorkerClient
{
    /// <summary>How long a cancelled Worker gets to stop ffmpeg and delete its temp files before the Job Object kills it.</summary>
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(3);

    /// <param name="workerPath">Morph.Worker.exe, normally next to Morph.exe.</param>
    public static async Task<JobResult> RunAsync(string workerPath, JobRequest request, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var job = new WorkerJobObject(request.Engine == "media" ? 4UL << 30 : 2UL << 30);
        using var process = Process.Start(new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        }) ?? throw new InvalidOperationException("Morph.Worker.exe did not start.");
        job.Assign(process); // before the request is written, so ffmpeg can only start inside the job

        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JobJson.Default.JobRequest)).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        // Cancel = close stdin (the Worker stops ffmpeg and deletes temps), then kill the whole job if it lingers.
        using var cancel = ct.Register(() =>
        {
            process.StandardInput.Close();
            _ = Task.Delay(CancelGrace, CancellationToken.None).ContinueWith(_ => job.Dispose(), TaskScheduler.Default);
        });

        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        JobResult? result = null;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            WorkerMessage? message;
            try
            {
                message = JsonSerializer.Deserialize(line, JobJson.Default.WorkerMessage);
            }
            catch (JsonException)
            {
                continue; // a stray line from a native library; the crash result below covers a missing result
            }

            if (message?.Progress is { } p)
            {
                progress?.Report(p);
            }

            result = message?.Result ?? result;
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (result is not null)
        {
            return result;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        return ct.IsCancellationRequested
            ? new JobResult(request.Id, JobStatus.Cancelled, [], null, null, elapsed)
            : new JobResult(request.Id, JobStatus.Failed, [], "Morph's converter stopped unexpectedly.",
                $"exit code {process.ExitCode}: {await stderr.ConfigureAwait(false)}", elapsed);
    }
}
