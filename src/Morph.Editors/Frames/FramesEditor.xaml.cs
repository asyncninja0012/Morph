using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

using Morph.Core.Jobs;
using Morph.Overlay;

namespace Morph.Editors.Frames;

/// <summary>
/// Save frames (ED-8) for video: the player (the Worker's Morph-made copy, <see cref="PlaybackLoader"/>, never the dropped file), a
/// filmstrip timeline, one-frame steps (1 ÷ the frame rate the timeline preview reports), and "Add frame", which collects frames as
/// thumbnails with their time (click one to go back to it, × removes it). Done saves each as a full-size PNG; with none added, the
/// frame on screen. Keyboard: Space plays, ←/→ one frame (Shift: 1 s), Home/End, A adds, Delete removes the frame at the playhead
/// (else the newest). Returns <see cref="Core.Jobs.Frames"/> parameters; remembers nothing.
/// </summary>
public sealed partial class FramesEditor : UserControl, IEditor
{
    private const double ThumbWidth = 112;

    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x8C, 0x28)));

    private readonly TimelineLoader _timeline;
    private readonly PlaybackLoader _playback;
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly List<(int Frame, ImageSource? Picture)> _frames = [];
    private readonly Stack<List<(int Frame, ImageSource? Picture)>> _undo = new();
    private CancellationTokenSource? _copying;
    private double _duration = 1;
    private double _fps = 30;
    private int _frame;
    private bool _ready;
    private bool _playing;
    private bool _dragging;

    public FramesEditor(TimelineLoader timeline, PlaybackLoader playback)
    {
        InitializeComponent();
        (_timeline, _playback) = (timeline, playback);
        _clock.Tick += (_, _) => Follow();
        Player.MediaOpened += (_, _) => Opened();
        Player.MediaFailed += (_, e) => Unplayable(e.ErrorException?.Message);
        Player.MediaEnded += (_, _) => Pause();
        PlayButton.Click += (_, _) => Toggle();
        BackButton.Click += (_, _) => Step(-1);
        ForwardButton.Click += (_, _) => Step(1);
        AddButton.Click += (_, _) => AddAsync();
        Timeline.MouseLeftButtonDown += (_, e) =>
        {
            Timeline.Focus();
            Pause();
            _dragging = true;
            Timeline.CaptureMouse();
            Go(FrameMath.At(e.GetPosition(Timeline).X / Timeline.Width * _duration, _fps));
            e.Handled = true;
        };
        Timeline.MouseMove += (_, e) =>
        {
            if (_dragging)
            {
                Go(FrameMath.At(e.GetPosition(Timeline).X / Timeline.Width * _duration, _fps));
            }
        };
        Timeline.MouseLeftButtonUp += (_, _) => Timeline.ReleaseMouseCapture();
        Timeline.LostMouseCapture += (_, _) => _dragging = false;
        KeyDown += OnKey;
        Unloaded += (_, _) => StopPlayback();
        GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus == this)
            {
                Timeline.Focus(); // the shell focuses the editor; the timeline is what the keys work on
            }
        };
    }

    public string Title => "Save frames";

    public FrameworkElement View => this;

    public bool CanUndo => _undo.Count > 0;

    public event EventHandler? Changed;

    public async Task LoadAsync(string path, JsonObject? settings, CancellationToken ct)
    {
        StopPlayback();
        _undo.Clear();
        _frames.Clear();

        // Sized like Trim's player: as wide as the shell allows, the picture under what's left of the height.
        var work = Screens.WorkAreaAtCursor();
        var width = Math.Clamp(work.Width * 0.8 - 60, 360, 720);
        var strip = await _timeline(path, width / Timeline.Height, ct);
        if (strip.Duration <= 0)
        {
            throw new EngineException($"Morph can't tell how long {System.IO.Path.GetFileName(path)} is.");
        }

        var bitmap = BitmapSource.Create(strip.Width, strip.Height, 96, 96, PixelFormats.Bgra32, null, strip.Bgra, strip.Width * 4);
        bitmap.Freeze();
        Strip.Source = bitmap;
        var aspect = strip.SourceWidth / strip.SourceHeight;
        var height = Math.Min(width / aspect, Math.Max(120, work.Height * 0.8 - 430));
        (Screen.Width, Screen.Height) = (Math.Min(width, height * aspect), height);
        Timeline.Width = TrayScroller.MaxWidth = Summary.MaxWidth = width;
        TrayScroller.Height = ThumbWidth / aspect + 44;
        (_duration, _fps, _frame) = (strip.Duration, strip.FrameRate > 0 ? strip.FrameRate : 30, 0);
        ScreenText.Text = "Getting the player ready…";
        Collected();
        Changed?.Invoke(this, EventArgs.Empty);
        _ = MakeCopyAsync(path, ct);
    }

    public void Undo()
    {
        var before = _undo.Pop();
        _frames.Clear();
        _frames.AddRange(before);
        Collected();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public JsonObject Parameters() =>
        Morph.Core.Jobs.Frames.ToParameters(_frames.Count > 0 ? _frames.Select(f => FrameMath.Start(f.Frame, _fps)) : [FrameMath.Start(_frame, _fps)]);

    public JsonObject Settings() => [];

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private async Task MakeCopyAsync(string path, CancellationToken ct)
    {
        var copying = _copying = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new Progress<double>(f =>
        {
            if (_copying == copying && !_ready)
            {
                ScreenText.Text = $"Getting the player ready… {f:P0}";
            }
        });
        try
        {
            var copy = await _playback(path, progress, copying.Token);
            if (_copying == copying)
            {
                Player.Source = new Uri(copy);
                Player.Pause(); // opens it (LoadedBehavior Manual) and shows the first frame
            }
        }
        catch (OperationCanceledException) when (copying.IsCancellationRequested)
        {
        }
        catch (EngineException ex)
        {
            if (_copying == copying)
            {
                Unplayable(ex.Message);
            }
        }
    }

    private void Opened()
    {
        _ready = true;
        PlayButton.IsEnabled = true;
        ScreenText.Text = "";
        Go(_frame);
    }

    private void Unplayable(string? reason)
    {
        _ready = false;
        PlayButton.IsEnabled = false;
        ScreenText.Text = $"Morph can't play it here. {reason} You can still pick frames on the timeline.".Replace("  ", " ", StringComparison.Ordinal);
    }

    private void StopPlayback()
    {
        _copying?.Cancel();
        _copying = null;
        _clock.Stop();
        (_ready, _playing) = (false, false);
        Player.Close();
        Player.Source = null;
        (PlayButton.IsEnabled, PlayButton.Content) = (false, "Play");
    }

    private void Toggle()
    {
        if (!_ready)
        {
            return;
        }

        if (_playing)
        {
            Pause();
            return;
        }

        if (_frame >= FrameMath.Last(_duration, _fps))
        {
            Go(0);
        }

        Player.Play();
        (_playing, PlayButton.Content) = (true, "Pause");
        _clock.Start();
    }

    private void Pause()
    {
        if (!_playing)
        {
            return;
        }

        Player.Pause();
        _clock.Stop();
        (_playing, PlayButton.Content) = (false, "Play");
        Go(FrameMath.At(Player.Position.TotalSeconds, _fps)); // settle on a whole frame
    }

    private void Follow()
    {
        _frame = Math.Min(FrameMath.At(Player.Position.TotalSeconds, _fps), FrameMath.Last(_duration, _fps));
        Redraw();
    }

    private void Step(int frames)
    {
        Pause();
        Go(_frame + frames);
    }

    // Paused, the player shows the frame at its position; a quarter frame in keeps rounding from showing the one before.
    private void Go(int frame)
    {
        _frame = Math.Clamp(frame, 0, FrameMath.Last(_duration, _fps));
        if (_ready && !_playing)
        {
            Player.Position = TimeSpan.FromSeconds(FrameMath.Start(_frame, _fps) + 0.25 / _fps);
        }

        Redraw();
    }

    // The thumbnail is the player's own picture of the Morph-made copy, taken once the paused player has drawn the frame.
    private async void AddAsync()
    {
        Pause();
        var frame = _frame;
        if (_frames.Any(f => f.Frame == frame))
        {
            return;
        }

        ImageSource? picture = null;
        if (_ready)
        {
            await Task.Delay(150);
            picture = Snapshot();
        }

        Remember();
        _frames.Add((frame, picture));
        Collected();
        TrayScroller.ScrollToRightEnd();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private BitmapSource? Snapshot()
    {
        if (Player.ActualWidth < 1 || Player.ActualHeight < 1)
        {
            return null;
        }

        var scale = ThumbWidth / Player.ActualWidth;
        var target = new RenderTargetBitmap((int)Math.Ceiling(ThumbWidth), (int)Math.Ceiling(Player.ActualHeight * scale), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(Player), null, new Rect(0, 0, ThumbWidth, Player.ActualHeight * scale));
        }

        target.Render(visual);
        target.Freeze();
        return target;
    }

    private void Remove(int frame)
    {
        Remember();
        _frames.RemoveAll(f => f.Frame == frame);
        Collected();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Remember() => _undo.Push([.. _frames]);

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var onButton = e.OriginalSource is ButtonBase;
        switch (e.Key)
        {
            case Key.Space when mods == ModifierKeys.None && !onButton:
                Toggle();
                break;
            case Key.Left or Key.Right when !onButton && (mods & ~ModifierKeys.Shift) == ModifierKeys.None:
                var direction = e.Key == Key.Right ? 1 : -1;
                Step(mods == ModifierKeys.Shift ? direction * (int)Math.Round(_fps) : direction);
                break;
            case Key.Home when mods == ModifierKeys.None:
                Step(int.MinValue / 2);
                break;
            case Key.End when mods == ModifierKeys.None:
                Step(int.MaxValue / 2);
                break;
            case Key.A when mods == ModifierKeys.None:
                AddAsync();
                break;
            case Key.Delete or Key.Back when mods == ModifierKeys.None && _frames.Count > 0:
                Remove(_frames.Any(f => f.Frame == _frame) ? _frame : _frames[^1].Frame);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Redraw()
    {
        var (w, h) = (Timeline.Width, Timeline.Height);
        if (double.IsNaN(w))
        {
            return; // not loaded yet
        }

        double X(int frame) => FrameMath.Start(frame, _fps) / _duration * w;
        void Add(double left, double width, double height, Brush fill, double top = 0)
        {
            var r = new Rectangle { Width = Math.Max(0, width), Height = height, Fill = fill };
            Canvas.SetLeft(r, left);
            Canvas.SetTop(r, top);
            Marks.Children.Add(r);
        }

        Marks.Children.Clear();
        foreach (var (frame, _) in _frames)
        {
            Add(X(frame) - 1.5, 3, h, Accent);
        }

        Add(X(_frame) - 1, 2, h, Brushes.White);
        Add(X(_frame) - 5, 10, 4, Brushes.White);
        var t = FrameMath.Start(_frame, _fps);
        TimeText.Text = $"{FrameMath.Format(t)} / {FrameMath.Format(_duration)} · frame {_frame + 1:N0}";
        AddButton.IsEnabled = !_frames.Any(f => f.Frame == _frame);
    }

    // The thumbnails, in time order; rebuilt only when the list changes (Redraw runs on every playback tick).
    private void Collected()
    {
        Tray.Children.Clear();
        foreach (var (frame, picture) in _frames.OrderBy(f => f.Frame))
        {
            Tray.Children.Add(Thumb(frame, picture));
        }

        Redraw();
        Summary.Text = _frames.Count switch
        {
            0 => "Add the frames you want (A). With none added, Done saves the frame on screen as a full-size PNG.",
            1 => "Done saves 1 frame as a full-size PNG next to the video.",
            var n => $"Done saves {n} frames as full-size PNGs next to the video.",
        };
    }

    private Border Thumb(int frame, ImageSource? picture)
    {
        var time = FrameMath.Format(FrameMath.Start(frame, _fps));
        var image = new Image { Source = picture, Width = ThumbWidth, Stretch = Stretch.Uniform };
        var label = new TextBlock { Text = time, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        var remove = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "Remove this frame",
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove the frame at {time}");
        remove.Click += (_, _) => Remove(frame);
        var footer = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(remove, Dock.Right);
        footer.Children.Add(remove);
        footer.Children.Add(label);
        var card = new Border
        {
            Child = new StackPanel { Children = { picture is null ? new Border { Width = ThumbWidth, Height = ThumbWidth * 9 / 16, Background = Brushes.Black } : image, footer } },
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = $"Frame {frame + 1:N0} at {time}. Click to go to it.",
        };
        card.SetResourceReference(Border.BackgroundProperty, "MorphCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "MorphCardStrokeBrush");

        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject d || !IsInButton(d))
            {
                Step(frame - _frame);
            }
        };
        return card;
    }

    private static bool IsInButton(DependencyObject d)
    {
        for (var node = d; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }
}
