using Morph.Overlay.Input;

namespace Morph.Overlay.Tests;

public sealed class ModifierTapTrackerTests
{
    private const uint LShift = 0xA0, RControl = 0xA3, LAlt = 0xA4, KeyA = 0x41, Escape = 0x1B;

    private readonly ModifierTapTracker _tracker = new();

    [Theory]
    [InlineData(LShift, ModifierKey.Shift)]
    [InlineData(0x10u, ModifierKey.Shift)]
    [InlineData(RControl, ModifierKey.Ctrl)]
    [InlineData(LAlt, ModifierKey.Alt)]
    public void Quick_press_and_release_is_a_tap(uint vk, ModifierKey expected)
    {
        Assert.Null(_tracker.OnKey(vk, isDown: true, 1000));
        Assert.Equal(expected, _tracker.OnKey(vk, isDown: false, 1150));
    }

    [Fact]
    public void Held_longer_than_400ms_is_not_a_tap()
    {
        _tracker.OnKey(LShift, isDown: true, 1000);
        Assert.Null(_tracker.OnKey(LShift, isDown: false, 1401));
    }

    [Fact]
    public void Auto_repeat_does_not_restart_the_timer()
    {
        _tracker.OnKey(LShift, isDown: true, 1000);
        _tracker.OnKey(LShift, isDown: true, 1300);
        Assert.Null(_tracker.OnKey(LShift, isDown: false, 1500));
    }

    [Fact]
    public void Another_key_during_the_press_makes_it_a_chord()
    {
        _tracker.OnKey(LShift, isDown: true, 1000);
        _tracker.OnKey(KeyA, isDown: true, 1050);
        _tracker.OnKey(KeyA, isDown: false, 1060);
        Assert.Null(_tracker.OnKey(LShift, isDown: false, 1100));
    }

    [Fact]
    public void Two_modifiers_together_are_not_a_tap()
    {
        _tracker.OnKey(LShift, isDown: true, 1000);
        _tracker.OnKey(RControl, isDown: true, 1010);
        Assert.Null(_tracker.OnKey(RControl, isDown: false, 1050));
        Assert.Null(_tracker.OnKey(LShift, isDown: false, 1060));
    }

    [Fact]
    public void Non_modifier_keys_are_never_taps()
    {
        _tracker.OnKey(Escape, isDown: true, 1000);
        Assert.Null(_tracker.OnKey(Escape, isDown: false, 1010));
    }

    [Fact]
    public void Tick_count_wraparound_is_handled()
    {
        _tracker.OnKey(LShift, isDown: true, uint.MaxValue - 50);
        Assert.Equal(ModifierKey.Shift, _tracker.OnKey(LShift, isDown: false, 49));
    }
}
