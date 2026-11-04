namespace Morph.Overlay.Input;

public enum ModifierKey { Shift, Ctrl, Alt }

public enum DragGestureKind
{
    /// <summary>Primary button held and moved past the system drag threshold.</summary>
    DragStarted,
    /// <summary>Shift, Ctrl or Alt went down on its own during a candidate drag.</summary>
    ModifierPressed,
    /// <summary>The held modifier was released, or another key was pressed with it.</summary>
    ModifierReleased,
    EscapePressed,
    /// <summary>Primary button released after <see cref="DragStarted"/>.</summary>
    DragEnded,
}

/// <param name="X">Cursor position in physical screen pixels when the gesture was seen.</param>
/// <param name="Timestamp"><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> on the input thread, for latency measurement.</param>
/// <param name="Key">Set only for <see cref="DragGestureKind.ModifierPressed"/> and <see cref="DragGestureKind.ModifierReleased"/>.</param>
public sealed record DragGesture(DragGestureKind Kind, int X, int Y, long Timestamp, ModifierKey? Key = null);
