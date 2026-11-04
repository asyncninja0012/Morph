namespace Morph.Overlay.Input;

internal readonly record struct ModifierChange(ModifierKey Key, bool IsDown);

/// <summary>
/// Pure state machine fed from the keyboard hook: reports when a modifier goes down on its own and when that hold ends.
/// A hold ends on key up, or when any other key goes down (a chord is not a wheel gesture).
/// How long the key must be held before the wheel opens is the controller's decision. Must stay allocation-free and fast.
/// </summary>
internal sealed class ModifierHoldTracker
{
    private uint _heldVk; // 0 = nothing held

    public ModifierChange? OnKey(uint vk, bool isDown)
    {
        if (isDown)
        {
            if (vk == _heldVk)
            {
                return null; // auto-repeat while held
            }

            if (_heldVk != 0)
            {
                var held = _heldVk;
                _heldVk = 0;
                return new ModifierChange(ToModifier(held)!.Value, IsDown: false);
            }

            if (ToModifier(vk) is { } modifier)
            {
                _heldVk = vk;
                return new ModifierChange(modifier, IsDown: true);
            }

            return null;
        }

        if (vk != _heldVk)
        {
            return null;
        }

        _heldVk = 0;
        return new ModifierChange(ToModifier(vk)!.Value, IsDown: false);
    }

    public void Reset() => _heldVk = 0;

    // Low-level hooks report sided codes (VK_LSHIFT...); injected input may use the generic ones.
    private static ModifierKey? ToModifier(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => ModifierKey.Shift,
        0x11 or 0xA2 or 0xA3 => ModifierKey.Ctrl,
        0x12 or 0xA4 or 0xA5 => ModifierKey.Alt,
        _ => null,
    };
}
