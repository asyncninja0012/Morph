using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

using Morph.Overlay.Theme;

namespace Morph.App.Tools;

/// <summary>
/// Resize settings (IT-2), opened by a drop on the Resize wedge: a preset or an exact size in pixels.
/// Raises <see cref="RunClicked"/> with the job parameters; nothing runs if the window is closed.
/// </summary>
public sealed partial class ResizeWindow : Window
{
    private const int MaxEdge = 30_000; // Magick's limit in the Worker

    private static readonly Dictionary<string, (string Hint, JsonObject Parameters)> Choices = new()
    {
        ["half"] = ("Half the width and the height. Never enlarges.", new() { ["scale"] = 0.5 }),
        ["1080p"] = ("Fits inside 1920 × 1080, either way round. Never enlarges.", new() { ["long"] = 1920, ["short"] = 1080 }),
        ["720p"] = ("Fits inside 1280 × 720, either way round. Never enlarges.", new() { ["long"] = 1280, ["short"] = 720 }),
        ["2048"] = ("The longest side becomes 2048 px. Never enlarges.", new() { ["long"] = 2048 }),
        ["custom"] = ("An exact size, which may enlarge. With the aspect ratio kept, the picture fits inside it; leave a box empty to set one side only.", []),
    };

    public ResizeWindow(IReadOnlyList<string> files)
    {
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));

        FilesText.Text = files is [var one] ? Path.GetFileName(one) : $"{files.Count} files";
        FilesText.ToolTip = string.Join(Environment.NewLine, files);
        foreach (var size in Sizes.Children.OfType<RadioButton>())
        {
            size.Checked += (_, _) =>
            {
                SizeHint.Text = Choices[(string)size.Tag].Hint;
                CustomPanel.Visibility = size == CustomButton ? Visibility.Visible : Visibility.Collapsed;
                Validate();
            };
        }

        SizeHint.Text = Choices["half"].Hint;
        WidthBox.TextChanged += (_, _) => Validate();
        HeightBox.TextChanged += (_, _) => Validate();
        KeepRatio.Click += (_, _) => Validate();
        RunButton.Click += (_, _) =>
        {
            RunClicked?.Invoke(Parameters());
            Close();
        };
    }

    public event Action<JsonObject>? RunClicked;

    private JsonObject Parameters()
    {
        if (CustomButton.IsChecked != true)
        {
            var chosen = Sizes.Children.OfType<RadioButton>().First(r => r.IsChecked == true);
            return Choices[(string)chosen.Tag].Parameters.DeepClone().AsObject();
        }

        var parameters = new JsonObject();
        if (Pixels(WidthBox) is { } width)
        {
            parameters["width"] = width;
        }

        if (Pixels(HeightBox) is { } height)
        {
            parameters["height"] = height;
        }

        if (KeepRatio.IsChecked != true)
        {
            parameters["stretch"] = true;
        }

        return parameters;
    }

    // Custom needs at least one side; a side that's typed must be a whole number of pixels.
    private void Validate() => RunButton.IsEnabled = CustomButton.IsChecked != true
        || ((Pixels(WidthBox) is not null || Pixels(HeightBox) is not null)
            && (WidthBox.Text.Trim().Length == 0 || Pixels(WidthBox) is not null)
            && (HeightBox.Text.Trim().Length == 0 || Pixels(HeightBox) is not null)
            && (KeepRatio.IsChecked == true || (Pixels(WidthBox) is not null && Pixels(HeightBox) is not null)));

    private static int? Pixels(TextBox box) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var px) && px is > 0 and <= MaxEdge ? px : null;
}
