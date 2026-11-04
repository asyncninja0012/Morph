using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

using Morph.Core.Jobs;

namespace Morph.App.Toasts;

/// <summary>
/// One toast per drop (JB-2): progress and Cancel while running, then Show in folder, or the reason and Copy details.
/// Toasts stack up from the bottom-right of the primary work area, newest at the bottom.
/// </summary>
public sealed class ToastHost
{
    private static readonly TimeSpan SuccessLinger = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CancelledLinger = TimeSpan.FromSeconds(3);

    private readonly Dispatcher _dispatcher;
    private readonly List<ToastWindow> _windows = [];
    private readonly List<(JobGroup Group, Action Refresh)> _groups = [];

    public ToastHost(JobQueue queue, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        queue.Updated += u => _dispatcher.BeginInvoke(() => OnUpdate(u));
    }

    /// <summary>Call on the UI thread right after enqueueing, before any update for these jobs is dispatched.</summary>
    public void Track(IReadOnlyList<(JobRequest Request, JobHandle Handle)> jobs, string title, string target)
    {
        var group = new JobGroup(jobs);
        var window = Open();
        window.CancelClicked += group.Cancel;
        window.ShowFolderClicked += () => ShowInFolder(group.Outputs);
        window.CopyDetailsClicked += () => CopyDetails(group.Details);

        void Refresh()
        {
            window.Render(group.Status switch
            {
                JobStatus.Running => new ToastView("", title, $"Converting to {target}…", group.Fraction, CanCancel: true),
                JobStatus.Succeeded => new ToastView("", title,
                    group.Outputs is [var one] ? $"Saved {Path.GetFileName(one)}" : $"Saved {group.Outputs.Count} files", null, CanShowFolder: true),
                JobStatus.Cancelled => new ToastView("", title, "Cancelled. Nothing was saved.", null),
                _ => new ToastView("", title, group.UserMessage ?? "Morph couldn't finish this.", null,
                    CanShowFolder: group.Outputs.Count > 0, CanCopyDetails: group.Details is not null),
            });

            if (group.Status is JobStatus.Succeeded or JobStatus.Cancelled)
            {
                CloseLater(window, group.Status == JobStatus.Succeeded ? SuccessLinger : CancelledLinger);
            }
        }

        _groups.Add((group, Refresh));
        window.Closed += (_, _) => _groups.RemoveAll(g => g.Group == group); // closing hides the toast; the jobs keep running
        Refresh();
    }

    /// <summary>A toast with nothing running, e.g. every dropped file was already in the target format.</summary>
    public void Info(string title, string message)
    {
        var window = Open();
        window.Render(new ToastView("", title, message, null));
        CloseLater(window, CancelledLinger);
    }

    private void OnUpdate(JobUpdate update)
    {
        foreach (var (group, refresh) in _groups.ToList())
        {
            if (group.Apply(update))
            {
                refresh();
            }
        }
    }

    private ToastWindow Open()
    {
        var window = new ToastWindow();
        window.CloseClicked += window.Close;
        window.SizeChanged += (_, _) => Arrange();
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            Arrange();
        };
        _windows.Add(window);
        Arrange();
        window.Show();
        return window;
    }

    private void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom;
        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            var w = _windows[i];
            var height = double.IsNaN(w.ActualHeight) || w.ActualHeight == 0 ? 120 : w.ActualHeight;
            w.Left = area.Right - w.Width;
            w.Top = bottom - height;
            bottom -= height - 12; // cards overlap their transparent shadow margins
        }
    }

    // Stays while the pointer is over it, so a toast never vanishes under the user's hand.
    private static void CloseLater(ToastWindow window, TimeSpan after)
    {
        var timer = new DispatcherTimer { Interval = after };
        timer.Tick += (_, _) =>
        {
            if (!window.IsMouseOver)
            {
                timer.Stop();
                window.Close();
            }
        };
        window.Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    // shortcut: selects the first output only; SHOpenFolderAndSelectItems can select them all if batches need it.
    private static void ShowInFolder(IReadOnlyList<string> outputs)
    {
        if (outputs is [var first, ..])
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = false })?.Dispose();
        }
    }

    private static void CopyDetails(string? details)
    {
        try
        {
            Clipboard.SetText(details ?? "");
        }
        catch (COMException ex)
        {
            Console.Error.WriteLine($"Copy details failed: {ex.Message}"); // clipboard held by another app
        }
    }
}
