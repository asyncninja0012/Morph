using System.Globalization;
using System.Text.Json.Nodes;

using Morph.Core.Jobs;

namespace Morph.Engines;

internal enum Preset { Small, Balanced, High }

/// <summary>
/// Compress and Resize parameters (IT-1, IT-2), shared by the imaging, media and PDF engines. capabilities.json sets them:
/// compress { "preset": "small" | "balanced" | "high", "targetMb"?: 10, "size"?: "keep", "long"?: 1920, "short"?: 1080 };
/// resize { "scale": 0.5 }, { "long": 1920, "short": 1080 } or, from the Resize window, { "width"?: 800, "height"?: 600, "stretch"?: true }.
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

    /// <summary>
    /// The size Resize asks for, or null when nothing would change. Presets keep the aspect ratio and never upscale; an exact
    /// size from the Resize window may enlarge, and with "stretch" sets both sides as given.
    /// </summary>
    public static (int Width, int Height)? Resize(int width, int height, JsonObject? parameters)
    {
        var (w, h) = (Number(parameters?["width"]), Number(parameters?["height"]));
        if (w is not null || h is not null)
        {
            (int, int) size = parameters?["stretch"]?.GetValue<bool>() == true && w is { } sw && h is { } sh
                ? ((int)sw, (int)sh)
                : Round(width, height, Math.Min(w / Math.Max(1, width) ?? double.MaxValue, h / Math.Max(1, height) ?? double.MaxValue));
            return size == (width, height) ? null : size;
        }

        var factor = Number(parameters?["scale"]) is { } scale
            ? scale
            : Factor(width, height, Number(parameters?["long"]) ?? int.MaxValue, Number(parameters?["short"]) ?? int.MaxValue);
        return Scaled(width, height, factor);
    }

    /// <summary>
    /// The frame Compress shrinks to: the Compress window's choice ("keep", or a long/short edge), else the preset's cap.
    /// </summary>
    public static (int Width, int Height)? CompressSize(int width, int height, JsonObject? parameters, int presetLong, int presetShort = int.MaxValue) =>
        (string?)parameters?["size"] == "keep" ? null
        : Number(parameters?["long"]) is { } l ? Fit(width, height, (int)l, (int)(Number(parameters?["short"]) ?? int.MaxValue))
        : Fit(width, height, presetLong, presetShort);

    /// <summary>Shrinks to fit a long and a short edge (1080p = 1920 × 1080 either way round); null if it already fits.</summary>
    public static (int Width, int Height)? Fit(int width, int height, int longEdge, int shortEdge = int.MaxValue) =>
        Scaled(width, height, Factor(width, height, longEdge, shortEdge));

    /// <summary>
    /// Rotate (IT-3): { "turn": 90 | 180 | 270 | 0, "flip"?: "horizontal" | "vertical" }. Clockwise; the flip comes after the turn.
    /// Anything else, or nothing to do, is a quarter turn right.
    /// </summary>
    public static (int Degrees, string? Flip) Rotate(JsonObject? parameters)
    {
        var turn = Number(parameters?["turn"]) is { } t ? (((int)t % 360) + 360) % 360 : 90;
        var flip = (string?)parameters?["flip"] is "horizontal" or "vertical" ? (string?)parameters?["flip"] : null;
        return turn is not (0 or 90 or 180 or 270) || (turn == 0 && flip is null) ? (90, null) : (turn, flip);
    }

    /// <summary>Protect/unlock (IT-5): { "password": "..." } from the password window. It never goes to a log or a file name.</summary>
    public static string Password(JsonObject? parameters) =>
        (string?)parameters?["password"] is { Length: > 0 } password ? password : throw new EngineException("Type a password first.", "no password parameter");

    // Any JSON number: a JsonObject built in code holds an int where parsed JSON would give a double.
    private static double? Number(JsonNode? node) =>
        node is null ? null : double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static double Factor(int width, int height, double longEdge, double shortEdge) =>
        Math.Min(longEdge / Math.Max(1, Math.Max(width, height)), shortEdge / Math.Max(1, Math.Min(width, height)));

    private static (int, int)? Scaled(int width, int height, double factor) =>
        factor >= 1 || width <= 0 || height <= 0 ? null : Round(width, height, factor);

    private static (int, int) Round(int width, int height, double factor) =>
        (Math.Max(1, (int)Math.Round(width * factor)), Math.Max(1, (int)Math.Round(height * factor)));
}
