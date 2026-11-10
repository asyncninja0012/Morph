using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Morph.Engines.Documents;

/// <summary>
/// Ruled tables on a PDF page (task 2.13): the horizontal and vertical lines the page draws (stroked lines, rectangle edges,
/// and thin filled rectangles, which is how browsers draw borders) are grouped where they touch; a group with at least two
/// row lines and two column lines is a grid, and each word goes into the cell its centre falls in. A table without drawn
/// lines stays text: guessing columns from gaps misreads ordinary multi-column pages.
/// </summary>
// shortcut: merged cells come out as one cell plus empty ones, and a table split across pages becomes two tables.
internal static class PdfTables
{
    private const double Tolerance = 2; // points: lines this close count as touching, or as the same line
    private const double Thin = 2.5; // points: a filled rectangle this thin is a line
    private const int MaxSegments = 5000; // a page this full of lines is a drawing, not a table

    /// <param name="At">y of a horizontal segment, x of a vertical one.</param>
    private readonly record struct Segment(bool Horizontal, double At, double From, double To);

    /// <summary>Finds the page's ruled tables and takes their words out of <paramref name="words"/>.</summary>
    /// <param name="runs">The formatted runs of one word.</param>
    /// <returns>Each table with the y of its top edge.</returns>
    public static List<(double Top, Table Table)> Find(Page page, List<Word> words, Func<Word, List<Run>> runs)
    {
        var result = new List<(double, Table)>();
        var segments = Segments(page);
        if (segments.Count > MaxSegments)
        {
            return result;
        }

        foreach (var group in Groups(segments))
        {
            var ys = Cluster(group.Where(s => s.Horizontal).Select(s => s.At)).OrderDescending().ToList();
            var xs = Cluster(group.Where(s => !s.Horizontal).Select(s => s.At)).Order().ToList();
            if (ys.Count < 2 || xs.Count < 2 || (ys.Count - 1) * (xs.Count - 1) < 2)
            {
                continue; // a single box (a frame around a note) isn't a table
            }

            var cells = new List<Word>?[ys.Count - 1, xs.Count - 1];
            var taken = new HashSet<Word>();
            foreach (var word in words)
            {
                var centre = word.BoundingBox.Centroid;
                var row = ys.FindIndex(1, y => centre.Y >= y) - 1;
                var column = xs.FindIndex(1, x => centre.X <= x) - 1;
                if (centre.Y <= ys[0] && row >= 0 && centre.X >= xs[0] && column >= 0)
                {
                    (cells[row, column] ??= []).Add(word);
                    taken.Add(word);
                }
            }

            // Double borders and padding leave empty rows and columns; drop them.
            var rows = Enumerable.Range(0, ys.Count - 1).Where(r => Enumerable.Range(0, xs.Count - 1).Any(c => cells[r, c] is not null)).ToList();
            var columns = Enumerable.Range(0, xs.Count - 1).Where(c => rows.Any(r => cells[r, c] is not null)).ToList();
            if (rows.Count * columns.Count < 2)
            {
                continue;
            }

            words.RemoveAll(taken.Contains);
            result.Add((ys[0], new Table([.. rows.Select(r => (IReadOnlyList<IReadOnlyList<Run>>)[.. columns.Select(c => (IReadOnlyList<Run>)Cell(cells[r, c], runs))])])));
        }

        return result;
    }

    // A cell's words line by line (top to bottom, then left to right), joined with spaces.
    private static List<Run> Cell(List<Word>? words, Func<Word, List<Run>> runs)
    {
        var result = new List<Run>();
        if (words is null)
        {
            return result;
        }

        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Top))
        {
            if (lines.Count == 0 || word.BoundingBox.Centroid.Y < lines[^1][0].BoundingBox.Bottom)
            {
                lines.Add([]);
            }

            lines[^1].Add(word);
        }

        foreach (var word in lines.SelectMany(l => l.OrderBy(w => w.BoundingBox.Left)))
        {
            if (result.Count > 0)
            {
                result.Add(result[^1] with { Text = " " });
            }

            result.AddRange(runs(word));
        }

        return PdfReader.Clean(result, keepBold: true);
    }

    private static List<Segment> Segments(Page page)
    {
        var segments = new List<Segment>();
        foreach (var path in page.Paths.Where(p => !p.IsClipping && (p.IsFilled || p.IsStroked)))
        {
            foreach (var subpath in path)
            {
                if (subpath.GetBoundingRectangle() is not { } box)
                {
                    continue;
                }

                if (box.Height <= Thin && box.Width > Tolerance * 2)
                {
                    segments.Add(new Segment(true, box.Centroid.Y, box.Left, box.Right));
                }
                else if (box.Width <= Thin && box.Height > Tolerance * 2)
                {
                    segments.Add(new Segment(false, box.Centroid.X, box.Bottom, box.Top));
                }
                else if (path.IsStroked)
                {
                    Edges(subpath, segments); // a stroked rectangle or polyline; a thick filled one is shading, not a border
                }
            }
        }

        return segments;
    }

    private static void Edges(PdfSubpath subpath, List<Segment> segments)
    {
        PdfPoint start = default, current = default;
        foreach (var command in subpath.Commands)
        {
            switch (command)
            {
                case PdfSubpath.Move move:
                    start = current = move.Location;
                    break;
                case PdfSubpath.Line line:
                    Edge(line.From, line.To);
                    current = line.To;
                    break;
                case PdfSubpath.Close:
                    Edge(current, start);
                    current = start;
                    break;
                case PdfSubpath.BezierCurve curve:
                    current = curve.EndPoint;
                    break;
            }
        }

        void Edge(PdfPoint a, PdfPoint b)
        {
            if (Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.X - b.X) > Tolerance * 2)
            {
                segments.Add(new Segment(true, a.Y, Math.Min(a.X, b.X), Math.Max(a.X, b.X)));
            }
            else if (Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) > Tolerance * 2)
            {
                segments.Add(new Segment(false, a.X, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
            }
        }
    }

    // Connected groups: a horizontal and a vertical segment are joined when they cross or meet.
    private static IEnumerable<List<Segment>> Groups(List<Segment> segments)
    {
        var parent = Enumerable.Range(0, segments.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);

        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = i + 1; j < segments.Count; j++)
            {
                var (a, b) = (segments[i], segments[j]);
                if (a.Horizontal != b.Horizontal && Touch(a, b) && Touch(b, a))
                {
                    parent[Root(i)] = Root(j);
                }
            }
        }

        return Enumerable.Range(0, segments.Count).GroupBy(Root).Select(g => g.Select(i => segments[i]).ToList());

        static bool Touch(Segment a, Segment b) => b.At >= a.From - Tolerance && b.At <= a.To + Tolerance;
    }

    // Positions within the tolerance are one line (a border drawn twice, or both edges of a thin rectangle).
    private static List<double> Cluster(IEnumerable<double> values)
    {
        var result = new List<List<double>>();
        foreach (var value in values.Order())
        {
            if (result.Count > 0 && value - result[^1][^1] <= Tolerance)
            {
                result[^1].Add(value);
            }
            else
            {
                result.Add([value]);
            }
        }

        return [.. result.Select(c => c.Average())];
    }
}
