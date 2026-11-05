using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Extensions.Logging;

using Morph.Core.Jobs;

namespace Morph.App.Toasts;

/// <summary>
/// Shows a toast only when a drop fails (developer's call, overrides JB-2's progress/success toast): the plain reason,
/// Copy details, and Show in folder when other files in the drop did convert. Success and cancel are silent.
/// Toasts stack up from the bottom-right of the primary work area, newest at the bottom.
/// </summary>
public sealed class ToastHost
{
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _log;
    private readonly List<ToastWindow> _windows = [];
    private readonly List<(JobGroup Group, Action Refresh)> _groups = [];

    public ToastHost(JobQueue queue, Dispatcher dispatcher, ILogger log)
    {
        _dispatcher = dispatcher;
        _log = log;
        queue.Updated += u => _dispatcher.BeginInvoke(() => OnUpdate(u));
    }

    /// <summary>Call on the UI thread right after enqueueing, before any update for these jobs is dispatched.</summary>
    public void Track(IReadOnlyList<(JobRequest Request, JobHandle Handle)> jobs, string title)
    {
        var group = new JobGroup(jobs);

        void Refresh()
        {
            if (group.Status is JobStatus.Running or JobStatus.Succeeded or JobStatus.Cancelled)
            {
                return;
            }

            _groups.RemoveAll(g => g.Group == group);
            var window = Open();
            window.ShowFolderClicked += () => ShowInFolder(group.Outputs);
            window.CopyDetailsClicked += () => CopyDetails(group.Details);
            window.Render(new ToastView("", title, group.UserMessage ?? "Morph couldn't finish this.",
                CanShowFolder: group.Outputs.Count > 0, CanCopyDetails: group.Details is not null));
        }

        _groups.Add((group, Refresh));
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

    private const double Gap = 12; // from the screen edge and between cards

    private void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom;
        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            var w = _windows[i];
            var height = double.IsNaN(w.ActualHeight) || w.ActualHeight == 0 ? 120 : w.ActualHeight;
            w.Left = area.Right - w.Width - Gap;
            w.Top = bottom - height - Gap;
            bottom -= height + Gap / 2;
        }
    }

    // shortcut: selects the first output only; SHOpenFolderAndSelectItems can select them all if batches need it.
    private static void ShowInFolder(IReadOnlyList<string> outputs)
    {
        if (outputs is [var first, ..])
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = false })?.Dispose();
        }
    }

    private void CopyDetails(string? details)
    {
        try
        {
            Clipboard.SetText(details ?? "");
        }
        catch (COMException ex)
        {
            _log.LogWarning(ex, "Copy details failed: the clipboard is held by another app");
        }
    }
}
