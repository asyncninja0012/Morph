using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

using Morph.Core.Jobs;
using Morph.Overlay;

namespace Morph.Editors.Pages;

/// <summary>
/// The PDF page organizer (ED-5): page thumbnails (from the Worker, <see cref="PageSheet"/> batches) to reorder by dragging, turn,
/// delete, keep only some of (extract a range), and split into several files. Mouse: click picks a page, Ctrl/Shift+click several,
/// drag moves them. Keyboard: arrows move (Shift: extend the selection, Ctrl+←/→: move the selected pages), Space toggles one,
/// Ctrl+A picks all, R/L turn, Delete removes, K keeps only them, S splits after. Returns <see cref="PageOrder"/> parameters.
/// </summary>
public sealed partial class PageEditor : UserControl, IEditor
{
    private const double TileWidth = 128;
    private const double TileHeight = 152;
    private const double Gap = 4; // margin around each tile

    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x8C, 0x28)));
    private static readonly Brush Picked = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xF2, 0x8C, 0x28)));
    private static readonly Brush Focused = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0x80, 0x80, 0x80)));

    private readonly PageSheetLoader _load;
    private readonly Stack<IReadOnlyList<PagePick>> _undo = new();
    private readonly Dictionary<int, ImageSource> _thumbs = [];
    private readonly HashSet<int> _selected = [];
    private IReadOnlyList<PagePick> _pages = [];
    private CancellationTokenSource? _loading;
    private int _count; // pages in the original
    private int _focus;
    private int _anchor;
    private Point? _pressAt;
    private int _pressed = -1; // a click on a selected page: select only it, unless it turns into a drag
    private bool _dragging;
    private int _dropAt = -1;

    public PageEditor(PageSheetLoader load)
    {
        InitializeComponent();
        _load = load;
        DropMark.Fill = Accent;
        TurnLeftButton.Click += (_, _) => Apply(PageMath.Turn(_pages, Targets(), -90));
        TurnRightButton.Click += (_, _) => Apply(PageMath.Turn(_pages, Targets(), 90));
        DeleteButton.Click += (_, _) => Delete();
        KeepButton.Click += (_, _) => Keep();
        SplitButton.Click += (_, _) => Apply(PageMath.ToggleSplit(_pages, Targets().Max()));
        Tiles.MouseLeftButtonDown += OnDown;
        Tiles.MouseMove += OnMove;
        Tiles.MouseLeftButtonUp += (_, _) => EndPress();
        Tiles.LostMouseCapture += (_, _) => EndPress();
        PreviewKeyDown += OnKey; // before the ScrollViewer, which takes the arrow keys for scrolling
        Unloaded += (_, _) => _loading?.Cancel();
        GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus == this)
            {
                Tiles.Focus(); // the shell focuses the editor; the pages are what the keys work on
            }
        };
    }

    public string Title => "Pages";

    public FrameworkElement View => this;

    public bool CanUndo => _undo.Count > 0;

    public event EventHandler? Changed;

    public async Task LoadAsync(string path, JsonObject? settings, CancellationToken ct)
    {
        _loading?.Cancel();
        var loading = _loading = CancellationTokenSource.CreateLinkedTokenSource(ct);
        (_focus, _anchor) = (0, 0);
        _undo.Clear();
        _thumbs.Clear();
        _selected.Clear();
        var first = await _load(path, 1, ct);
        _count = first.Pages;
        Add(first, 1);
        _pages = PageMath.Original(_count);

        // As many columns as fit 80% of the work area (3 to 5, so the window stays narrow), as many rows as fit what's left of its height.
        var work = Screens.WorkAreaAtCursor();
        var tile = (Width: TileWidth + 2 * Gap, Height: TileHeight + 2 * Gap);
        var columns = (int)Math.Clamp(Math.Floor((work.Width * 0.8 - 80) / tile.Width), 3, 5);
        var rows = Math.Ceiling((double)_count / columns);
        Scroller.Width = columns * tile.Width + 20;
        Scroller.Height = Math.Min(rows * tile.Height + 4, Math.Max(tile.Height + 4, work.Height * 0.8 - 250));
        Summary.MaxWidth = Hint.MaxWidth = Scroller.Width;
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
        _ = LoadRestAsync(path, loading.Token);
    }

    public void Undo()
    {
        _pages = _undo.Pop();
        _selected.Clear();
        _focus = Math.Min(_focus, _pages.Count - 1);
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public JsonObject Parameters() => PageOrder.ToParameters(_pages);

    public JsonObject Settings() => [];

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // Thumbnails past the first batch, while the editor is already usable.
    private async Task LoadRestAsync(string path, CancellationToken ct)
    {
        try
        {
            for (var first = 1 + PageSheet.Batch; first <= _count; first += PageSheet.Batch)
            {
                var sheet = await _load(path, first, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                Add(sheet, first);
                Render();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (EngineException)
        {
            Hint.Text = "Some page pictures couldn't be made. Their pages still move, turn and delete.";
        }
    }

    // Cuts a sheet into one picture per page.
    private void Add(PreviewImage sheet, int first)
    {
        var bitmap = BitmapSource.Create(sheet.Width, sheet.Height, 96, 96, PixelFormats.Bgra32, null, sheet.Bgra, sheet.Width * 4);
        bitmap.Freeze();
        var k = sheet.Width / (double)(PageSheet.Columns * PageSheet.Cell); // 1 unless the preview was shrunk
        var cell = (int)Math.Floor(PageSheet.Cell * k);
        for (var i = 0; i < Math.Min(PageSheet.Batch, _count - first + 1); i++)
        {
            var rect = new Int32Rect((int)(i % PageSheet.Columns * PageSheet.Cell * k), (int)(i / PageSheet.Columns * PageSheet.Cell * k), cell, cell);
            if (cell > 0 && rect.X + rect.Width <= sheet.Width && rect.Y + rect.Height <= sheet.Height)
            {
                var thumb = new CroppedBitmap(bitmap, rect);
                thumb.Freeze();
                _thumbs[first + i] = thumb;
            }
        }
    }

    // The selected pages, or the focused one when none are.
    private IReadOnlyCollection<int> Targets() => _selected.Count > 0 ? _selected : [_focus];

    private void Apply(IReadOnlyList<PagePick> pages, IEnumerable<int>? select = null)
    {
        if (pages.SequenceEqual(_pages))
        {
            return;
        }

        _undo.Push(_pages);
        _pages = pages;
        if (select is not null)
        {
            _selected.Clear();
            _selected.UnionWith(select);
        }

        _selected.RemoveWhere(i => i >= _pages.Count);
        _focus = Math.Clamp(_focus, 0, _pages.Count - 1);
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Delete()
    {
        var first = Targets().Min();
        Apply(PageMath.Delete(_pages, Targets()), []);
        _focus = Math.Min(first, _pages.Count - 1);
        Render();
    }

    private void Keep()
    {
        Apply(PageMath.KeepOnly(_pages, Targets()), []);
        _focus = 0;
        Render();
    }

    private void MoveTo(int gap)
    {
        if (gap < 0 || gap > _pages.Count)
        {
            return;
        }

        var (pages, moved) = PageMath.Move(_pages, Targets(), gap);
        _focus = moved[^1];
        Apply(pages, moved);
    }

    private void SelectOnly(int i)
    {
        _selected.Clear();
        _selected.Add(i);
        (_focus, _anchor) = (i, i);
    }

    private void Range(int i)
    {
        _selected.Clear();
        _selected.UnionWith(Enumerable.Range(Math.Min(_anchor, i), Math.Abs(_anchor - i) + 1));
        _focus = i;
    }

    private void Toggle(int i)
    {
        if (!_selected.Remove(i))
        {
            _selected.Add(i);
        }

        (_focus, _anchor) = (i, i);
    }

    private int? TileAt(DependencyObject? source)
    {
        for (var d = source; d is not null && d != Tiles; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Border { Tag: int i })
            {
                return i;
            }
        }

        return null;
    }

    private Rect Bounds(int i)
    {
        var tile = (FrameworkElement)Tiles.Children[i];
        return tile.TransformToAncestor(Tiles).TransformBounds(new Rect(0, 0, tile.ActualWidth, tile.ActualHeight));
    }

    // The gap (0..Count) a drop at `at` goes into: before or after the tile under it, or after the last one.
    private int GapAt(Point at)
    {
        for (var i = 0; i < Tiles.Children.Count; i++)
        {
            var b = Bounds(i);
            b.Inflate(Gap, Gap);
            if (b.Contains(at))
            {
                return at.X < b.Left + b.Width / 2 ? i : i + 1;
            }
        }

        return _pages.Count;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Tiles.Focus();
        _pressed = -1;
        if (TileAt(e.OriginalSource as DependencyObject) is not { } i)
        {
            _selected.Clear();
            Render();
            return;
        }

        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            Toggle(i);
        }
        else if (mods.HasFlag(ModifierKeys.Shift))
        {
            Range(i);
        }
        else if (!_selected.Contains(i))
        {
            SelectOnly(i);
        }
        else
        {
            (_pressed, _focus) = (i, i);
        }

        _pressAt = e.GetPosition(Tiles);
        Tiles.CaptureMouse();
        Render();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is not { } from || e.LeftButton != MouseButtonState.Pressed || _selected.Count == 0)
        {
            return;
        }

        var at = e.GetPosition(Tiles);
        if (!_dragging && (at - from).Length < 6)
        {
            return;
        }

        _dragging = true;
        _dropAt = GapAt(at);
        var edge = _dropAt < _pages.Count ? Bounds(_dropAt) : Bounds(_pages.Count - 1);
        Canvas.SetLeft(DropMark, (_dropAt < _pages.Count ? edge.Left - Gap : edge.Right + Gap) - DropMark.Width / 2);
        Canvas.SetTop(DropMark, edge.Top);
        DropMark.Height = edge.Height;
        DropMark.Visibility = Visibility.Visible;
        var y = e.GetPosition(Scroller).Y;
        if (y < 24)
        {
            Scroller.LineUp();
        }
        else if (y > Scroller.ActualHeight - 24)
        {
            Scroller.LineDown();
        }
    }

    private void EndPress()
    {
        if (_pressAt is null)
        {
            return;
        }

        var (dragging, drop, pressed) = (_dragging, _dropAt, _pressed);
        (_pressAt, _dragging, _dropAt, _pressed) = (null, false, -1, -1);
        DropMark.Visibility = Visibility.Collapsed;
        Tiles.ReleaseMouseCapture();
        if (dragging && drop >= 0)
        {
            MoveTo(drop);
        }
        else if (pressed >= 0)
        {
            SelectOnly(pressed);
            Render();
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is ButtonBase || _pages.Count == 0)
        {
            return; // the buttons take their own keys
        }

        var mods = Keyboard.Modifiers;
        var columns = Math.Max(1, (int)(Tiles.ActualWidth / (TileWidth + 2 * Gap)));
        switch (e.Key)
        {
            case Key.Left or Key.Right when mods == ModifierKeys.Control:
                MoveTo(e.Key == Key.Left ? Targets().Min() - 1 : Targets().Max() + 2);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End when (mods & ~ModifierKeys.Shift) == ModifierKeys.None:
                var next = Math.Clamp(e.Key switch
                {
                    Key.Left => _focus - 1,
                    Key.Right => _focus + 1,
                    Key.Up => _focus - columns,
                    Key.Down => _focus + columns,
                    Key.Home => 0,
                    _ => _pages.Count - 1,
                }, 0, _pages.Count - 1);
                if (mods == ModifierKeys.Shift)
                {
                    Range(next);
                }
                else
                {
                    SelectOnly(next);
                }

                Render();
                ((FrameworkElement)Tiles.Children[_focus]).BringIntoView();
                break;
            case Key.Space when mods == ModifierKeys.None:
                Toggle(_focus);
                Render();
                break;
            case Key.A when mods == ModifierKeys.Control:
                _selected.UnionWith(Enumerable.Range(0, _pages.Count));
                Render();
                break;
            case Key.R when mods == ModifierKeys.None:
                Apply(PageMath.Turn(_pages, Targets(), 90));
                break;
            case Key.L when mods == ModifierKeys.None:
            case Key.R when mods == ModifierKeys.Shift:
                Apply(PageMath.Turn(_pages, Targets(), -90));
                break;
            case Key.Delete or Key.Back when mods == ModifierKeys.None:
                Delete();
                break;
            case Key.K when mods == ModifierKeys.None:
                Keep();
                break;
            case Key.S when mods == ModifierKeys.None:
                Apply(PageMath.ToggleSplit(_pages, Targets().Max()));
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Render()
    {
        Tiles.Children.Clear();
        for (var i = 0; i < _pages.Count; i++)
        {
            var pick = _pages[i];
            var picked = _selected.Contains(i);
            var image = new Image
            {
                Source = _thumbs.GetValueOrDefault(pick.Source),
                Width = PageSheet.Cell,
                Height = PageSheet.Cell,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(pick.Rotate),
            };
            var label = new TextBlock { Text = pick.Rotate == 0 ? $"{pick.Source}" : $"{pick.Source}  ↻ {pick.Rotate}°", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            var content = new Grid();
            content.Children.Add(new StackPanel { Children = { image, label } });
            var split = pick.SplitAfter && i < _pages.Count - 1;
            if (split)
            {
                content.Children.Add(new Rectangle { Width = 4, Fill = Accent, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -10, 0), RadiusX = 2, RadiusY = 2 });
            }

            var tile = new Border
            {
                Tag = i,
                Child = content,
                Width = TileWidth,
                Height = TileHeight,
                Margin = new Thickness(Gap),
                Padding = new Thickness(2, 2, 2, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2),
                BorderBrush = picked ? Accent : i == _focus && Tiles.IsKeyboardFocused ? Focused : Brushes.Transparent,
                Background = picked ? Picked : Brushes.Transparent,
                ToolTip = split ? $"Page {pick.Source}. A new file starts after it." : $"Page {pick.Source}",
            };
            AutomationProperties.SetName(tile, $"Page {pick.Source}{(pick.Rotate != 0 ? $", turned {pick.Rotate}°" : "")}{(picked ? ", selected" : "")}{(split ? ", new file after" : "")}");
            Tiles.Children.Add(tile);
        }

        var files = PageMath.Files(_pages);
        Summary.Text = $"{_pages.Count} of {_count} pages{(files > 1 ? $", saved as {files} files" : "")}{(_selected.Count > 0 ? $". {_selected.Count} selected" : "")}.";
        SplitButton.IsEnabled = Targets().Max() < _pages.Count - 1;
        DeleteButton.IsEnabled = Targets().Count < _pages.Count;
    }
}
