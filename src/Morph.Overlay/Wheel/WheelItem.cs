namespace Morph.Overlay.Wheel;

/// <summary>One wedge. <paramref name="Glyph"/> is a Segoe Fluent Icons character.</summary>
/// <param name="DisabledReason">Shown in the centre while hovered (CV-6); dropping then means "open <paramref name="FixLink"/>", not "run".</param>
// The App maps Core's WheelOption onto this, so the view doesn't depend on the matrix.
public sealed record WheelItem(string Id, string Label, string Glyph, string? DisabledReason = null, Uri? FixLink = null);
