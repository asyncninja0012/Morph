using System.Text.Json;
using System.Text.Json.Nodes;

namespace Morph.Core.Settings;

/// <summary>
/// The last settings per tool (ED-6), kept under "tools" in settings.json; other keys in the file are left as they are.
/// Thread-safe; saves go to a temp file first, then replace settings.json.
/// </summary>
public sealed class ToolSettings
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string _path;
    private readonly JsonObject _root;
    private readonly SemaphoreSlim _save = new(1, 1);

    private ToolSettings(string path, JsonObject root)
    {
        _path = path;
        _root = root;
    }

    /// <summary>Reads <paramref name="path"/>. A missing file is empty; an unreadable one is renamed to settings.bad.json and replaced.</summary>
    /// <returns>The settings, and why the file couldn't be read (null if it could).</returns>
    public static async Task<(ToolSettings Settings, Exception? Error)> LoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return (new(path, []), null);
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return (new(path, JsonNode.Parse(text) as JsonObject ?? throw new JsonException("settings.json isn't a JSON object")), null);
        }
        catch (JsonException ex)
        {
            File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "settings.bad.json"), overwrite: true);
            return (new(path, []), ex);
        }
    }

    /// <summary>A copy of the tool's last settings, or null if it has none.</summary>
    public JsonObject? Get(string toolId)
    {
        lock (_root)
        {
            return _root["tools"]?[toolId] is JsonObject saved ? saved.DeepClone().AsObject() : null;
        }
    }

    public async Task SetAsync(string toolId, JsonObject settings, CancellationToken ct)
    {
        lock (_root)
        {
            if (_root["tools"] is not JsonObject tools)
            {
                _root["tools"] = tools = [];
            }

            tools[toolId] = settings.DeepClone();
        }

        await _save.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string text;
            lock (_root)
            {
                text = _root.ToJsonString(Indented); // taken inside the save lock, so a later save never writes older text
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, text, ct).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _save.Release();
        }
    }
}
