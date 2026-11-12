using System.Diagnostics;
using System.IO;
using System.Windows;

using Microsoft.Extensions.Logging;

using Morph.Core.Jobs;
using Morph.Core.Packs;
using Morph.Overlay.Theme;

namespace Morph.App.Packs;

/// <summary>
/// Asks before downloading a pack (PK-1, PK-2): download size, disk use, free space and the licence; then shows the
/// download. Cancel or closing stops it and keeps what was downloaded, so the next try resumes.
/// </summary>
public sealed partial class PackWindow : Window
{
    private readonly PackInfo _pack;
    private readonly PackStore _store;
    private readonly ILogger _log;
    private readonly TaskCompletionSource<string?> _result = new();
    private CancellationTokenSource? _download;

    private PackWindow(PackInfo pack, PackStore store, ILogger log)
    {
        _pack = pack;
        _store = store;
        _log = log;
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));

        Title = $"Download {pack.Name}";
        HeadingText.Text = $"{pack.Name} is a one-time download";
        LicenceLink.Inlines.Clear();
        LicenceLink.Inlines.Add($"{pack.Name} licence");
        LicenceLink.Click += (_, _) => Process.Start(new ProcessStartInfo(pack.LicenceUrl.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        CancelButton.Click += (_, _) => Close(); // IsCancel only clicks it in a modeless window
        DownloadButton.Click += (_, _) => DownloadAsync();
        Closed += (_, _) =>
        {
            _download?.Cancel();
            _result.TrySetResult(null);
        };
        Loaded += (_, _) => ShowSizesAsync();
    }

    /// <summary>The pack's folder, after asking and downloading if it isn't installed; null if the user cancelled.</summary>
    public static Task<string?> EnsureAsync(PackInfo pack, PackStore store, ILogger log)
    {
        if (store.Folder(pack) is { } folder)
        {
            return Task.FromResult<string?>(folder);
        }

        var window = new PackWindow(pack, store, log);
        window.Show();
        window.Activate();
        return window._result.Task;
    }

    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{Math.Max(1, bytes >> 20)} MB";

    // Disk checks run off the UI thread.
    private async void ShowSizesAsync()
    {
        var (downloaded, needed, free) = await Task.Run(() => (_store.Downloaded(_pack), _store.SpaceNeeded(_pack), _store.FreeSpace()));
        SizeText.Text = $"Downloads {Size(_pack.Size)}" + (downloaded > 0 ? $" ({Size(downloaded)} already here, it picks up from there)" : "") +
            $" and takes {Size(_pack.InstalledSize)} on disk. {Size(free)} free on {Path.GetPathRoot(_store.Root)}. " +
            "It can be removed later in Settings → Packs.";
        if (free < needed)
        {
            ShowStatus($"Not enough space: it needs {Size(needed)}. Free some space, then try again.");
            DownloadButton.IsEnabled = false;
        }
    }

    private async void DownloadAsync()
    {
        DownloadButton.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        Bar.Maximum = _pack.Size;
        ShowStatus("Starting…");
        _download = new();
        var progress = new Progress<long>(bytes =>
        {
            Bar.Value = bytes;
            StatusText.Text = bytes < _pack.Size ? $"{Size(bytes)} of {Size(_pack.Size)}" : "Checking and unpacking…";
        });
        _log.LogInformation("Pack {Pack} {Version}: download from {Url}", _pack.Id, _pack.Version, _pack.Url);
        try
        {
            var folder = await Task.Run(() => _store.InstallAsync(_pack, progress, _download.Token));
            _log.LogInformation("Pack {Pack} {Version} installed in {Folder}", _pack.Id, _pack.Version, folder);
            _result.TrySetResult(folder);
            Close();
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Pack {Pack}: download cancelled", _pack.Id);
        }
        catch (Exception ex) when (ex is EngineException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.LogWarning(ex, "Pack {Pack}: install failed ({Details})", _pack.Id, (ex as EngineException)?.Details);
            Bar.Visibility = Visibility.Collapsed;
            ShowStatus(ex is EngineException e ? e.UserMessage : $"{_pack.Name} couldn't be installed: {ex.Message}");
            DownloadButton.Content = "Try again";
            DownloadButton.IsEnabled = true;
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }
}
