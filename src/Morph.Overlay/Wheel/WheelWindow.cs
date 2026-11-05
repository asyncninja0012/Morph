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
    public const double SizeDip = 264; // 240 ring + room for the shadow
    private static readonly Point Centre = new(SizeDip / 2, SizeDip / 2);
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly Canvas _canvas = new() { Width = SizeDip, Height = SizeDip };
    private readonly ScaleTransform _scale = new(1, 1, SizeDip / 2, SizeDip / 2);
    private readonly List<(Path Wedge, TextBlock Icon, TextBlock Label)> _wedges = [];

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

    /// <summary>Item under a drag position (DragOver coordinates; no mouse events arrive during OLE drags).</summary>
    public WheelItem? ItemAt(DragEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        var index = WheelGeometry.HitTest(pos.X - Centre.X, pos.Y - Centre.Y, _items.Count);
        return index is { } i ? _items[i] : null;
    }

    public void Highlight(WheelItem? item)
    {
        int? index = item is null ? null : IndexOf(item);
        if (index == _highlighted)
        {
            return;
        }

        if (_highlighted is { } old)
        {
            Paint(old, highlighted: false);
        }

        if (index is { } i)
        {
            Paint(i, highlighted: true);
        }

        _highlighted = index;
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

    private int? IndexOf(WheelItem item)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], item))
            {
                return i;
            }
        }

        return null;
    }

    private void Paint(int i, bool highlighted)
    {
        var (wedge, icon, label) = _wedges[i];
        var p = _palette;
        wedge.Fill = highlighted ? p.Accent : p.Wedge;
        wedge.Stroke = highlighted ? p.Accent : p.Stroke;
        icon.Foreground = highlighted ? p.OnAccent : p.Icon;
        label.Foreground = highlighted ? p.OnAccent : p.Label;
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
