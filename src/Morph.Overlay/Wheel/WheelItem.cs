namespace Morph.Overlay.Wheel;

/// <summary>One wedge. <paramref name="Glyph"/> is a Segoe Fluent Icons character.</summary>
// shortcut: spike-local type; replaced by Core's WheelOption/ToolDescriptor when the capability matrix lands (task 1.2).
public sealed record WheelItem(string Id, string Label, string Glyph);
