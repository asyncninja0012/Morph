using System.IO;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Win32;

using Morph.Overlay.Theme;

namespace Morph.App.Actions;

/// <summary>
/// Copy to… / Move to… (FA-2): the recent folders first, one click each, then "Choose a folder…" (the Windows folder picker).
/// Raises <see cref="Chosen"/> with the folder; nothing happens if the window is closed.
/// </summary>
public sealed partial class FolderWindow : Window
{
    /// <param name="recent">Recent folders that still exist, newest first.</param>
    public FolderWindow(IReadOnlyList<string> files, bool move, IReadOnlyList<string> recent)
    {
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));

        Title = move ? "Move to" : "Copy to";
        FilesText.Text = files is [var one] ? Path.GetFileName(one) : $"{files.Count} items";
        FilesText.ToolTip = string.Join(Environment.NewLine, files);
        RecentLabel.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var folder in recent)
        {
            var name = new TextBlock { Text = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : folder, FontWeight = FontWeights.SemiBold };
            var path = new TextBlock { Text = folder, TextTrimming = TextTrimming.CharacterEllipsis };
            path.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var button = new Button
            {
                Content = new StackPanel { Children = { name, path } },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
                ToolTip = folder,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{Title} {folder}");
            button.Click += (_, _) => Choose(folder);
            Recent.Children.Add(button);
        }

        ChooseButton.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = Title, InitialDirectory = recent.Count > 0 ? recent[0] : Path.GetDirectoryName(files[0]) ?? "" };
            if (picker.ShowDialog(this) == true)
            {
                Choose(picker.FolderName);
            }
        };
        CancelButton.Click += (_, _) => Close(); // IsCancel closes only a ShowDialog window
        Loaded += (_, _) => (Recent.Children.Count > 0 ? (UIElement)Recent.Children[0] : ChooseButton).Focus();
    }

    public event Action<string>? Chosen;

    private void Choose(string folder)
    {
        Chosen?.Invoke(folder);
        Close();
    }
}
