using System.Globalization;

using Morph.Core.Jobs;

namespace Morph.Editors.Trim;

/// <summary>What the trim editor keeps: from <paramref name="In"/> to <paramref name="Out"/>, cut at each split (sorted, inside), in seconds.</summary>
public sealed record TrimCut(double In, double Out, IReadOnlyList<double> Splits);

/// <summary>The trim editor's rules, kept apart from the view so they can be tested: every part stays at least <see cref="MinPart"/> long.</summary>
public static class TrimMath
{
    public const double MinPart = 0.1;

    public static TrimCut Whole(double duration) => new(0, duration, []);

    /// <summary>Moves the start; splits it passes are dropped.</summary>
    public static TrimCut SetIn(TrimCut cut, double at)
    {
        var start = Math.Clamp(at, 0, Math.Max(0, cut.Out - MinPart));
        return cut with { In = start, Splits = [.. cut.Splits.Where(s => s >= start + MinPart)] };
    }

    /// <summary>Moves the end; splits it passes are dropped.</summary>
    public static TrimCut SetOut(TrimCut cut, double at, double duration)
    {
        var end = Math.Clamp(at, Math.Min(duration, cut.In + MinPart), duration);
        return cut with { Out = end, Splits = [.. cut.Splits.Where(s => s <= end - MinPart)] };
    }

    /// <summary>A split at <paramref name="at"/>, unless that leaves a part shorter than <see cref="MinPart"/>.</summary>
    public static TrimCut AddSplit(TrimCut cut, double at)
    {
        if (at < cut.In + MinPart || at > cut.Out - MinPart || cut.Splits.Any(s => Math.Abs(s - at) < MinPart))
        {
            return cut;
        }

        return cut with { Splits = [.. cut.Splits.Append(at).Order()] };
    }

    /// <summary>Moves split <paramref name="index"/>, held between its neighbours.</summary>
    public static TrimCut MoveSplit(TrimCut cut, int index, double at)
    {
        var low = (index == 0 ? cut.In : cut.Splits[index - 1]) + MinPart;
        var high = (index == cut.Splits.Count - 1 ? cut.Out : cut.Splits[index + 1]) - MinPart;
        var splits = cut.Splits.ToArray();
        splits[index] = Math.Clamp(at, low, Math.Max(low, high));
        return cut with { Splits = splits };
    }

    public static TrimCut RemoveSplit(TrimCut cut, int index) => cut with { Splits = [.. cut.Splits.Where((_, i) => i != index)] };

    /// <summary>The split closest to <paramref name="at"/>, or null when there are none.</summary>
    public static int? Nearest(TrimCut cut, double at) =>
        cut.Splits.Count == 0 ? null : cut.Splits.Select((s, i) => (Distance: Math.Abs(s - at), i)).Min().i;

    /// <summary>The parts the job makes, in order.</summary>
    public static IReadOnlyList<TrimSegment> Segments(TrimCut cut)
    {
        double[] edges = [cut.In, .. cut.Splits, cut.Out];
        return [.. edges.Zip(edges.Skip(1), (a, b) => new TrimSegment(a, b))];
    }

    /// <summary>"1:05.3", or "1:02:05.3" past an hour.</summary>
    public static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(Math.Max(0, seconds), 1));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }
}
