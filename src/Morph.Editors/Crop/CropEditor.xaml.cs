using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

using Morph.Core.Jobs;
using Morph.Overlay;

namespace Morph.Editors.Crop;

/// <summary>Gets the Worker's preview of a file (the App never decodes dropped files itself).</summary>
public delegate Task<PreviewImage> PreviewLoader(string path, CancellationToken ct);

/// <summary>
/// Crop (ED-1) for images, video (one frame) and PDF (page 1; the box applies to every page): drag the handles or the box,
/// or draw a new one; shape chips; size readout. Keyboard: arrows move the box, Shift+arrows resize it.
/// Returns { x, y, w, h } in source pixels (PDF: points) and remembers the shape chip.
/// </summary>
public sealed partial class CropEditor : UserControl, IEditor
{
    private const double HandleSize = 10; // DIPs
    private const double Grab = 8; // DIPs around an edge or corner that pick it up

    private static readonly (string Id, string Label, double? Ratio)[] Shapes =
    [
        ("free", "Free", null), ("original", "Original", null), ("1:1", "1:1", 1), ("4:5", "4:5", 4.0 / 5), ("9:16", "9:16", 9.0 / 16), ("16:9", "16:9", 16.0 / 9),
    ];

    private readonly PreviewLoader _load;
    private readonly Stack<(Rect Box, string Shape)> _undo = new();
    private readonly Rectangle[] _handles = new Rectangle[8];
    private Size _source;
    private double _scale = 1; // DIPs per source unit
    private bool _points; // PDF: the source is in points, not pixels
    private Rect _box;
    private string _shape = "free";
    private CropHandle _drag;
    private Point _dragFrom; // source units
    private Rect _dragStart;
    private Rect _beforeDrag;
    private bool _settingShape; // chip changed from code, not by the user

    public CropEditor(PreviewLoader load)
    {
        InitializeComponent();
        _load = load;
        foreach (var (id, label, _) in Shapes)
        {
            var chip = new RadioButton { Content = label, Tag = id, GroupName = "CropShape", MinWidth = 64, Margin = new Thickness(0, 0, 4, 0) };
            chip.SetResourceReference(StyleProperty, typeof(ToggleButton));
            chip.Checked += (_, _) =>
            {
                if (!_settingShape)
                {
                    Remember();
                    SetShape(id);
                }
            };
            Aspects.Children.Add(chip);
        }

        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1 };
            Overlay.Children.Add(_handles[i]);
        }

        Stage.MouseLeftButtonDown += OnDown;
        Stage.MouseMove += OnMove;
        Stage.MouseLeftButtonUp += (_, _) => EndDrag();
        Stage.LostMouseCapture += (_, _) => EndDrag();
        KeyDown += OnKey;
        GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus == this)
            {
                Stage.Focus(); // the shell focuses the editor; the box is what the keys move
            }
        };
    }

    public string Title => "Crop";

    public FrameworkElement View => this;

    public bool CanUndo => _undo.Count > 0;

    public event EventHandler? Changed;

    public async Task LoadAsync(string path, JsonObject? settings, CancellationToken ct)
    {
        var preview = await _load(path, ct);
        var bitmap = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgra32, null, preview.Bgra, preview.Width * 4);
        bitmap.Freeze();
        Picture.Source = bitmap;
        _source = new Size(preview.SourceWidth, preview.SourceHeight);
        _points = string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

        // As large as the shell allows (80% of the work area) less its header, chips and footer.
        var work = Screens.WorkAreaAtCursor();
        _scale = Math.Min((work.Width * 0.8 - 60) / _source.Width, (work.Height * 0.8 - 190) / _source.Height);
        _scale = Math.Min(_scale, Math.Max(1, 640 / Math.Max(_source.Width, _source.Height))); // small pictures grow only to 640 DIPs
        Stage.Width = Math.Max(1, _source.Width * _scale);
        Stage.Height = Math.Max(1, _source.Height * _scale);

        _undo.Clear();
        SetShape((string?)settings?["shape"] is { } saved && Shapes.Any(s => s.Id == saved) ? saved : "free");
    }

    public void Undo()
    {
        var (box, shape) = _undo.Pop();
        _shape = shape;
        CheckChip();
        _box = box;
        Draw();
    }

    public JsonObject Parameters() => _points
        ? new() { ["x"] = Math.Round(_box.X, 2), ["y"] = Math.Round(_box.Y, 2), ["w"] = Math.Round(_box.Width, 2), ["h"] = Math.Round(_box.Height, 2) }
        : new() { ["x"] = (int)Math.Round(_box.X), ["y"] = (int)Math.Round(_box.Y), ["w"] = (int)Math.Round(_box.Width), ["h"] = (int)Math.Round(_box.Height) };

    public JsonObject Settings() => new() { ["shape"] = _shape };

    private double? Ratio => _shape == "original" ? _source.Width / _source.Height : Shapes.First(s => s.Id == _shape).Ratio;

    private double MinSize => Math.Max(1, 16 / _scale); // 16 DIPs on screen

    private void SetShape(string id)
    {
        _shape = id;
        CheckChip();
        _box = CropMath.Largest(_source, Ratio);
        Draw();
    }

    private void CheckChip()
    {
        _settingShape = true;
        foreach (var chip in Aspects.Children.OfType<RadioButton>())
        {
            chip.IsChecked = (string)chip.Tag == _shape;
        }

        _settingShape = false;
    }

    private void Remember()
    {
        _undo.Push((_box, _shape));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Stage.Focus();
        var at = e.GetPosition(Stage);
        _dragFrom = new Point(at.X / _scale, at.Y / _scale);
        _dragStart = _beforeDrag = _box;
        _drag = HandleAt(at);
        if (_drag == CropHandle.None)
        {
            // Outside the box: draw a new one from here.
            _dragStart = new Rect(_dragFrom, new Size(0, 0));
            _drag = CropHandle.BottomRight;
        }

        Stage.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(Stage);
        if (_drag == CropHandle.None)
        {
            Stage.Cursor = HandleAt(at) switch
            {
                CropHandle.Move => Cursors.SizeAll,
                CropHandle.Left or CropHandle.Right => Cursors.SizeWE,
                CropHandle.Top or CropHandle.Bottom => Cursors.SizeNS,
                CropHandle.TopLeft or CropHandle.BottomRight => Cursors.SizeNWSE,
                CropHandle.TopRight or CropHandle.BottomLeft => Cursors.SizeNESW,
                _ => Cursors.Cross,
            };
            return;
        }

        var to = new Point(at.X / _scale, at.Y / _scale);
        _box = _drag == CropHandle.Move
            ? CropMath.Move(_dragStart, to - _dragFrom, _source)
            : CropMath.Resize(_dragStart, _drag, to, _source, Ratio, MinSize);
        Draw();
    }

    private void EndDrag()
    {
        if (_drag == CropHandle.None)
        {
            return;
        }

        _drag = CropHandle.None;
        Stage.ReleaseMouseCapture();
        if (_box.Width < MinSize || _box.Height < MinSize)
        {
            _box = _beforeDrag; // a click outside the box, not a drag
            Draw();
        }

        if (_box != _beforeDrag)
        {
            _undo.Push((_beforeDrag, _shape));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Arrows move by 1% of the picture; Shift+arrows move the right or bottom edge.
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down) || e.OriginalSource is RadioButton)
        {
            return; // arrows on the chips pick a chip
        }

        var step = Math.Max(1, Math.Round(Math.Max(_source.Width, _source.Height) / 100));
        var (dx, dy) = e.Key switch { Key.Left => (-step, 0.0), Key.Right => (step, 0.0), Key.Up => (0.0, -step), _ => (0.0, step) };
        Remember();
        _box = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? CropMath.Resize(_box, dx != 0 ? CropHandle.Right : CropHandle.Bottom, new Point(_box.Right + dx, _box.Bottom + dy), _source, Ratio, MinSize)
            : CropMath.Move(_box, new Vector(dx, dy), _source);
        Draw();
        e.Handled = true;
    }

    private CropHandle HandleAt(Point at)
    {
        var b = new Rect(_box.X * _scale, _box.Y * _scale, _box.Width * _scale, _box.Height * _scale);
        static bool Near(double a, double v) => Math.Abs(a - v) <= Grab;
        var inY = at.Y >= b.Top - Grab && at.Y <= b.Bottom + Grab;
        var inX = at.X >= b.Left - Grab && at.X <= b.Right + Grab;
        var (left, right, top, bottom) = (Near(at.X, b.Left) && inY, Near(at.X, b.Right) && inY, Near(at.Y, b.Top) && inX, Near(at.Y, b.Bottom) && inX);
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => CropHandle.TopLeft,
            (_, true, true, _) => CropHandle.TopRight,
            (true, _, _, true) => CropHandle.BottomLeft,
            (_, true, _, true) => CropHandle.BottomRight,
            (true, _, _, _) => CropHandle.Left,
            (_, true, _, _) => CropHandle.Right,
            (_, _, true, _) => CropHandle.Top,
            (_, _, _, true) => CropHandle.Bottom,
            _ => b.Contains(at) ? CropHandle.Move : CropHandle.None,
        };
    }

    private void Draw()
    {
        var b = new Rect(_box.X * _scale, _box.Y * _scale, _box.Width * _scale, _box.Height * _scale);
        Shade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(new Rect(0, 0, Stage.Width, Stage.Height)), new RectangleGeometry(b) },
        };
        Canvas.SetLeft(Frame, b.X);
        Canvas.SetTop(Frame, b.Y);
        Frame.Width = b.Width;
        Frame.Height = b.Height;

        Point[] spots =
        [
            b.TopLeft, new(b.X + b.Width / 2, b.Top), b.TopRight, new(b.Right, b.Y + b.Height / 2),
            b.BottomRight, new(b.X + b.Width / 2, b.Bottom), b.BottomLeft, new(b.Left, b.Y + b.Height / 2),
        ];
        for (var i = 0; i < spots.Length; i++)
        {
            Canvas.SetLeft(_handles[i], spots[i].X - HandleSize / 2);
            Canvas.SetTop(_handles[i], spots[i].Y - HandleSize / 2);
        }

        var unit = _points ? "pt" : "px";
        SizeText.Text = $"{Math.Round(_box.Width):N0} × {Math.Round(_box.Height):N0} {unit}";
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
