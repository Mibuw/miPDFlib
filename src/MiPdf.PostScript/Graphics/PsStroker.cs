using MiPdf.Graphics;

namespace MiPdf.PostScript.Graphics;

/// <summary>
/// <c>strokepath</c>: the outline of the area a stroke would paint, as a path to fill with the nonzero rule. The stroke
/// is built in user space (line width and dashes are user-space lengths) from rectangles per segment, joins and caps,
/// all oriented counterclockwise so that their union is filled.
/// </summary>
internal static class PsStroker
{
    public static PsPath Outline(PsGState gs)
    {
        var result = new PsPath();
        if (!gs.Ctm.TryInvert(out var toUser)) return result;
        var ctm = gs.Ctm;
        double scale = Math.Sqrt(Math.Abs(ctm.A * ctm.D - ctm.B * ctm.C));
        // a zero line width is the thinnest line the device can draw: one device unit
        double half = gs.LineWidth > 0 ? gs.LineWidth / 2 : 0.5 / Math.Max(scale, 1e-9);
        foreach (var (points, closed) in Subpaths(gs.Path.Flatten(gs.Flatness), toUser))
        {
            foreach (var (piece, pieceClosed) in Dash(points, closed, gs.Dash, gs.DashOffset))
                StrokePolyline(piece, pieceClosed, half, gs, ctm, result);
        }
        return result;
    }

    /// <summary>The subpaths of a flattened device-space path as user-space polylines.</summary>
    private static IEnumerable<(List<(double X, double Y)> Points, bool Closed)> Subpaths(PsPath flat, Matrix toUser)
    {
        List<(double, double)>? current = null;
        foreach (var s in flat.Segments)
        {
            switch (s.Kind)
            {
                case PsSegmentKind.MoveTo:
                    if (current != null) yield return (current, false);
                    current = new List<(double, double)> { toUser.Transform(s.X3, s.Y3) };
                    break;
                case PsSegmentKind.LineTo:
                    current?.Add(toUser.Transform(s.X3, s.Y3));
                    break;
                case PsSegmentKind.ClosePath:
                    if (current != null) yield return (current, true);
                    current = null;
                    break;
            }
        }
        if (current != null) yield return (current, false);
    }

    /// <summary>Splits polylines by the dash pattern (lengths in user space).</summary>
    private static IEnumerable<(List<(double X, double Y)>, bool)> Dash(List<(double X, double Y)> points, bool closed, double[] dash, double offset)
    {
        if (dash.Length == 0)
        {
            yield return (points, closed);
            yield break;
        }
        var all = new List<(double X, double Y)>(points);
        if (closed && all.Count > 0) all.Add(all[0]);
        // the elements alternate between on and off cyclically: an odd pattern is repeated once ([6] is 6 on, 6 off)
        if (dash.Length % 2 == 1) dash = dash.Concat(dash).ToArray();
        double total = dash.Sum();
        if (total <= 0)
        {
            yield return (points, closed);
            yield break;
        }
        // position in the pattern at the start
        double phase = offset % total;
        if (phase < 0) phase += total;
        int index = 0;
        while (phase >= dash[index])
        {
            phase -= dash[index];
            index = (index + 1) % dash.Length;
        }
        double remaining = dash[index] - phase;
        bool on = index % 2 == 0;
        var piece = on ? new List<(double X, double Y)> { all[0] } : null;
        for (int i = 1; i < all.Count; i++)
        {
            var (x0, y0) = all[i - 1];
            var (x1, y1) = all[i];
            double length = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)), done = 0;
            int guard = 0;
            while (length - done > remaining && guard++ < 100_000)
            {
                done += remaining;
                double t = done / length;
                var p = (x0 + (x1 - x0) * t, y0 + (y1 - y0) * t);
                if (on)
                {
                    piece!.Add(p);
                    yield return (piece, false);
                    piece = null;
                }
                else piece = new List<(double X, double Y)> { p };
                on = !on;
                index = (index + 1) % dash.Length;
                remaining = dash[index];
            }
            remaining -= length - done;
            piece?.Add((x1, y1));
        }
        if (piece != null && piece.Count > 1) yield return (piece, false);
    }

    private static void StrokePolyline(List<(double X, double Y)> points, bool closed, double half, PsGState gs, Matrix ctm, PsPath result)
    {
        // drop repeated points
        var p = new List<(double X, double Y)>();
        foreach (var point in points)
            if (p.Count == 0 || Math.Abs(point.X - p[^1].X) > 1e-12 || Math.Abs(point.Y - p[^1].Y) > 1e-12) p.Add(point);
        if (closed && p.Count > 1 && Math.Abs(p[0].X - p[^1].X) < 1e-12 && Math.Abs(p[0].Y - p[^1].Y) < 1e-12) p.RemoveAt(p.Count - 1);
        if (p.Count == 1)
        {
            // a single point: round and square caps paint a dot
            if (gs.LineCap == 1) Circle(p[0], half, ctm, result);
            else if (gs.LineCap == 2) Polygon(new[] { (p[0].X - half, p[0].Y - half), (p[0].X + half, p[0].Y - half), (p[0].X + half, p[0].Y + half), (p[0].X - half, p[0].Y + half) }, ctm, result);
            return;
        }
        int segments = closed ? p.Count : p.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            var (dx, dy) = Unit(a, b);
            // square caps extend the first and last segment
            if (!closed && gs.LineCap == 2)
            {
                if (i == 0) a = (a.X - dx * half, a.Y - dy * half);
                if (i == segments - 1) b = (b.X + dx * half, b.Y + dy * half);
            }
            double nx = -dy * half, ny = dx * half;
            Polygon(new[] { (a.X + nx, a.Y + ny), (a.X - nx, a.Y - ny), (b.X - nx, b.Y - ny), (b.X + nx, b.Y + ny) }, ctm, result);
        }
        // joins at the inner points (and the start of a closed path)
        for (int i = closed ? 0 : 1; i < (closed ? p.Count : p.Count - 1); i++)
            Join(p[(i - 1 + p.Count) % p.Count], p[i], p[(i + 1) % p.Count], half, gs, ctm, result);
        if (!closed && gs.LineCap == 1)
        {
            Circle(p[0], half, ctm, result);
            Circle(p[^1], half, ctm, result);
        }
    }

    private static void Join((double X, double Y) a, (double X, double Y) v, (double X, double Y) b, double half, PsGState gs, Matrix ctm, PsPath result)
    {
        if (gs.LineJoin == 1)
        {
            Circle(v, half, ctm, result);
            return;
        }
        var (d1x, d1y) = Unit(a, v);
        var (d2x, d2y) = Unit(v, b);
        double cross = d1x * d2y - d1y * d2x;
        if (Math.Abs(cross) < 1e-12) return; // straight on
        // the outer side of the turn
        double side = cross > 0 ? -1 : 1;
        var o1 = (v.X - d1y * half * side, v.Y + d1x * half * side);
        var o2 = (v.X - d2y * half * side, v.Y + d2x * half * side);
        double cos = Math.Clamp(d1x * d2x + d1y * d2y, -1, 1);
        double theta = Math.PI - Math.Acos(cos); // angle between the segments
        bool miter = gs.LineJoin == 0 && 1 / Math.Sin(theta / 2) <= gs.MiterLimit;
        if (miter)
        {
            // the tip where the outer edges meet
            double length = half / Math.Sin(theta / 2);
            double bx = o1.Item1 + o2.Item1 - 2 * v.X, by = o1.Item2 + o2.Item2 - 2 * v.Y;
            double bl = Math.Sqrt(bx * bx + by * by);
            var tip = (v.X + bx / bl * length, v.Y + by / bl * length);
            Polygon(new[] { v, o1, tip, o2 }, ctm, result);
        }
        else Polygon(new[] { v, o1, o2 }, ctm, result);
    }

    private static (double X, double Y) Unit((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy);
        return l == 0 ? (1, 0) : (dx / l, dy / l);
    }

    /// <summary>Adds a closed polygon, oriented counterclockwise in device space.</summary>
    private static void Polygon(IReadOnlyList<(double X, double Y)> points, Matrix ctm, PsPath result)
    {
        var device = points.Select(q => ctm.Transform(q.X, q.Y)).ToList();
        double area = 0;
        for (int i = 0; i < device.Count; i++)
        {
            var (x0, y0) = device[i];
            var (x1, y1) = device[(i + 1) % device.Count];
            area += x0 * y1 - x1 * y0;
        }
        if (Math.Abs(area) < 1e-12) return;
        if (area < 0) device.Reverse();
        result.MoveTo(device[0].X, device[0].Y);
        for (int i = 1; i < device.Count; i++) result.LineTo(device[i].X, device[i].Y);
        result.ClosePath();
    }

    /// <summary>A circle (an ellipse in device space), counterclockwise.</summary>
    private static void Circle((double X, double Y) c, double r, Matrix ctm, PsPath result)
    {
        var points = new (double, double)[32];
        for (int i = 0; i < points.Length; i++)
        {
            double a = 2 * Math.PI * i / points.Length;
            points[i] = (c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }
        Polygon(points, ctm, result);
    }
}
