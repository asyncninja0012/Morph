using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

using Morph.Core.Jobs;

namespace Morph.App.Jobs;

/// <summary>Runs one job in a fresh Morph.Worker.exe (PLAN §2): untrusted files are never parsed in the App process.</summary>
// shortcut: no Job Object, progress or cancellation; task 0.5 adds the Job Object, 1.3/1.4 the queue and protocol.
internal static class WorkerClient
{
    public static async Task<JobResult> RunAsync(JobRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Morph.Worker.exe"))
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

        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, JobJson.Default.JobRequest));
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var lastLine = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

        try
        {
            if (lastLine is not null && JsonSerializer.Deserialize(lastLine, JobJson.Default.JobResult) is { } result)
            {
                return result;
            }
        }
        catch (JsonException)
        {
            // Falls through to the crash result below, which carries stderr.
        }

        return new JobResult(request.Id, JobStatus.Failed, [], "Morph's converter stopped unexpectedly.",
            $"exit code {process.ExitCode}: {await stderr}", Stopwatch.GetElapsedTime(started));
    }
}
