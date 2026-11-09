using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Morph.Core.Formats;

namespace Morph.Core.Capabilities;

public enum WheelGroup { Convert, Tools, Actions }

/// <summary>One wedge's identity. <paramref name="Glyph"/> is a Segoe Fluent Icons character; <paramref name="Hint"/> the hover text.</summary>
/// <param name="Id">"convert.png", "compress", "action.copy-path".</param>
/// <param name="Defaults">Engine parameters used unless settings say otherwise (CV-4), e.g. { "quality": 90 }. Per tool, so per output format.</param>
/// <param name="MinFiles">Fewest files the tool makes sense for; 2 for merge.</param>
/// <param name="Choices">Presets on the wheel's outer ring (Compress → Small/Balanced/High); the wedge itself runs <paramref name="Defaults"/>.</param>
public sealed record ToolDescriptor(string Id, string Label, string Glyph, string Hint, WheelGroup Wheel, JsonObject? Defaults = null,
    int MinFiles = 1, IReadOnlyList<ToolChoice>? Choices = null)
{
    /// <summary>Job parameters: the tool's defaults with the choice's on top; null choice = the wedge itself.</summary>
    public JsonObject? ParametersFor(string? choiceId)
    {
        var choice = Choices?.FirstOrDefault(c => c.Id == choiceId);
        if (choice is null)
        {
            return Defaults?.DeepClone().AsObject();
        }

        var merged = Defaults?.DeepClone().AsObject() ?? [];
        foreach (var (key, value) in choice.Defaults)
        {
            merged[key] = value?.DeepClone();
        }

        return merged;
    }
}

/// <summary>One preset of a tool, e.g. Compress → "Under 10 MB".</summary>
/// <param name="Kinds">Only offered when every file is one of these kinds; null = all the tool takes.</param>
public sealed record ToolChoice(string Id, string Label, JsonObject Defaults, IReadOnlyList<FileKind>? Kinds = null);

/// <summary>One row of <c>capabilities.json</c> (CV-3): this input format can run this tool with this engine.</summary>
/// <param name="Out">Output format id for conversions; null for tools that keep the format.</param>
/// <param name="Requires">Requirement ids (e.g. a Store codec) that must be present, else the option is disabled (CV-6).</param>
/// <param name="Beta">Approximate conversion, labelled beta (CV-5).</param>
public sealed record CapabilityRow(string In, string Tool, string Engine, string? Out = null,
    IReadOnlyList<string>? Requires = null, bool Beta = false)
{
    public string ToolId => Tool == "convert" ? $"convert.{Out}" : Tool;
}

/// <summary>Why an option can be unavailable and where to fix it (CV-6).</summary>
public sealed record Requirement(string Reason, Uri? Fix = null);

/// <param name="DisabledReason">Non-null when the tool fits the files but something it needs is missing.</param>
/// <param name="Choices">The tool's presets that fit every file.</param>
public sealed record WheelOption(ToolDescriptor Tool, string? DisabledReason, Uri? FixLink, bool Beta, IReadOnlyList<ToolChoice> Choices);

/// <summary>
/// The capability matrix (PLAN §2): which tools the wheel offers for a set of files. An option appears only if it
/// fits every file (IX-3). A conversion also counts files already in the target format as fitting, so a JPG + PNG
/// drag still offers PNG, while a PNG-only drag doesn't.
/// </summary>
public sealed class CapabilityMatrix
{
    private readonly IReadOnlyList<ToolDescriptor> _tools;
    private readonly IReadOnlyDictionary<string, Requirement> _requirements;
    private readonly Dictionary<(string In, string ToolId), CapabilityRow> _rows;
    private readonly Func<string, bool> _isAvailable;

    private CapabilityMatrix(CapabilityFile file, Func<string, bool> isAvailable)
    {
        _tools = file.Tools;
        _requirements = file.Requirements;
        _rows = file.Rows.ToDictionary(r => (r.In, r.ToolId));
        _isAvailable = isAvailable;
        Rows = file.Rows;

        var unknown = file.Rows.Select(r => r.ToolId).Concat(file.Rows.SelectMany(r => r.Requires ?? []))
            .FirstOrDefault(id => !_tools.Any(t => t.Id == id) && !_requirements.ContainsKey(id));
        if (unknown is not null)
        {
            throw new InvalidDataException($"capabilities.json: '{unknown}' is used by a row but not declared.");
        }
    }

    public IReadOnlyList<CapabilityRow> Rows { get; }

    /// <summary>Reads the <c>capabilities.json</c> shipped inside Morph.Core.</summary>
    /// <param name="isAvailable">Whether a requirement id is met right now; called on every lookup, so cache probes.</param>
    public static CapabilityMatrix LoadDefault(Func<string, bool> isAvailable)
    {
        using var json = typeof(CapabilityMatrix).Assembly.GetManifestResourceStream("capabilities.json")
            ?? throw new InvalidOperationException("capabilities.json is not embedded in Morph.Core.");
        return Load(json, isAvailable);
    }

    public static CapabilityMatrix Load(Stream json, Func<string, bool> isAvailable) =>
        new(JsonSerializer.Deserialize(json, CapabilityJson.Default.CapabilityFile)
            ?? throw new InvalidDataException("capabilities.json is empty."), isAvailable);

    /// <summary>Options for <paramref name="wheel"/>, in declaration order. No files (wheel warm-up) → every tool.</summary>
    public IReadOnlyList<WheelOption> OptionsFor(IReadOnlyList<FileFormat> files, WheelGroup wheel)
    {
        var options = new List<WheelOption>();
        foreach (var tool in _tools.Where(t => t.Wheel == wheel))
        {
            var target = tool.Id.StartsWith("convert.", StringComparison.Ordinal) ? tool.Id["convert.".Length..] : null;
            if (files.Count > 0 && (files.Count < tool.MinFiles || files.All(f => f.Id == target)))
            {
                continue;
            }

            var fits = true;
            var beta = false;
            Requirement? missing = null;
            foreach (var file in files.Where(f => f.Id != target))
            {
                if (!_rows.TryGetValue((file.Id, tool.Id), out var row))
                {
                    fits = false;
                    break;
                }

                beta |= row.Beta;
                missing ??= row.Requires?.Where(r => !_isAvailable(r)).Select(r => _requirements[r]).FirstOrDefault();
            }

            if (fits)
            {
                var choices = (tool.Choices ?? []).Where(c => c.Kinds is null || files.All(f => c.Kinds.Contains(f.Kind))).ToList();
                options.Add(new WheelOption(tool, missing?.Reason, missing?.Fix, beta, choices));
            }
        }

        return options;
    }

    public ToolDescriptor? Tool(string id) => _tools.FirstOrDefault(t => t.Id == id);

    /// <summary>The row that handles <paramref name="file"/> for <paramref name="toolId"/>; null if none does.</summary>
    public CapabilityRow? RowFor(FileFormat file, string toolId) => _rows.GetValueOrDefault((file.Id, toolId));
}

internal sealed record CapabilityFile(IReadOnlyList<ToolDescriptor> Tools, IReadOnlyDictionary<string, Requirement> Requirements,
    IReadOnlyList<CapabilityRow> Rows);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, ReadCommentHandling = JsonCommentHandling.Skip,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CapabilityFile))]
internal sealed partial class CapabilityJson : JsonSerializerContext;
