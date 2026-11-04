using System.Text.Json.Serialization;

namespace Morph.Core.Jobs;

/// <summary>One unit of work for <c>Morph.Worker</c> (PLAN §2). Output goes to <paramref name="OutputDirectory"/>.</summary>
/// <param name="Engine">Engine id, e.g. "imaging".</param>
/// <param name="ToolId">e.g. "convert.png".</param>
public sealed record JobRequest(Guid Id, string ToolId, string Engine, IReadOnlyList<string> Inputs, string OutputDirectory);

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

/// <summary>App ↔ Worker wire format: one <see cref="JobRequest"/> on stdin, one <see cref="JobResult"/> line on stdout.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(JobRequest))]
[JsonSerializable(typeof(JobResult))]
public sealed partial class JobJson : JsonSerializerContext;
