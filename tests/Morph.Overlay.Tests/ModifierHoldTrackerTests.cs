using Morph.Overlay.Input;

namespace Morph.Overlay.Tests;

public sealed class ModifierHoldTrackerTests
{
    private const uint LShift = 0xA0, RControl = 0xA3, LAlt = 0xA4, KeyA = 0x41, Escape = 0x1B;

    private readonly ModifierHoldTracker _tracker = new();

    [Theory]
    [InlineData(LShift, ModifierKey.Shift)]
    [InlineData(0x10u, ModifierKey.Shift)]
    [InlineData(RControl, ModifierKey.Ctrl)]
    [InlineData(LAlt, ModifierKey.Alt)]
    public void Press_and_release_are_reported(uint vk, ModifierKey key)
    {
        Assert.Equal(new ModifierChange(key, IsDown: true), _tracker.OnKey(vk, isDown: true));
        Assert.Equal(new ModifierChange(key, IsDown: false), _tracker.OnKey(vk, isDown: false));
    }

    [Fact]
    public void Auto_repeat_is_ignored()
    {
        _tracker.OnKey(LShift, isDown: true);
        Assert.Null(_tracker.OnKey(LShift, isDown: true));
        Assert.Null(_tracker.OnKey(LShift, isDown: true));
    }

    [Fact]
    public void Another_key_during_the_hold_ends_it()
    {
        _tracker.OnKey(LShift, isDown: true);
        Assert.Equal(new ModifierChange(ModifierKey.Shift, IsDown: false), _tracker.OnKey(KeyA, isDown: true));
        Assert.Null(_tracker.OnKey(KeyA, isDown: false));
        Assert.Null(_tracker.OnKey(LShift, isDown: false)); // already reported
    }

    [Fact]
    public void A_second_modifier_ends_the_hold_and_does_not_start_one()
    {
        _tracker.OnKey(LShift, isDown: true);
        Assert.Equal(new ModifierChange(ModifierKey.Shift, IsDown: false), _tracker.OnKey(RControl, isDown: true));
        Assert.Null(_tracker.OnKey(RControl, isDown: false));
    }

    [Fact]
    public void Non_modifier_keys_are_never_reported()
    {
        Assert.Null(_tracker.OnKey(Escape, isDown: true));
        Assert.Null(_tracker.OnKey(Escape, isDown: false));
    }

    [Fact]
    public void Release_without_a_seen_press_is_ignored()
    {
        // e.g. Shift was already down when the drag (and the hook) started
        Assert.Null(_tracker.OnKey(LShift, isDown: false));
    }
}
