using System.Text.Json.Nodes;

namespace Morph.Core.Settings;

/// <summary>
/// Settings → Wheel (IX-7): wedges pinned to the front in the user's order, hidden wedges, and whether the rest is ordered by
/// use. Stored in settings.json as <c>tools["settings.wheel"]</c>; use counts as <c>tools["settings.usage"]</c>.
/// </summary>
public sealed record WheelPrefs(IReadOnlyList<string> Pinned, IReadOnlyList<string> Hidden, bool Learn = true)
{
    public const string SettingsId = "settings.wheel";
    public const string UsageId = "settings.usage";

    public static WheelPrefs Default { get; } = new([], []);

    public static WheelPrefs From(JsonObject? json) => json is null ? Default : new(
        Strings(json["pinned"]), Strings(json["hidden"]), json["learn"]?.GetValue<bool>() ?? true);

    public JsonObject ToJson() => new()
    {
        ["pinned"] = new JsonArray([.. Pinned.Select(p => (JsonNode?)p)]),
        ["hidden"] = new JsonArray([.. Hidden.Select(h => (JsonNode?)h)]),
        ["learn"] = Learn,
    };

    /// <summary>
    /// The wheel's order: pinned wedges first in pinned order, then the rest most-used first when <see cref="Learn"/> is on,
    /// ties (and everything when it's off) in the given order; hidden wedges are left out.
    /// </summary>
    public IReadOnlyList<T> Apply<T>(IReadOnlyList<T> items, Func<T, string> id, IReadOnlyDictionary<string, int> uses)
    {
        var shown = items.Where(i => !Hidden.Contains(id(i))).ToList();
        var pinned = Pinned.SelectMany(p => shown.Where(i => id(i) == p)).ToList();
        var rest = shown.Except(pinned).Select((item, index) => (item, index));
        if (Learn)
        {
            rest = rest.OrderByDescending(r => uses.GetValueOrDefault(id(r.item))).ThenBy(r => r.index);
        }

        return [.. pinned, .. rest.Select(r => r.item)];
    }

    /// <summary>Use counts from <c>tools["settings.usage"]</c>.</summary>
    public static Dictionary<string, int> Usage(JsonObject? json) =>
        json?.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => p.Value!.GetValue<int>()) ?? [];

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(n => n?.GetValue<string>()).OfType<string>()] : [];
}
