using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Morph.Core.Jobs;
using Morph.Overlay.Theme;

namespace Morph.App.Tools;

/// <summary>
/// The Keep / Discard preview after Remove background (IT-4). The PNGs are already saved next to the originals (output rule);
/// Keep, Enter, Esc or closing keeps them, Discard raises <see cref="DiscardClicked"/> to send them all to the Recycle Bin.
/// Several results page with ←/→. Pictures come from the Worker's preview job, never decoded here.
/// </summary>
public sealed partial class BackgroundWindow : Window
{
    private readonly IReadOnlyList<string> _outputs;
    private readonly Func<string, CancellationToken, Task<PreviewImage>> _preview;
    private readonly CancellationTokenSource _closing = new();
    private int _index;

    public BackgroundWindow(IReadOnlyList<string> outputs, Func<string, CancellationToken, Task<PreviewImage>> preview)
    {
        _outputs = outputs;
        _preview = preview;
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));

        Paging.Visibility = outputs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = outputs.Count > 1
            ? $"Saved {outputs.Count} PNGs with see-through backgrounds. Discard sends them all to the Recycle Bin."
            : $"Saved as {Path.GetFileName(outputs[0])}. Discard sends it to the Recycle Bin.";
        PreviousButton.Click += (_, _) => Show(_index - 1);
        NextButton.Click += (_, _) => Show(_index + 1);
        KeepButton.Click += (_, _) => Close();
        DiscardButton.Click += (_, _) =>
        {
            DiscardClicked?.Invoke();
            Close();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right && outputs.Count > 1)
            {
                Show(_index + (e.Key == Key.Left ? -1 : 1));
                e.Handled = true;
            }
        };
        Closed += (_, _) => _closing.Cancel();
        Loaded += (_, _) =>
        {
            KeepButton.Focus();
            Show(0);
        };
    }

    public event Action? DiscardClicked;

    private async void Show(int index)
    {
        _index = (index + _outputs.Count) % _outputs.Count;
        var path = _outputs[_index];
        NameText.Text = Path.GetFileName(path);
        CountText.Text = $"{_index + 1} of {_outputs.Count}";
        Picture.Source = null;
        Loading.Visibility = Visibility.Visible;
        try
        {
            var preview = await _preview(path, _closing.Token);
            if (_outputs[_index] != path)
            {
                return; // paged on meanwhile
            }

            Picture.Source = BitmapSource.Create(preview.Width, preview.Height, 96, 96, PixelFormats.Bgra32, null, preview.Bgra, preview.Width * 4);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (EngineException ex)
        {
            HintText.Text = ex.UserMessage;
        }

        Loading.Visibility = Visibility.Collapsed;
    }
}
