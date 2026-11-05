namespace MiPdf.Rendering.Raster;

/// <summary>
/// Scanline polygon rasterizer with anti-aliasing: each pixel row is sampled with <see cref="SubSamples"/> sub-rows,
/// each sub-row covers the spans between its edge crossings exactly (fractional coverage at the span ends). Supports
/// the nonzero winding and even-odd rules. Coordinates are device pixels (y down).
/// </summary>
internal sealed class Rasterizer
{
    private const int SubSamples = 16;

    private struct Edge
    {
        public double X0, Y0, X1, Y1, Slope;
        public int Direction;
    }

    private readonly List<Edge> _edges = new();
    private double _minY = double.MaxValue, _maxY = double.MinValue, _minX = double.MaxValue, _maxX = double.MinValue;

    public bool IsEmpty => _edges.Count == 0;

    public void Clear()
    {
        _edges.Clear();
        (_minX, _minY, _maxX, _maxY) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
    }

    /// <summary>Adds a closed polygon (the closing edge is added automatically).</summary>
    public void AddPolygon(IReadOnlyList<(double X, double Y)> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            AddEdge(a.X, a.Y, b.X, b.Y);
        }
    }

    public void AddEdge(double x0, double y0, double x1, double y1)
    {
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1) || y0 == y1) return;
        int direction = 1;
        if (y0 > y1)
        {
            (x0, y0, x1, y1) = (x1, y1, x0, y0);
            direction = -1;
        }
        _edges.Add(new Edge { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Slope = (x1 - x0) / (y1 - y0), Direction = direction });
        _minY = Math.Min(_minY, y0);
        _maxY = Math.Max(_maxY, y1);
        _minX = Math.Min(_minX, Math.Min(x0, x1));
        _maxX = Math.Max(_maxX, Math.Max(x0, x1));
    }

    /// <summary>
    /// Rasterizes the polygons and calls <paramref name="row"/> for every pixel row with coverage: the row,
    /// the first and last column (inclusive) and the coverage values (0..1) indexed by column.
    /// </summary>
    public void Rasterize(int width, int height, bool evenOdd, Action<int, int, int, float[]> row)
    {
        if (_edges.Count == 0) return;
        int y0 = Math.Max(0, (int)Math.Floor(_minY)), y1 = Math.Min(height - 1, (int)Math.Ceiling(_maxY));
        int x0 = Math.Max(0, (int)Math.Floor(_minX)), x1 = Math.Min(width - 1, (int)Math.Ceiling(_maxX));
        if (y0 > y1 || x0 > x1) return;
        _edges.Sort((a, b) => a.Y0.CompareTo(b.Y0));
        var coverage = new float[width + 2];
        var active = new List<int>();
        var crossings = new List<(double X, int Direction)>();
        int next = 0;
        const float weight = 1f / SubSamples;
        for (int y = y0; y <= y1; y++)
        {
            // edges overlapping this pixel row
            while (next < _edges.Count && _edges[next].Y0 < y + 1) active.Add(next++);
            active.RemoveAll(i => _edges[i].Y1 <= y);
            if (active.Count == 0) continue;
            int spanMin = int.MaxValue, spanMax = int.MinValue;
            for (int s = 0; s < SubSamples; s++)
            {
                double sy = y + (s + 0.5) / SubSamples;
                crossings.Clear();
                foreach (int i in active)
                {
                    var e = _edges[i];
                    if (sy < e.Y0 || sy >= e.Y1) continue;
                    crossings.Add((e.X0 + (sy - e.Y0) * e.Slope, e.Direction));
                }
                if (crossings.Count < 2) continue;
                crossings.Sort((a, b) => a.X.CompareTo(b.X));
                int winding = 0;
                for (int c = 0; c < crossings.Count - 1; c++)
                {
                    winding += crossings[c].Direction;
                    bool inside = evenOdd ? ((c + 1) & 1) == 1 : winding != 0;
                    if (!inside) continue;
                    double a = Math.Clamp(crossings[c].X, 0, width), b = Math.Clamp(crossings[c + 1].X, 0, width);
                    if (b <= a) continue;
                    AddSpan(coverage, a, b, weight);
                    spanMin = Math.Min(spanMin, (int)a);
                    spanMax = Math.Max(spanMax, Math.Min(width - 1, (int)b));
                }
            }
            if (spanMin > spanMax) continue;
            for (int x = spanMin; x <= spanMax; x++) coverage[x] = Math.Min(coverage[x], 1f);
            row(y, spanMin, spanMax, coverage);
            Array.Clear(coverage, spanMin, spanMax - spanMin + 1);
        }
    }

    private static void AddSpan(float[] coverage, double a, double b, float weight)
    {
        int ia = (int)a, ib = (int)b;
        if (ia == ib)
        {
            coverage[ia] += (float)(b - a) * weight;
            return;
        }
        coverage[ia] += (float)(ia + 1 - a) * weight;
        for (int i = ia + 1; i < ib; i++) coverage[i] += weight;
        if (ib < coverage.Length) coverage[ib] += (float)(b - ib) * weight;
    }
}
