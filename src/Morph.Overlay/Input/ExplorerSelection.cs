using System.IO;
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Morph.Overlay.Input;

/// <summary>
/// The files selected in the File Explorer window or on the desktop that was in front when the hotkey was pressed (IX-5).
/// Shell windows come from Shell.Application (late-bound, like a script would); the active tab of a Windows 11 Explorer
/// window is the one whose shell browser owns the first ShellTabWindowClass child (tabs share the top-level window).
/// </summary>
public static class ExplorerSelection
{
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser

    /// <summary>
    /// Selected files and folders that exist on disk (virtual items such as Control Panel entries are left out); empty
    /// when the window isn't Explorer or the desktop, or nothing is selected. Runs on its own STA thread: Shell calls go
    /// out of process to Explorer, and the File.Exists checks are I/O.
    /// </summary>
    public static Task<IReadOnlyList<string>> GetAsync(nint foreground)
    {
        var done = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Read(new HWND(foreground)));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "Explorer selection" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>A File Explorer window or the desktop: the only places the hotkey reads a selection from.</summary>
    public static bool IsShellWindow(nint hwnd) => ClassName(new HWND(hwnd)) is "CabinetWClass" or "Progman" or "WorkerW";

    private static IReadOnlyList<string> Read(HWND foreground)
    {
        if (!IsShellWindow(foreground))
        {
            return [];
        }

        var kind = ClassName(foreground);

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
        try
        {
            dynamic windows = shell.Windows();
            dynamic? view = null;
            if (kind == "CabinetWClass")
            {
                var tab = PInvoke.FindWindowEx(foreground, HWND.Null, "ShellTabWindowClass", null);
                foreach (var window in windows)
                {
                    if ((nint)(long)window.HWND == (nint)foreground && (tab.IsNull || TabOf(window) == tab))
                    {
                        view = window.Document;
                        break;
                    }
                }
            }
            else
            {
                object location = 0; // CSIDL_DESKTOP
                object root = Type.Missing;
                view = windows.FindWindowSW(ref location, ref root, SwcDesktop, out int _, SwfoNeedDispatch).Document;
            }

            if (view is null)
            {
                return [];
            }

            var files = new List<string>();
            foreach (var item in view.SelectedItems())
            {
                string path = item.Path;
                if (File.Exists(path) || Directory.Exists(path))
                {
                    files.Add(path);
                }
            }

            return files;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static unsafe HWND TabOf(object window)
    {
        if (window is not Windows.Win32.System.Com.IServiceProvider provider)
        {
            return HWND.Null;
        }

        var service = TopLevelBrowser;
        var iid = typeof(IShellBrowser).GUID;
        provider.QueryService(&service, &iid, out var raw);
        try
        {
            ((IShellBrowser)Marshal.GetObjectForIUnknown((nint)raw)).GetWindow(out var hwnd);
            return hwnd;
        }
        finally
        {
            Marshal.Release((nint)raw);
        }
    }

    private static unsafe string ClassName(HWND hwnd)
    {
        var buffer = stackalloc char[64];
        var length = PInvoke.GetClassName(hwnd, buffer, 64);
        return new string(buffer, 0, length);
    }
}
