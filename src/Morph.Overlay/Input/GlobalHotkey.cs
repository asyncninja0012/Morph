using System.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Morph.Overlay.Input;

/// <summary>
/// The keyboard path's hotkey (IX-5), Alt+Shift+Space: Ctrl+Alt+Space is taken by other apps (Claude), and Ctrl+Alt+letter is
/// AltGr+letter, which types characters on many layouts. RegisterHotKey on a message-only window; create it on the UI thread.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const string Name = "Alt+Shift+Space";
    private const int Id = 1;
    private const uint VkSpace = 0x20;

    private readonly HwndSource _window;

    /// <exception cref="InvalidOperationException">Another app already holds the hotkey.</exception>
    public GlobalHotkey()
    {
        _window = new HwndSource(new HwndSourceParameters("Morph hotkey") { ParentWindow = new nint(-3) }); // HWND_MESSAGE
        _window.AddHook(WndProc);
        if (!PInvoke.RegisterHotKey(new HWND(_window.Handle), Id, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VkSpace))
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeErrorMessage();
            _window.Dispose();
            throw new InvalidOperationException($"{Name} could not be registered: {error}");
        }
    }

    /// <summary>Raised on the UI thread with the window that was in front when the keys were pressed.</summary>
    public event Action<nint>? Pressed;

    public void Dispose()
    {
        PInvoke.UnregisterHotKey(new HWND(_window.Handle), Id);
        _window.Dispose();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg == PInvoke.WM_HOTKEY && wParam == Id)
        {
            handled = true;
            Pressed?.Invoke(PInvoke.GetForegroundWindow());
        }

        return 0;
    }
}
