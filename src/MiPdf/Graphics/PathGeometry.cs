namespace MiPdf.Graphics;

/// <summary>A path as built by the path construction operators, in user space.</summary>
internal sealed class PathGeometry
{
    internal enum Kind { Move, Line, Curve, Close }

    internal readonly record struct Segment(Kind Kind, double X1, double Y1, double X2 = 0, double Y2 = 0, double X3 = 0, double Y3 = 0);

    public List<Segment> Segments { get; } = new();

    public (double X, double Y) Current { get; private set; }

    private (double X, double Y) _start;

    public bool IsEmpty => Segments.Count == 0;

    public void MoveTo(double x, double y)
    {
        Segments.Add(new Segment(Kind.Move, x, y));
        Current = _start = (x, y);
    }

    public void LineTo(double x, double y)
    {
        if (Segments.Count == 0) MoveTo(Current.X, Current.Y);
        Segments.Add(new Segment(Kind.Line, x, y));
        Current = (x, y);
    }

    public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        if (Segments.Count == 0) MoveTo(Current.X, Current.Y);
        Segments.Add(new Segment(Kind.Curve, x1, y1, x2, y2, x3, y3));
        Current = (x3, y3);
    }

    public void Close()
    {
        if (Segments.Count == 0 || Segments[^1].Kind == Kind.Close) return;
        Segments.Add(new Segment(Kind.Close, _start.X, _start.Y));
        Current = _start;
    }

    public void Rectangle(double x, double y, double w, double h)
    {
        MoveTo(x, y);
        LineTo(x + w, y);
        LineTo(x + w, y + h);
        LineTo(x, y + h);
        Close();
    }

    public void Clear()
    {
        Segments.Clear();
        Current = _start = (0, 0);
    }

    /// <summary>
    /// The subpaths as polylines after transforming with <paramref name="matrix"/>, curves flattened to within
    /// <paramref name="tolerance"/> (in the transformed space).
    /// </summary>
    public List<(List<(double X, double Y)> Points, bool Closed)> Flatten(Matrix matrix, double tolerance)
    {
        var result = new List<(List<(double, double)>, bool)>();
        List<(double X, double Y)>? points = null;
        // an open subpath needs a segment (a single moveto paints nothing; "m x y l x y" is a point)
        void AddOpen()
        {
            if (points is { Count: >= 2 }) result.Add((points, false));
        }
        foreach (var s in Segments)
        {
            switch (s.Kind)
            {
                case Kind.Move:
                    AddOpen();
                    points = new List<(double, double)> { matrix.Transform(s.X1, s.Y1) };
                    break;
                case Kind.Line:
                    points!.Add(matrix.Transform(s.X1, s.Y1));
                    break;
                case Kind.Curve:
                    FlattenCurve(points!, points![^1], matrix.Transform(s.X1, s.Y1), matrix.Transform(s.X2, s.Y2), matrix.Transform(s.X3, s.Y3), tolerance);
                    break;
                case Kind.Close:
                    if (points is { Count: >= 2 }) result.Add((points, true));
                    // a following subpath without moveto starts at the same point
                    points = new List<(double, double)> { matrix.Transform(s.X1, s.Y1) };
                    break;
            }
        }
        AddOpen();
        return result;
    }

    /// <summary>The bounds of the path, curves by their extent (not their control points); null for an empty path.</summary>
    public PdfRectangle? Bounds()
    {
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        void Add(double x, double y)
        {
            left = Math.Min(left, x); right = Math.Max(right, x);
            bottom = Math.Min(bottom, y); top = Math.Max(top, y);
        }
        (double X, double Y) current = (0, 0);
        foreach (var s in Segments)
        {
            if (s.Kind == Kind.Curve)
            {
                foreach (var t in CurveExtrema(current.X, s.X1, s.X2, s.X3).Concat(CurveExtrema(current.Y, s.Y1, s.Y2, s.Y3)))
                {
                    var p = CurvePoint(current, (s.X1, s.Y1), (s.X2, s.Y2), (s.X3, s.Y3), t);
                    Add(p.X, p.Y);
                }
                Add(s.X3, s.Y3);
                current = (s.X3, s.Y3);
            }
            else
            {
                Add(s.X1, s.Y1);
                current = (s.X1, s.Y1);
            }
        }
        return left <= right ? new PdfRectangle(left, bottom, right, top) : null;
    }

    /// <summary>The parameters in (0, 1) where a coordinate of a cubic Bézier curve has a zero derivative.</summary>
    public static IEnumerable<double> CurveExtrema(double p0, double p1, double p2, double p3)
    {
        // derivative: 3(a t² + b t + c)
        double a = -p0 + 3 * p1 - 3 * p2 + p3, b = 2 * (p0 - 2 * p1 + p2), c = p1 - p0;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12 && -c / b is > 0 and < 1) yield return -c / b;
            yield break;
        }
        double disc = b * b - 4 * a * c;
        if (disc < 0) yield break;
        double root = Math.Sqrt(disc);
        foreach (var t in new[] { (-b + root) / (2 * a), (-b - root) / (2 * a) })
            if (t is > 0 and < 1) yield return t;
    }

    /// <summary>The point of a cubic Bézier curve at parameter <paramref name="t"/>.</summary>
    public static (double X, double Y) CurvePoint((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3, double t)
    {
        double u = 1 - t;
        double b0 = u * u * u, b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t;
        return (b0 * p0.X + b1 * p1.X + b2 * p2.X + b3 * p3.X, b0 * p0.Y + b1 * p1.Y + b2 * p2.Y + b3 * p3.Y);
    }

    private static void FlattenCurve(List<(double X, double Y)> points, (double X, double Y) p0, (double X, double Y) p1,
        (double X, double Y) p2, (double X, double Y) p3, double tolerance)
    {
        // number of segments from the second differences (Wang's formula)
        double dx = Math.Max(Math.Abs(p0.X - 2 * p1.X + p2.X), Math.Abs(p1.X - 2 * p2.X + p3.X));
        double dy = Math.Max(Math.Abs(p0.Y - 2 * p1.Y + p2.Y), Math.Abs(p1.Y - 2 * p2.Y + p3.Y));
        double d = Math.Sqrt(dx * dx + dy * dy);
        int n = d <= 0 || !double.IsFinite(d) ? 1 : (int)Math.Clamp(Math.Ceiling(Math.Sqrt(0.75 * d / tolerance)), 1, 1000);
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, e = t * t * t;
            points.Add((a * p0.X + b * p1.X + c * p2.X + e * p3.X, a * p0.Y + b * p1.Y + c * p2.Y + e * p3.Y));
        }
    }
}
