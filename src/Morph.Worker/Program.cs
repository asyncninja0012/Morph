using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Morph.Core.Jobs;
using Morph.Engines.Imaging;
using Morph.Engines.Media;

// Runs one job per process (PLAN §2): a JobRequest line on stdin, WorkerMessage lines on stdout.
// stdin closing before the job ends means cancel. The App also kills this whole Job Object if we don't stop in time.
var started = Stopwatch.GetTimestamp();
using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
var request = JsonSerializer.Deserialize(await stdin.ReadLineAsync() ?? "null", JobJson.Default.JobRequest)
    ?? throw new InvalidOperationException("No job on stdin.");

using var cts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    await stdin.ReadToEndAsync(CancellationToken.None);
    await cts.CancelAsync();
}, CancellationToken.None);

await using var stdout = Console.OpenStandardOutput();
var writeLock = new Lock();
void Write(WorkerMessage message)
{
    lock (writeLock)
    {
        JsonSerializer.Serialize(stdout, message, JobJson.Default.WorkerMessage);
        stdout.WriteByte((byte)'\n');
        stdout.Flush();
    }
}

IEngine[] engines = [new ImagingEngine(), new MediaEngine()];
var namer = new OutputNamer(request.OutputDirectory);
JobResult result;
try
{
    var engine = engines.FirstOrDefault(e => e.Id == request.Engine)
        ?? throw new EngineException("This tool isn't available.", $"unknown engine {request.Engine}");
    await engine.RunAsync(request, namer, new SyncProgress<JobProgress>(p => Write(new WorkerMessage(Progress: p))), cts.Token);
    cts.Token.ThrowIfCancellationRequested();
    result = new JobResult(request.Id, JobStatus.Succeeded, namer.CommitAll(), null, null, Stopwatch.GetElapsedTime(started));
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    namer.AbortAll();
    result = new JobResult(request.Id, JobStatus.Cancelled, [], null, null, Stopwatch.GetElapsedTime(started));
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

Write(new WorkerMessage(Result: result));
return result.Status == JobStatus.Succeeded ? 0 : 1;
