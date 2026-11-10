using MiPdf.Graphics;

namespace MiPdf.PostScript.Graphics;

internal enum PsSegmentKind : byte
{
    MoveTo,
    LineTo,
    CurveTo,
    ClosePath,
}

/// <summary>A path segment in device space; a curve uses all three points, moves and lines only the last one.</summary>
internal readonly record struct PsSegment(PsSegmentKind Kind, double X1, double Y1, double X2, double Y2, double X3, double Y3)
{
    public static PsSegment Move(double x, double y) => new(PsSegmentKind.MoveTo, 0, 0, 0, 0, x, y);
    public static PsSegment Line(double x, double y) => new(PsSegmentKind.LineTo, 0, 0, 0, 0, x, y);
    public static readonly PsSegment Close = new(PsSegmentKind.ClosePath, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The current path (PLRM 4.4): segments in device space, as PostScript transforms coordinates by the CTM when the
/// path is built. The current point is the end of the last segment (the start of the subpath after a closepath).
/// </summary>
internal sealed class PsPath
{
    private readonly List<PsSegment> _segments;
    private double _startX, _startY;

    public PsPath() => _segments = new List<PsSegment>();

    private PsPath(List<PsSegment> segments, double startX, double startY, bool hasPoint, double x, double y)
    {
        _segments = segments;
        _startX = startX;
        _startY = startY;
        HasCurrentPoint = hasPoint;
        CurrentX = x;
        CurrentY = y;
    }

    public IReadOnlyList<PsSegment> Segments => _segments;

    public bool HasCurrentPoint { get; private set; }

    public double CurrentX { get; private set; }

    public double CurrentY { get; private set; }

    public bool IsEmpty => _segments.Count == 0;

    public PsPath Clone() => new(new List<PsSegment>(_segments), _startX, _startY, HasCurrentPoint, CurrentX, CurrentY);

    public void Clear()
    {
        _segments.Clear();
        HasCurrentPoint = false;
    }

    public void MoveTo(double x, double y)
    {
        // consecutive moves: only the last one counts
        if (_segments.Count > 0 && _segments[^1].Kind == PsSegmentKind.MoveTo) _segments[^1] = PsSegment.Move(x, y);
        else _segments.Add(PsSegment.Move(x, y));
        _startX = CurrentX = x;
        _startY = CurrentY = y;
        HasCurrentPoint = true;
    }

    public void LineTo(double x, double y)
    {
        RequirePoint();
        _segments.Add(PsSegment.Line(x, y));
        CurrentX = x;
        CurrentY = y;
    }

    public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        RequirePoint();
        _segments.Add(new PsSegment(PsSegmentKind.CurveTo, x1, y1, x2, y2, x3, y3));
        CurrentX = x3;
        CurrentY = y3;
    }

    public void ClosePath()
    {
        if (!HasCurrentPoint) return;
        if (_segments.Count > 0 && _segments[^1].Kind is not (PsSegmentKind.ClosePath or PsSegmentKind.MoveTo)) _segments.Add(PsSegment.Close);
        CurrentX = _startX;
        CurrentY = _startY;
    }

    private void RequirePoint()
    {
        if (!HasCurrentPoint) throw new Interpreter.PsError("nocurrentpoint");
        // a segment after closepath starts a new subpath at the same point
        if (_segments.Count > 0 && _segments[^1].Kind == PsSegmentKind.ClosePath) _segments.Add(PsSegment.Move(CurrentX, CurrentY));
    }

    /// <summary>Adds all segments of another path (in device space).</summary>
    public void Append(PsPath other)
    {
        foreach (var s in other._segments)
        {
            switch (s.Kind)
            {
                case PsSegmentKind.MoveTo: MoveTo(s.X3, s.Y3); break;
                case PsSegmentKind.LineTo: LineTo(s.X3, s.Y3); break;
                case PsSegmentKind.CurveTo: CurveTo(s.X1, s.Y1, s.X2, s.Y2, s.X3, s.Y3); break;
                default: ClosePath(); break;
            }
        }
    }

    /// <summary>
    /// An arc around (cx, cy) in user space from angle a1 to a2 (degrees), counterclockwise or (negative) clockwise, as
    /// Bézier curves of at most 90 degrees transformed to device space; a line joins it to the current point.
    /// </summary>
    public void Arc(Matrix ctm, double cx, double cy, double r, double a1, double a2, bool clockwise)
    {
        if (clockwise) while (a2 > a1) a2 -= 360;
        else while (a2 < a1) a2 += 360;
        double sweep = a2 - a1;
        var (sx, sy) = ctm.Transform(cx + r * Cos(a1), cy + r * Sin(a1));
        if (HasCurrentPoint) LineTo(sx, sy);
        else MoveTo(sx, sy);
        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / 90 - 1e-9));
        if (sweep == 0) return;
        double step = sweep / pieces;
        double k = 4.0 / 3 * Math.Tan(step * Math.PI / 720) * r; // control point distance
        double angle = a1;
        for (int i = 0; i < pieces; i++)
        {
            double b = angle + step;
            double x0 = cx + r * Cos(angle), y0 = cy + r * Sin(angle);
            double x3 = cx + r * Cos(b), y3 = cy + r * Sin(b);
            var (p1x, p1y) = ctm.Transform(x0 - k * Sin(angle), y0 + k * Cos(angle));
            var (p2x, p2y) = ctm.Transform(x3 + k * Sin(b), y3 - k * Cos(b));
            var (p3x, p3y) = ctm.Transform(x3, y3);
            CurveTo(p1x, p1y, p2x, p2y, p3x, p3y);
            angle = b;
        }
    }

    private static double Cos(double degrees) => Math.Cos(degrees * Math.PI / 180);
    private static double Sin(double degrees) => Math.Sin(degrees * Math.PI / 180);

    /// <summary>The bounding box of the points (control points included, as <c>pathbbox</c> may).</summary>
    public (double X0, double Y0, double X1, double Y1)? Bounds()
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        bool any = false;
        foreach (var s in _segments)
        {
            if (s.Kind == PsSegmentKind.ClosePath) continue;
            Include(s.X3, s.Y3);
            if (s.Kind == PsSegmentKind.CurveTo)
            {
                Include(s.X1, s.Y1);
                Include(s.X2, s.Y2);
            }
        }
        return any ? (x0, y0, x1, y1) : null;

        void Include(double x, double y)
        {
            any = true;
            x0 = Math.Min(x0, x);
            y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x);
            y1 = Math.Max(y1, y);
        }
    }

    /// <summary>The path with curves replaced by line segments within <paramref name="tolerance"/> (device units).</summary>
    public PsPath Flatten(double tolerance)
    {
        tolerance = Math.Max(tolerance, 0.01);
        var result = new PsPath();
        double cx = 0, cy = 0;
        foreach (var s in _segments)
        {
            switch (s.Kind)
            {
                case PsSegmentKind.MoveTo:
                    result.MoveTo(s.X3, s.Y3);
                    break;
                case PsSegmentKind.LineTo:
                    result.LineTo(s.X3, s.Y3);
                    break;
                case PsSegmentKind.CurveTo:
                {
                    // number of steps from the second differences of the control polygon
                    double dd = Math.Max(Math.Abs(cx - 2 * s.X1 + s.X2) + Math.Abs(cy - 2 * s.Y1 + s.Y2),
                        Math.Abs(s.X1 - 2 * s.X2 + s.X3) + Math.Abs(s.Y1 - 2 * s.Y2 + s.Y3));
                    int n = Math.Clamp((int)Math.Ceiling(Math.Sqrt(dd * 0.75 / tolerance)), 1, 1000);
                    for (int i = 1; i <= n; i++)
                    {
                        double t = (double)i / n, u = 1 - t;
                        double x = u * u * u * cx + 3 * u * u * t * s.X1 + 3 * u * t * t * s.X2 + t * t * t * s.X3;
                        double y = u * u * u * cy + 3 * u * u * t * s.Y1 + 3 * u * t * t * s.Y2 + t * t * t * s.Y3;
                        result.LineTo(x, y);
                    }
                    break;
                }
                default:
                    result.ClosePath();
                    break;
            }
            if (s.Kind != PsSegmentKind.ClosePath)
            {
                cx = s.X3;
                cy = s.Y3;
            }
            else
            {
                cx = result.CurrentX;
                cy = result.CurrentY;
            }
        }
        result.HasCurrentPoint = HasCurrentPoint;
        result.CurrentX = CurrentX;
        result.CurrentY = CurrentY;
        return result;
    }

    /// <summary>Each subpath in reverse order (<c>reversepath</c>).</summary>
    public PsPath Reverse()
    {
        var result = new PsPath();
        int i = 0;
        while (i < _segments.Count)
        {
            if (_segments[i].Kind != PsSegmentKind.MoveTo)
            {
                i++;
                continue;
            }
            int start = i++;
            while (i < _segments.Count && _segments[i].Kind is PsSegmentKind.LineTo or PsSegmentKind.CurveTo) i++;
            bool closed = i < _segments.Count && _segments[i].Kind == PsSegmentKind.ClosePath;
            // points of the subpath
            var last = _segments[i - 1];
            result.MoveTo(last.X3, last.Y3);
            for (int j = i - 1; j > start; j--)
            {
                var s = _segments[j];
                var prev = _segments[j - 1];
                if (s.Kind == PsSegmentKind.CurveTo) result.CurveTo(s.X2, s.Y2, s.X1, s.Y1, prev.X3, prev.Y3);
                else result.LineTo(prev.X3, prev.Y3);
            }
            if (closed)
            {
                result.ClosePath();
                i++;
            }
        }
        return result;
    }

    /// <summary>The path transformed (for output in another coordinate system).</summary>
    public IEnumerable<PsSegment> Transformed(Matrix m)
    {
        foreach (var s in _segments)
        {
            if (s.Kind == PsSegmentKind.ClosePath)
            {
                yield return s;
                continue;
            }
            var (x3, y3) = m.Transform(s.X3, s.Y3);
            if (s.Kind == PsSegmentKind.CurveTo)
            {
                var (x1, y1) = m.Transform(s.X1, s.Y1);
                var (x2, y2) = m.Transform(s.X2, s.Y2);
                yield return new PsSegment(s.Kind, x1, y1, x2, y2, x3, y3);
            }
            else yield return s with { X3 = x3, Y3 = y3 };
        }
    }
}

internal static class MatrixExtensions
{
    /// <summary>The inverse; false for a singular matrix.</summary>
    public static bool TryInvert(this Matrix m, out Matrix inverse)
    {
        double det = m.A * m.D - m.B * m.C;
        if (det == 0 || !double.IsFinite(det))
        {
            inverse = Matrix.Identity;
            return false;
        }
        inverse = new Matrix(m.D / det, -m.B / det, -m.C / det, m.A / det,
            (m.C * m.F - m.D * m.E) / det, (m.B * m.E - m.A * m.F) / det);
        return true;
    }

    /// <summary>The matrix applied to a distance (without translation).</summary>
    public static (double X, double Y) TransformDistance(this Matrix m, double dx, double dy) => (m.A * dx + m.C * dy, m.B * dx + m.D * dy);
}
