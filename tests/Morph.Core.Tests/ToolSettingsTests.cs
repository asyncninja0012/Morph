using System.Text.Json.Nodes;

using Morph.Core.Settings;

namespace Morph.Core.Tests;

public sealed class ToolSettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("morph-settings-").FullName;

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task A_tools_settings_survive_a_restart_and_other_keys_are_kept()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "theme": "dark" }""", TestContext.Current.CancellationToken);
        var (settings, error) = await ToolSettings.LoadAsync(SettingsPath, TestContext.Current.CancellationToken);
        Assert.Null(error);
        Assert.Null(settings.Get("crop"));

        await settings.SetAsync("crop", new() { ["aspect"] = "16:9" }, TestContext.Current.CancellationToken);
        var (reloaded, _) = await ToolSettings.LoadAsync(SettingsPath, TestContext.Current.CancellationToken);

        Assert.Equal("16:9", (string?)reloaded.Get("crop")?["aspect"]);
        Assert.Equal("dark", (string?)JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath, TestContext.Current.CancellationToken))!["theme"]);
    }

    [Fact]
    public async Task Get_returns_a_copy()
    {
        var (settings, _) = await ToolSettings.LoadAsync(SettingsPath, TestContext.Current.CancellationToken);
        await settings.SetAsync("crop", new() { ["aspect"] = "1:1" }, TestContext.Current.CancellationToken);

        settings.Get("crop")!["aspect"] = "changed";

        Assert.Equal("1:1", (string?)settings.Get("crop")!["aspect"]);
    }

    [Fact]
    public async Task A_damaged_file_is_set_aside_and_settings_start_empty()
    {
        await File.WriteAllTextAsync(SettingsPath, "{ not json", TestContext.Current.CancellationToken);

        var (settings, error) = await ToolSettings.LoadAsync(SettingsPath, TestContext.Current.CancellationToken);

        Assert.NotNull(error);
        Assert.Null(settings.Get("crop"));
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(Path.Combine(_folder, "settings.bad.json"), TestContext.Current.CancellationToken));
    }
}
