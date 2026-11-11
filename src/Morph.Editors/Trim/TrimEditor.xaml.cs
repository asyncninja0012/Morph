using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Overlay;

namespace Morph.Editors.Trim;

/// <summary>
/// Trim and split (ED-4) for video and sound: a player, a filmstrip (or waveform) timeline with start and end handles and split
/// markers, and Fast cut (streams copied, parts start at a keyframe) or Exact cut (re-encoded). The player plays the Worker's
/// Morph-made copy (<see cref="PlaybackLoader"/>), never the dropped file; until it's ready the cut can already be marked.
/// Mouse: drag the handles and splits, click or drag elsewhere to move the playhead. Keyboard: Space plays, arrows move the
/// playhead 1 s (Shift: 0.1 s), Home/End jump to the start/end, I/O set them, S splits, Delete removes the nearest split, F/E
/// pick the cut. Returns <see cref="Trims"/> parameters; remembers the cut mode.
/// </summary>
public sealed partial class TrimEditor : UserControl, IEditor
{
    private const double Grab = 8; // DIPs from a handle or split that pick it up

    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x8C, 0x28)));
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)));

    private static readonly (string Id, string Label, Key Key)[] ModeList = [("fast", "Fast cut", Key.F), ("exact", "Exact cut", Key.E)];

    private readonly TimelineLoader _timeline;
    private readonly PlaybackLoader _playback;
    private readonly Stack<TrimCut> _undo = new();
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private CancellationTokenSource? _copying;
    private TrimCut _cut = TrimMath.Whole(1);
    private TrimCut? _before; // at the start of a drag
    private Drag _drag;
    private int _dragSplit;
    private double _duration = 1;
    private double _playhead;
    private bool _video;
    private bool _exact;
    private bool _ready; // the copy is open in the player
    private bool _playing;

    public TrimEditor(TimelineLoader timeline, PlaybackLoader playback)
    {
        InitializeComponent();
        (_timeline, _playback) = (timeline, playback);
        foreach (var (id, label, key) in ModeList)
        {
            var chip = new RadioButton { Content = label, Tag = id, GroupName = "TrimMode", ToolTip = $"{label} ({key})", MinWidth = 88, Margin = new Thickness(0, 0, 4, 0) };
            chip.SetResourceReference(StyleProperty, typeof(ToggleButton));
            chip.Checked += (_, _) =>
            {
                _exact = id == "exact";
                Redraw();
            };
            Modes.Children.Add(chip);
        }

        _clock.Tick += (_, _) => Tick();
        Player.MediaOpened += (_, _) => Opened();
        Player.MediaFailed += (_, e) => Unplayable(e.ErrorException?.Message);
        Player.MediaEnded += (_, _) => Pause();
        Timeline.MouseLeftButtonDown += OnDown;
        Timeline.MouseMove += OnMove;
        Timeline.MouseLeftButtonUp += (_, _) => EndDrag();
        Timeline.LostMouseCapture += (_, _) => EndDrag();
        PlayButton.Click += (_, _) => Toggle();
        InButton.Click += (_, _) => Apply(TrimMath.SetIn(_cut, _playhead));
        OutButton.Click += (_, _) => Apply(TrimMath.SetOut(_cut, _playhead, _duration));
        SplitButton.Click += (_, _) => Apply(TrimMath.AddSplit(_cut, _playhead));
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

    private enum Drag { None, Playhead, In, Out, Split }

    public string Title => "Trim";

    public FrameworkElement View => this;

    public bool CanUndo => _undo.Count > 0;

    public event EventHandler? Changed;

    public async Task LoadAsync(string path, JsonObject? settings, CancellationToken ct)
    {
        StopPlayback();
        _undo.Clear();
        _exact = (string?)settings?["mode"] == "exact";
        foreach (var chip in Modes.Children.OfType<RadioButton>())
        {
            chip.IsChecked = (string)chip.Tag == (_exact ? "exact" : "fast");
        }

        // As wide as the shell allows (80% of the work area, at most 720), the picture under what's left of the height.
        _video = FileFormat.FromExtension(System.IO.Path.GetExtension(path)).Kind == FileKind.Video;
        var work = Screens.WorkAreaAtCursor();
        var width = Math.Clamp(work.Width * 0.8 - 60, 360, 720);
        Timeline.Height = _video ? 64 : 96;
        var strip = await _timeline(path, width / Timeline.Height, ct);
        if (strip.Duration <= 0)
        {
            throw new EngineException($"Morph can't tell how long {System.IO.Path.GetFileName(path)} is.");
        }

        var bitmap = BitmapSource.Create(strip.Width, strip.Height, 96, 96, PixelFormats.Bgra32, null, strip.Bgra, strip.Width * 4);
        bitmap.Freeze();
        Strip.Source = bitmap;
        if (_video)
        {
            var aspect = strip.SourceWidth / strip.SourceHeight;
            var height = Math.Min(width / aspect, Math.Max(120, work.Height * 0.8 - 330));
            (Screen.Width, Screen.Height) = (Math.Min(width, height * aspect), height);
        }
        else
        {
            (Screen.Width, Screen.Height) = (width, 40); // room for "getting ready"; nothing to show after
        }

        Timeline.Width = width;
        ModeHint.MaxWidth = Summary.MaxWidth = width;
        (_duration, _cut, _playhead) = (strip.Duration, TrimMath.Whole(strip.Duration), 0);
        ScreenText.Text = "Getting the player ready…";
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
        _ = MakeCopyAsync(path, ct);
    }

    public void Undo()
    {
        _cut = _undo.Pop();
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public JsonObject Parameters() => Trims.ToParameters(TrimMath.Segments(_cut), _exact);

    public JsonObject Settings() => new() { ["mode"] = _exact ? "exact" : "fast" };

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static bool Same(TrimCut a, TrimCut b) => a.In == b.In && a.Out == b.Out && a.Splits.SequenceEqual(b.Splits);

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
        if (!_video)
        {
            Screen.Height = 0;
        }

        Seek(_playhead);
    }

    private void Unplayable(string? reason)
    {
        _ready = false;
        PlayButton.IsEnabled = false;
        ScreenText.Text = $"Morph can't play it here. {reason} You can still mark where to cut.".Replace("  ", " ", StringComparison.Ordinal);
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

        if (_playhead < _cut.In || _playhead >= _cut.Out - 0.05)
        {
            Seek(_cut.In);
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
        _playhead = Math.Clamp(Player.Position.TotalSeconds, 0, _duration);
        Redraw();
    }

    // While playing: follow the player, and stop at the end of what's kept.
    private void Tick()
    {
        _playhead = Math.Clamp(Player.Position.TotalSeconds, 0, _duration);
        if (_playhead >= _cut.Out)
        {
            Pause();
            Seek(_cut.Out);
            return;
        }

        Redraw();
    }

    private void Seek(double at)
    {
        _playhead = Math.Clamp(at, 0, _duration);
        if (_ready && !_playing)
        {
            Player.Position = TimeSpan.FromSeconds(_playhead); // paused + ScrubbingEnabled: shows that frame
        }

        Redraw();
    }

    private void Apply(TrimCut cut)
    {
        if (Same(cut, _cut))
        {
            return;
        }

        _undo.Push(_cut);
        _cut = cut;
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private double X(double seconds) => seconds / _duration * Timeline.Width;

    private double ToTime(double x) => Math.Clamp(x / Timeline.Width * _duration, 0, _duration);

    private (Drag Drag, int Split) Under(double x)
    {
        if (Math.Abs(x - X(_cut.In)) <= Grab)
        {
            return (Drag.In, 0);
        }

        if (Math.Abs(x - X(_cut.Out)) <= Grab)
        {
            return (Drag.Out, 0);
        }

        return TrimMath.Nearest(_cut, ToTime(x)) is { } i && Math.Abs(x - X(_cut.Splits[i])) <= Grab ? (Drag.Split, i) : (Drag.Playhead, 0);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Timeline.Focus();
        var x = e.GetPosition(Timeline).X;
        (_drag, _dragSplit) = Under(x);
        _before = _cut;
        if (_drag == Drag.Playhead)
        {
            Pause();
            Seek(ToTime(x));
        }

        Timeline.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var x = e.GetPosition(Timeline).X;
        if (_drag == Drag.None)
        {
            Timeline.Cursor = Under(x).Drag == Drag.Playhead ? Cursors.Hand : Cursors.SizeWE;
            return;
        }

        var at = ToTime(x);
        switch (_drag)
        {
            case Drag.In:
                _cut = TrimMath.SetIn(_before!, at);
                Seek(_cut.In);
                break;
            case Drag.Out:
                _cut = TrimMath.SetOut(_before!, at, _duration);
                Seek(_cut.Out);
                break;
            case Drag.Split:
                _cut = TrimMath.MoveSplit(_before!, _dragSplit, at);
                Seek(_cut.Splits[_dragSplit]);
                break;
            default:
                Seek(at);
                break;
        }
    }

    private void EndDrag()
    {
        if (_drag == Drag.None)
        {
            return;
        }

        var dragged = _drag != Drag.Playhead && _before is { } before && !Same(before, _cut);
        _drag = Drag.None;
        Timeline.ReleaseMouseCapture();
        if (dragged)
        {
            _undo.Push(_before!);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

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
                Pause();
                Seek(_playhead + (e.Key == Key.Right ? 1 : -1) * (mods == ModifierKeys.Shift ? 0.1 : 1));
                break;
            case Key.Home when mods == ModifierKeys.None:
                Pause();
                Seek(_cut.In);
                break;
            case Key.End when mods == ModifierKeys.None:
                Pause();
                Seek(_cut.Out);
                break;
            case Key.I when mods == ModifierKeys.None:
                Apply(TrimMath.SetIn(_cut, _playhead));
                break;
            case Key.O when mods == ModifierKeys.None:
                Apply(TrimMath.SetOut(_cut, _playhead, _duration));
                break;
            case Key.S when mods == ModifierKeys.None:
                Apply(TrimMath.AddSplit(_cut, _playhead));
                break;
            case Key.Delete or Key.Back when mods == ModifierKeys.None && TrimMath.Nearest(_cut, _playhead) is { } i:
                Apply(TrimMath.RemoveSplit(_cut, i));
                break;
            case Key.F or Key.E when mods == ModifierKeys.None:
                Modes.Children.OfType<RadioButton>().First(c => (string)c.Tag == (e.Key == Key.E ? "exact" : "fast")).IsChecked = true;
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

        Marks.Children.Clear();
        void Add(double left, double width, double height, Brush fill, double top = 0)
        {
            var r = new Rectangle { Width = Math.Max(0, width), Height = height, Fill = fill };
            Canvas.SetLeft(r, left);
            Canvas.SetTop(r, top);
            Marks.Children.Add(r);
        }

        var (left, right) = (X(_cut.In), X(_cut.Out));
        Add(0, left, h, Dim);
        Add(right, w - right, h, Dim);
        Add(left, right - left, 3, Accent);
        Add(left, right - left, 3, Accent, h - 3);
        foreach (var split in _cut.Splits)
        {
            Add(X(split) - 1.5, 3, h, Brushes.White);
        }

        Add(left - 4, 8, h, Accent);
        Add(right - 4, 8, h, Accent);
        Add(X(_playhead) - 1, 2, h, Brushes.White);
        Add(X(_playhead) - 5, 10, 4, Brushes.White);

        TimeText.Text = $"{TrimMath.Format(_playhead)} / {TrimMath.Format(_duration)}";
        var parts = _cut.Splits.Count + 1;
        Summary.Text = parts == 1
            ? $"Keeps {TrimMath.Format(_cut.In)} to {TrimMath.Format(_cut.Out)} ({TrimMath.Format(_cut.Out - _cut.In)} long)."
            : $"Keeps {TrimMath.Format(_cut.In)} to {TrimMath.Format(_cut.Out)} as {parts} files, split at {string.Join(", ", _cut.Splits.Select(TrimMath.Format))}.";
        ModeHint.Text = _exact
            ? "Exact cut: each part starts and ends where you marked. It's re-encoded, so it takes longer."
            : "Fast cut: no quality loss, done in seconds. Each part starts at the nearest keyframe at or before your mark, so it can start a moment early.";
    }
}
