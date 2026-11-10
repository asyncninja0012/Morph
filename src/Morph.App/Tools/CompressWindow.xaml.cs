using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

using Morph.Core.Formats;
using Morph.Overlay.Theme;

namespace Morph.App.Tools;

/// <summary>
/// Compress settings (IT-1), opened by a drop on the Compress wedge itself; the presets on the wheel's ring skip it.
/// Raises <see cref="RunClicked"/> with the job parameters; nothing runs if the window is closed.
/// </summary>
public sealed partial class CompressWindow : Window
{
    private static readonly Dictionary<string, string> Hints = new()
    {
        ["small"] = "Smallest files. Photos up to 1600 px, video up to 720p; the loss in quality shows.",
        ["balanced"] = "Much smaller with little visible loss. Photos up to 2560 px, video up to 1080p.",
        ["high"] = "Full size with light compression; hard to tell from the original.",
    };

    public CompressWindow(IReadOnlyList<string> files)
    {
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));

        FilesText.Text = files is [var one] ? Path.GetFileName(one) : $"{files.Count} files";
        FilesText.ToolTip = string.Join(Environment.NewLine, files);
        _ = ShowSizeAsync(files);
        // Picture size means nothing to audio or PDF.
        PictureRow.Visibility = files.Any(f => FileFormat.FromExtension(Path.GetExtension(f)).Kind is FileKind.Image or FileKind.Video)
            ? Visibility.Visible : Visibility.Collapsed;

        foreach (var preset in Presets.Children.OfType<RadioButton>())
        {
            preset.Checked += (_, _) => PresetHint.Text = Hints[(string)preset.Tag];
        }

        PresetHint.Text = Hints["balanced"];
        TargetCheck.Click += (_, _) => Validate();
        TargetBox.TextChanged += (_, _) => Validate();
        TargetUnit.SelectionChanged += (_, _) => Validate();
        RunButton.Click += (_, _) =>
        {
            RunClicked?.Invoke(Parameters());
            Close();
        };
    }

    public event Action<JsonObject>? RunClicked;

    private JsonObject Parameters()
    {
        var preset = Presets.Children.OfType<RadioButton>().First(r => r.IsChecked == true);
        var parameters = new JsonObject { ["preset"] = (string)preset.Tag };
        switch ((PictureSize.SelectedItem as ComboBoxItem)?.Tag as string)
        {
            case "keep":
                parameters["size"] = "keep";
                break;
            case { } edge:
                var longEdge = int.Parse(edge, CultureInfo.InvariantCulture);
                parameters["long"] = longEdge;
                parameters["short"] = longEdge == 2560 ? longEdge : longEdge * 9 / 16;
                break;
        }

        if (TargetCheck.IsChecked == true && TargetMb() is { } mb)
        {
            parameters["targetMb"] = mb;
        }

        return parameters;
    }

    // The engines take decimal megabytes; 1 KB is the least that makes sense, 100 GB the most.
    private double? TargetMb()
    {
        var perUnit = double.Parse((string)((ComboBoxItem)TargetUnit.SelectedItem).Tag, CultureInfo.InvariantCulture);
        return double.TryParse(TargetBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size) && size * perUnit is var mb and >= 0.001 and <= 100_000
            ? mb : null;
    }

    private void Validate() => RunButton.IsEnabled = TargetCheck.IsChecked != true || TargetMb() is not null;

    // Off the UI thread: the files may sit on a slow network share.
    private async Task ShowSizeAsync(IReadOnlyList<string> files)
    {
        var bytes = await Task.Run(() => files.Sum(f =>
        {
            try
            {
                return new FileInfo(f).Length;
            }
            catch (IOException)
            {
                return 0L;
            }
        }));
        SizeText.Text = $"Original size: {Megabytes(bytes)}";
    }

    private static string Megabytes(long bytes) => bytes >= 1_000_000
        ? $"{bytes / 1_000_000.0:0.#} MB"
        : $"{Math.Max(1, bytes / 1000)} KB";
}
