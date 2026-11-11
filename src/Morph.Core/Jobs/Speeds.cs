using System.Globalization;
using System.Text.Json.Nodes;

namespace Morph.Core.Jobs;

/// <summary>The Change speed job's parameters (ED-7): { "speed": 2 }, one of <see cref="Stops"/>. Sound keeps its pitch.</summary>
public static class Speeds
{
    /// <summary>The slider's stops (PRD ED-7), slowest first.</summary>
    public static readonly IReadOnlyList<double> Stops = [0.25, 0.5, 1, 1.5, 2, 4];

    public static JsonObject ToParameters(double speed) => new() { ["speed"] = speed };

    /// <exception cref="EngineException">No speed, or one that isn't a stop.</exception>
    public static double Parse(JsonObject? parameters)
    {
        var node = parameters?["speed"];
        return node is JsonValue && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && Stops.Contains(speed)
            ? speed
            : throw new EngineException("Morph couldn't read the speed.", $"bad speed: {node?.ToJsonString()}");
    }

    /// <summary>How the speed reads: "2×", "0.25×".</summary>
    public static string Label(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    /// <summary>The output name's suffix: "-2x", "-0.25x".</summary>
    public static string Suffix(double speed) => "-" + speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    /// <summary>
    /// ffmpeg's pitch-keeping tempo filter, as a chain of steps between 0.5 and 2 (atempo sounds best in that range):
    /// 4 → "atempo=2,atempo=2", 0.25 → "atempo=0.5,atempo=0.5".
    /// </summary>
    public static string Atempo(double speed)
    {
        var steps = new List<string>();
        for (; speed > 2; speed /= 2)
        {
            steps.Add("atempo=2");
        }

        for (; speed < 0.5; speed /= 0.5)
        {
            steps.Add("atempo=0.5");
        }

        steps.Add($"atempo={speed.ToString("0.###", CultureInfo.InvariantCulture)}");
        return string.Join(',', steps);
    }
}
