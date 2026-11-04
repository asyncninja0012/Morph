using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Morph.Core.Jobs;

/// <summary>One unit of work for <c>Morph.Worker</c> (PLAN §2). Output goes to <paramref name="OutputDirectory"/>.</summary>
/// <param name="Engine">Engine id, e.g. "imaging".</param>
/// <param name="ToolId">e.g. "convert.png".</param>
/// <param name="Parameters">Tool defaults (CV-4), later merged with quality settings and editor output; engines read what they know.</param>
public sealed record JobRequest(Guid Id, string ToolId, string Engine, IReadOnlyList<string> Inputs, string OutputDirectory,
    JsonObject? Parameters = null);

/// <param name="Fraction">0..1; NaN = indeterminate.</param>
public sealed record JobProgress(double Fraction, string? Stage);

public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }

public sealed record JobResult(Guid Id, JobStatus Status, IReadOnlyList<string> Outputs, string? UserMessage, string? Details, TimeSpan Elapsed);

/// <summary>Thrown by engines; the host turns it into a failed <see cref="JobResult"/> showing <see cref="UserMessage"/>.</summary>
public sealed class EngineException(string userMessage, string? details = null) : Exception(userMessage)
{
    public string UserMessage { get; } = userMessage;

    public string? Details { get; } = details;
}

/// <summary>Implemented in Morph.Engines and run only inside Morph.Worker.</summary>
public interface IEngine
{
    string Id { get; }

    /// <summary>Writes only to temp paths from <paramref name="namer"/>; the host commits or aborts them.</summary>
    Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct);
}

/// <summary>One stdout line from the Worker: progress while running, then exactly one result.</summary>
public sealed record WorkerMessage(JobProgress? Progress = null, JobResult? Result = null);

/// <summary>
/// App ↔ Worker wire format: one <see cref="JobRequest"/> line on stdin, <see cref="WorkerMessage"/> lines on stdout.
/// Closing stdin asks the Worker to cancel.
/// </summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JobRequest))]
[JsonSerializable(typeof(WorkerMessage))]
public sealed partial class JobJson : JsonSerializerContext;

/// <summary>Reports on the calling thread, in order (unlike <see cref="Progress{T}"/>, which posts to the thread pool).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
