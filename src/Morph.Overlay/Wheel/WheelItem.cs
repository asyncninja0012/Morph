namespace Morph.Overlay.Wheel;

/// <summary>One wedge. <paramref name="Glyph"/> is a Segoe Fluent Icons character; <paramref name="Beta"/> marks an approximate conversion (CV-5).</summary>
/// <param name="Choices">Presets shown on the outer ring while this wedge is under the pointer (ids like "compress/small").</param>
// The App maps Core's WheelOption onto this, so the view doesn't depend on the matrix.
public sealed record WheelItem(string Id, string Label, string Glyph, bool Beta = false, IReadOnlyList<WheelItem>? Choices = null);
