using System.IO;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Morph.Core.Actions;
using Morph.Core.Settings;

namespace Morph.App.Float;

/// <summary>
/// The Float shelf's items (FA-1): paths only, never copies, kept in settings.json under <c>action.float</c> with whether the
/// shelf was open, so both survive a restart. Items leave only when the user removes them. Call on the UI thread.
/// </summary>
internal sealed class FloatShelf(Task<ToolSettings> settings, ILogger log)
{
    private const string Key = "action.float"; // { "items": [ ... ], "open": true }

    private List<string> _items = [];
    private bool _open;
    private FloatWindow? _window;
    private int _renders;

    /// <summary>Reads the saved shelf and opens it if it was open when Morph last quit.</summary>
    public async Task LoadAsync()
    {
        var saved = (await settings).Get(Key);
        _items = [.. (saved?["items"] as JsonArray ?? []).Select(n => (string?)n).OfType<string>()];
        if (saved?["open"] is JsonValue open && open.TryGetValue<bool>(out var wasOpen) && wasOpen)
        {
            Show();
        }
    }

    /// <summary>Parks <paramref name="paths"/> and shows the shelf.</summary>
    public void Add(IReadOnlyList<string> paths)
    {
        _items = [.. FileActions.AddToFloat(_items, paths)];
        log.LogInformation("Float: parked {Paths}", string.Join(", ", paths));
        Show();
    }

    /// <summary>The tray's Float shelf item: shows it, or hides it if it's showing.</summary>
    public void Toggle()
    {
        if (_window?.IsVisible == true)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    private void Show()
    {
        if (_window is null)
        {
            _window = new FloatWindow();
            _window.Dropped += Add;
            _window.RemoveClicked += path =>
            {
                _items.Remove(path);
                Changed();
            };
            _window.ClearClicked += () =>
            {
                _items.Clear();
                Changed();
            };
            _window.HideClicked += Hide;
        }

        _open = true;
        Changed();
        _window.Show();
    }

    private void Hide()
    {
        _window?.Hide();
        _open = false;
        Save();
    }

    private void Changed()
    {
        Save();
        RenderAsync();
    }

    // Which items still exist (and which are folders) is file I/O, so it's checked off the UI thread; only the latest render shows.
    private async void RenderAsync()
    {
        var render = ++_renders;
        var items = _items.ToList();
        var shown = await Task.Run(() => items.Select(p => new FloatItem(p, Directory.Exists(p), !File.Exists(p) && !Directory.Exists(p))).ToList());
        if (render == _renders)
        {
            _window?.Render(shown);
        }
    }

    private async void Save()
    {
        var value = new JsonObject { ["items"] = new JsonArray([.. _items.Select(p => JsonValue.Create(p))]), ["open"] = _open };
        try
        {
            await (await settings).SetAsync(Key, value, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Couldn't save the Float shelf");
        }
    }
}
