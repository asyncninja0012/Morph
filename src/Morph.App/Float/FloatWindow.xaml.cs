using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Morph.Overlay.Theme;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Morph.App.Float;

/// <summary>One parked item; <see cref="Folder"/> and <see cref="Missing"/> are checked off the UI thread.</summary>
public sealed record FloatItem(string Path, bool Folder, bool Missing);

/// <summary>
/// The Float shelf (FA-1), view only: <see cref="FloatShelf"/> owns the items. Never takes the focus (WS_EX_NOACTIVATE, like the
/// toasts), so parking or dragging out doesn't pull the user out of the app they're in. Items are dragged out as copies or
/// links, never moves: the shelf only points at the user's files.
/// </summary>
public sealed partial class FloatWindow : Window
{
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private Point? _press;

    public FloatWindow()
    {
        InitializeComponent();
        var hwnd = new HWND(new WindowInteropHelper(this).EnsureHandle());
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
            | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, MorphGlass.TryApplyBlur(this, dark, round: true));

        var area = SystemParameters.WorkArea;
        Scroller.MaxHeight = area.Height * 0.6;
        Left = area.Right - Width - 12;
        Top = area.Top + area.Height * 0.2;

        ClearButton.Click += (_, _) => ClearClicked?.Invoke();
        HideButton.Click += (_, _) => HideClicked?.Invoke();
        Header.MouseLeftButtonDown += (_, _) => DragMove(); // the buttons handle their own presses

        DragEnter += (_, e) => Highlight(e, on: true);
        DragOver += (_, e) => Highlight(e, on: true);
        DragLeave += (_, e) => Highlight(e, on: false);
        Drop += (_, e) =>
        {
            Highlight(e, on: false);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            {
                Dropped?.Invoke(paths);
            }
        };
    }

    /// <summary>Files or folders dropped onto the shelf.</summary>
    public event Action<IReadOnlyList<string>>? Dropped;

    public event Action<string>? RemoveClicked;

    public event Action? ClearClicked;

    public event Action? HideClicked;

    /// <summary>Shows the items in order; missing ones (moved or deleted since) are dimmed and can't be dragged.</summary>
    public void Render(IReadOnlyList<FloatItem> items)
    {
        Items.Children.Clear();
        foreach (var item in items)
        {
            Items.Children.Add(Row(item.Path, item.Folder, item.Missing));
        }

        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Only a drag carrying files lights the shelf up; anything else (text, a browser image) is refused.
    private void Highlight(DragEventArgs e, bool on)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        // Never Move: the source would delete its file, and the shelf only keeps the path.
        e.Effects = !files ? DragDropEffects.None
            : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
            : e.AllowedEffects & DragDropEffects.Link;
        if (on && files)
        {
            Frame.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        }
        else
        {
            Frame.BorderBrush = Brushes.Transparent;
        }

        e.Handled = true;
    }

    private Border Row(string path, bool folder, bool missing)
    {
        var name = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : path;
        var icon = new Image { Source = TypeIcon(path, folder), Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) };
        var text = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, missing ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        if (missing)
        {
            text.Text = $"{name} (missing)";
        }

        var remove = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "Remove from the shelf (the file stays where it is)",
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove {name} from the shelf");
        remove.Click += (_, _) => RemoveClicked?.Invoke(path);

        var grid = new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new(), new() { Width = GridLength.Auto } } };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 4, 2, 4),
            Margin = new Thickness(0, 0, 4, 2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Opacity = missing ? 0.6 : 1,
            ToolTip = missing ? $"{path}\nMoved or deleted since it was parked." : $"{path}\nDrag out to copy it anywhere. Double-click to show it in its folder.",
        };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "MorphCardBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        if (missing)
        {
            return row;
        }

        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                Toasts.ToastHost.ShowInFolder([path]);
                return;
            }

            _press = e.GetPosition(this);
        };
        row.MouseLeftButtonUp += (_, _) => _press = null;
        row.MouseMove += (_, e) =>
        {
            if (_press is not { } start || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var moved = e.GetPosition(this) - start;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _press = null;
            DragDrop.DoDragDrop(row, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Link);
        };
        return row;
    }

    // Windows' own icon for the file type, looked up by name only (SHGFI_USEFILEATTRIBUTES): the file itself is never opened.
    private static unsafe ImageSource? TypeIcon(string path, bool folder)
    {
        var key = folder ? "\\folder" : Path.GetExtension(path);
        if (Icons.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var info = default(SHFILEINFOW);
        var attributes = folder ? FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL;
        ImageSource? image = null;
        fixed (char* name = folder ? "folder" : "file" + key)
        {
            if (PInvoke.SHGetFileInfo(name, attributes, &info, (uint)sizeof(SHFILEINFOW), SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_SMALLICON | SHGFI_FLAGS.SHGFI_USEFILEATTRIBUTES) != 0
                && !info.hIcon.IsNull)
            {
                try
                {
                    image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                }
                finally
                {
                    PInvoke.DestroyIcon(info.hIcon);
                }
            }
        }

        Icons[key] = image;
        return image;
    }
}
