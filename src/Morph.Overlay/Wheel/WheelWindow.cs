using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

using Morph.Overlay.Theme;

using Windows.Win32.Foundation;

namespace Morph.Overlay.Wheel;

/// <summary>
/// The radial wheel, design A "Fluent ring" (docs/design/wheel-a-fluent-ring.svg) in Morph glass (<see cref="MorphGlass"/>):
/// a blurred snapshot of the screen behind it under a warm tint, orange highlight. View only: no logic beyond drawing and hit-testing.
/// </summary>
internal sealed class WheelWindow : Window
{
    public const double SizeDip = 2 * WheelGeometry.RingOuterRadius + 16; // preset ring + room for the shadow; see-through outside what's drawn
    private static readonly Point Centre = new(SizeDip / 2, SizeDip / 2);
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly Canvas _canvas = new() { Width = SizeDip, Height = SizeDip };
    private readonly ScaleTransform _scale = new(1, 1, SizeDip / 2, SizeDip / 2);
    private readonly List<(Path Wedge, TextBlock Icon, TextBlock Label)> _wedges = [];
    private readonly List<(Path Wedge, TextBlock Label)> _choices = [];

    // Frosted glass: layered windows can't get DWM blur, so the wheel blurs a snapshot of what's behind it.
    private readonly Image _behind = new()
    {
        Width = SizeDip,
        Height = SizeDip,
        Stretch = Stretch.Fill,
        Effect = new BlurEffect { Radius = 30, RenderingBias = RenderingBias.Performance },
    };

    private readonly Grid _glassLayer = new()
    {
        Width = SizeDip,
        Height = SizeDip,
        Clip = new EllipseGeometry(Centre, WheelGeometry.DiscRadius, WheelGeometry.DiscRadius),
        IsHitTestVisible = false,
    };

    private bool _glass;
    private IReadOnlyList<WheelItem> _items = [];
    private Palette _palette = Palette.For(dark: false, glass: false);
    private int? _highlighted;
    private int? _expanded; // wedge whose presets are on the outer ring
    private int? _highlightedChoice;

    public WheelWindow()
    {
        Title = "Morph wheel";
        Width = Height = SizeDip;
        AllowsTransparency = true; // per-pixel alpha: drops outside the disc fall through to the catcher
        Background = Brushes.Transparent;
        AllowDrop = true;
        _canvas.RenderTransform = _scale;
        _glassLayer.Children.Add(_behind);
        Content = _canvas;
        Hwnd = NativeWindows.Prepare(this);
    }

    public HWND Hwnd { get; }

    public void SetContent(IReadOnlyList<WheelItem> items, string count, string caption, bool dark)
    {
        _items = items;
        _glass = MorphGlass.TransparencyEnabled();
        _palette = Palette.For(dark, _glass);
        _highlighted = null;
        _expanded = null;
        _highlightedChoice = null;
        _choices.Clear();
        _wedges.Clear();
        _canvas.Children.Clear();

        var p = _palette;
        // An opaque disc casts the shadow; glass (when on) covers it, then the tint.
        var shadow = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Direction = 270, Opacity = 0.22 };
        _canvas.Children.Add(Circle(WheelGeometry.DiscRadius, p.Base, p.Stroke, shadow));
        if (_glass)
        {
            _canvas.Children.Add(_glassLayer);
            _canvas.Children.Add(Circle(WheelGeometry.DiscRadius, p.Disc, p.Stroke));
        }


        for (var i = 0; i < items.Count; i++)
        {
            var wedge = new Path { Data = WheelGeometry.Wedge(i, items.Count, Centre), StrokeThickness = 1 };
            var mid = i * 360.0 / items.Count;
            var anchor = WheelGeometry.PointAt(mid, WheelGeometry.ContentRadius);
            var icon = Text(items[i].Glyph, 16, FontWeights.Normal, anchor + new Vector(0, -9), IconFont);
            var label = Text(items[i].Label, 11, FontWeights.SemiBold, anchor + new Vector(0, 12));
            if (items[i].Beta)
            {
                label.Inlines.Add(new System.Windows.Documents.Run(" beta") { FontSize = 8, FontWeight = FontWeights.Normal });
            }
            _canvas.Children.Add(wedge);
            _canvas.Children.Add(icon);
            _canvas.Children.Add(label);
            _wedges.Add((wedge, icon, label));
            Paint(i, highlighted: false);
        }

        _canvas.Children.Add(Circle(WheelGeometry.CentreRadius, p.CentreFill, p.Stroke));
        var countText = Text(count, 18, FontWeights.SemiBold, new Point(0, -6));
        countText.Foreground = p.Label;
        var captionText = Text(caption, 10, FontWeights.Normal, new Point(0, 12));
        captionText.Foreground = p.Icon;
        _canvas.Children.Add(countText);
        _canvas.Children.Add(captionText);
    }

    /// <summary>Item under a drag position (DragOver coordinates; no mouse events arrive during OLE drags): a wedge, or a preset of the open ring.</summary>
    public WheelItem? ItemAt(DragEventArgs e) => ItemAt(e.GetPosition(_canvas));

    /// <summary>Item under a point on the wheel (mouse coordinates, keyboard path).</summary>
    public WheelItem? ItemAt(Point pos)
    {
        var (wedge, choice, _) = Hit(pos);
        return choice is { } c ? _items[_expanded!.Value].Choices![c] : wedge is { } w ? _items[w] : null;
    }

    /// <summary>Highlights what's under the pointer and opens or closes the preset ring; returns <see cref="ItemAt"/>.</summary>
    public WheelItem? Hover(DragEventArgs e) => Hover(e.GetPosition(_canvas));

    public WheelItem? Hover(Point pos)
    {
        var (wedge, choice, inRing) = Hit(pos);
        if (inRing)
        {
            // On the ring: the wedge stays open, only the preset highlight moves.
            SetChoice(choice);
            SetWedge(_expanded, highlighted: choice is null);
            return ItemAt(pos);
        }

        SetChoice(null);
        if (wedge != _expanded)
        {
            Collapse();
            if (wedge is { } w && _items[w].Choices is { Count: > 0 })
            {
                Expand(w);
            }
        }

        SetWedge(wedge, highlighted: true);
        return wedge is { } i ? _items[i] : null;
    }

    /// <summary>
    /// Whether the pointer is over anything drawn. WPF raises DragLeave each time the pointer crosses from one shape to
    /// the next (wedge → ring band → preset), not only when it leaves the window; those must not close the ring.
    /// </summary>
    public bool IsOver(DragEventArgs e) => VisualTreeHelper.HitTest(_canvas, e.GetPosition(_canvas)) is not null;

    /// <summary>Mouse position on the wheel, for <see cref="Hover(Point)"/> and <see cref="ItemAt(Point)"/>.</summary>
    public Point Position(System.Windows.Input.MouseEventArgs e) => e.GetPosition(_canvas);

    /// <summary>What Enter would run: the highlighted preset, else the highlighted wedge.</summary>
    public WheelItem? Current =>
        _highlightedChoice is { } c && _expanded is { } open ? _items[open].Choices![c] : _highlighted is { } w ? _items[w] : null;

    /// <summary>Keyboard (IX-5): moves the highlight <paramref name="by"/> wedges clockwise, opening the new wedge's preset ring.</summary>
    public void StepWedge(int by)
    {
        if (_items.Count == 0)
        {
            return;
        }

        var next = _highlighted is { } w ? ((w + by) % _items.Count + _items.Count) % _items.Count : by > 0 ? 0 : _items.Count - 1;
        SetChoice(null);
        Collapse();
        if (_items[next].Choices is { Count: > 0 })
        {
            Expand(next);
        }

        SetWedge(next, highlighted: true);
    }

    /// <summary>Keyboard: moves through the open ring's presets; past either end it's back on the wedge itself.</summary>
    public void StepChoice(int by)
    {
        if (_expanded is not { } open)
        {
            return;
        }

        var slots = _items[open].Choices!.Count + 1; // the wedge, then each preset
        var at = ((_highlightedChoice ?? -1) + 1 + by) % slots;
        var choice = (at + slots) % slots - 1;
        SetChoice(choice < 0 ? null : choice);
        SetWedge(open, highlighted: choice < 0);
    }

    public void ClearHover()
    {
        SetChoice(null);
        Collapse();
        SetWedge(null, highlighted: false);
    }

    public void ShowAt(System.Drawing.Point cursor)
    {
        var rect = NativeWindows.CenteredSquare(cursor, SizeDip);
        _behind.Source = _glass ? NativeWindows.CaptureScreen(rect) : null; // the wheel is hidden here, so it isn't in the shot
        NativeWindows.Place(Hwnd, rect);
        Show();

        var duration = TimeSpan.FromMilliseconds(120);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _canvas.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, duration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, duration) { EasingFunction = ease });
    }

    /// <param name="InRing">Past the disc's edge with a preset ring open: the ring owns the point, even between presets.</param>
    private (int? Wedge, int? Choice, bool InRing) Hit(Point pos)
    {
        var (dx, dy) = (pos.X - Centre.X, pos.Y - Centre.Y);
        if (_expanded is { } open && Math.Sqrt(dx * dx + dy * dy) > WheelGeometry.DiscRadius)
        {
            return (null, WheelGeometry.HitTestChoice(dx, dy, open, _items.Count, _items[open].Choices!.Count), true);
        }

        return (WheelGeometry.HitTest(dx, dy, _items.Count), null, false);
    }

    // The highlighted wedge; while one of its presets is highlighted it keeps only an accent outline.
    private void SetWedge(int? index, bool highlighted)
    {
        if (_highlighted is { } old && old != index)
        {
            Paint(old, highlighted: false);
        }

        if (index is { } i)
        {
            Paint(i, highlighted, outline: !highlighted);
        }

        _highlighted = index;
    }

    private void SetChoice(int? index)
    {
        if (index == _highlightedChoice)
        {
            return;
        }

        var p = _palette;
        for (var i = 0; i < _choices.Count; i++)
        {
            var on = i == index;
            _choices[i].Wedge.Fill = on ? p.Accent : p.Base;
            _choices[i].Wedge.Stroke = on ? p.Accent : p.Stroke;
            _choices[i].Label.Foreground = on ? p.OnAccent : p.Label;
        }

        _highlightedChoice = index;
    }

    private void Expand(int wedge)
    {
        var choices = _items[wedge].Choices!;
        // Nearly invisible but not see-through: keeps the drag on the wheel across the gap to the ring.
        _canvas.Children.Add(new Path { Data = WheelGeometry.ChoiceBand(wedge, _items.Count, choices.Count, Centre), Fill = Band });
        for (var i = 0; i < choices.Count; i++)
        {
            var path = new Path { Data = WheelGeometry.Choice(wedge, _items.Count, i, choices.Count, Centre), StrokeThickness = 1 };
            var anchor = WheelGeometry.PointAt(WheelGeometry.ChoiceAngle(wedge, _items.Count, i, choices.Count), WheelGeometry.RingContentRadius);
            var label = Text(choices[i].Label, 10.5, FontWeights.SemiBold, anchor);
            _canvas.Children.Add(path);
            _canvas.Children.Add(label);
            _choices.Add((path, label));
        }

        _expanded = wedge;
        _highlightedChoice = -1; // makes SetChoice paint every preset
        SetChoice(null);
    }

    private void Collapse()
    {
        if (_expanded is null)
        {
            return;
        }

        var added = 1 + 2 * _choices.Count; // the band, then a wedge and a label per preset, all on top
        _canvas.Children.RemoveRange(_canvas.Children.Count - added, added);
        _choices.Clear();
        _expanded = null;
        _highlightedChoice = null;
    }

    private void Paint(int i, bool highlighted, bool outline = false)
    {
        var (wedge, icon, label) = _wedges[i];
        var p = _palette;
        wedge.Fill = highlighted ? p.Accent : p.Wedge;
        wedge.Stroke = highlighted || outline ? p.Accent : p.Stroke;
        wedge.StrokeThickness = outline ? 2 : 1;
        icon.Foreground = highlighted ? p.OnAccent : p.Icon;
        label.Foreground = highlighted ? p.OnAccent : p.Label;
    }

    private static readonly Brush Band = FrozenBrush(Color.FromArgb(1, 0, 0, 0));

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Ellipse Circle(double radius, Brush fill, Brush stroke, Effect? effect = null)
    {
        var e = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = fill, Stroke = stroke, StrokeThickness = 1, Effect = effect };
        Canvas.SetLeft(e, Centre.X - radius);
        Canvas.SetTop(e, Centre.Y - radius);
        return e;
    }

    // Centred on an offset from the wheel centre.
    private static TextBlock Text(string text, double size, FontWeight weight, Point offset, FontFamily? font = null)
    {
        const double width = 100;
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Width = width,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
        };
        if (font is not null)
        {
            t.FontFamily = font;
        }

        Canvas.SetLeft(t, Centre.X + offset.X - width / 2);
        Canvas.SetTop(t, Centre.Y + offset.Y - size * 0.68);
        return t;
    }

    /// <summary>
    /// Morph colours. Glass: warm translucent tints over the blurred snapshot. Solid (transparency effects off): opaque.
    /// <paramref name="Base"/> is the opaque disc under everything that casts the shadow.
    /// </summary>
    private sealed record Palette(Brush Base, Brush Disc, Brush Stroke, Brush Wedge, Brush Icon, Brush Label, Brush CentreFill, Brush Accent, Brush OnAccent)
    {
        public static Palette For(bool dark, bool glass)
        {
            var accent = Frozen(MorphGlass.Accent(dark));
            var onAccent = Frozen(MorphGlass.OnAccent(dark));
            return (dark, glass) switch
            {
                (false, true) => new(Hex("#FBF7F4"), Hex("#8CFFF3EA"), Hex("#B3FFFFFF"), Hex("#59FFFFFF"), Hex("#6B4630"), Hex("#24150C"), Hex("#99FFFFFF"), accent, onAccent),
                (true, true) => new(Hex("#2A211C"), Hex("#A61F1510"), Hex("#33FFFFFF"), Hex("#17FFFFFF"), Hex("#E6CDBE"), Hex("#FFFFFF"), Hex("#59000000"), accent, onAccent),
                (false, false) => new(Hex("#FBF7F4"), Hex("#FBF7F4"), Hex("#EADFD8"), Hex("#FFFFFF"), Hex("#6B4630"), Hex("#1A1A1A"), Hex("#F7F2EE"), accent, onAccent),
                (true, false) => new(Hex("#2C2C2C"), Hex("#2C2C2C"), Hex("#454545"), Hex("#383838"), Hex("#C5C5C5"), Hex("#FFFFFF"), Hex("#1C1C1C"), accent, onAccent),
            };
        }

        private static Brush Hex(string hex) => Frozen((Color)ColorConverter.ConvertFromString(hex));

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
