using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Content;

/// <summary>
/// A horizontal or vertical rule on a page: a stroked straight line, a thin filled rectangle (the way many programs
/// draw lines), or the edge of a filled area such as a cell background. Coordinates are user space of the page.
/// </summary>
public sealed record PdfRule
{
    /// <summary>The left end of a horizontal rule, the bottom end of a vertical one.</summary>
    public (double X, double Y) Start { get; init; }

    /// <summary>The right end of a horizontal rule, the top end of a vertical one.</summary>
    public (double X, double Y) End { get; init; }

    /// <summary>True for a horizontal rule, false for a vertical one.</summary>
    public bool IsHorizontal { get; init; }

    /// <summary>The thickness in points (0 for the edge of a filled area).</summary>
    public double Thickness { get; init; }

    /// <summary>True for the edge of a filled area (a background), false for a line drawn as such.</summary>
    public bool IsEdgeOfFill { get; init; }

    /// <summary>The length in points.</summary>
    public double Length => IsHorizontal ? End.X - Start.X : End.Y - Start.Y;
}

/// <summary>Extracts graphics of a page that matter for its layout: the rules of tables, frames and separators.</summary>
public static class PdfGraphicsExtractor
{
    /// <summary>
    /// Returns the horizontal and vertical rules of a page (also those in form XObjects): stroked straight lines and
    /// rectangle outlines, thin filled rectangles (at most 3 pt thick) and the edges of other filled rectangles. Pieces
    /// on one line that touch or overlap are joined. Curves, clipping paths and slanted lines are left out.
    /// </summary>
    public static IReadOnlyList<PdfRule> ExtractRules(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        try
        {
            var collector = new RuleCollector(page.CropBox);
            collector.Process(page.GetContentBytes(), page.GetInheritable(PdfName.Resources) as PdfDictionary, Matrix.Identity, 0);
            return RuleCollector.Join(collector.Rules);
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex))
        {
            throw new PdfException("Graphics extraction failed (damaged content): " + ex.Message, ex);
        }
    }
}

/// <summary>Walks content streams and records the rules of the paths painted.</summary>
internal sealed class RuleCollector
{
    private const double Thin = 3;           // filled rectangles up to this thickness are lines
    private const double Straight = 0.5;      // a segment within this of horizontal or vertical is straight
    private readonly PdfRectangle _page;
    public readonly List<PdfRule> Rules = new();

    public RuleCollector(PdfRectangle page) => _page = page;

    private sealed class Subpath
    {
        public readonly List<(double X, double Y)> Points = new();
        public bool Closed, Curved, Rectangle;
    }

    public void Process(byte[] content, PdfDictionary? resources, Matrix initial, int depth)
    {
        if (depth > 12) return;
        var stack = new Stack<(Matrix Ctm, double LineWidth)>();
        var ctm = initial;
        double lineWidth = 1;
        var path = new List<Subpath>();
        Subpath? current = null;
        (double X, double Y) Map(double x, double y) => ctm.Transform(x, y);

        foreach (var op in ContentParser.Parse(content))
        {
            var o = op.Operands;
            double N(int i) => i < o.Count && o[i] is PdfNumber n ? n.Value : 0;
            switch (op.Operator)
            {
                case "q": stack.Push((ctm, lineWidth)); break;
                case "Q": if (stack.Count > 0) (ctm, lineWidth) = stack.Pop(); break;
                case "cm": if (o.Count >= 6) ctm = new Matrix(N(0), N(1), N(2), N(3), N(4), N(5)).Multiply(ctm); break;
                case "w": lineWidth = N(0); break;
                case "m":
                    current = new Subpath();
                    current.Points.Add(Map(N(0), N(1)));
                    path.Add(current);
                    break;
                case "l":
                    if (current == null) { current = new Subpath(); path.Add(current); }
                    current.Points.Add(Map(N(0), N(1)));
                    break;
                case "c" or "v" or "y":
                    if (current != null)
                    {
                        current.Curved = true;
                        current.Points.Add(Map(N(o.Count - 2), N(o.Count - 1)));
                    }
                    break;
                case "h":
                    if (current != null) current.Closed = true;
                    break;
                case "re":
                {
                    double x = N(0), y = N(1), w = N(2), h = N(3);
                    var r = new Subpath { Closed = true, Rectangle = true };
                    r.Points.AddRange(new[] { Map(x, y), Map(x + w, y), Map(x + w, y + h), Map(x, y + h) });
                    path.Add(r);
                    current = null;
                    break;
                }
                case "S" or "s":
                    if (op.Operator == "s" && current != null) current.Closed = true;
                    Stroke(path, lineWidth * Scale(ctm));
                    path.Clear(); current = null;
                    break;
                case "f" or "F" or "f*":
                    Fill(path);
                    path.Clear(); current = null;
                    break;
                case "B" or "B*" or "b" or "b*":
                    if (op.Operator.StartsWith('b') && current != null) current.Closed = true;
                    Fill(path);
                    Stroke(path, lineWidth * Scale(ctm));
                    path.Clear(); current = null;
                    break;
                case "n":
                    path.Clear(); current = null;
                    break;
                case "Do":
                    if (o.Count >= 1 && o[0] is PdfName name && resources?.GetDictionary(PdfName.XObject)?.GetStream(name) is { } form
                        && form.GetName(PdfName.Subtype) == PdfName.Form)
                    {
                        var matrix = form.GetArray(PdfName.Get("Matrix")) is { Count: 6 } m
                            ? new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5)).Multiply(ctm)
                            : ctm;
                        Process(form.GetBytes(), form.GetDictionary(PdfName.Resources) ?? resources, matrix, depth + 1);
                    }
                    break;
            }
        }
    }

    /// <summary>How much the matrix scales lengths (for line widths).</summary>
    private static double Scale(Matrix m) => Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));

    private void Stroke(List<Subpath> path, double width)
    {
        foreach (var sub in path)
        {
            if (sub.Curved) continue;
            var points = sub.Points;
            int count = sub.Closed ? points.Count : points.Count - 1;
            for (int i = 0; i < count; i++)
                AddSegment(points[i], points[(i + 1) % points.Count], width, edge: false);
        }
    }

    private void Fill(List<Subpath> path)
    {
        foreach (var sub in path)
        {
            if (sub.Curved || !IsRectangle(sub.Points, out var r)) continue;
            double w = r.Width, h = r.Height;
            if (w <= Thin && h > w) AddSegment(((r.Left + r.Right) / 2, r.Bottom), ((r.Left + r.Right) / 2, r.Top), w, edge: false);
            else if (h <= Thin) AddSegment((r.Left, (r.Bottom + r.Top) / 2), (r.Right, (r.Bottom + r.Top) / 2), h, edge: false);
            // a larger area (a background), not the whole page: its edges divide like rules
            else if (w * h < 0.5 * _page.Width * _page.Height)
            {
                AddSegment((r.Left, r.Bottom), (r.Right, r.Bottom), 0, edge: true);
                AddSegment((r.Left, r.Top), (r.Right, r.Top), 0, edge: true);
                AddSegment((r.Left, r.Bottom), (r.Left, r.Top), 0, edge: true);
                AddSegment((r.Right, r.Bottom), (r.Right, r.Top), 0, edge: true);
            }
        }
    }

    /// <summary>True for four corners (a closing fifth point allowed) of an axis-aligned rectangle.</summary>
    private static bool IsRectangle(List<(double X, double Y)> points, out PdfRectangle r)
    {
        r = default;
        var p = points.Count == 5 && Math.Abs(points[4].X - points[0].X) < Straight && Math.Abs(points[4].Y - points[0].Y) < Straight
            ? points.Take(4).ToList() : points;
        if (p.Count != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            var (a, b) = (p[i], p[(i + 1) % 4]);
            if (Math.Abs(a.X - b.X) > Straight && Math.Abs(a.Y - b.Y) > Straight) return false;
        }
        r = new PdfRectangle(p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));
        return r.Width > 0 && r.Height > 0;
    }

    private void AddSegment((double X, double Y) a, (double X, double Y) b, double width, bool edge)
    {
        double dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
        if (dy <= Straight && dx >= 1)
        {
            double y = (a.Y + b.Y) / 2;
            Rules.Add(new PdfRule { Start = (Math.Min(a.X, b.X), y), End = (Math.Max(a.X, b.X), y), IsHorizontal = true, Thickness = width, IsEdgeOfFill = edge });
        }
        else if (dx <= Straight && dy >= 1)
        {
            double x = (a.X + b.X) / 2;
            Rules.Add(new PdfRule { Start = (x, Math.Min(a.Y, b.Y)), End = (x, Math.Max(a.Y, b.Y)), IsHorizontal = false, Thickness = width, IsEdgeOfFill = edge });
        }
    }

    /// <summary>Joins rules on one line that touch or overlap (within a point); drawn lines win over edges of fills.</summary>
    public static List<PdfRule> Join(List<PdfRule> rules)
    {
        var result = new List<PdfRule>();
        foreach (var group in rules.GroupBy(r => r.IsHorizontal))
        {
            bool horizontal = group.Key;
            double Across(PdfRule r) => horizontal ? r.Start.Y : r.Start.X;
            double From(PdfRule r) => horizontal ? r.Start.X : r.Start.Y;
            double To(PdfRule r) => horizontal ? r.End.X : r.End.Y;
            var sorted = group.OrderBy(Across).ThenBy(From).ToList();
            var used = new bool[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                if (used[i]) continue;
                var r = sorted[i];
                double across = Across(r), from = From(r), to = To(r), thickness = r.Thickness;
                bool edge = r.IsEdgeOfFill;
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    for (int j = i + 1; j < sorted.Count && Across(sorted[j]) - across <= 1; j++)
                    {
                        if (used[j] || From(sorted[j]) > to + 1 || To(sorted[j]) < from - 1) continue;
                        from = Math.Min(from, From(sorted[j]));
                        to = Math.Max(to, To(sorted[j]));
                        thickness = Math.Max(thickness, sorted[j].Thickness);
                        edge &= sorted[j].IsEdgeOfFill;
                        used[j] = grew = true;
                    }
                }
                result.Add(horizontal
                    ? new PdfRule { Start = (from, across), End = (to, across), IsHorizontal = true, Thickness = thickness, IsEdgeOfFill = edge }
                    : new PdfRule { Start = (across, from), End = (across, to), IsHorizontal = false, Thickness = thickness, IsEdgeOfFill = edge });
            }
        }
        return result;
    }
}
