using System.Windows.Interop;

using Microsoft.Extensions.Logging;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Morph.App.Tray;

/// <summary>
/// Morph's notification-area icon: a click opens the job history; the menu has Job history, Open logs folder and Quit. Shell_NotifyIcon through CsWin32 rather than WinForms
/// NotifyIcon: WinForms would add ~13 MB to an MSIX that is already over its size budget (PLAN R3).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = PInvoke.WM_APP + 1;
    private const uint OpenLogsCommand = 1;
    private const uint QuitCommand = 2;
    private const uint HistoryCommand = 3;
    private const uint NinKeySelect = PInvoke.NIN_SELECT | 0x1; // NIN_SELECT | NINF_KEY; not in the CsWin32 metadata

    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private readonly HICON _icon;
    private readonly ILogger _log;

    /// <param name="png">The icon image (Windows reads PNG icon data directly).</param>
    public TrayIcon(byte[] png, ILogger log)
    {
        _log = log;
        // A hidden top-level window, not message-only: only top-level windows get the TaskbarCreated broadcast.
        _window = new HwndSource(new HwndSourceParameters("Morph tray") { WindowStyle = unchecked((int)WINDOW_STYLE.WS_POPUP) });
        _window.AddHook(WndProc);
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        var size = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        unsafe
        {
            fixed (byte* bits = png)
            {
                _icon = PInvoke.CreateIconFromResourceEx(bits, (uint)png.Length, true, 0x00030000, size, size, IMAGE_FLAGS.LR_DEFAULTCOLOR);
            }
        }

        Add();
    }

    public event Action? HistoryClicked;

    public event Action? OpenLogsClicked;

    public event Action? QuitClicked;

    public void Dispose()
    {
        var data = Data();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
        PInvoke.DestroyIcon(_icon);
        _window.Dispose();
    }

    // Also called again after Explorer restarts, which drops every icon.
    private void Add()
    {
        var data = Data();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = "Morph";
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
        {
            _log.LogWarning("Tray icon could not be added (Explorer not running yet?); it is retried when the taskbar starts");
            return;
        }

        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
    }

    private NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = new HWND(_window.Handle),
        uID = 1,
    };

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg == _taskbarCreated)
        {
            Add();
        }
        else if ((uint)msg == CallbackMessage && (uint)(lParam & 0xFFFF) is PInvoke.NIN_SELECT or NinKeySelect)
        {
            HistoryClicked?.Invoke();
            handled = true;
        }
        else if ((uint)msg == CallbackMessage && (uint)(lParam & 0xFFFF) == PInvoke.WM_CONTEXTMENU)
        {
            // Version 4: wParam holds the anchor point in screen coordinates.
            ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
            handled = true;
        }

        return 0;
    }

    private static unsafe void Append(HMENU menu, MENU_ITEM_FLAGS flags, uint id, string? text)
    {
        fixed (char* p = text)
        {
            PInvoke.AppendMenu(menu, flags, id, p);
        }
    }

    private unsafe void ShowMenu(int x, int y)
    {
        var menu = PInvoke.CreatePopupMenu();
        try
        {
            Append(menu, MENU_ITEM_FLAGS.MF_STRING, HistoryCommand, "Job history");
            Append(menu, MENU_ITEM_FLAGS.MF_STRING, OpenLogsCommand, "Open logs folder");
            Append(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
            Append(menu, MENU_ITEM_FLAGS.MF_STRING, QuitCommand, "Quit Morph");

            // Documented requirement: without it the menu doesn't close when the user clicks elsewhere.
            var hwnd = new HWND(_window.Handle);
            PInvoke.SetForegroundWindow(hwnd);
            var command = (uint)PInvoke.TrackPopupMenuEx(menu,
                (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN),
                x, y, hwnd, null).Value;
            PInvoke.PostMessage(hwnd, PInvoke.WM_NULL, 0, 0);

            switch (command)
            {
                case HistoryCommand:
                    HistoryClicked?.Invoke();
                    break;
                case OpenLogsCommand:
                    OpenLogsClicked?.Invoke();
                    break;
                case QuitCommand:
                    QuitClicked?.Invoke();
                    break;
            }
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }
}
