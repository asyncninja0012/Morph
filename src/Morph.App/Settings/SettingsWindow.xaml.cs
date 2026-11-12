using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

using Morph.App.Packs;
using Morph.Core.Capabilities;
using Morph.Core.Packs;
using Morph.Core.Settings;
using Morph.Overlay.Theme;

namespace Morph.App.Settings;

/// <summary>What the settings window needs from the App.</summary>
/// <param name="Wedges">Every wedge per wheel, in the matrix's order.</param>
public sealed record SettingsHost(ToolSettings Settings, IReadOnlyList<(WheelGroup Group, string Id, string Label)> Wedges, PackStore Packs,
    string LogsFolder, ILogger Log);

/// <summary>
/// Settings (PRD 6.8): General (start at sign-in, where results go), Keys (which held key opens which wheel, IX-1), Wheel (pin,
/// reorder, hide, order by use, IX-7), Quality (CV-4), Packs (PK-3) and About. Every change is saved at once to settings.json
/// (tools["settings.*"]) and raises <see cref="PreferencesChanged"/>; the App re-reads them.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    public const string KeysId = "settings.keys";
    public const string OutputId = "settings.output";
    public const string QualityId = "settings.quality";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly string[] Keys = ["Shift", "Ctrl", "Alt"];

    private static readonly (string Id, string Name, string File, string What)[] PackInfos =
    [
        (OfficeApps.PackId, "Office pack (LibreOffice)", Path.Combine("program", "soffice.exe"), "Slides, spreadsheets, DOC, OpenDocument and Pages when Microsoft Office isn't installed."),
        ("bg-model", "Background pack", "model.onnx", "Remove BG: the cut-out model and the engine that runs it."),
    ];

    private readonly SettingsHost _host;
    private readonly Dictionary<string, DispatcherTimer> _pendingSaves = [];
    private Dictionary<WheelGroup, string> _keys = [];
    private WheelPrefs _wheel = WheelPrefs.Default;
    private Dictionary<string, int> _usage = [];
    private JsonObject _quality = [];
    private string? _folder;
    private readonly bool _loading = true; // false once the constructor has filled the controls

    public SettingsWindow(SettingsHost host)
    {
        _host = host;
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));

        LoadGeneral();
        LoadKeys();
        LoadWheel();
        LoadQuality();
        LoadAbout();
        Loaded += (_, _) => RefreshPacksAsync();
        _loading = false;
    }

    public event Action? PreferencesChanged;

    public event Action? PacksChanged;

    /// <summary>About → Show the tour again.</summary>
    public event Action? TourClicked;

    /// <summary>Which wheel each held key opens: Shift → Convert, Ctrl → Tools, Alt → Actions unless remapped.</summary>
    public static Dictionary<string, WheelGroup> KeyMap(JsonObject? json)
    {
        var map = new Dictionary<string, WheelGroup> { ["Shift"] = WheelGroup.Convert, ["Ctrl"] = WheelGroup.Tools, ["Alt"] = WheelGroup.Actions };
        if (json is not null && Keys.All(k => Enum.TryParse<WheelGroup>((string?)json[k], out _))
            && Keys.Select(k => (string?)json[k]).Distinct().Count() == 3)
        {
            foreach (var key in Keys)
            {
                map[key] = Enum.Parse<WheelGroup>((string)json[key]!);
            }
        }

        return map;
    }

    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{Math.Max(1, bytes >> 20)} MB";

    // Saves one settings.json entry; sliders wait until they've been still for a moment.
    private void Save(string id, JsonObject value, bool soon = false)
    {
        if (_loading)
        {
            return;
        }

        if (!soon)
        {
            SaveNowAsync(id, value);
            return;
        }

        if (!_pendingSaves.TryGetValue(id, out var timer))
        {
            _pendingSaves[id] = timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        }

        timer.Stop();
        timer.Tick -= OnTick;
        timer.Tick += OnTick;
        timer.Start();

        void OnTick(object? sender, EventArgs e)
        {
            timer.Stop();
            timer.Tick -= OnTick;
            SaveNowAsync(id, value);
        }
    }

    private async void SaveNowAsync(string id, JsonObject value)
    {
        try
        {
            await _host.Settings.SetAsync(id, value, CancellationToken.None);
            PreferencesChanged?.Invoke();
        }
        catch (IOException ex)
        {
            _host.Log.LogWarning(ex, "Settings: couldn't save {Id}", id);
        }
    }

    // General: start at sign-in is the HKCU Run key itself (no setting to drift); results folder.
    private void LoadGeneral()
    {
        StartupBox.IsEnabled = false;
        _ = Task.Run(() =>
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue("Morph") is string;
        }).ContinueWith(t =>
        {
            StartupBox.IsChecked = t.Result;
            StartupBox.IsEnabled = Environment.ProcessPath is not null;
        }, TaskScheduler.FromCurrentSynchronizationContext());
        StartupBox.Click += async (_, _) =>
        {
            var on = StartupBox.IsChecked == true;
            await Task.Run(() =>
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                if (on)
                {
                    run.SetValue("Morph", $"\"{Environment.ProcessPath}\"");
                }
                else
                {
                    run.DeleteValue("Morph", throwOnMissingValue: false);
                }
            });
            _host.Log.LogInformation("Settings: start at sign-in {State}", on ? "on" : "off");
        };

        _folder = (string?)_host.Settings.Get(OutputId)?["folder"];
        NextToOriginal.IsChecked = _folder is null;
        FixedFolder.IsChecked = _folder is not null;
        FolderText.Text = _folder ?? "";
        NextToOriginal.Checked += (_, _) => SetFolder(null);
        FixedFolder.Checked += (_, _) =>
        {
            if (_folder is null && !ChooseFolder())
            {
                NextToOriginal.IsChecked = true;
            }
        };
        ChooseFolderButton.Click += (_, _) => ChooseFolder();
    }

    private bool ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Where should Morph save results?" };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        SetFolder(dialog.FolderName);
        FixedFolder.IsChecked = true;
        return true;
    }

    private void SetFolder(string? folder)
    {
        _folder = folder;
        FolderText.Text = folder ?? "";
        Save(OutputId, new() { ["folder"] = folder });
    }

    private void LoadKeys()
    {
        _keys = KeyMap(_host.Settings.Get(KeysId)).ToDictionary(p => p.Value, p => p.Key);
        foreach (var (box, group) in new[] { (ConvertKey, WheelGroup.Convert), (ToolsKey, WheelGroup.Tools), (ActionsKey, WheelGroup.Actions) })
        {
            box.ItemsSource = Keys;
            box.SelectedItem = _keys[group];
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is not string key || _keys[group] == key)
                {
                    return;
                }

                // The wheel that had this key takes this wheel's old one.
                var other = _keys.First(p => p.Value == key).Key;
                _keys[other] = _keys[group];
                _keys[group] = key;
                ConvertKey.SelectedItem = _keys[WheelGroup.Convert];
                ToolsKey.SelectedItem = _keys[WheelGroup.Tools];
                ActionsKey.SelectedItem = _keys[WheelGroup.Actions];
                Save(KeysId, new JsonObject(_keys.Select(p => KeyValuePair.Create(p.Value, (JsonNode?)p.Key.ToString()))));
            };
        }
    }

    private void LoadWheel()
    {
        _wheel = WheelPrefs.From(_host.Settings.Get(WheelPrefs.SettingsId));
        _usage = WheelPrefs.Usage(_host.Settings.Get(WheelPrefs.UsageId));
        WheelGroupBox.ItemsSource = new[] { "Convert wheel", "Tools wheel", "Actions wheel" };
        WheelGroupBox.SelectedIndex = 0;
        WheelGroupBox.SelectionChanged += (_, _) => ShowWedges(null);
        LearnBox.IsChecked = _wheel.Learn;
        LearnBox.Click += (_, _) => SaveWheel(_wheel with { Learn = LearnBox.IsChecked == true }, Selected());
        ForgetButton.Click += (_, _) =>
        {
            _usage = [];
            Save(WheelPrefs.UsageId, []);
            ShowWedges(Selected());
        };
        WedgeList.SelectionChanged += (_, _) => UpdateWedgeButtons();
        PinButton.Click += (_, _) =>
        {
            if (Selected() is { } id)
            {
                SaveWheel(_wheel.Pinned.Contains(id)
                    ? _wheel with { Pinned = [.. _wheel.Pinned.Where(p => p != id)] }
                    : _wheel with { Pinned = [.. _wheel.Pinned, id], Hidden = [.. _wheel.Hidden.Where(h => h != id)] }, id);
            }
        };
        HideButton.Click += (_, _) =>
        {
            if (Selected() is { } id)
            {
                SaveWheel(_wheel.Hidden.Contains(id)
                    ? _wheel with { Hidden = [.. _wheel.Hidden.Where(h => h != id)] }
                    : _wheel with { Hidden = [.. _wheel.Hidden, id], Pinned = [.. _wheel.Pinned.Where(p => p != id)] }, id);
            }
        };
        UpButton.Click += (_, _) => MovePinned(-1);
        DownButton.Click += (_, _) => MovePinned(1);
        ShowWedges(null);
    }

    private WheelGroup CurrentGroup => (WheelGroup)Math.Max(0, WheelGroupBox.SelectedIndex);

    private string? Selected() => (WedgeList.SelectedItem as ListBoxItem)?.Tag as string;

    private void MovePinned(int by)
    {
        if (Selected() is not { } id || _wheel.Pinned.ToList() is var pinned && pinned.IndexOf(id) is var at && at < 0)
        {
            return;
        }

        var to = at + by;
        var group = _host.Wedges.Where(w => w.Group == CurrentGroup).Select(w => w.Id).ToHashSet();
        while (to >= 0 && to < pinned.Count && !group.Contains(pinned[to]))
        {
            to += by; // pins of other wheels share the list; skip them
        }

        if (to < 0 || to >= pinned.Count)
        {
            return;
        }

        (pinned[at], pinned[to]) = (pinned[to], pinned[at]);
        SaveWheel(_wheel with { Pinned = pinned }, id);
    }

    private void SaveWheel(WheelPrefs prefs, string? select)
    {
        _wheel = prefs;
        Save(WheelPrefs.SettingsId, prefs.ToJson());
        ShowWedges(select);
    }

    // The wheel's order as it will open, then the hidden wedges (so they can be shown again).
    private void ShowWedges(string? select)
    {
        var all = _host.Wedges.Where(w => w.Group == CurrentGroup).ToList();
        var ordered = _wheel.Apply(all, w => w.Id, _usage).Concat(all.Where(w => _wheel.Hidden.Contains(w.Id)));
        WedgeList.Items.Clear();
        foreach (var (_, id, label) in ordered)
        {
            var notes = new List<string>();
            if (_wheel.Pinned.Contains(id))
            {
                notes.Add("pinned");
            }

            if (_wheel.Hidden.Contains(id))
            {
                notes.Add("hidden");
            }

            if (_usage.GetValueOrDefault(id) is var n and > 0)
            {
                notes.Add(n == 1 ? "used once" : $"used {n} times");
            }

            var item = new ListBoxItem { Content = notes.Count > 0 ? $"{label}  ·  {string.Join(", ", notes)}" : label, Tag = id };
            System.Windows.Automation.AutomationProperties.SetName(item, item.Content.ToString());
            WedgeList.Items.Add(item);
            if (id == select)
            {
                WedgeList.SelectedItem = item;
            }
        }

        UpdateWedgeButtons();
    }

    private void UpdateWedgeButtons()
    {
        var id = Selected();
        var pinned = id is not null && _wheel.Pinned.Contains(id);
        PinButton.IsEnabled = HideButton.IsEnabled = id is not null;
        PinButton.Content = pinned ? "Unpin" : "Pin to front";
        HideButton.Content = id is not null && _wheel.Hidden.Contains(id) ? "Show" : "Hide";
        UpButton.IsEnabled = DownButton.IsEnabled = pinned;
    }

    // Quality: one slider per picture format, one choice for video and one for sound; each saves an override of the tool's defaults.
    private void LoadQuality()
    {
        _quality = _host.Settings.Get(QualityId) ?? [];
        foreach (var (tool, label, fallback) in new[] { ("convert.jpg", "JPG", 90), ("convert.webp", "WebP", 75), ("convert.avif", "AVIF", 80), ("convert.heic", "HEIC", 90) })
        {
            var value = (int?)_quality[tool]?["quality"] ?? fallback;
            var text = new TextBlock { Text = $"{label} quality: {value}", Margin = new Thickness(0, 8, 0, 4) };
            var slider = new Slider { Minimum = 40, Maximum = 100, Value = value, TickFrequency = 5, IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 10 };
            System.Windows.Automation.AutomationProperties.SetName(slider, $"{label} quality");
            slider.ValueChanged += (_, e) =>
            {
                text.Text = $"{label} quality: {(int)e.NewValue}";
                SetQuality([tool], "quality", (int)e.NewValue, soon: true);
            };
            QualityPanel.Children.Add(text);
            QualityPanel.Children.Add(slider);
        }

        AddChoice("Video (MP4, MKV, WebM)", ["convert.mp4", "convert.mkv", "convert.webm"], "quality",
            [("Smaller files", 28), ("Balanced", 23), ("Better looking", 18)], 23);
        AddChoice("Sound (MP3, M4A)", ["convert.mp3", "convert.m4a"], "bitrate",
            [("128 kbps", 128), ("192 kbps", 192), ("256 kbps", 256), ("320 kbps", 320)], 192);
    }

    private void AddChoice(string label, string[] tools, string key, (string Text, int Value)[] options, int fallback)
    {
        var title = new TextBlock { Text = label, Margin = new Thickness(0, 16, 0, 4) };
        var box = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = options.Select(o => o.Text).ToList() };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        var current = (int?)_quality[tools[0]]?[key] ?? fallback;
        box.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value == current));
        box.SelectionChanged += (_, _) => SetQuality(tools, key, options[box.SelectedIndex].Value);
        QualityPanel.Children.Add(title);
        QualityPanel.Children.Add(box);
    }

    private void SetQuality(string[] tools, string key, int value, bool soon = false)
    {
        foreach (var tool in tools)
        {
            if (_quality[tool] is not JsonObject entry)
            {
                _quality[tool] = entry = [];
            }

            entry[key] = value;
        }

        Save(QualityId, (JsonObject)_quality.DeepClone(), soon);
    }

    private async void RefreshPacksAsync()
    {
        var rows = await Task.Run(() => PackInfos.Select(p => (Info: p,
            Installed: PackStore.Installed(_host.Packs.Root, p.Id, p.File) is not null,
            Size: _host.Packs.SizeOnDisk(p.Id),
            Published: PackStore.Known.FirstOrDefault(k => k.Id == p.Id))).ToList());
        PackList.Children.Clear();
        foreach (var (info, installed, size, published) in rows)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = info.Name, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = info.What, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
            text.Children.Add(new TextBlock
            {
                Text = installed ? $"Installed, {Size(size)} on disk" : published is null ? "Not available yet" : $"Not installed ({Size(published.Size)} download)",
                Opacity = 0.8,
            });
            row.Children.Add(text);
            if (installed || published is not null)
            {
                var button = new Button { Content = installed ? "Remove" : "Download", MinWidth = 96, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
                System.Windows.Automation.AutomationProperties.SetName(button, $"{button.Content} {info.Name}");
                Grid.SetColumn(button, 1);
                button.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    if (installed)
                    {
                        await RemovePackAsync(info.Id, info.Name);
                    }
                    else if (await PackWindow.EnsureAsync(published!, _host.Packs, _host.Log) is not null)
                    {
                        PacksChanged?.Invoke();
                    }

                    RefreshPacksAsync();
                };
                row.Children.Add(button);
            }

            PackList.Children.Add(row);
        }
    }

    private async Task RemovePackAsync(string id, string name)
    {
        try
        {
            await Task.Run(() => _host.Packs.Remove(id));
            PackStatus.Text = $"{name} removed.";
            _host.Log.LogInformation("Pack {Pack} removed", id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PackStatus.Text = $"{name} is in use. Try again when Morph has finished what it's doing.";
            _host.Log.LogWarning(ex, "Pack {Pack}: remove failed", id);
        }

        PacksChanged?.Invoke();
    }

    private void LoadAbout()
    {
        VersionText.Text = $"Morph {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}";
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        LicencesButton.Click += (_, _) => Process.Start(new ProcessStartInfo(notices) { UseShellExecute = true })?.Dispose();
        LicencesButton.IsEnabled = File.Exists(notices);
        TourButton.Click += (_, _) => TourClicked?.Invoke();
        LogsButton.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.LogsFolder}\"") { UseShellExecute = false })?.Dispose();
    }
}
