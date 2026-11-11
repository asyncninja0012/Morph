using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;

using Microsoft.Extensions.Logging;

using Morph.App.Toasts;
using Morph.Core.Actions;
using Morph.Core.Settings;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Morph.App.Actions;

/// <summary>
/// The Actions wheel (FA-2, FA-3): Copy to…, Move to…, Copy path (Unix style on its ring), Open terminal here and Share. They work on
/// files and folders alike and run in the App: no Worker, since nothing is parsed. Failures show a toast.
/// </summary>
internal sealed class ActionRunner(ToastHost toasts, Task<ToolSettings> settings, ILogger log)
{
    private const string FoldersKey = "action.folders"; // settings.json: { "recent": [ ... ] }

    public void Run(string id, IReadOnlyList<string> paths)
    {
        switch (id)
        {
            case "action.copy-path" or "action.copy-path/unix":
                CopyPathAsync(paths, unix: id.EndsWith("/unix", StringComparison.Ordinal));
                break;
            case "action.copy-to" or "action.move-to":
                AskFolderAsync(paths, move: id == "action.move-to");
                break;
            case "action.terminal":
                OpenTerminalAsync(paths[0]);
                break;
            case "action.share":
                Share(paths);
                break;
            default:
                log.LogInformation("{Action} isn't wired up yet", id);
                break;
        }
    }

    private void Fail(string title, string message, Exception? ex = null)
    {
        log.LogWarning(ex, "{Title}: {Message}", title, message);
        toasts.ShowMessage(title, message);
    }

    // The clipboard can be held for a moment by another app (clipboard managers); a few quick retries.
    private async void CopyPathAsync(IReadOnlyList<string> paths, bool unix)
    {
        var text = FileActions.PathText(paths, unix);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                log.LogInformation("Copied {Count} path(s){Style}", paths.Count, unix ? " (Unix style)" : "");
                return;
            }
            catch (COMException ex) when (attempt < 5)
            {
                log.LogDebug(ex, "Clipboard busy, retrying");
                await Task.Delay(30);
            }
            catch (COMException ex)
            {
                Fail("Copy path", "Another app is holding the clipboard. Try again.", ex);
                return;
            }
        }
    }

    private async void AskFolderAsync(IReadOnlyList<string> paths, bool move)
    {
        var store = await settings;
        var saved = (store.Get(FoldersKey)?["recent"] as JsonArray)?.Select(n => (string?)n).OfType<string>().ToList() ?? [];
        var recent = await Task.Run(() => saved.Where(Directory.Exists).ToList()); // I/O: off the UI thread
        var window = new FolderWindow(paths, move, recent);
        window.Chosen += async folder =>
        {
            var verb = move ? "Move" : "Copy";
            try
            {
                await OnStaThread(() => Transfer(paths, folder, move));
                log.LogInformation("{Verb} {Count} item(s) to {Folder}", verb, paths.Count, folder);
            }
            catch (Win32Exception ex)
            {
                Fail($"{verb} to", ex.NativeErrorCode == 0 ? $"The {verb.ToLowerInvariant()} was stopped part way." : $"Windows couldn't {verb.ToLowerInvariant()} everything to {folder}.", ex);
            }

            try
            {
                await store.SetAsync(FoldersKey, new JsonObject { ["recent"] = new JsonArray([.. FileActions.AddRecent(saved, folder).Select(f => JsonValue.Create(f))]) }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Couldn't save the recent folders");
            }
        };
        window.Show();
        window.Activate();
    }

    // Shell file operations show their own progress and conflict UI, which wants an STA thread with its own message loop.
    private static Task OnStaThread(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work();
                done.SetResult();
            }
            catch (Win32Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "Copy or move" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // Windows' own copy/move (SHFileOperation): its progress window for big copies, a name like "a - Copy.txt" instead of
    // overwriting, folders included, and Explorer's undo. The only place Morph moves an original (CLAUDE.md: explicit Move to…).
    private static void Transfer(IReadOnlyList<string> paths, string folder, bool move)
    {
        var from = string.Join('\0', paths) + "\0\0"; // NUL-separated, double-NUL-terminated
        var to = folder + "\0\0";
        unsafe
        {
            fixed (char* f = from, t = to)
            {
                var op = new SHFILEOPSTRUCTW
                {
                    wFunc = move ? PInvoke.FO_MOVE : PInvoke.FO_COPY,
                    pFrom = f,
                    pTo = t,
                    fFlags = (ushort)(FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_RENAMEONCOLLISION | FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR),
                };
                var error = PInvoke.SHFileOperation(ref op);
                if (error != 0 || op.fAnyOperationsAborted)
                {
                    throw new Win32Exception(error, error != 0 ? $"SHFileOperation failed with 0x{error:X}" : "Cancelled");
                }
            }
        }
    }

    // Windows Terminal in the folder (the item itself if it's a folder, else the one it's in); PowerShell without it.
    private async void OpenTerminalAsync(string path)
    {
        var folder = await Task.Run(() => Directory.Exists(path) ? path : Path.GetDirectoryName(path));
        if (folder is null)
        {
            return;
        }

        try
        {
            if (FileActions.TerminalCanOpen(folder))
            {
                try
                {
                    using var terminal = Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = false, ArgumentList = { "-d", folder } });
                    log.LogInformation("Terminal opened in {Folder}", folder);
                    return;
                }
                catch (Win32Exception ex)
                {
                    log.LogInformation(ex, "Windows Terminal isn't installed; opening PowerShell");
                }
            }

            using var shell = Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder });
            log.LogInformation("PowerShell opened in {Folder}", folder);
        }
        catch (Win32Exception ex)
        {
            Fail("Open terminal", "Windows couldn't open a terminal here.", ex);
        }
    }

    // The Windows share sheet needs a window to belong to: a 1×1 see-through one on the screen under the pointer, closed when sharing ends.
    private void Share(IReadOnlyList<string> paths)
    {
        var anchor = new Window
        {
            Width = 1,
            Height = 1,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, // the monitor under the pointer
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
        };
        anchor.Show();
        var hwnd = new WindowInteropHelper(anchor).Handle;
        var dispatcher = anchor.Dispatcher;
        void Done() => dispatcher.BeginInvoke(anchor.Close);
        _ = Task.Delay(TimeSpan.FromMinutes(10)).ContinueWith(_ => Done(), TaskScheduler.Default); // if neither event comes

        try
        {
            var manager = DataTransferManagerInterop.GetForWindow(hwnd);
            manager.DataRequested += async (_, e) =>
            {
                var deferral = e.Request.GetDeferral();
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var path in paths)
                    {
                        items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
                    }

                    var data = e.Request.Data;
                    data.Properties.Title = paths is [var one] ? Path.GetFileName(one) : $"{paths.Count} items";
                    data.SetStorageItems(items);
                    data.ShareCompleted += (_, _) => Done();
                    data.ShareCanceled += (_, _) => Done();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning(ex, "Share couldn't open {Paths}", paths);
                    e.Request.FailWithDisplayText("Morph couldn't open these files to share.");
                    Done();
                }
                finally
                {
                    deferral.Complete();
                }
            };
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
            log.LogInformation("Share opened for {Count} item(s)", paths.Count);
        }
        catch (COMException ex)
        {
            anchor.Close();
            Fail("Share", "Windows couldn't open the share window.", ex);
        }
    }
}
