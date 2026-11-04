namespace Morph.Overlay.Input;

public enum ModifierKey { Shift, Ctrl, Alt }

public enum DragGestureKind
{
    /// <summary>Primary button held and moved past the system drag threshold.</summary>
    DragStarted,
    /// <summary>Shift, Ctrl or Alt pressed and released alone within 400 ms during a candidate drag.</summary>
    ModifierTapped,
    EscapePressed,
    /// <summary>Primary button released after <see cref="DragStarted"/>.</summary>
    DragEnded,
}

/// <param name="X">Cursor position in physical screen pixels when the gesture was seen.</param>
/// <param name="Timestamp"><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> on the input thread, for latency measurement.</param>
/// <param name="Key">Set only for <see cref="DragGestureKind.ModifierTapped"/>.</param>
public sealed record DragGesture(DragGestureKind Kind, int X, int Y, long Timestamp, ModifierKey? Key = null);
