using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Win32;

using Morph.Overlay.Input;

namespace Morph.Overlay.Wheel;

/// <summary>
/// Modifier pressed during a drag → show invisible drop catchers at once (the probe runs during the hold) →
/// once the key has been held for <see cref="HoldDelay"/> and an OLE file drag has entered the catchers, show the wheel
/// at the cursor. Releasing the key closes it (PLAN §3, IX-1/IX-4). Lives on the UI thread; create it there.
/// </summary>
public sealed class DragWheelController : IDisposable
{
    /// <summary>A press shorter than this is a tap and never opens the wheel.</summary>
    public static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250); // cold first use measured ~160 ms; an invisible catcher lingering longer is harmless
    private static readonly TimeSpan DropGrace = TimeSpan.FromSeconds(2);

    private readonly DragGestureDetector _detector;
    private readonly Func<ModifierKey, IReadOnlyList<string>, IReadOnlyList<WheelItem>> _itemsFor;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly WheelWindow _wheel = new();
    private readonly List<DropCatcherWindow> _catchers = [];
    private readonly DispatcherTimer _probeTimer;
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _dropGraceTimer;

    private State _state = State.Idle;
    private bool _dragging;
    private int _oleDragsInside; // OLE calls DragLeave on the old target before DragEnter on the new one, so this stays 0 or 1
    private ModifierKey _group;
    private long _pressTimestamp;
    private bool _holdElapsed;
    private bool _fileDragConfirmed;
    private IReadOnlyList<string> _files = [];
    private bool _virtualFiles;
    private bool _dark = IsDarkMode();

    /// <param name="itemsFor">Wedges for the held key and the dragged paths (or virtual file names).</param>
    public DragWheelController(DragGestureDetector detector, Func<ModifierKey, IReadOnlyList<string>, IReadOnlyList<WheelItem>> itemsFor)
    {
        _detector = detector;
        _itemsFor = itemsFor;
        _probeTimer = new DispatcherTimer(ProbeTimeout, DispatcherPriority.Normal, (_, _) => OnProbeTimeout(), _dispatcher) { IsEnabled = false };
        _holdTimer = new DispatcherTimer(HoldDelay, DispatcherPriority.Normal, (_, _) => OnHoldElapsed(), _dispatcher) { IsEnabled = false };
        _dropGraceTimer = new DispatcherTimer(DropGrace, DispatcherPriority.Normal, (_, _) => Hide("drop grace expired"), _dispatcher) { IsEnabled = false };

        _wheel.DragEnter += OnWheelDragOver;
        _wheel.DragOver += OnWheelDragOver;
        _wheel.DragLeave += OnWheelDragLeave;
        _wheel.Drop += OnWheelDrop;
        EnsureCatchers(NativeWindows.MonitorRects().Count);

        _detector.GestureDetected += OnGesture;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        WarmUp();
    }

    /// <summary>A file drag was dropped on a wedge. Virtual files (Outlook) have already been copied to a temp folder.</summary>
    public event Action<WheelItem, DroppedFiles>? ItemDropped;

    // shortcut: spike diagnostics (task 0.3); becomes ILogger in Phase 1.
    public event Action<string>? Diagnostic;

    public void Dispose()
    {
        _detector.GestureDetected -= OnGesture;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _probeTimer.Stop();
        _holdTimer.Stop();
        _dropGraceTimer.Stop();
        _wheel.Close();
        _catchers.ForEach(c => c.Dispose());
    }

    private enum State { Idle, Probing, WheelOpen }

    /// <summary>
    /// Renders every window once off-screen at startup so the first real show skips JIT and render setup
    /// (cold first show measured 360–530 ms vs 40 ms warm; IX-2 allows 100 ms).
    /// </summary>
    private void WarmUp()
    {
        var offScreen = new Windows.Win32.Foundation.RECT(-32000, -32000, -31000, -31000);
        _wheel.SetContent(_itemsFor(ModifierKey.Shift, []), "0", "files", _dark);
        NativeWindows.Place(_wheel.Hwnd, offScreen);
        _wheel.Show();
        _catchers.ForEach(c => c.ShowOn(offScreen));
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _wheel.Hide();
            _catchers.ForEach(c => c.Hide());
        });
    }

    // Input thread → UI thread.
    private void OnGesture(DragGesture g) => _dispatcher.BeginInvoke(() => Handle(g));

    private void Handle(DragGesture g)
    {
        switch (g.Kind)
        {
            case DragGestureKind.DragStarted:
                _dragging = true;
                break;

            case DragGestureKind.ModifierPressed when _dragging && _state == State.Idle && g.Key is { } key:
                _group = key;
                _pressTimestamp = g.Timestamp;
                _holdElapsed = false;
                _fileDragConfirmed = false;
                _holdTimer.Start();
                ShowCatchers();
                break;

            // Once the button is up a drop may still be in flight; Drop/DragLeave or the grace timer hide instead.
            case DragGestureKind.ModifierReleased when _dragging && _state != State.Idle && g.Key == _group:
                Hide(_state == State.WheelOpen ? "key released: wheel closed, drag continues" : "key released before the hold delay");
                break;

            case DragGestureKind.EscapePressed when _state != State.Idle:
                Hide("Esc");
                break;

            case DragGestureKind.DragEnded:
                _dragging = false;
                if (_state != State.Idle)
                {
                    // Hiding now could pull the target out from under an OLE drop that is still being delivered,
                    // letting it land in the window beneath. Wait for Drop/DragLeave instead.
                    if (_oleDragsInside == 0)
                    {
                        Hide("button released");
                    }
                    else
                    {
                        _dropGraceTimer.Start();
                    }
                }

                break;
        }
    }

    private void ShowCatchers()
    {
        var monitors = NativeWindows.MonitorRects();
        EnsureCatchers(monitors.Count);
        for (var i = 0; i < _catchers.Count; i++)
        {
            if (i < monitors.Count)
            {
                _catchers[i].ShowOn(monitors[i]);
            }
        }

        _state = State.Probing;
        _detector.SuppressEscape = true;
        _probeTimer.Start();
        Diagnostic?.Invoke($"probe: catchers up on {monitors.Count} monitor(s) {Elapsed()} after press");
    }

    private void EnsureCatchers(int count)
    {
        while (_catchers.Count < count)
        {
            var catcher = new DropCatcherWindow();
            catcher.Surface.DragEnter += OnCatcherDragEnter;
            catcher.Surface.DragOver += OnCatcherDragOver;
            catcher.Surface.DragLeave += OnCatcherDragLeave;
            catcher.Surface.Drop += OnCatcherDrop;
            _catchers.Add(catcher);
        }
    }

    private void OnHoldElapsed()
    {
        _holdTimer.Stop();
        _holdElapsed = true;
        if (_state == State.Probing && _fileDragConfirmed)
        {
            ShowWheel();
        }
    }

    private void OnProbeTimeout()
    {
        _probeTimer.Stop();
        if (_state == State.Probing && !_fileDragConfirmed)
        {
            Hide("probe: no DragEnter (not an OLE drag, or OLE did not retarget)");
        }
    }

    private void OnCatcherDragEnter(object sender, DragEventArgs e)
    {
        _oleDragsInside++;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (_state != State.Probing || _fileDragConfirmed)
        {
            return;
        }

        _probeTimer.Stop();
        if (DragFiles.Peek(e.Data) is not var (files, isVirtual))
        {
            Hide("probe: OLE drag without files");
            return;
        }

        _files = files;
        _virtualFiles = isVirtual;
        _fileDragConfirmed = true;
        Diagnostic?.Invoke($"probe: file DragEnter {Elapsed()} after press ({files.Count} path(s))");
        if (_holdElapsed)
        {
            ShowWheel();
        }
    }

    // Outside the wheel: show the "no" cursor; a release here cancels (IX-4).
    private void OnCatcherDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCatcherDragLeave(object sender, DragEventArgs e) => OnOleLeave();

    private void OnCatcherDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        _oleDragsInside = 0;
        Hide("dropped outside the wheel: cancelled");
    }

    private void OnWheelDragOver(object sender, DragEventArgs e)
    {
        if (e.RoutedEvent == UIElement.DragEnterEvent)
        {
            _oleDragsInside++;
        }

        var item = _wheel.Hover(e);
        // Copy only, so the source never moves or deletes the original.
        e.Effects = item is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnWheelDragLeave(object sender, DragEventArgs e)
    {
        if (!_wheel.IsOver(e))
        {
            _wheel.ClearHover();
        }

        OnOleLeave();
    }

    private void OnWheelDrop(object sender, DragEventArgs e)
    {
        var item = _wheel.ItemAt(e);
        e.Effects = item is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
        _oleDragsInside = 0;
        var files = _files;
        var isVirtual = _virtualFiles;
        Hide(item is null ? "dropped on the wheel centre: cancelled" : $"dropped on {item.Id}");
        if (item is null)
        {
            return;
        }

        if (isVirtual)
        {
            try
            {
                files = DragFiles.ExtractVirtual(e.Data);
            }
            catch (Exception ex) when (ex is IOException or COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                Diagnostic?.Invoke($"virtual files could not be read: {ex.Message}");
                return;
            }
        }

        ItemDropped?.Invoke(item, new DroppedFiles(files, isVirtual));
    }

    private void OnOleLeave()
    {
        _oleDragsInside = Math.Max(0, _oleDragsInside - 1);
        if (!_dragging && _oleDragsInside == 0 && _state != State.Idle)
        {
            Hide("drag left after release");
        }
    }

    private void ShowWheel()
    {
        var items = _itemsFor(_group, _files);
        var (count, caption) = Describe(_files);
        _wheel.SetContent(items, count, caption, _dark);
        _wheel.ShowAt(NativeWindows.CursorPosition());
        _state = State.WheelOpen;
        Diagnostic?.Invoke($"wheel: {_group} shown {Elapsed()} after press");
    }

    private void Hide(string reason)
    {
        _probeTimer.Stop();
        _holdTimer.Stop();
        _dropGraceTimer.Stop();
        _detector.SuppressEscape = false;
        if (_state == State.Idle)
        {
            return;
        }

        _wheel.Hide();
        _catchers.ForEach(c => c.Hide());
        _state = State.Idle;
        _files = [];
        Diagnostic?.Invoke($"hidden: {reason}");
    }

    private string Elapsed() => $"{Stopwatch.GetElapsedTime(_pressTimestamp).TotalMilliseconds:F0} ms";

    private static (string Count, string Caption) Describe(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return ("·", "attachments");
        }

        var extensions = files.Select(f => Path.GetExtension(f).TrimStart('.').ToUpperInvariant()).Distinct().ToList();
        var noun = files.Count == 1 ? "file" : "files";
        var caption = extensions is [{ Length: > 0 } ext] ? $"{ext} {noun}" : noun;
        return (files.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), caption);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            _dark = IsDarkMode();
        }
    }

    // One small registry value, read at startup and on theme change, never on the show path.
    private static bool IsDarkMode() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
}
