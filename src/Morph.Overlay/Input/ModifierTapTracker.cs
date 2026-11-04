namespace Morph.Overlay.Input;

/// <summary>
/// Pure state machine fed from the keyboard hook: a modifier pressed and released with no other key
/// in between, within <see cref="MaxTapMilliseconds"/>, is a tap. Must stay allocation-free and fast.
/// </summary>
internal sealed class ModifierTapTracker
{
    public const uint MaxTapMilliseconds = 400;

    private uint _pendingVk;   // 0 = no tap in progress
    private uint _downAt;

    /// <param name="timeMs">KBDLLHOOKSTRUCT.time; wraps around, so only differences are used.</param>
    public ModifierKey? OnKey(uint vk, bool isDown, uint timeMs)
    {
        if (isDown)
        {
            if (vk == _pendingVk)
            {
                return null; // auto-repeat while held
            }

            // A second key while a modifier is down makes it a chord, not a tap.
            var startsTap = _pendingVk == 0 && ToModifier(vk) is not null;
            _pendingVk = startsTap ? vk : 0;
            _downAt = timeMs;
            return null;
        }

        if (vk != _pendingVk)
        {
            return null;
        }

        _pendingVk = 0;
        return timeMs - _downAt <= MaxTapMilliseconds ? ToModifier(vk) : null;
    }

    public void Reset() => _pendingVk = 0;

    // Low-level hooks report sided codes (VK_LSHIFT...); injected input may use the generic ones.
    private static ModifierKey? ToModifier(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => ModifierKey.Shift,
        0x11 or 0xA2 or 0xA3 => ModifierKey.Ctrl,
        0x12 or 0xA4 or 0xA5 => ModifierKey.Alt,
        _ => null,
    };
}
