using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Morph.Core.Formats;
using Morph.Overlay.Theme;

namespace Morph.App.Merge;

/// <summary>One row of the merge list.</summary>
public sealed record MergeRow(int Index, int Count, string Path)
{
    public int Number => Index + 1;

    public string Name => System.IO.Path.GetFileName(Path);

    public string Glyph => FileFormat.FromExtension(System.IO.Path.GetExtension(Path)).Kind switch
    {
        FileKind.Image => "",
        FileKind.Video => "",
        FileKind.Audio => "",
        _ => "",
    };

    public bool CanMoveUp => Index > 0;

    public bool CanMoveDown => Index < Count - 1;

    public string UpLabel => $"Move {Name} up";

    public string DownLabel => $"Move {Name} down";

    public override string ToString() => $"{Number}. {Name}"; // what screen readers announce for the row
}

/// <summary>Lets the user set the order before a merge (IT-3); starts in drop order. Raises <see cref="MergeClicked"/>.</summary>
public sealed partial class MergeOrderWindow : Window
{
    private readonly List<string> _files;

    public MergeOrderWindow(IReadOnlyList<string> files)
    {
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));
        _files = [.. files];
        Render();
        MergeButton.Click += (_, _) =>
        {
            MergeClicked?.Invoke(_files);
            Close();
        };
    }

    /// <summary>The files in the chosen order.</summary>
    public event Action<IReadOnlyList<string>>? MergeClicked;

    private void Render() => FileList.ItemsSource = _files.Select((f, i) => new MergeRow(i, _files.Count, f)).ToList();

    private void OnRowButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: string direction, DataContext: MergeRow row })
        {
            return;
        }

        var to = row.Index + (direction == "up" ? -1 : 1);
        (_files[row.Index], _files[to]) = (_files[to], _files[row.Index]);
        Render();

        // Keep keyboard focus on the same button of the moved row, so repeated Space presses keep moving it.
        Dispatcher.InvokeAsync(() =>
        {
            var target = FileList.ItemContainerGenerator.ContainerFromIndex(to) is DependencyObject container
                ? FindButton(container, direction) : null;
            if (target is { IsEnabled: true })
            {
                target.Focus();
            }
            else
            {
                MergeButton.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private static Button? FindButton(DependencyObject parent, string tag)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button { Tag: string t } button && t == tag)
            {
                return button;
            }

            if (FindButton(child, tag) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
