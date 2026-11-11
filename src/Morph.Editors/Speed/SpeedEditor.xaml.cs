using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Editors.Trim;
using Morph.Overlay;

namespace Morph.Editors.Speed;

/// <summary>
/// Change speed (ED-7) for video and sound: the player plays the Worker's Morph-made copy (<see cref="PlaybackLoader"/>, never the
/// dropped file) at the chosen speed, a position bar, and a speed slider with the stops in <see cref="Speeds.Stops"/>. The speed can
/// be picked before the player is ready. Keyboard: Space plays, ←/→ move 1 s (Shift: 0.1 s) unless a slider has the focus, +/− change
/// the speed. Returns <see cref="Speeds"/> parameters; remembers the speed.
/// </summary>
public sealed partial class SpeedEditor : UserControl, IEditor
{
    private const double ThumbHalf = 10; // the slider's track starts and ends this far in, where the thumb's centre stops

    private readonly PlaybackLoader _playback;
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stack<int> _undo = new();
    private CancellationTokenSource? _copying;
    private double _duration;
    private int _stop = 2; // index into Speeds.Stops: 1×
    private bool _video;
    private bool _ready;
    private bool _playing;
    private bool _following; // the clock is moving the position bar, not the user

    public SpeedEditor(PlaybackLoader playback)
    {
        InitializeComponent();
        _playback = playback;
        _clock.Tick += (_, _) => Follow();
        Player.MediaOpened += (_, _) => Opened();
        Player.MediaFailed += (_, e) => Unplayable(e.ErrorException?.Message);
        Player.MediaEnded += (_, _) =>
        {
            Pause();
            Seek(0);
        };
        PlayButton.Click += (_, _) => Toggle();
        Position.ValueChanged += (_, e) =>
        {
            if (!_following)
            {
                Seek(e.NewValue);
            }
        };
        SpeedSlider.ValueChanged += (_, e) => Pick((int)Math.Round(e.NewValue));
        Ticks.SizeChanged += (_, _) => PlaceTicks();
        KeyDown += OnKey;
        Unloaded += (_, _) => StopPlayback();
        GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus == this)
            {
                SpeedSlider.Focus(); // the shell focuses the editor; the speed is what it's for
            }
        };

        foreach (var speed in Speeds.Stops)
        {
            Ticks.Children.Add(new TextBlock { Text = Speeds.Label(speed), FontSize = 12 });
        }
    }

    public string Title => "Change speed";

    public FrameworkElement View => this;

    public bool CanUndo => _undo.Count > 0;

    public event EventHandler? Changed;

    public Task LoadAsync(string path, JsonObject? settings, CancellationToken ct)
    {
        StopPlayback();
        _undo.Clear();
        var saved = settings?["speed"] is JsonValue v && v.TryGetValue<double>(out var s) ? Speeds.Stops.ToList().IndexOf(s) : -1;
        _stop = saved > 0 && Speeds.Stops[saved] != 1 ? saved : 4; // 2× until something else was used
        SpeedSlider.Value = _stop;

        // As wide as Trim's player; the picture's height is set once the copy says its shape.
        _video = FileFormat.FromExtension(System.IO.Path.GetExtension(path)).Kind == FileKind.Video;
        var width = Math.Clamp(Screens.WorkAreaAtCursor().Width * 0.8 - 60, 360, 720);
        (Screen.Width, Screen.Height) = (width, _video ? width * 9 / 16 : 40);
        Summary.MaxWidth = Hint.MaxWidth = width;
        _duration = 0;
        (Position.Value, Position.Maximum, Position.IsEnabled) = (0, 1, false);
        ScreenText.Text = "Getting the player ready…";
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
        _ = MakeCopyAsync(path, ct);
        return Task.CompletedTask;
    }

    public void Undo()
    {
        _stop = _undo.Pop();
        SpeedSlider.Value = _stop; // Pick sees no change, so nothing is pushed
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public JsonObject Parameters() => Speeds.ToParameters(Speeds.Stops[_stop]);

    public JsonObject Settings() => new() { ["speed"] = Speeds.Stops[_stop] };

    private void Pick(int stop)
    {
        stop = Math.Clamp(stop, 0, Speeds.Stops.Count - 1);
        if (stop == _stop)
        {
            return;
        }

        _undo.Push(_stop);
        _stop = stop;
        SpeedSlider.Value = stop;
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
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
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        (Position.Maximum, Position.IsEnabled, PlayButton.IsEnabled) = (Math.Max(_duration, 0.1), true, true);
        ScreenText.Text = "";
        if (_video && Player.NaturalVideoWidth > 0 && Player.NaturalVideoHeight > 0)
        {
            var aspect = (double)Player.NaturalVideoWidth / Player.NaturalVideoHeight;
            var height = Math.Min(Screen.Width / aspect, Math.Max(120, Screens.WorkAreaAtCursor().Height * 0.8 - 360));
            (Screen.Width, Screen.Height) = (Math.Min(Screen.Width, height * aspect), height);
        }
        else if (!_video)
        {
            Screen.Height = 0;
        }

        Redraw();
    }

    private void Unplayable(string? reason)
    {
        _ready = false;
        PlayButton.IsEnabled = Position.IsEnabled = false;
        ScreenText.Text = $"Morph can't play it here. {reason} You can still pick the speed.".Replace("  ", " ", StringComparison.Ordinal);
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

        Player.SpeedRatio = Speeds.Stops[_stop];
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
        Follow();
    }

    private void Seek(double at)
    {
        if (!_ready)
        {
            return;
        }

        Player.Position = TimeSpan.FromSeconds(Math.Clamp(at, 0, _duration));
        Follow();
    }

    private void Follow()
    {
        _following = true;
        Position.Value = Math.Clamp(Player.Position.TotalSeconds, 0, Position.Maximum);
        _following = false;
        TimeText.Text = $"{TrimMath.Format(Player.Position.TotalSeconds)} / {TrimMath.Format(_duration)}";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var onControl = e.OriginalSource is ButtonBase or RangeBase;
        switch (e.Key)
        {
            case Key.Space when mods == ModifierKeys.None && e.OriginalSource is not ButtonBase:
                Toggle();
                break;
            case Key.Left or Key.Right when !onControl && (mods & ~ModifierKeys.Shift) == ModifierKeys.None:
                Seek(Player.Position.TotalSeconds + (e.Key == Key.Right ? 1 : -1) * (mods == ModifierKeys.Shift ? 0.1 : 1));
                break;
            case Key.OemPlus or Key.Add when (mods & ~ModifierKeys.Shift) == ModifierKeys.None:
                Pick(_stop + 1);
                break;
            case Key.OemMinus or Key.Subtract when mods == ModifierKeys.None:
                Pick(_stop - 1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void PlaceTicks()
    {
        var span = Ticks.ActualWidth - 2 * ThumbHalf;
        for (var i = 0; i < Ticks.Children.Count; i++)
        {
            var label = (TextBlock)Ticks.Children[i];
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, ThumbHalf + span * i / (Ticks.Children.Count - 1) - label.DesiredSize.Width / 2);
        }
    }

    private void Redraw()
    {
        var speed = Speeds.Stops[_stop];
        SpeedText.Text = Speeds.Label(speed);
        if (_playing)
        {
            Player.SpeedRatio = speed;
        }

        var how = speed > 1 ? $"Plays {Speeds.Label(speed)} faster" : speed < 1 ? $"Plays at {Speeds.Label(speed)}, slower" : "Same speed: nothing to change";
        Summary.Text = _duration > 0 && speed != 1 ? $"{how}: {TrimMath.Format(_duration / speed)} long instead of {TrimMath.Format(_duration)}." : $"{how}.";
        TimeText.Text = $"{TrimMath.Format(_ready ? Player.Position.TotalSeconds : 0)} / {TrimMath.Format(_duration)}";
    }
}
