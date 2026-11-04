using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Morph.Overlay.Input;

/// <summary>
/// Watches for "file drag + modifier held" on a dedicated input thread (PLAN §3, D1).
/// Raw input (RIDEV_INPUTSINK) is always on and passive; WH_KEYBOARD_LL exists only during a candidate drag.
/// <see cref="GestureDetected"/> is raised on the input thread: handlers must return quickly and marshal to UI themselves.
/// </summary>
public sealed class DragGestureDetector : IDisposable
{
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort HidUsageMouse = 0x02;
    private const uint VkEscape = 0x1B;

    private readonly ModifierHoldTracker _modifiers = new();
    private DragTracker? _drag;
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private HwndSource? _window;
    private UnhookWindowsHookExSafeHandle? _keyboardHook;
    private HOOKPROC? _hookProc; // kept in a field so the GC never collects the delegate while the hook is live

    private volatile bool _suppressEscape;

    public event Action<DragGesture>? GestureDetected;

    /// <summary>
    /// While true, the hook swallows Esc so it closes our overlay without cancelling the OLE drag (IX-4).
    /// Set from the UI thread while the overlay is up.
    /// </summary>
    public bool SuppressEscape
    {
        get => _suppressEscape;
        set => _suppressEscape = value;
    }

    /// <summary>Starts the input thread; completes once raw input is registered.</summary>
    public Task StartAsync()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(started)) { Name = "Morph input", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA); // HwndSource/Dispatcher require STA
        _thread.Start();
        return started.Task;
    }

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        if (_thread is not null && _thread != Thread.CurrentThread)
        {
            _thread.Join();
        }
    }

    private void Run(TaskCompletionSource started)
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _drag = new DragTracker(PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDRAG), PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYDRAG));
            _window = new HwndSource(new HwndSourceParameters("Morph input") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
            _window.AddHook(WndProc);
            RegisterMouse(new HWND(_window.Handle), RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK);
        }
        catch (Exception ex)
        {
            started.SetException(ex);
            return;
        }

        started.SetResult();
        try
        {
            Dispatcher.Run();
        }
        finally
        {
            RemoveKeyboardHook();
            RegisterMouse(HWND.Null, RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE);
            _window.Dispose();
        }
    }

    private static void RegisterMouse(HWND target, RAWINPUTDEVICE_FLAGS flags)
    {
        ReadOnlySpan<RAWINPUTDEVICE> devices = [new() { usUsagePage = HidUsagePageGeneric, usUsage = HidUsageMouse, dwFlags = flags, hwndTarget = target }];
        if (!PInvoke.RegisterRawInputDevices(devices, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices failed ({Marshal.GetLastPInvokeError()}).");
        }
    }

    // Not setting handled: WM_INPUT must reach DefWindowProc so the system can clean up.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)PInvoke.WM_INPUT)
        {
            OnRawInput(lParam);
        }

        return IntPtr.Zero;
    }

    private unsafe void OnRawInput(IntPtr handle)
    {
        RAWINPUT raw;
        var size = (uint)sizeof(RAWINPUT);
        if (PInvoke.GetRawInputData(new HRAWINPUT(handle), RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, (void*)&raw, &size, (uint)sizeof(RAWINPUTHEADER)) == unchecked((uint)-1)
            || raw.header.dwType != (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE)
        {
            return;
        }

        var drag = _drag!;
        var buttons = raw.data.mouse.Anonymous.Anonymous.usButtonFlags;
        // Raw input reports physical buttons; with swapped buttons the primary is the physical right one.
        var swapped = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0;
        var downFlag = swapped ? PInvoke.RI_MOUSE_RIGHT_BUTTON_DOWN : PInvoke.RI_MOUSE_LEFT_BUTTON_DOWN;
        var upFlag = swapped ? PInvoke.RI_MOUSE_RIGHT_BUTTON_UP : PInvoke.RI_MOUSE_LEFT_BUTTON_UP;

        if ((buttons & upFlag) != 0)
        {
            if (drag.ButtonUp())
            {
                RemoveKeyboardHook();
                Raise(DragGestureKind.DragEnded);
            }

            return;
        }

        if ((buttons & downFlag) != 0)
        {
            var p = CursorPosition();
            drag.ButtonDown(p.X, p.Y);
            return;
        }

        if (drag.IsWatching)
        {
            var p = CursorPosition();
            if (drag.Move(p.X, p.Y))
            {
                if (IsIgnoredContext())
                {
                    drag.Reset();
                    return;
                }

                InstallKeyboardHook();
                Raise(DragGestureKind.DragStarted, p);
            }
        }
    }

    /// <summary>Our own windows are in the foreground, or the cursor is hidden (games, video players).</summary>
    private static unsafe bool IsIgnoredContext()
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), &pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return true;
        }

        var info = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        return PInvoke.GetCursorInfo(ref info) && (info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) == 0;
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook is not null)
        {
            return;
        }

        _modifiers.Reset();
        _hookProc ??= KeyboardHookProc;
        _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _hookProc, PInvoke.GetModuleHandle((string?)null), 0);
        if (_keyboardHook.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            _keyboardHook = null;
            throw new InvalidOperationException($"SetWindowsHookEx(WH_KEYBOARD_LL) failed ({error}).");
        }
    }

    private void RemoveKeyboardHook()
    {
        _keyboardHook?.Dispose();
        _keyboardHook = null;
    }

    // Runs inside the system's input path: no I/O, no locks, no waiting; anything slow goes through BeginInvoke.
    private unsafe LRESULT KeyboardHookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            var key = (KBDLLHOOKSTRUCT*)lParam.Value;
            var msg = (uint)wParam.Value;
            var isDown = msg is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
            if (isDown || msg is PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP)
            {
                var now = Stopwatch.GetTimestamp();
                if (key->vkCode == VkEscape)
                {
                    if (isDown)
                    {
                        _dispatcher!.BeginInvoke(() => Raise(DragGestureKind.EscapePressed, CursorPosition(), now));
                    }

                    if (_suppressEscape)
                    {
                        return new LRESULT(1); // the drag source never sees Esc, so its drag goes on
                    }
                }

                if (_modifiers.OnKey(key->vkCode, isDown) is { } change)
                {
                    var kind = change.IsDown ? DragGestureKind.ModifierPressed : DragGestureKind.ModifierReleased;
                    _dispatcher!.BeginInvoke(() => Raise(kind, CursorPosition(), now, change.Key));
                }
            }
        }

        return PInvoke.CallNextHookEx(null, code, wParam, lParam);
    }

    private void Raise(DragGestureKind kind) => Raise(kind, CursorPosition(), Stopwatch.GetTimestamp());

    private void Raise(DragGestureKind kind, System.Drawing.Point p) => Raise(kind, p, Stopwatch.GetTimestamp());

    private void Raise(DragGestureKind kind, System.Drawing.Point p, long timestamp, ModifierKey? key = null) =>
        GestureDetected?.Invoke(new DragGesture(kind, p.X, p.Y, timestamp, key));

    private static System.Drawing.Point CursorPosition() =>
        PInvoke.GetCursorPos(out var p) ? p : default;
}
