using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;

using Microsoft.Extensions.Logging;

using Morph.Core.Jobs;
using Morph.Overlay;
using Morph.Overlay.Theme;

namespace Morph.Editors;

/// <param name="Files">Each file with its job parameters, in drop order.</param>
/// <param name="Settings">The editor's settings after the last file, to remember for next time (ED-6).</param>
public sealed record EditResult(IReadOnlyList<EditedFile> Files, JsonObject Settings);

/// <summary>
/// The window every editor opens in (ED-6): file name (k of n), the editor, and Undo · Cancel · Done.
/// Enter = Done, Esc = Cancel, Ctrl+Z = Undo. Several files: "Same for all" or "One by one" (Done reads Next until the last).
/// Opens on the cursor's monitor, sized to the editor, at most 80% of that monitor's work area.
/// </summary>
public sealed partial class EditorShell : Window
{
    private readonly IEditor _editor;
    private readonly EditSession _session;
    private readonly ILogger _log;
    private readonly TaskCompletionSource<EditResult?> _result = new();
    private readonly CancellationTokenSource _closing = new();
    private JsonObject? _settings;
    private bool _shown; // the current file loaded; false while loading or after it failed
    private bool _failed; // the current file couldn't be shown: Done reads Skip

    private EditorShell(IEditor editor, IReadOnlyList<string> files, JsonObject? lastSettings, ILogger log)
    {
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));

        _editor = editor;
        _session = new EditSession(files);
        _settings = lastSettings;
        _log = log;

        Title = editor.Title;
        var work = Screens.WorkAreaAtCursor();
        MaxWidth = work.Width * 0.8;
        MaxHeight = work.Height * 0.8;
        EditorHost.Content = editor.View;
        ModePanel.Visibility = files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AllButton.Checked += (_, _) => SetStepThrough(false);
        StepButton.Checked += (_, _) => SetStepThrough(true);

        editor.Changed += (_, _) => UndoButton.IsEnabled = _shown && editor.CanUndo;
        UndoButton.Click += (_, _) => Undo();
        DoneButton.Click += (_, _) => Next();
        CancelButton.Click += (_, _) => Close(); // IsCancel closes only a ShowDialog window
        KeyDown += (_, e) =>
        {
            // A text box in the editor handles its own Ctrl+Z first.
            if (!e.Handled && e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Undo();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => ShowCurrent();
        // The editor grows once its file loads; keep the window centred where it opened (until the user resizes it).
        SizeChanged += (_, e) =>
        {
            if (IsLoaded && SizeToContent != SizeToContent.Manual && e.PreviousSize.Width > 0)
            {
                Left -= (e.NewSize.Width - e.PreviousSize.Width) / 2;
                Top -= (e.NewSize.Height - e.PreviousSize.Height) / 2;
            }
        };
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _result.TrySetResult(null);
        };
    }

    /// <summary>Opens the shell for a drop. Returns each file's parameters, or null if cancelled or no file could be shown.</summary>
    public static Task<EditResult?> EditAsync(IEditor editor, IReadOnlyList<string> files, JsonObject? lastSettings, ILogger log)
    {
        ArgumentOutOfRangeException.ThrowIfZero(files.Count);
        var shell = new EditorShell(editor, files, lastSettings, log);
        shell.Show();
        shell.Activate();
        return shell._result.Task;
    }

    private void SetStepThrough(bool stepThrough)
    {
        _session.StepThrough = stepThrough;
        ShowHeader();
    }

    private void Undo()
    {
        if (_shown && _editor.CanUndo)
        {
            _editor.Undo();
        }
    }

    private void Next()
    {
        IReadOnlyList<EditedFile>? finished;
        if (_shown)
        {
            _settings = _editor.Settings();
            finished = _session.Done(_editor.Parameters());
        }
        else
        {
            finished = _session.Skip();
        }

        if (finished is not null)
        {
            _result.TrySetResult(finished.Count > 0 ? new EditResult(finished, _settings!) : null);
            Close();
            return;
        }

        ModePanel.IsEnabled = false;
        ShowCurrent();
    }

    private async void ShowCurrent()
    {
        _shown = _failed = false;
        DoneButton.IsEnabled = UndoButton.IsEnabled = false;
        EditorHost.Visibility = Visibility.Visible;
        MessageText.Visibility = Visibility.Collapsed;
        // While the next file loads, the editor still shows the last one: dim it and take no input there.
        (EditorHost.IsEnabled, EditorHost.Opacity) = (false, 0.4);
        CancelButton.Focus(); // so Esc still works
        ShowHeader();
        var file = _session.Current;
        try
        {
            await _editor.LoadAsync(file, _settings, _closing.Token);
            _shown = true;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        catch (EngineException ex)
        {
            _log.LogWarning(ex, "{Editor} couldn't show {File}: {Details}", _editor.Title, file, ex.Details);
            _failed = true;
            EditorHost.Visibility = Visibility.Hidden; // keeps its size, so the window doesn't jump
            MessageText.Text = $"Morph couldn't open {Path.GetFileName(file)}. {ex.Message}";
            MessageText.Visibility = Visibility.Visible;
        }

        (EditorHost.IsEnabled, EditorHost.Opacity) = (true, 1);
        ShowHeader();
        DoneButton.IsEnabled = true;
        UndoButton.IsEnabled = _shown && _editor.CanUndo;
        // Something must hold focus, or Enter and Esc go nowhere.
        if (!_shown || !_editor.View.Focus())
        {
            DoneButton.Focus();
        }
    }

    private void ShowHeader()
    {
        var name = Path.GetFileName(_session.Current);
        var left = _session.Count - _session.Number;
        FileText.Text = _session.Count == 1 ? name
            : _session.StepThrough ? $"{name} · {_session.Number} of {_session.Count}"
            : left > 0 ? $"{name} and {left} more" : name;
        FileText.ToolTip = _session.Current;
        DoneButton.Content = _failed ? "Skip" : _session.IsLast ? "Done" : "Next";
    }
}
