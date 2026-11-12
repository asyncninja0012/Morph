using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Microsoft.Extensions.Logging;

using Morph.Overlay.Theme;

namespace Morph.App.Onboarding;

/// <summary>
/// First-run demo (PRD 6.9): drag the bundled sample picture out of this window, hold the Convert key until the wheel opens,
/// drop on PNG; the PNG appearing completes it. Then one page on the other ways in. Skip works at any time; no sign-up.
/// The picture is Morph's own (embedded), copied to <paramref name="demoFolder"/>; the PNG lands wherever results go.
/// </summary>
public sealed partial class OnboardingWindow : Window
{
    private readonly string _sample;
    private readonly string _resultFolder;
    private readonly string _convertKey;
    private readonly ILogger _log;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(3) };
    private FileSystemWatcher? _watcher;
    private Point? _press;
    private string? _result;

    /// <param name="resultFolder">Where Morph saves results: the demo folder, or the folder chosen in Settings.</param>
    /// <param name="keys">The keys that open the Convert, Tools and Actions wheels.</param>
    public OnboardingWindow(string demoFolder, string? resultFolder, (string Convert, string Tools, string Actions) keys, ILogger log)
    {
        _sample = Path.Combine(demoFolder, "sample.jpg");
        _resultFolder = resultFolder ?? demoFolder;
        _convertKey = keys.Convert;
        _log = log;
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));

        Step1.Text = "1.  Start dragging the picture and keep the mouse button down.";
        Step2.Text = $"2.  Hold {keys.Convert} until the wheel opens around the pointer.";
        Step3.Text = "3.  Drop it on PNG.";
        ToolsText.Text = $"Hold {keys.Tools} while dragging for tools: compress, resize, crop, annotate, remove the background, trim a video and more.";
        ActionsText.Text = $"Hold {keys.Actions} while dragging for actions: copy or move to a recent folder, copy the path, share, or park files on the Float shelf.";
        Highlight(1);

        using (var png = typeof(OnboardingWindow).Assembly.GetManifestResourceStream("sample.jpg")!)
        {
            SampleImage.Source = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); // Morph's own picture, not a dropped file
        }

        Tile.PreviewMouseLeftButtonDown += (_, e) => _press = e.GetPosition(Tile);
        Tile.PreviewMouseLeftButtonUp += (_, _) => _press = null;
        Tile.PreviewMouseMove += OnTileMouseMove;
        ShowLink.Click += (_, _) => ShowInFolder(_result ?? _sample);
        _retry.Tick += (_, _) =>
        {
            _retry.Stop();
            if (_result is null)
            {
                Highlight(1);
                StatusText.Text = $"No PNG yet. Start dragging, then hold {_convertKey} until the wheel appears and drop on PNG. Let go of the picture anywhere else to try again.";
            }
        };
        SkipButton.Click += (_, _) => Close();
        NextButton.Click += (_, _) =>
        {
            if (MorePage.Visibility == Visibility.Visible)
            {
                Close();
                return;
            }

            DemoPage.Visibility = Visibility.Collapsed;
            MorePage.Visibility = Visibility.Visible;
            SkipButton.Visibility = Visibility.Collapsed;
            NextButton.Content = "Done";
            NextButton.Style = (Style)FindResource("AccentButtonStyle");
            NextButton.Focus();
        };
        Loaded += (_, _) => PrepareAsync(demoFolder);
        Closed += (_, _) =>
        {
            _retry.Stop();
            _watcher?.Dispose();
        };
    }

    // Copies the picture out, clears an old demo PNG and watches for the new one; file work stays off the UI thread.
    private async void PrepareAsync(string demoFolder)
    {
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(demoFolder);
                using (var source = typeof(OnboardingWindow).Assembly.GetManifestResourceStream("sample.jpg")!)
                using (var file = File.Create(_sample))
                {
                    source.CopyTo(file);
                }

                foreach (var old in Directory.EnumerateFiles(demoFolder, "sample*.png"))
                {
                    File.Delete(old); // the demo folder is Morph's own
                }
            });
            _watcher = new FileSystemWatcher(_resultFolder, "sample*.png") { EnableRaisingEvents = true };
            _watcher.Created += (_, e) => Dispatcher.BeginInvoke(() => Succeeded(e.FullPath));
            Tile.Focus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.LogWarning(ex, "Onboarding: couldn't prepare the demo in {Folder}", demoFolder);
            StatusText.Text = "The demo picture couldn't be set up here. Try Morph on any picture in File Explorer instead.";
        }
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_press is not { } start || e.LeftButton != MouseButtonState.Pressed || _watcher is null)
        {
            return;
        }

        var at = e.GetPosition(Tile);
        if (Math.Abs(at.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(at.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _press = null;
        _retry.Stop();
        Highlight(2);
        StatusText.Text = $"Now hold {_convertKey}…";
        var effect = DragDrop.DoDragDrop(Tile, new DataObject(DataFormats.FileDrop, new[] { _sample }), DragDropEffects.Copy);
        _log.LogInformation("Onboarding: drag ended ({Effect})", effect);
        if (_result is null)
        {
            Highlight(3);
            StatusText.Text = "Converting…";
            _retry.Start(); // the PNG shows up a moment after a drop on the wheel
        }
    }

    private void Succeeded(string png)
    {
        if (_result is not null)
        {
            return;
        }

        _result = png;
        _retry.Stop();
        Highlight(4);
        StatusText.Text = $"Done: {Path.GetFileName(png)} was saved next to the picture. Morph never changes the original.";
        ShowLink.Inlines.Clear();
        ShowLink.Inlines.Add("Show the PNG");
        NextButton.Style = (Style)FindResource("AccentButtonStyle");
        NextButton.Focus();
        _log.LogInformation("Onboarding: demo done ({Png})", png);
    }

    // The current step in Morph orange; finished ones dimmed.
    private void Highlight(int step)
    {
        var steps = new[] { Step1, Step2, Step3 };
        for (var i = 0; i < steps.Length; i++)
        {
            steps[i].FontWeight = i + 1 == step ? FontWeights.SemiBold : FontWeights.Normal;
            steps[i].Opacity = i + 1 < step ? 0.6 : 1;
            steps[i].Foreground = (System.Windows.Media.Brush)FindResource(i + 1 == step ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        }
    }

    private static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
}
