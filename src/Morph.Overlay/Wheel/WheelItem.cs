namespace Morph.Overlay.Wheel;

/// <summary>One wedge. <paramref name="Glyph"/> is a Segoe Fluent Icons character.</summary>
// The App maps Core's WheelOption onto this, so the view doesn't depend on the matrix.
public sealed record WheelItem(string Id, string Label, string Glyph);
