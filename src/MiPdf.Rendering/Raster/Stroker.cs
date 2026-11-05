namespace MiPdf.Rendering.Raster;

/// <summary>Line style of a stroke (ISO 32000-2, 8.4.3).</summary>
internal sealed record StrokeStyle(double Width, int Cap, int Join, double MiterLimit, double[] Dash, double DashPhase);

/// <summary>
/// Converts polylines into polygons covering the stroke: a quadrilateral per segment, joins and caps as separate
/// polygons. All polygons are oriented the same way, so filling them with the nonzero rule gives their union.
/// Works in user space (the polygons are transformed afterwards), so strokes are correct for any transformation.
/// </summary>
internal static class Stroker
{
    private const int MaxDashSegments = 100_000;

    public static List<List<(double X, double Y)>> Stroke(List<(List<(double X, double Y)> Points, bool Closed)> subpaths,
        StrokeStyle style, double tolerance)
    {
        var polygons = new List<List<(double, double)>>();
        double half = style.Width / 2;
        if (half <= 0) return polygons;
        int roundSteps = Math.Clamp((int)Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - tolerance / half))), 8, 128);
        foreach (var (points, closed) in subpaths)
        {
            var line = Dedupe(points, closed);
            if (line.Count == 0) continue;
            var pieces = style.Dash.Length > 0 && style.Dash.Any(d => d > 0) ? Dash(line, closed, style) : new() { (line, closed) };
            foreach (var (piece, pieceClosed) in pieces) StrokePolyline(piece, pieceClosed, style, half, roundSteps, polygons);
        }
        return polygons;
    }

    private static List<(double X, double Y)> Dedupe(List<(double X, double Y)> points, bool closed)
    {
        var result = new List<(double X, double Y)>(points.Count);
        foreach (var p in points)
            if (result.Count == 0 || Math.Abs(result[^1].X - p.X) > 1e-9 || Math.Abs(result[^1].Y - p.Y) > 1e-9) result.Add(p);
        if (closed && result.Count > 1 && Math.Abs(result[0].X - result[^1].X) < 1e-9 && Math.Abs(result[0].Y - result[^1].Y) < 1e-9)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static void StrokePolyline(List<(double X, double Y)> line, bool closed, StrokeStyle style, double half, int roundSteps,
        List<List<(double, double)>> polygons)
    {
        if (line.Count == 1)
        {
            // a point: only round and square caps paint something
            if (style.Cap == 1) polygons.Add(Circle(line[0], half, roundSteps));
            else if (style.Cap == 2)
                polygons.Add(Oriented(new() { (line[0].X - half, line[0].Y - half), (line[0].X + half, line[0].Y - half), (line[0].X + half, line[0].Y + half), (line[0].X - half, line[0].Y + half) }));
            return;
        }
        int segments = closed ? line.Count : line.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            var a = line[i];
            var b = line[(i + 1) % line.Count];
            var (dx, dy) = Direction(a, b);
            // square caps extend the first and last segment of open lines
            if (!closed && style.Cap == 2)
            {
                if (i == 0) a = (a.X - dx * half, a.Y - dy * half);
                if (i == segments - 1) b = (b.X + dx * half, b.Y + dy * half);
            }
            double nx = -dy * half, ny = dx * half;
            polygons.Add(Oriented(new() { (a.X + nx, a.Y + ny), (b.X + nx, b.Y + ny), (b.X - nx, b.Y - ny), (a.X - nx, a.Y - ny) }));
        }
        // joins at the inner vertices (and at the start of closed lines)
        for (int i = closed ? 0 : 1; i < (closed ? line.Count : line.Count - 1); i++)
        {
            var prev = line[(i - 1 + line.Count) % line.Count];
            var p = line[i];
            var next = line[(i + 1) % line.Count];
            Join(prev, p, next, style, half, roundSteps, polygons);
        }
        if (!closed && style.Cap == 1)
        {
            polygons.Add(Circle(line[0], half, roundSteps));
            polygons.Add(Circle(line[^1], half, roundSteps));
        }
    }

    private static void Join((double X, double Y) prev, (double X, double Y) p, (double X, double Y) next, StrokeStyle style,
        double half, int roundSteps, List<List<(double, double)>> polygons)
    {
        var (d1x, d1y) = Direction(prev, p);
        var (d2x, d2y) = Direction(p, next);
        double cross = d1x * d2y - d1y * d2x;
        if (Math.Abs(cross) < 1e-12 && d1x * d2x + d1y * d2y > 0) return; // straight on
        if (style.Join == 1)
        {
            polygons.Add(Circle(p, half, roundSteps));
            return;
        }
        // the outer side of the turn
        double side = cross > 0 ? -1 : 1;
        var a = (X: p.X - d1y * half * side, Y: p.Y + d1x * half * side);
        var b = (X: p.X - d2y * half * side, Y: p.Y + d2x * half * side);
        if (style.Join == 0)
        {
            double cosTheta = -(d1x * d2x + d1y * d2y); // angle between the segments
            double sinHalf = Math.Sqrt(Math.Max(0, (1 - cosTheta) / 2));
            if (sinHalf > 1e-9 && 1 / sinHalf <= style.MiterLimit)
            {
                // intersection of the two offset lines
                double denominator = d1x * d2y - d1y * d2x;
                if (Math.Abs(denominator) > 1e-12)
                {
                    double t = ((b.X - a.X) * d2y - (b.Y - a.Y) * d2x) / denominator;
                    var tip = (a.X + d1x * t, a.Y + d1y * t);
                    polygons.Add(Oriented(new() { p, a, tip, b }));
                    return;
                }
            }
        }
        polygons.Add(Oriented(new() { p, a, b }));
    }

    private static (double X, double Y) Direction((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        return length > 0 ? (dx / length, dy / length) : (1, 0);
    }

    private static List<(double, double)> Circle((double X, double Y) center, double radius, int steps)
    {
        var points = new List<(double, double)>(steps);
        for (int i = 0; i < steps; i++)
        {
            double angle = 2 * Math.PI * i / steps;
            points.Add((center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }
        return points;
    }

    /// <summary>Makes the polygon counterclockwise (positive area), so that overlapping parts do not cancel out.</summary>
    private static List<(double, double)> Oriented(List<(double X, double Y)> polygon)
    {
        double area = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        if (area < 0) polygon.Reverse();
        return polygon.Select(p => (p.X, p.Y)).ToList();
    }

    /// <summary>Splits a polyline into the dashes of the dash pattern (open pieces).</summary>
    private static List<(List<(double X, double Y)>, bool)> Dash(List<(double X, double Y)> line, bool closed, StrokeStyle style)
    {
        var result = new List<(List<(double X, double Y)>, bool)>();
        var pattern = style.Dash.Select(Math.Abs).ToArray();
        if (pattern.Length % 2 == 1) pattern = pattern.Concat(pattern).ToArray();
        double total = pattern.Sum();
        if (total <= 0) return new() { (line, closed) };
        // position in the pattern after the phase
        double phase = style.DashPhase % total;
        if (phase < 0) phase += total;
        int index = 0;
        while (phase >= pattern[index])
        {
            phase -= pattern[index];
            index = (index + 1) % pattern.Length;
        }
        double remaining = pattern[index] - phase;
        bool on = index % 2 == 0;
        var current = on ? new List<(double X, double Y)> { line[0] } : null;
        int count = closed ? line.Count : line.Count - 1;
        for (int i = 0; i < count && result.Count < MaxDashSegments; i++)
        {
            var a = line[i];
            var b = line[(i + 1) % line.Count];
            double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            double position = 0;
            while (length - position > remaining && result.Count < MaxDashSegments)
            {
                position += remaining;
                double t = position / length;
                var point = (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                if (on)
                {
                    current!.Add(point);
                    result.Add((current, false));
                    current = null;
                }
                else current = new List<(double X, double Y)> { point };
                on = !on;
                index = (index + 1) % pattern.Length;
                remaining = pattern[index];
            }
            remaining -= length - position;
            current?.Add(b);
        }
        if (current is { Count: > 1 }) result.Add((current, false));
        return result;
    }
}
