using System.Globalization;
using System.Text.Json.Nodes;

namespace Morph.Engines;

internal enum Preset { Small, Balanced, High }

/// <summary>
/// Compress and Resize parameters (IT-1, IT-2), shared by the imaging, media and PDF engines. capabilities.json sets them:
/// compress { "preset": "small" | "balanced" | "high" } or { "targetMb": 10 }; resize { "scale": 0.5 } or { "long": 1920, "short": 1080 }.
/// </summary>
internal static class Presets
{
    public static Preset Compress(JsonObject? parameters) => (string?)parameters?["preset"] switch
    {
        "small" => Preset.Small,
        "high" => Preset.High,
        _ => Preset.Balanced,
    };

    /// <summary>"Under 10 MB" in bytes: decimal megabytes, so it's also under 10 MiB, whichever the upload form means.</summary>
    public static long? TargetBytes(JsonObject? parameters) =>
        Number(parameters?["targetMb"]) is { } mb ? (long)(mb * 1_000_000) : null;

    public static string Megabytes(long bytes) => (bytes / 1_000_000.0).ToString("0.##", CultureInfo.CurrentCulture);

    /// <summary>The size a Resize choice asks for, keeping the aspect ratio. Null when the picture is already that small: never upscales.</summary>
    public static (int Width, int Height)? Resize(int width, int height, JsonObject? parameters)
    {
        var factor = Number(parameters?["scale"]) is { } scale
            ? scale
            : Factor(width, height, Number(parameters?["long"]) ?? int.MaxValue, Number(parameters?["short"]) ?? int.MaxValue);
        return Scaled(width, height, factor);
    }

    /// <summary>Shrinks to fit a long and a short edge (1080p = 1920 × 1080 either way round); null if it already fits.</summary>
    public static (int Width, int Height)? Fit(int width, int height, int longEdge, int shortEdge = int.MaxValue) =>
        Scaled(width, height, Factor(width, height, longEdge, shortEdge));

    // Any JSON number: a JsonObject built in code holds an int where parsed JSON would give a double.
    private static double? Number(JsonNode? node) =>
        node is null ? null : double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static double Factor(int width, int height, double longEdge, double shortEdge) =>
        Math.Min(longEdge / Math.Max(1, Math.Max(width, height)), shortEdge / Math.Max(1, Math.Min(width, height)));

    private static (int, int)? Scaled(int width, int height, double factor) =>
        factor >= 1 || width <= 0 || height <= 0
            ? null
            : (Math.Max(1, (int)Math.Round(width * factor)), Math.Max(1, (int)Math.Round(height * factor)));
}
