using System.Globalization;
using System.Text.Json.Nodes;

namespace Morph.Core.Jobs;

/// <summary>
/// The Save frames job's parameters (ED-8): { "times": [seconds, ...] }, each a frame to save as a full-size PNG, in the order the
/// user added them. Each is a frame's start time (frame k of a 30 fps video is k / 30).
/// </summary>
public static class Frames
{
    /// <summary>The most frames one job saves.</summary>
    public const int MaxFrames = 500;

    public static JsonObject ToParameters(IEnumerable<double> times) =>
        new() { ["times"] = new JsonArray([.. times.Select(t => JsonValue.Create(Math.Round(t, 6)))]) };

    /// <exception cref="EngineException">No times, too many, or one that isn't a time.</exception>
    public static IReadOnlyList<double> Parse(JsonObject? parameters)
    {
        if (parameters?["times"] is not JsonArray { Count: > 0 } list)
        {
            throw new EngineException("Add a frame first.", "no times");
        }

        if (list.Count > MaxFrames)
        {
            throw new EngineException($"Morph saves at most {MaxFrames} frames at once.", $"{list.Count} times");
        }

        return
        [
            .. list.Select(n => n is JsonValue && double.TryParse(n.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && double.IsFinite(t) && t >= 0
                ? t
                : throw new EngineException("Morph couldn't read which frames to save.", $"bad time: {n?.ToJsonString()}")),
        ];
    }

    /// <summary>The output name's suffix for a frame at <paramref name="seconds"/>: "-frame-12.5s", "-frame-0s".</summary>
    public static string Suffix(double seconds) => $"-frame-{Math.Round(seconds, 3).ToString("0.###", CultureInfo.InvariantCulture)}s";
}
