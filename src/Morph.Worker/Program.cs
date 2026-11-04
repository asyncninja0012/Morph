using System.Diagnostics;
using System.Text.Json;

using Morph.Core.Jobs;
using Morph.Engines.Imaging;

// Runs one job per process (PLAN §2): JobRequest JSON on stdin, one JobResult JSON line on stdout.
// shortcut: no progress lines, cancellation or Job Object yet; task 0.5 adds the Job Object, task 1.4 the full protocol.
var started = Stopwatch.GetTimestamp();
var request = await JsonSerializer.DeserializeAsync(Console.OpenStandardInput(), JobJson.Default.JobRequest)
    ?? throw new InvalidOperationException("No job on stdin.");

IEngine[] engines = [new ImagingEngine()];
var namer = new OutputNamer(request.OutputDirectory);
JobResult result;
try
{
    var engine = engines.FirstOrDefault(e => e.Id == request.Engine)
        ?? throw new EngineException("This tool isn't available.", $"unknown engine {request.Engine}");
    await engine.RunAsync(request, namer, new Progress<JobProgress>(), CancellationToken.None);
    result = new JobResult(request.Id, JobStatus.Succeeded, namer.CommitAll(), null, null, Stopwatch.GetElapsedTime(started));
}
catch (EngineException ex)
{
    namer.AbortAll();
    result = new JobResult(request.Id, JobStatus.Failed, [], ex.UserMessage, ex.Details, Stopwatch.GetElapsedTime(started));
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    namer.AbortAll();
    result = new JobResult(request.Id, JobStatus.Failed, [], "Morph couldn't convert this file.", ex.ToString(), Stopwatch.GetElapsedTime(started));
}

await using var stdout = Console.OpenStandardOutput();
await JsonSerializer.SerializeAsync(stdout, result, JobJson.Default.JobResult);
stdout.WriteByte((byte)'\n');
return result.Status == JobStatus.Succeeded ? 0 : 1;
