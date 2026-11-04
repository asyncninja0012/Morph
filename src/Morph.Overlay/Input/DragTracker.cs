namespace Morph.Overlay.Input;

/// <summary>Pure state machine: button down, then movement out of the drag rectangle centred on the press point.</summary>
internal sealed class DragTracker(int dragWidth, int dragHeight)
{
    private bool _pressed;
    private bool _dragging;
    private int _startX;
    private int _startY;

    /// <summary>True while the button is down and the drag has not started yet (only then do moves matter).</summary>
    public bool IsWatching => _pressed && !_dragging;

    public void ButtonDown(int x, int y)
    {
        _pressed = true;
        _dragging = false;
        _startX = x;
        _startY = y;
    }

    /// <returns>True exactly once, when the cursor first leaves the drag rectangle.</returns>
    public bool Move(int x, int y)
    {
        if (!IsWatching)
        {
            return false;
        }

        if (Math.Abs(x - _startX) * 2 > dragWidth || Math.Abs(y - _startY) * 2 > dragHeight)
        {
            _dragging = true;
            return true;
        }

        return false;
    }

    /// <returns>True if a drag had started (so its end must be reported).</returns>
    public bool ButtonUp()
    {
        var wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        return wasDragging;
    }

    /// <summary>Forget the current press, e.g. when a filter rejects the drag.</summary>
    public void Reset() => _pressed = _dragging = false;
}
