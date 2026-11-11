using Morph.Core.Jobs;

namespace Morph.Editors.Pages;

/// <summary>The page organizer's edits on the new order, kept apart from the view so they can be tested. Selections are positions in the order.</summary>
public static class PageMath
{
    public static IReadOnlyList<PagePick> Original(int count) => [.. Enumerable.Range(1, count).Select(n => new PagePick(n))];

    /// <summary>Moves the selected pages, in their order, to <paramref name="to"/> (a gap 0..Count in the current order); returns where they ended up.</summary>
    public static (IReadOnlyList<PagePick> Pages, IReadOnlyList<int> Moved) Move(IReadOnlyList<PagePick> pages, IReadOnlyCollection<int> selected, int to)
    {
        var picked = selected.Order().ToList();
        var rest = pages.Where((_, i) => !selected.Contains(i)).ToList();
        var at = Math.Clamp(to - picked.Count(i => i < to), 0, rest.Count);
        rest.InsertRange(at, picked.Select(i => pages[i]));
        return (rest, [.. Enumerable.Range(at, picked.Count)]);
    }

    public static IReadOnlyList<PagePick> Turn(IReadOnlyList<PagePick> pages, IReadOnlyCollection<int> selected, int degrees) =>
        [.. pages.Select((p, i) => selected.Contains(i) ? p with { Rotate = (((p.Rotate + degrees) % 360) + 360) % 360 } : p)];

    /// <summary>Drops the selected pages; never all of them (a PDF needs a page).</summary>
    public static IReadOnlyList<PagePick> Delete(IReadOnlyList<PagePick> pages, IReadOnlyCollection<int> selected) =>
        selected.Count >= pages.Count || selected.Count == 0 ? pages : [.. pages.Where((_, i) => !selected.Contains(i))];

    /// <summary>Keeps only the selected pages (extract a range).</summary>
    public static IReadOnlyList<PagePick> KeepOnly(IReadOnlyList<PagePick> pages, IReadOnlyCollection<int> selected) =>
        selected.Count == 0 ? pages : [.. pages.Where((_, i) => selected.Contains(i))];

    /// <summary>Starts (or stops starting) a new file after the page at <paramref name="index"/>; the last page can't.</summary>
    public static IReadOnlyList<PagePick> ToggleSplit(IReadOnlyList<PagePick> pages, int index) =>
        index < 0 || index >= pages.Count - 1 ? pages : [.. pages.Select((p, i) => i == index ? p with { SplitAfter = !p.SplitAfter } : p)];

    public static int Files(IReadOnlyList<PagePick> pages) => 1 + pages.Take(pages.Count - 1).Count(p => p.SplitAfter);
}
