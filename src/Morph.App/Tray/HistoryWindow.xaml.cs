using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Morph.App.Toasts;
using Morph.Core.Jobs;
using Morph.Overlay.Theme;

namespace Morph.App.Tray;

/// <summary>
/// The tray's job history (JB-6): the last 50 drops, newest first, with Cancel while running, Show in folder, and Undo for
/// 10 minutes (JB-5), and Float to park the outputs on the Float shelf (FA-1). Rows are built once per entry and updated in place, so focus and hover survive progress updates.
/// </summary>
public sealed partial class HistoryWindow : Window
{
    private readonly JobHistory _history;
    private readonly Dictionary<JobHistoryEntry, Row> _rows = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) }; // Undo disappears after 10 minutes

    public HistoryWindow(JobHistory history)
    {
        _history = history;
        InitializeComponent();
        var dark = MorphGlass.IsDark();
        MorphTheme.Apply(this, dark, glass: false);
        SourceInitialized += (_, _) => MorphTheme.Apply(this, dark, MorphGlass.TryApplyBackdrop(this, dark));
        _clock.Tick += (_, _) => Refresh();
        Loaded += (_, _) => _clock.Start();
        Closed += (_, _) => _clock.Stop();
        Refresh();
    }

    public event Action<JobHistoryEntry>? UndoClicked;

    public event Action<IReadOnlyList<string>>? FloatClicked;

    /// <summary>Call on the UI thread after the history changes.</summary>
    public void Refresh()
    {
        foreach (var gone in _rows.Keys.Except(_history.Entries).ToList())
        {
            Rows.Children.Remove(_rows[gone].Card);
            _rows.Remove(gone);
        }

        for (var i = 0; i < _history.Entries.Count; i++)
        {
            var entry = _history.Entries[i];
            if (!_rows.TryGetValue(entry, out var row))
            {
                row = new Row(entry, this);
                _rows[entry] = row;
                Rows.Children.Insert(i, row.Card);
            }

            row.Update(_history.CanUndo(entry));
        }

        EmptyText.Visibility = _history.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Status(JobHistoryEntry e) => e.Group.Status switch
    {
        _ when e.UndoError is not null => $"Undo didn't finish: {e.UndoError}",
        _ when e.Undone => "Undone: the output is in the Recycle Bin",
        JobStatus.Running => double.IsNaN(e.Group.Fraction) || e.Group.Fraction <= 0 ? "Working…" : $"Working… {e.Group.Fraction:P0}",
        JobStatus.Succeeded => $"Done at {e.Finished:t}" + (e.Group.Outputs is [var one] ? $" · {Path.GetFileName(one)}" : $" · {e.Group.Outputs.Count} files"),
        JobStatus.Cancelled => "Cancelled",
        _ => e.Group.UserMessage ?? "Morph couldn't finish this.",
    };

    private sealed class Row
    {
        private readonly JobHistoryEntry _entry;
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        private readonly ProgressBar _bar = new() { Height = 4, Margin = new Thickness(0, 6, 0, 0), Maximum = 1 };
        private readonly Button _cancel = new() { Content = "Cancel" };
        private readonly Button _folder = new() { Content = "Show in folder", Margin = new Thickness(8, 0, 0, 0) };
        private readonly Button _undo = new() { Content = "Undo", Margin = new Thickness(8, 0, 0, 0) };
        private readonly Button _float = new() { Content = "Float", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Park the output on the Float shelf" };

        public Row(JobHistoryEntry entry, HistoryWindow window)
        {
            _entry = entry;
            var title = new TextBlock
            {
                Text = $"{entry.Tool} · " + (entry.Inputs is [var one] ? Path.GetFileName(one) : $"{entry.Inputs.Count} files"),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = string.Join(Environment.NewLine, entry.Inputs),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _cancel.Click += (_, _) => entry.Group.Cancel();
            _folder.Click += (_, _) => ToastHost.ShowInFolder(entry.Group.Outputs);
            _undo.Click += (_, _) => window.UndoClicked?.Invoke(entry);
            _float.Click += (_, _) => window.FloatClicked?.Invoke(entry.Group.Outputs);
            System.Windows.Automation.AutomationProperties.SetName(_float, $"Float {title.Text}");
            System.Windows.Automation.AutomationProperties.SetName(_undo, $"Undo {title.Text}");
            System.Windows.Automation.AutomationProperties.SetName(_cancel, $"Cancel {title.Text}");

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(_cancel);
            buttons.Children.Add(_folder);
            buttons.Children.Add(_float);
            buttons.Children.Add(_undo);
            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(_status);
            stack.Children.Add(_bar);
            stack.Children.Add(buttons);
            Card = new Border { Child = stack, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            Card.SetResourceReference(Border.BackgroundProperty, "MorphCardBrush");
            Card.SetResourceReference(Border.BorderBrushProperty, "MorphCardStrokeBrush");
        }

        public Border Card { get; }

        public void Update(bool canUndo)
        {
            var group = _entry.Group;
            var running = group.Status == JobStatus.Running;
            _status.Text = Status(_entry);
            _status.ToolTip = _entry.Group.Outputs.Count > 0 ? string.Join(Environment.NewLine, _entry.Group.Outputs) : null;
            _bar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            _bar.IsIndeterminate = running && (double.IsNaN(group.Fraction) || group.Fraction <= 0);
            _bar.Value = double.IsNaN(group.Fraction) ? 0 : group.Fraction;
            _cancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            _folder.Visibility = !running && !_entry.Undone && group.Outputs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _float.Visibility = _folder.Visibility;
            _undo.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
