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

namespace Morph.Editors.Annotate;

/// <summary>
/// Annotate (ED-2) for pictures and PDF pages: arrow, box, ellipse, text, highlighter, pen and pixelate (pictures only), in a
/// colour and line width; undo and redo. Mouse: drag to draw, click to type (Text). Keyboard: letters pick the tool, Space adds
/// a mark in the middle, arrows move the newest mark on the page, Shift+arrows resize it, Delete removes it, Page Up/Down turn
/// PDF pages. Returns <see cref="Annotations"/> parameters; remembers the tool, colour and width.
/// </summary>
public sealed partial class AnnotateEditor : UserControl, IEditor
{
    private static readonly (string Id, string Label, Key Key)[] ToolList =
    [
        ("arrow", "Arrow", Key.A), ("rect", "Box", Key.R), ("ellipse", "Ellipse", Key.E), ("text", "Text", Key.T), ("highlight", "Highlight", Key.H), ("pen", "Pen", Key.D), ("pixelate", "Pixelate", Key.P),
    ];

    private static readonly (string Hex, string Name)[] ColorList =
    [
        ("#E5383B", "Red"), ("#F28C28", "Orange"), ("#FFD60A", "Yellow"), ("#2FB344", "Green"), ("#1E88E5", "Blue"), ("#111111", "Black"), ("#FFFFFF", "White"),
    ];

    private static readonly (string Id, string Label, double Dips)[] StrokeList = [("thin", "Thin", 2), ("medium", "Medium", 4), ("thick", "Thick", 8)];

    private readonly PreviewLoader _load;
    private readonly Stack<List<AnnotationShape>> _undo = new();
    private readonly Stack<List<AnnotationShape>> _redo = new();
    private List<AnnotationShape> _shapes = [];
    private AnnotationShape? _drawing; // being dragged out
    private TextBox? _typing;
    private Point _typingAt; // source units
    private BitmapSource? _bitmap;
    private string _path = "";
    private CancellationToken _ct;
    private bool _pdf;
    private int _page = 1;
    private int _pages = 1;
    private int _loads; // newest page load; older ones are dropped
    private bool _loading; // keys and mouse wait (disabling the stage would drop keyboard focus)
    private Size _source;
    private double _scale = 1; // DIPs per source unit
    private string _tool = "arrow";
    private string _color = ColorList[0].Hex;
    private string _stroke = "medium";

    public AnnotateEditor(PreviewLoader load)
    {
        InitializeComponent();
        _load = load;
        foreach (var (id, label, key) in ToolList)
        {
            Tools.Children.Add(Chip(label, id, "AnnotateTool", $"{label} ({key})", () => _tool = id));
        }

        foreach (var (hex, name) in ColorList)
        {
            var swatch = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Stroke = Brushes.Gray, StrokeThickness = 1 };
            var chip = Chip(swatch, hex, "AnnotateColor", name, () => _color = hex);
            chip.MinWidth = 0;
            AutomationProperties.SetName(chip, name);
            ColorChips.Children.Add(chip);
        }

        foreach (var (id, label, _) in StrokeList)
        {
            Strokes.Children.Add(Chip(label, id, "AnnotateStroke", $"{label} line", () => _stroke = id));
        }

        Stage.MouseLeftButtonDown += OnDown;
        Stage.MouseMove += OnMove;
        Stage.MouseLeftButtonUp += (_, _) => EndDraw();
        Stage.LostMouseCapture += (_, _) => EndDraw();
        RedoButton.Click += (_, _) => Redo();
        PrevPage.Click += (_, _) => Turn(_page - 1);
        NextPage.Click += (_, _) => Turn(_page + 1);
        KeyDown += OnKey;
        GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus == this)
            {
                Stage.Focus(); // the shell focuses the editor; the stage is what the keys draw on
            }
        };
    }

    public string Title => "Annotate";

    public FrameworkElement View => this;

    public bool CanUndo => _undo.Count > 0;

    public event EventHandler? Changed;

    private double Stroke => StrokeList.First(s => s.Id == _stroke).Dips / _scale;

    public async Task LoadAsync(string path, JsonObject? settings, CancellationToken ct)
    {
        CloseText(commit: false);
        (_path, _ct) = (path, ct);
        _pdf = string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
        _shapes = [];
        _undo.Clear();
        _redo.Clear();
        _tool = (string?)settings?["tool"] is { } tool && ToolList.Any(t => t.Id == tool) && !(_pdf && tool == "pixelate") ? tool : "arrow";
        _color = (string?)settings?["color"] is { } color && ColorList.Any(c => c.Hex == color) ? color : ColorList[0].Hex;
        _stroke = (string?)settings?["stroke"] is { } stroke && StrokeList.Any(s => s.Id == stroke) ? stroke : "medium";
        Tools.Children.OfType<RadioButton>().Last().Visibility = _pdf ? Visibility.Collapsed : Visibility.Visible; // pixelate: Redact hides PDF content
        CheckChips();
        await ShowPageAsync(1, ++_loads);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        CloseText(commit: false);
        _redo.Push(_shapes);
        _shapes = _undo.Pop();
        Draw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public JsonObject Parameters()
    {
        CloseText(commit: true);
        return Annotations.ToParameters(_shapes);
    }

    public JsonObject Settings() => new() { ["tool"] = _tool, ["color"] = _color, ["stroke"] = _stroke };

    private RadioButton Chip(object content, string id, string group, string tip, Action pick)
    {
        var chip = new RadioButton { Content = content, Tag = id, GroupName = group, ToolTip = tip, MinWidth = 56, Margin = new Thickness(0, 0, 4, 0) };
        chip.SetResourceReference(StyleProperty, typeof(ToggleButton));
        chip.Checked += (_, _) =>
        {
            CloseText(commit: true); // in the colour and width it was typed in
            pick();
            Stage.Cursor = _tool == "text" ? Cursors.IBeam : Cursors.Cross;
        };
        return chip;
    }

    private void CheckChips()
    {
        foreach (var (panel, id) in new[] { (Tools, _tool), (ColorChips, _color), (Strokes, _stroke) })
        {
            foreach (var chip in panel.Children.OfType<RadioButton>())
            {
                chip.IsChecked = (string)chip.Tag == id;
            }
        }
    }

    private void Remember()
    {
        _undo.Push([.. _shapes]);
        _redo.Clear();
        RedoButton.IsEnabled = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        CloseText(commit: false);
        _undo.Push(_shapes);
        _shapes = _redo.Pop();
        Draw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add(AnnotationShape shape)
    {
        Remember();
        _shapes.Add(shape);
        Draw();
    }

    private async void Turn(int page)
    {
        if (page < 1 || page > _pages || page == _page)
        {
            return;
        }

        CloseText(commit: true);
        StatusText.Visibility = Visibility.Collapsed;
        try
        {
            await ShowPageAsync(page, ++_loads);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
        }
        catch (EngineException ex)
        {
            StatusText.Text = $"Morph couldn't show page {page}. {ex.Message}";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private async Task ShowPageAsync(int page, int load)
    {
        (_loading, Stage.IsHitTestVisible) = (true, false);
        try
        {
            var preview = await _load(_path, page, _ct);
            if (load != _loads)
            {
                return; // a newer page was asked for meanwhile
            }

            var bitmap = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgra32, null, preview.Bgra, preview.Width * 4);
            bitmap.Freeze();
            (_bitmap, Picture.Source, _page, _pages) = (bitmap, bitmap, page, preview.Pages);
            _source = new Size(preview.SourceWidth, preview.SourceHeight);

            // As large as the shell allows (80% of the work area) less its header, three toolbar rows and footer; like crop.
            var work = Screens.WorkAreaAtCursor();
            _scale = Math.Min((work.Width * 0.8 - 60) / _source.Width, (work.Height * 0.8 - 270) / _source.Height);
            _scale = Math.Min(_scale, Math.Max(1, 640 / Math.Max(_source.Width, _source.Height)));
            Stage.Width = Math.Max(1, _source.Width * _scale);
            Stage.Height = Math.Max(1, _source.Height * _scale);
            Ink.RenderTransform = new ScaleTransform(_scale, _scale);

            PagePanel.Visibility = _pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            PageText.Text = $"Page {_page} of {_pages}";
            PrevPage.IsEnabled = _page > 1;
            NextPage.IsEnabled = _page < _pages;
            Draw();
        }
        finally
        {
            if (load == _loads)
            {
                (_loading, Stage.IsHitTestVisible) = (false, true);
            }
        }
    }

    private Point ToSource(Point at) => new(Math.Clamp(at.X / _scale, 0, _source.Width), Math.Clamp(at.Y / _scale, 0, _source.Height));

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_typing?.IsMouseOver == true)
        {
            return; // a click inside the text being typed
        }

        CloseText(commit: true);
        Stage.Focus();
        var at = ToSource(e.GetPosition(Stage));
        if (_tool == "text")
        {
            OpenText(at);
        }
        else
        {
            _drawing = new AnnotationShape(_tool, [(at.X, at.Y), (at.X, at.Y)], _color, Stroke, Page: _page);
            Stage.CaptureMouse();
        }

        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_drawing is null)
        {
            return;
        }

        var at = ToSource(e.GetPosition(Stage));
        if (_drawing.Type is "highlight" or "pen")
        {
            var last = _drawing.Points[^1];
            if (Math.Abs(at.X - last.X) * _scale + Math.Abs(at.Y - last.Y) * _scale >= 2)
            {
                _drawing = _drawing with { Points = [.. _drawing.Points, (at.X, at.Y)] };
            }
        }
        else
        {
            _drawing = _drawing with { Points = [_drawing.Points[0], (at.X, at.Y)] };
        }

        Draw();
    }

    private void EndDraw()
    {
        if (_drawing is not { } shape)
        {
            return;
        }

        _drawing = null;
        Stage.ReleaseMouseCapture();
        var (_, _, w, h) = Annotations.Box(shape);
        var big = shape.Type is "arrow" or "highlight" or "pen" ? Math.Max(w, h) * _scale >= 4 : w * _scale >= 4 && h * _scale >= 4;
        if (big)
        {
            Add(shape);
        }
        else
        {
            Draw(); // a click, not a drag
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || _loading)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        var onChip = e.OriginalSource is ButtonBase;
        if ((e.Key == Key.Y && mods == ModifierKeys.Control) || (e.Key == Key.Z && mods == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            Redo();
        }
        else if (e.Key is Key.PageUp or Key.PageDown && mods == ModifierKeys.None)
        {
            Turn(_page + (e.Key == Key.PageDown ? 1 : -1));
        }
        else if (mods == ModifierKeys.None && ToolList.FirstOrDefault(t => t.Key == e.Key) is { Id: { } id } && !(_pdf && id == "pixelate"))
        {
            _tool = id;
            CheckChips();
        }
        else if (e.Key == Key.Space && mods == ModifierKeys.None && !onChip)
        {
            AddInMiddle();
        }
        else if (e.Key is Key.Delete or Key.Back && mods == ModifierKeys.None && Newest() is { } i)
        {
            Remember();
            _shapes.RemoveAt(i);
            Draw();
        }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && !onChip && Newest() is { } n)
        {
            var step = Math.Max(1, Math.Round(Math.Max(_source.Width, _source.Height) / 100));
            var (dx, dy) = e.Key switch { Key.Left => (-step, 0.0), Key.Right => (step, 0.0), Key.Up => (0.0, -step), _ => (0.0, step) };
            var shape = _shapes[n];
            IReadOnlyList<(double X, double Y)> points = mods.HasFlag(ModifierKeys.Shift) && shape.Points.Count == 2
                ? [shape.Points[0], Clamp(shape.Points[1].X + dx, shape.Points[1].Y + dy)]
                : shape.Points.Select(p => Clamp(p.X + dx, p.Y + dy)).ToList();
            Remember();
            _shapes[n] = shape with { Points = points };
            Draw();
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private (double X, double Y) Clamp(double x, double y) => (Math.Clamp(x, 0, _source.Width), Math.Clamp(y, 0, _source.Height));

    // The newest mark on this page: what the keys move.
    private int? Newest()
    {
        var i = _shapes.FindLastIndex(s => s.Page == _page);
        return i < 0 ? null : i;
    }

    // Keyboard drawing: a mark a quarter of the picture wide in the middle (text: a box to type in).
    private void AddInMiddle()
    {
        var (cx, cy, dx, dy) = (_source.Width / 2, _source.Height / 2, _source.Width / 8, _source.Height / 8);
        if (_tool == "text")
        {
            OpenText(new Point(cx - dx, cy));
            return;
        }

        (double, double)[] points = _tool switch
        {
            "arrow" => [(cx - dx, cy + dy), (cx + dx, cy - dy)],
            "highlight" or "pen" => [(cx - dx, cy), (cx + dx, cy)],
            _ => [(cx - dx, cy - dy), (cx + dx, cy + dy)],
        };
        Add(new AnnotationShape(_tool, points, _color, Stroke, Page: _page));
    }

    private void OpenText(Point at)
    {
        _typingAt = at;
        var size = Annotations.FontSize(Stroke) * _scale;
        _typing = new TextBox
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_color)),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = size * 2,
            MinHeight = 0,
            MaxLength = 500,
        };
        AutomationProperties.SetName(_typing, "Text. Enter to place it, Esc to drop it.");
        _typing.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                CloseText(commit: e.Key == Key.Enter);
                Stage.Focus();
                e.Handled = true; // not the shell's Done or Cancel
            }
        };
        _typing.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is not null)
            {
                CloseText(commit: true); // focus moved on in the window (not: the window was left)
            }
        };
        Canvas.SetLeft(_typing, at.X * _scale);
        Canvas.SetTop(_typing, at.Y * _scale);
        Typing.Children.Add(_typing);
        _typing.Focus();
    }

    private void CloseText(bool commit)
    {
        if (_typing is not { } box)
        {
            return;
        }

        _typing = null; // first: removing it moves focus, which calls back here
        Typing.Children.Remove(box);
        if (commit && !string.IsNullOrWhiteSpace(box.Text))
        {
            Add(new AnnotationShape("text", [(_typingAt.X, _typingAt.Y)], _color, Stroke, box.Text.Trim(), _page));
        }
    }

    private void Draw()
    {
        Ink.Children.Clear();
        foreach (var shape in _shapes.Where(s => s.Page == _page).Append(_drawing).OfType<AnnotationShape>())
        {
            foreach (var mark in Marks(shape))
            {
                Ink.Children.Add(mark);
            }
        }

        RedoButton.IsEnabled = _redo.Count > 0;
    }

    // The same marks the engines draw (Annotations has the shared geometry), in source units on the scaled Ink canvas.
    private IEnumerable<UIElement> Marks(AnnotationShape shape)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(shape.Color));
        var (x, y, w, h) = Annotations.Box(shape);
        static System.Windows.Shapes.Path Stroked(Geometry g, Brush stroke, double width) => new()
        {
            Data = g,
            Stroke = stroke,
            StrokeThickness = width,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        static Point P((double X, double Y) p) => new(p.X, p.Y);

        switch (shape.Type)
        {
            case "rect":
                yield return Stroked(new RectangleGeometry(new Rect(x, y, w, h)), brush, shape.Stroke);
                break;
            case "ellipse":
                yield return Stroked(new EllipseGeometry(new Rect(x, y, w, h)), brush, shape.Stroke);
                break;
            case "arrow":
                var (end, head) = Annotations.Arrow(shape.Points[0], shape.Points[1], shape.Stroke);
                yield return Stroked(new LineGeometry(P(shape.Points[0]), P(end)), brush, shape.Stroke);
                yield return new Polygon { Points = [.. head.Select(P)], Fill = brush };
                break;
            case "highlight":
                var ink = new SolidColorBrush(brush.Color) { Opacity = Annotations.HighlightOpacity };
                var line = new Polyline { Points = [.. shape.Points.Select(P)], Stroke = ink, StrokeThickness = Annotations.HighlightWidth(shape.Stroke) };
                (line.StrokeStartLineCap, line.StrokeEndLineCap, line.StrokeLineJoin) = (PenLineCap.Round, PenLineCap.Round, PenLineJoin.Round);
                yield return line;
                break;
            case "pen":
                var pen = new Polyline { Points = [.. shape.Points.Select(P)], Stroke = brush, StrokeThickness = shape.Stroke };
                (pen.StrokeStartLineCap, pen.StrokeEndLineCap, pen.StrokeLineJoin) = (PenLineCap.Round, PenLineCap.Round, PenLineJoin.Round);
                yield return pen;
                break;
            case "text":
                var text = new TextBlock { Text = shape.Text, FontFamily = new FontFamily("Segoe UI"), FontSize = Annotations.FontSize(shape.Stroke), Foreground = brush };
                Canvas.SetLeft(text, x);
                Canvas.SetTop(text, y);
                yield return text;
                break;
            case "pixelate" when Pixelated(x, y, w, h) is { } cells:
                yield return cells;
                break;
        }
    }

    // The preview's pixels under the box, shrunk to the engine's cell count and shown blocky.
    private Image? Pixelated(double x, double y, double w, double h)
    {
        if (_bitmap is null)
        {
            return null;
        }

        var k = _bitmap.PixelWidth / _source.Width; // preview pixels per source unit
        var left = (int)Math.Clamp(Math.Floor(x * k), 0, _bitmap.PixelWidth - 1);
        var top = (int)Math.Clamp(Math.Floor(y * k), 0, _bitmap.PixelHeight - 1);
        var width = (int)Math.Clamp(Math.Ceiling((x + w) * k) - left, 1, _bitmap.PixelWidth - left);
        var height = (int)Math.Clamp(Math.Ceiling((y + h) * k) - top, 1, _bitmap.PixelHeight - top);
        var block = Annotations.PixelBlock(_source.Width, _source.Height) * k;
        var cells = new TransformedBitmap(new CroppedBitmap(_bitmap, new Int32Rect(left, top, width, height)),
            new ScaleTransform(Math.Max(1, Math.Ceiling(width / block)) / width, Math.Max(1, Math.Ceiling(height / block)) / height));
        var image = new Image { Source = cells, Stretch = Stretch.Fill, Width = width / k, Height = height / k };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, left / k);
        Canvas.SetTop(image, top / k);
        return image;
    }
}
