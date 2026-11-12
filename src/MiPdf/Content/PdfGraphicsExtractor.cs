using MiPdf.Graphics;
using MiPdf.Objects;
using static MiPdf.Graphics.ContentColor;

namespace MiPdf.Content;

/// <summary>A horizontal or vertical rule on a page: a stroked straight line, a thin filled rectangle (the way many programs
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

/// <summary>The kind of a <see cref="PdfPathSegment"/>.</summary>
public enum PdfPathSegmentKind
{
    /// <summary>Begins a subpath at <see cref="PdfPathSegment.Point"/>.</summary>
    MoveTo,

    /// <summary>A straight line to <see cref="PdfPathSegment.Point"/>.</summary>
    LineTo,

    /// <summary>A cubic Bézier curve to <see cref="PdfPathSegment.Point"/> with two control points.</summary>
    CurveTo,

    /// <summary>A straight line back to the start of the subpath (<see cref="PdfPathSegment.Point"/>), closing it.</summary>
    ClosePath,
}

/// <summary>
/// A segment of a path in user space of the page: where it goes, and for a curve its control points (for the other
/// kinds the control points are the end point).
/// </summary>
public readonly record struct PdfPathSegment(PdfPathSegmentKind Kind, (double X, double Y) Point, (double X, double Y) Control1, (double X, double Y) Control2);

/// <summary>A subpath: segments starting with a <see cref="PdfPathSegmentKind.MoveTo"/>.</summary>
public sealed class PdfSubpath
{
    internal PdfSubpath(IReadOnlyList<PdfPathSegment> segments) => Segments = segments;

    /// <summary>The segments, the first one a <see cref="PdfPathSegmentKind.MoveTo"/>.</summary>
    public IReadOnlyList<PdfPathSegment> Segments { get; }

    /// <summary>True if the subpath ends with a <see cref="PdfPathSegmentKind.ClosePath"/>.</summary>
    public bool IsClosed => Segments.Count > 0 && Segments[^1].Kind == PdfPathSegmentKind.ClosePath;

    /// <summary>True if the subpath has curves.</summary>
    public bool IsCurved => Segments.Any(s => s.Kind == PdfPathSegmentKind.CurveTo);
}

/// <summary>The rule deciding which areas a filled or clipping path covers.</summary>
public enum PdfFillRule
{
    /// <summary>The nonzero winding number rule (operators <c>f</c>, <c>B</c>, <c>W</c>).</summary>
    NonZeroWinding,

    /// <summary>The even-odd rule (operators <c>f*</c>, <c>B*</c>, <c>W*</c>).</summary>
    EvenOdd,
}

/// <summary>
/// A path painted on a page (or, if asked for, used to clip): its subpaths in user space of the page and how it is
/// painted – filled, stroked or both, with colors, opacity and line style.
/// </summary>
public sealed class PdfPath
{
    internal PdfPath(IReadOnlyList<PdfSubpath> subpaths) => Subpaths = subpaths;

    /// <summary>The subpaths, in user space of the page (the transformations of the content are applied).</summary>
    public IReadOnlyList<PdfSubpath> Subpaths { get; }

    /// <summary>True if the path is filled.</summary>
    public bool IsFilled { get; init; }

    /// <summary>True if the path is stroked.</summary>
    public bool IsStroked { get; init; }

    /// <summary>True if the path is (also) used as a clipping path.</summary>
    public bool IsClipping { get; init; }

    /// <summary>The fill rule of a filled or clipping path.</summary>
    public PdfFillRule FillRule { get; init; }

    /// <summary>
    /// The fill color of a filled path: gray, RGB and CMYK as in the file (ICC-based colors by their number of
    /// components), other color spaces converted to RGB; null for a pattern or a path that is not filled.
    /// </summary>
    public PdfColor? FillColor { get; init; }

    /// <summary>The stroke color of a stroked path, like <see cref="FillColor"/>.</summary>
    public PdfColor? StrokeColor { get; init; }

    /// <summary>The constant opacity for filling (1 opaque, 0 invisible).</summary>
    public double FillOpacity { get; init; } = 1;

    /// <summary>The constant opacity for stroking.</summary>
    public double StrokeOpacity { get; init; } = 1;

    /// <summary>The line width in points of the page (scaled by the transformation; 0 is the thinnest line a device can show).</summary>
    public double LineWidth { get; init; }

    /// <summary>The shape at the ends of stroked lines.</summary>
    public LineCap LineCap { get; init; }

    /// <summary>The shape at the corners of stroked lines.</summary>
    public LineJoin LineJoin { get; init; }

    /// <summary>The dash pattern in points of the page (dash and gap lengths; empty for solid lines).</summary>
    public IReadOnlyList<double> DashArray { get; init; } = Array.Empty<double>();

    /// <summary>Where in the dash pattern the line starts, in points of the page.</summary>
    public double DashPhase { get; init; }

    /// <summary>The bounds of the path's segments, curves by their extent (without the line width).</summary>
    public PdfRectangle Bounds { get; init; }
}

/// <summary>Options for <see cref="PdfGraphicsExtractor.ExtractPaths"/>.</summary>
public sealed class PathExtractionOptions
{
    /// <summary>Also return paths that only clip and paint nothing (<c>W n</c>). Default: false.</summary>
    public bool IncludeClippingPaths { get; set; }

    /// <summary>Include paths in annotation appearances (e.g. ink annotations, stamps, form field borders). Default: false.</summary>
    public bool IncludeAnnotations { get; set; }

    /// <summary>
    /// Include paths in layers (optional content) that are hidden by default (default true, as for text extraction;
    /// false: only what a viewer shows, see <see cref="PdfDocument.Layers"/>).
    /// </summary>
    public bool IncludeHiddenLayers { get; set; } = true;
}

/// <summary>Extracts the graphics of a page: its paths, and the rules of tables, frames and separators among them.</summary>
public static class PdfGraphicsExtractor
{
    /// <summary>
    /// Returns the paths painted on a page (also those in form XObjects), in painting order, with their subpaths in user
    /// space of the page, how they are painted (fill, stroke, fill rule), their colors, opacity and line style. Text,
    /// images and shadings are not paths; glyph outlines of Type 3 fonts are not included.
    /// </summary>
    public static IReadOnlyList<PdfPath> ExtractPaths(PdfPage page, PathExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        try
        {
            return PathCollector.Collect(page, options ?? new PathExtractionOptions());
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex))
        {
            throw new PdfException("Graphics extraction failed (damaged content): " + ex.Message, ex);
        }
    }

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
            return RuleCollector.FromPage(page, includeHiddenLayers: true);
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex))
        {
            throw new PdfException("Graphics extraction failed (damaged content): " + ex.Message, ex);
        }
    }
}

/// <summary>Executes content streams and records the paths painted, with the graphics state they are painted in.</summary>
internal sealed class PathCollector
{
    private readonly PathExtractionOptions _options;
    private readonly PdfDocument? _document;
    private readonly List<PdfPath> _paths = new();
    private int _hidden;

    private sealed class State
    {
        public Matrix Ctm = Matrix.Identity;
        public double LineWidth = 1, DashPhase, FillAlpha = 1, StrokeAlpha = 1;
        public int Cap, Join;
        public double[] Dash = Array.Empty<double>();
        public ColorSpace FillSpace = ColorSpace.Gray, StrokeSpace = ColorSpace.Gray;
        public PdfColor? Fill = PdfColor.Black, Stroke = PdfColor.Black;
        public State Clone() => (State)MemberwiseClone();
    }

    private PathCollector(PdfDocument? document, PathExtractionOptions options)
    {
        _document = document;
        _options = options;
    }

    /// <summary>The bounds of the paths a content stream paints (e.g. a Type 3 glyph procedure), in its own space; null if none.</summary>
    public static PdfRectangle? PaintedBounds(byte[] content, PdfDictionary? resources)
    {
        var collector = new PathCollector(null, new PathExtractionOptions());
        collector.Process(content, resources, new State(), 1);
        return collector._paths.Count == 0 ? null : Text.PdfTextExtractor.Union(collector._paths.Select(p => p.Bounds));
    }

    public static List<PdfPath> Collect(PdfPage page, PathExtractionOptions options)
    {
        var collector = new PathCollector(page.Document, options);
        var resources = page.GetInheritable(PdfName.Resources) as PdfDictionary;
        collector.Process(page.GetContentBytes(), resources, new State(), 0);
        if (options.IncludeAnnotations)
        {
            foreach (var annot in page.Annotations)
            {
                if (annot.Dictionary.GetDictionary(PdfName.AP)?.Get(PdfName.N) is not PdfStream ap || collector.IsHidden(annot.Dictionary)) continue;
                int flags = annot.Dictionary.GetInt(PdfName.Get("F"));
                if ((flags & 0x2) != 0) continue; // hidden annotation
                var form = FormMatrix(ap);
                var box = Transformed(ap.GetRectangle(PdfName.BBox) ?? annot.Rect, form);
                var rect = annot.Rect;
                // the appearance's box mapped onto the annotation rectangle (ISO 32000-2, 12.5.5)
                double sx = box.Width > 0 ? rect.Width / box.Width : 1, sy = box.Height > 0 ? rect.Height / box.Height : 1;
                var fit = new Matrix(sx, 0, 0, sy, rect.Left - box.Left * sx, rect.Bottom - box.Bottom * sy);
                collector.Process(ap.GetBytes(), ap.GetDictionary(PdfName.Resources) ?? resources, new State { Ctm = form.Multiply(fit) }, 1);
            }
        }
        return collector._paths;
    }

    private static Matrix FormMatrix(PdfStream form) =>
        form.GetArray(PdfName.Get("Matrix")) is { Count: 6 } m
            ? new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5))
            : Matrix.Identity;

    private static PdfRectangle Transformed(PdfRectangle r, Matrix m)
    {
        var corners = new[] { m.Transform(r.Left, r.Bottom), m.Transform(r.Right, r.Bottom), m.Transform(r.Right, r.Top), m.Transform(r.Left, r.Top) };
        return new PdfRectangle(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    private bool IsHidden(PdfDictionary dict) => !_options.IncludeHiddenLayers && _document != null && _document.IsHiddenContent(dict[PdfName.Get("OC")]);

    private void Process(byte[] content, PdfDictionary? resources, State initial, int depth)
    {
        if (depth > 12) return;
        var stack = new Stack<State>();
        var marked = new Stack<bool>();
        var gs = initial;
        var subpaths = new List<List<PdfPathSegment>>();
        List<PdfPathSegment>? current = null;
        (double X, double Y) point = (0, 0), start = (0, 0);
        PdfFillRule? clip = null;

        foreach (var op in ContentParser.Parse(content))
        {
            var o = op.Operands;
            double N(int i) => i < o.Count && o[i] is PdfNumber n ? n.Value : 0;
            (double X, double Y) P(int i) => gs.Ctm.Transform(N(i), N(i + 1));
            switch (op.Operator)
            {
                case "q": stack.Push(gs.Clone()); break;
                case "Q": if (stack.Count > 0) gs = stack.Pop(); break;
                case "cm": if (o.Count >= 6) gs.Ctm = new Matrix(N(0), N(1), N(2), N(3), N(4), N(5)).Multiply(gs.Ctm); break;
                case "w": gs.LineWidth = N(0); break;
                case "J": gs.Cap = Math.Clamp((int)N(0), 0, 2); break;
                case "j": gs.Join = Math.Clamp((int)N(0), 0, 2); break;
                case "d":
                    if (o.Count >= 2 && o[0] is PdfArray dash)
                    {
                        gs.Dash = dash.OfType<PdfNumber>().Select(n => n.Value).ToArray();
                        gs.DashPhase = N(1);
                    }
                    break;
                case "gs":
                    if (o.Count >= 1 && o[0] is PdfName gsName && resources?.GetDictionary(PdfName.Get("ExtGState"))?.GetDictionary(gsName) is { } ext)
                        ApplyExtGState(ext, gs);
                    break;

                case "g": gs.FillSpace = ColorSpace.Gray; gs.Fill = ToColor(gs.FillSpace, o); break;
                case "G": gs.StrokeSpace = ColorSpace.Gray; gs.Stroke = ToColor(gs.StrokeSpace, o); break;
                case "rg": gs.FillSpace = ColorSpace.Rgb; gs.Fill = ToColor(gs.FillSpace, o); break;
                case "RG": gs.StrokeSpace = ColorSpace.Rgb; gs.Stroke = ToColor(gs.StrokeSpace, o); break;
                case "k": gs.FillSpace = ColorSpace.Cmyk; gs.Fill = ToColor(gs.FillSpace, o); break;
                case "K": gs.StrokeSpace = ColorSpace.Cmyk; gs.Stroke = ToColor(gs.StrokeSpace, o); break;
                case "cs":
                    if (o.Count >= 1) { gs.FillSpace = ResolveSpace(o[0], resources); gs.Fill = InitialColor(gs.FillSpace); }
                    break;
                case "CS":
                    if (o.Count >= 1) { gs.StrokeSpace = ResolveSpace(o[0], resources); gs.Stroke = InitialColor(gs.StrokeSpace); }
                    break;
                case "sc" or "scn": gs.Fill = ToColor(gs.FillSpace, o); break;
                case "SC" or "SCN": gs.Stroke = ToColor(gs.StrokeSpace, o); break;

                case "m":
                    point = start = P(0);
                    current = new List<PdfPathSegment> { new(PdfPathSegmentKind.MoveTo, point, point, point) };
                    subpaths.Add(current);
                    break;
                case "l":
                    if (current == null) break;   // no current point: ignored, as viewers do
                    point = P(0);
                    current.Add(new(PdfPathSegmentKind.LineTo, point, point, point));
                    break;
                case "c" or "v" or "y":
                {
                    if (current == null) break;
                    (double X, double Y) c1, c2, end;
                    if (op.Operator == "c") { c1 = P(0); c2 = P(2); end = P(4); }
                    else if (op.Operator == "v") { c1 = point; c2 = P(0); end = P(2); }
                    else { c1 = P(0); c2 = end = P(2); }
                    current.Add(new(PdfPathSegmentKind.CurveTo, end, c1, c2));
                    point = end;
                    break;
                }
                case "h":
                    if (current == null || current[^1].Kind == PdfPathSegmentKind.ClosePath) break;
                    current.Add(new(PdfPathSegmentKind.ClosePath, start, start, start));
                    point = start;
                    break;
                case "re":
                {
                    double x = N(0), y = N(1), w = N(2), h = N(3);
                    var corners = new[] { gs.Ctm.Transform(x, y), gs.Ctm.Transform(x + w, y), gs.Ctm.Transform(x + w, y + h), gs.Ctm.Transform(x, y + h) };
                    current = new List<PdfPathSegment> { new(PdfPathSegmentKind.MoveTo, corners[0], corners[0], corners[0]) };
                    for (int i = 1; i < 4; i++) current.Add(new(PdfPathSegmentKind.LineTo, corners[i], corners[i], corners[i]));
                    current.Add(new(PdfPathSegmentKind.ClosePath, corners[0], corners[0], corners[0]));
                    subpaths.Add(current);
                    point = start = corners[0];
                    break;
                }
                case "W": clip = PdfFillRule.NonZeroWinding; break;
                case "W*": clip = PdfFillRule.EvenOdd; break;

                case "S": Paint(fill: null, stroke: true, closeFirst: false); break;
                case "s": Paint(fill: null, stroke: true, closeFirst: true); break;
                case "f" or "F": Paint(fill: PdfFillRule.NonZeroWinding, stroke: false, closeFirst: false); break;
                case "f*": Paint(fill: PdfFillRule.EvenOdd, stroke: false, closeFirst: false); break;
                case "B": Paint(fill: PdfFillRule.NonZeroWinding, stroke: true, closeFirst: false); break;
                case "B*": Paint(fill: PdfFillRule.EvenOdd, stroke: true, closeFirst: false); break;
                case "b": Paint(fill: PdfFillRule.NonZeroWinding, stroke: true, closeFirst: true); break;
                case "b*": Paint(fill: PdfFillRule.EvenOdd, stroke: true, closeFirst: true); break;
                case "n": Paint(fill: null, stroke: false, closeFirst: false); break;

                case "BMC": marked.Push(false); break;
                case "BDC":
                {
                    bool hides = !_options.IncludeHiddenLayers && o.Count >= 2 && o[0] is PdfName { Value: "OC" } && o[1] is PdfName property
                        && _document != null && _document.IsHiddenContent(resources?.GetDictionary(PdfName.Get("Properties"))?[property]);
                    marked.Push(hides);
                    if (hides) _hidden++;
                    break;
                }
                case "EMC":
                    if (marked.Count > 0 && marked.Pop()) _hidden--;
                    break;

                case "Do":
                    if (o.Count >= 1 && o[0] is PdfName name && resources?.GetDictionary(PdfName.XObject)?.GetStream(name) is { } form
                        && form.GetName(PdfName.Subtype) == PdfName.Form && !IsHidden(form))
                    {
                        var inner = gs.Clone();
                        inner.Ctm = FormMatrix(form).Multiply(gs.Ctm);
                        Process(form.GetBytes(), form.GetDictionary(PdfName.Resources) ?? resources, inner, depth + 1);
                    }
                    break;
            }
        }

        void Paint(PdfFillRule? fill, bool stroke, bool closeFirst)
        {
            if (closeFirst && current != null && current[^1].Kind != PdfPathSegmentKind.ClosePath)
                current.Add(new(PdfPathSegmentKind.ClosePath, start, start, start));
            bool clipping = clip != null;
            // a lone move paints nothing
            var parts = subpaths.Where(s => s.Count > 1).Select(s => new PdfSubpath(s)).ToList();
            if (parts.Count > 0 && _hidden == 0 && (fill != null || stroke || (clipping && _options.IncludeClippingPaths)))
            {
                double scale = Math.Sqrt(Math.Abs(gs.Ctm.A * gs.Ctm.D - gs.Ctm.B * gs.Ctm.C));
                _paths.Add(new PdfPath(parts)
                {
                    IsFilled = fill != null,
                    IsStroked = stroke,
                    IsClipping = clipping,
                    FillRule = fill ?? clip ?? PdfFillRule.NonZeroWinding,
                    FillColor = fill != null ? gs.Fill : null,
                    StrokeColor = stroke ? gs.Stroke : null,
                    FillOpacity = gs.FillAlpha,
                    StrokeOpacity = gs.StrokeAlpha,
                    LineWidth = gs.LineWidth * scale,
                    LineCap = (LineCap)gs.Cap,
                    LineJoin = (LineJoin)gs.Join,
                    DashArray = gs.Dash.Select(d => d * scale).ToArray(),
                    DashPhase = gs.DashPhase * scale,
                    Bounds = Bounds(parts),
                });
            }
            subpaths = new List<List<PdfPathSegment>>();
            current = null;
            clip = null;
        }
    }

    private static void ApplyExtGState(PdfDictionary ext, State gs)
    {
        if (ext.Get(PdfName.Get("LW")) is PdfNumber lw) gs.LineWidth = lw.Value;
        if (ext.Get(PdfName.Get("LC")) is PdfNumber lc) gs.Cap = Math.Clamp((int)lc.Value, 0, 2);
        if (ext.Get(PdfName.Get("LJ")) is PdfNumber lj) gs.Join = Math.Clamp((int)lj.Value, 0, 2);
        if (ext.Get(PdfName.Get("D")) is PdfArray { Count: 2 } d && d[0].Resolve() is PdfArray dash)
        {
            gs.Dash = dash.OfType<PdfNumber>().Select(n => n.Value).ToArray();
            gs.DashPhase = d[1].Resolve() is PdfNumber phase ? phase.Value : 0;
        }
        if (ext.Get(PdfName.Get("ca")) is PdfNumber ca) gs.FillAlpha = Math.Clamp(ca.Value, 0, 1);
        if (ext.Get(PdfName.Get("CA")) is PdfNumber sa) gs.StrokeAlpha = Math.Clamp(sa.Value, 0, 1);
    }

    /// <summary>The bounds of subpaths: their points, and curves by their extrema.</summary>
    private static PdfRectangle Bounds(List<PdfSubpath> subpaths)
    {
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        void Add((double X, double Y) p)
        {
            left = Math.Min(left, p.X); right = Math.Max(right, p.X);
            bottom = Math.Min(bottom, p.Y); top = Math.Max(top, p.Y);
        }
        foreach (var sub in subpaths)
        {
            var previous = sub.Segments[0].Point;
            foreach (var s in sub.Segments)
            {
                Add(s.Point);
                if (s.Kind == PdfPathSegmentKind.CurveTo)
                    foreach (var t in PathGeometry.CurveExtrema(previous.X, s.Control1.X, s.Control2.X, s.Point.X).Concat(PathGeometry.CurveExtrema(previous.Y, s.Control1.Y, s.Control2.Y, s.Point.Y)))
                        Add(PathGeometry.CurvePoint(previous, s.Control1, s.Control2, s.Point, t));
                previous = s.Point;
            }
        }
        return new PdfRectangle(left, bottom, right, top);
    }
}

/// <summary>The rules among the paths of a page.</summary>
internal static class RuleCollector
{
    private const double Thin = 3;           // filled rectangles up to this thickness are lines
    private const double Straight = 0.5;      // a segment within this of horizontal or vertical is straight

    public static List<PdfRule> FromPage(PdfPage page, bool includeHiddenLayers)
    {
        var paths = PathCollector.Collect(page, new PathExtractionOptions { IncludeHiddenLayers = includeHiddenLayers });
        var rules = new List<PdfRule>();
        var area = page.CropBox;
        foreach (var path in paths)
        {
            if (path.IsFilled) Fill(path, area, rules);
            if (path.IsStroked) Stroke(path, rules);
        }
        return Join(rules);
    }

    /// <summary>The corner points of a subpath (a closing segment back to the start adds none).</summary>
    private static List<(double X, double Y)> Points(PdfSubpath sub) =>
        sub.Segments.Where(s => s.Kind != PdfPathSegmentKind.ClosePath).Select(s => s.Point).ToList();

    private static void Stroke(PdfPath path, List<PdfRule> rules)
    {
        foreach (var sub in path.Subpaths)
        {
            if (sub.IsCurved) continue;
            var points = Points(sub);
            int count = sub.IsClosed ? points.Count : points.Count - 1;
            for (int i = 0; i < count; i++)
                AddSegment(rules, points[i], points[(i + 1) % points.Count], path.LineWidth, edge: false);
        }
    }

    private static void Fill(PdfPath path, PdfRectangle page, List<PdfRule> rules)
    {
        foreach (var sub in path.Subpaths)
        {
            if (sub.IsCurved || !IsRectangle(Points(sub), out var r)) continue;
            double w = r.Width, h = r.Height;
            if (w <= Thin && h > w) AddSegment(rules, ((r.Left + r.Right) / 2, r.Bottom), ((r.Left + r.Right) / 2, r.Top), w, edge: false);
            else if (h <= Thin) AddSegment(rules, (r.Left, (r.Bottom + r.Top) / 2), (r.Right, (r.Bottom + r.Top) / 2), h, edge: false);
            // a larger area (a background), not the whole page: its edges divide like rules
            else if (w * h < 0.5 * page.Width * page.Height)
            {
                AddSegment(rules, (r.Left, r.Bottom), (r.Right, r.Bottom), 0, edge: true);
                AddSegment(rules, (r.Left, r.Top), (r.Right, r.Top), 0, edge: true);
                AddSegment(rules, (r.Left, r.Bottom), (r.Left, r.Top), 0, edge: true);
                AddSegment(rules, (r.Right, r.Bottom), (r.Right, r.Top), 0, edge: true);
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

    private static void AddSegment(List<PdfRule> rules, (double X, double Y) a, (double X, double Y) b, double width, bool edge)
    {
        double dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
        if (dy <= Straight && dx >= 1)
        {
            double y = (a.Y + b.Y) / 2;
            rules.Add(new PdfRule { Start = (Math.Min(a.X, b.X), y), End = (Math.Max(a.X, b.X), y), IsHorizontal = true, Thickness = width, IsEdgeOfFill = edge });
        }
        else if (dx <= Straight && dy >= 1)
        {
            double x = (a.X + b.X) / 2;
            rules.Add(new PdfRule { Start = (x, Math.Min(a.Y, b.Y)), End = (x, Math.Max(a.Y, b.Y)), IsHorizontal = false, Thickness = width, IsEdgeOfFill = edge });
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
