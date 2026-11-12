using System.Globalization;

namespace Morph.Editors.Frames;

/// <summary>Save frames' frame grid (ED-8): frame k starts at k ÷ fps; stepping moves by one frame.</summary>
public static class FrameMath
{
    /// <summary>The frame showing at <paramref name="seconds"/> (a hair of rounding counts as the frame that starts there).</summary>
    public static int At(double seconds, double fps) => (int)Math.Floor(Math.Max(0, seconds) * fps + 1e-6);

    /// <summary>When frame <paramref name="frame"/> starts.</summary>
    public static double Start(int frame, double fps) => frame / fps;

    /// <summary>A time to the millisecond, so neighbouring frames read differently: "0:12.533", "1:02:03.000".</summary>
    public static string Format(double seconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
        return t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>The last frame of a <paramref name="duration"/>-second video.</summary>
    public static int Last(double duration, double fps) => Math.Max(0, (int)Math.Ceiling(duration * fps - 1e-6) - 1);
}
