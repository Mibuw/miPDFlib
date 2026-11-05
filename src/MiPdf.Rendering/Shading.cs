using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.Rendering.Raster;

namespace MiPdf.Rendering;

/// <summary>An RGBA image covering a rectangle of the surface (pattern and shading colors per device pixel).</summary>
internal sealed class Layer
{
    public Layer(int x0, int y0, int width, int height)
    {
        X0 = x0;
        Y0 = y0;
        Width = width;
        Height = height;
        Rgba = new byte[(long)width * height * 4];
    }

    public int X0 { get; }
    public int Y0 { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public long IndexOf(int x, int y) => x < X0 || y < Y0 || x >= X0 + Width || y >= Y0 + Height ? -1 : ((long)(y - Y0) * Width + (x - X0)) * 4;

    public void Set(long i, Rgb c, byte alpha = 255)
    {
        Rgba[i] = c.R;
        Rgba[i + 1] = c.G;
        Rgba[i + 2] = c.B;
        Rgba[i + 3] = alpha;
    }
}

internal static class MatrixMath
{
    public static Matrix? Invert(Matrix m)
    {
        double det = m.A * m.D - m.B * m.C;
        if (Math.Abs(det) < 1e-14 || !double.IsFinite(det)) return null;
        return new Matrix(m.D / det, -m.B / det, -m.C / det, m.A / det, (m.C * m.F - m.D * m.E) / det, (m.B * m.E - m.A * m.F) / det);
    }

    public static Matrix FromArray(PdfArray? array) => array is { Count: 6 }
        ? new Matrix(array.GetDouble(0), array.GetDouble(1), array.GetDouble(2), array.GetDouble(3), array.GetDouble(4), array.GetDouble(5))
        : Matrix.Identity;
}

/// <summary>
/// Shadings (ISO 32000-2, 8.7.4.5): function-based (1), axial (2), radial (3), free-form and lattice-form triangle
/// meshes (4, 5), Coons and tensor-product patch meshes (6, 7). They are rendered into a <see cref="Layer"/> in
/// device space; pixels outside the shaded area stay transparent.
/// </summary>
internal abstract class Shading
{
    protected ColorSpace Space = ColorSpace.Gray;
    protected PdfFunction? Function;

    /// <summary>The background color (used by shading patterns only), or null.</summary>
    public Rgb? Background { get; private set; }

    /// <summary>The shading's bounding box in shading space, or null.</summary>
    public PdfRectangle? BBox { get; private set; }

    public static Shading? Load(PdfObject? value, PdfDictionary? resources)
    {
        if (value?.Resolve() is not PdfDictionary dict) return null;
        try
        {
            Shading? shading = dict.GetInt(PdfName.Get("ShadingType"), 0) switch
            {
                1 => new FunctionShading(dict),
                2 => new AxialShading(dict, radial: false),
                3 => new AxialShading(dict, radial: true),
                4 or 5 or 6 or 7 when dict is PdfStream stream => new MeshShading(stream),
                _ => null,
            };
            if (shading == null) return null;
            shading.Space = ColorSpace.Resolve(dict.Get(PdfName.ColorSpace), resources);
            shading.Function = PdfFunction.LoadCombined(dict.Get(PdfName.Get("Function")));
            if (dict.GetArray(PdfName.Get("Background")) is { Count: > 0 } bg)
                shading.Background = shading.Space.ToRgb(Pad(bg.Select(v => (v.Resolve() as PdfNumber)?.Value ?? 0).ToArray(), shading.Space.Components));
            shading.BBox = dict.GetRectangle(PdfName.BBox);
            return !shading.Space.IsNone && shading.Prepare() ? shading : null;
        }
        catch (Exception ex) when (ex is PdfException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    private static double[] Pad(double[] values, int n) => values.Length >= n ? values : values.Concat(new double[n - values.Length]).ToArray();

    protected virtual bool Prepare() => true;

    /// <summary>Renders the shading into the layer; <paramref name="toDevice"/> maps shading space to device pixels.</summary>
    public abstract void Render(Layer layer, Matrix toDevice);

    /// <summary>The color of shading values: function input(s) if there is a function, otherwise color components.</summary>
    protected Rgb ColorOf(ReadOnlySpan<double> values)
    {
        if (Function == null) return Space.ToRgb(values.Length >= Space.Components ? values : Pad(values.ToArray(), Space.Components));
        Span<double> output = stackalloc double[Math.Max(Function.Outputs, Space.Components)];
        output.Clear();
        Function.Evaluate(values, output[..Function.Outputs]);
        return Space.ToRgb(output);
    }

    /// <summary>A table of colors for a parameter range (1-input functions evaluated once per entry).</summary>
    protected Rgb[] Lookup(double t0, double t1, int size = 1024)
    {
        var table = new Rgb[size];
        Span<double> t = stackalloc double[1];
        for (int i = 0; i < size; i++)
        {
            t[0] = t0 + (t1 - t0) * i / (size - 1);
            table[i] = ColorOf(t);
        }
        return table;
    }

    protected static Rgb FromTable(Rgb[] table, double t0, double t1, double t)
    {
        double f = t1 == t0 ? 0 : (t - t0) / (t1 - t0);
        return table[Math.Clamp((int)Math.Round(f * (table.Length - 1)), 0, table.Length - 1)];
    }

    protected static double[] Numbers(PdfArray? array) =>
        array?.Select(v => (v.Resolve() as PdfNumber)?.Value ?? 0).ToArray() ?? Array.Empty<double>();

    // ------------------------------------------------------------------ type 1

    private sealed class FunctionShading : Shading
    {
        private readonly double[] _domain;
        private readonly Matrix _matrix;

        public FunctionShading(PdfDictionary dict)
        {
            _domain = Numbers(dict.GetArray(PdfName.Get("Domain"))) is { Length: 4 } d ? d : new double[] { 0, 1, 0, 1 };
            _matrix = MatrixMath.FromArray(dict.GetArray(PdfName.Get("Matrix")));
        }

        protected override bool Prepare() => Function != null && Function.Inputs == 2;

        public override void Render(Layer layer, Matrix toDevice)
        {
            if (MatrixMath.Invert(_matrix.Multiply(toDevice)) is not { } inverse) return;
            Span<double> xy = stackalloc double[2];
            for (int y = layer.Y0; y < layer.Y0 + layer.Height; y++)
                for (int x = layer.X0; x < layer.X0 + layer.Width; x++)
                {
                    var (u, v) = inverse.Transform(x + 0.5, y + 0.5);
                    if (u < _domain[0] || u > _domain[1] || v < _domain[2] || v > _domain[3]) continue;
                    xy[0] = u;
                    xy[1] = v;
                    layer.Set(layer.IndexOf(x, y), ColorOf(xy));
                }
        }
    }

    // ------------------------------------------------------------------ types 2 and 3

    private sealed class AxialShading : Shading
    {
        private readonly bool _radial;
        private readonly double[] _coords;
        private readonly double _t0, _t1;
        private readonly bool _extend0, _extend1;
        private Rgb[] _table = Array.Empty<Rgb>();

        public AxialShading(PdfDictionary dict, bool radial)
        {
            _radial = radial;
            _coords = Numbers(dict.GetArray(PdfName.Get("Coords")));
            var domain = Numbers(dict.GetArray(PdfName.Get("Domain")));
            (_t0, _t1) = domain.Length >= 2 ? (domain[0], domain[1]) : (0, 1);
            if (dict.GetArray(PdfName.Get("Extend")) is { Count: >= 2 } extend)
            {
                _extend0 = extend[0].Resolve() is PdfBoolean { Value: true };
                _extend1 = extend[1].Resolve() is PdfBoolean { Value: true };
            }
        }

        protected override bool Prepare()
        {
            if (_coords.Length < (_radial ? 6 : 4) || _coords.Any(c => !double.IsFinite(c))) return false;
            if (Function == null || Function.Inputs != 1) return false; // the function is required
            _table = Lookup(_t0, _t1);
            return true;
        }

        public override void Render(Layer layer, Matrix toDevice)
        {
            if (MatrixMath.Invert(toDevice) is not { } inverse) return;
            for (int y = layer.Y0; y < layer.Y0 + layer.Height; y++)
                for (int x = layer.X0; x < layer.X0 + layer.Width; x++)
                {
                    var (px, py) = inverse.Transform(x + 0.5, y + 0.5);
                    double? s = _radial ? RadialParameter(px, py) : AxialParameter(px, py);
                    if (s is not { } value) continue;
                    layer.Set(layer.IndexOf(x, y), FromTable(_table, 0, 1, value));
                }
        }

        private double? Clamp(double s)
        {
            if (s < 0) return _extend0 ? 0 : null;
            if (s > 1) return _extend1 ? 1 : null;
            return s;
        }

        private double? AxialParameter(double px, double py)
        {
            double dx = _coords[2] - _coords[0], dy = _coords[3] - _coords[1], length = dx * dx + dy * dy;
            if (length == 0) return null;
            return Clamp(((px - _coords[0]) * dx + (py - _coords[1]) * dy) / length);
        }

        /// <summary>The largest s with r(s) >= 0 whose circle contains the point (ISO 32000-2, 8.7.4.5.4).</summary>
        private double? RadialParameter(double px, double py)
        {
            double x0 = _coords[0], y0 = _coords[1], r0 = _coords[2], x1 = _coords[3], y1 = _coords[4], r1 = _coords[5];
            double cdx = x1 - x0, cdy = y1 - y0, dr = r1 - r0, pdx = px - x0, pdy = py - y0;
            double a = cdx * cdx + cdy * cdy - dr * dr, b = pdx * cdx + pdy * cdy + r0 * dr, c = pdx * pdx + pdy * pdy - r0 * r0;
            Span<double> roots = stackalloc double[2];
            int count = 0;
            if (Math.Abs(a) < 1e-12)
            {
                if (Math.Abs(b) < 1e-12) return null;
                roots[count++] = c / (2 * b);
            }
            else
            {
                double disc = b * b - a * c;
                if (disc < 0) return null;
                double sq = Math.Sqrt(disc);
                double s1 = (b + sq) / a, s2 = (b - sq) / a;
                roots[count++] = Math.Max(s1, s2);
                roots[count++] = Math.Min(s1, s2);
            }
            for (int i = 0; i < count; i++)
            {
                double s = roots[i];
                if (r0 + s * dr < 0) continue;
                if (Clamp(s) is { } valid) return valid;
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ types 4 to 7

    private sealed class MeshShading : Shading
    {
        private readonly PdfStream _stream;
        private readonly int _type;
        private int _bitsPerCoordinate, _bitsPerComponent, _bitsPerFlag, _verticesPerRow;
        private double[] _decode = Array.Empty<double>();
        private Rgb[]? _table;
        private double _tMin, _tMax;

        private readonly record struct Vertex(double X, double Y, double[] Values);

        public MeshShading(PdfStream stream)
        {
            _stream = stream;
            _type = stream.GetInt(PdfName.Get("ShadingType"));
        }

        private int ValueCount => Function != null ? 1 : Space.Components;

        protected override bool Prepare()
        {
            _bitsPerCoordinate = _stream.GetInt(PdfName.Get("BitsPerCoordinate"));
            _bitsPerComponent = _stream.GetInt(PdfName.BitsPerComponent);
            _bitsPerFlag = _stream.GetInt(PdfName.Get("BitsPerFlag"));
            _verticesPerRow = _stream.GetInt(PdfName.Get("VerticesPerRow"));
            _decode = Numbers(_stream.GetArray(PdfName.Decode));
            if (_bitsPerCoordinate is < 1 or > 32 || _bitsPerComponent is < 1 or > 16) return false;
            if (_type != 5 && _bitsPerFlag is not (2 or 4 or 8)) return false;
            if (_type == 5 && _verticesPerRow < 2) return false;
            if (_decode.Length < 4 + 2 * ValueCount) return false;
            if (Function != null)
            {
                (_tMin, _tMax) = (_decode[4], _decode[5]);
                _table = Lookup(_tMin, _tMax);
            }
            return true;
        }

        private sealed class BitReader(byte[] data)
        {
            private long _bit;

            public bool AtEnd(int bits) => _bit + bits > (long)data.Length * 8;

            public ulong Read(int bits)
            {
                ulong v = 0;
                for (int i = 0; i < bits; i++, _bit++)
                {
                    long index = _bit >> 3;
                    v = (v << 1) | (uint)(index < data.Length ? (data[index] >> (7 - (int)(_bit & 7))) & 1 : 0);
                }
                return v;
            }

            public void Align() => _bit = (_bit + 7) & ~7L;
        }

        private double Decode(BitReader reader, int bits, int pair)
        {
            double max = bits >= 32 ? uint.MaxValue : (1UL << bits) - 1;
            return _decode[2 * pair] + reader.Read(bits) * (_decode[2 * pair + 1] - _decode[2 * pair]) / max;
        }

        private (double X, double Y) ReadPoint(BitReader reader) => (Decode(reader, _bitsPerCoordinate, 0), Decode(reader, _bitsPerCoordinate, 1));

        private double[] ReadValues(BitReader reader)
        {
            var values = new double[ValueCount];
            for (int i = 0; i < values.Length; i++) values[i] = Decode(reader, _bitsPerComponent, 2 + i);
            return values;
        }

        private int VertexBits => 2 * _bitsPerCoordinate + ValueCount * _bitsPerComponent;

        public override void Render(Layer layer, Matrix toDevice)
        {
            var reader = new BitReader(_stream.GetBytes());
            var triangles = new List<(Vertex, Vertex, Vertex)>();
            switch (_type)
            {
                case 4: ReadFreeForm(reader, triangles); break;
                case 5: ReadLattice(reader, triangles); break;
                default: ReadPatches(reader, triangles, toDevice); break;
            }
            foreach (var (a, b, c) in triangles) FillTriangle(layer, Map(a, toDevice), Map(b, toDevice), Map(c, toDevice));
        }

        private static Vertex Map(Vertex v, Matrix m)
        {
            var (x, y) = m.Transform(v.X, v.Y);
            return v with { X = x, Y = y };
        }

        private void ReadFreeForm(BitReader reader, List<(Vertex, Vertex, Vertex)> triangles)
        {
            Vertex? a = null, b = null, c = null;
            var pending = new List<Vertex>();
            while (!reader.AtEnd(_bitsPerFlag + VertexBits) && triangles.Count < 2_000_000)
            {
                int flag = (int)reader.Read(_bitsPerFlag);
                var (x, y) = ReadPoint(reader);
                var vertex = new Vertex(x, y, ReadValues(reader));
                reader.Align();
                if (flag == 0 || a == null)
                {
                    pending.Add(vertex);
                    if (pending.Count == 3)
                    {
                        (a, b, c) = (pending[0], pending[1], pending[2]);
                        triangles.Add((a.Value, b.Value, c.Value));
                        pending.Clear();
                    }
                    continue;
                }
                // flag 1: (b, c, new); flag 2: (a, c, new)
                (a, b, c) = flag == 1 ? (b, c, vertex) : (a, c, vertex);
                triangles.Add((a!.Value, b!.Value, c!.Value));
            }
        }

        private void ReadLattice(BitReader reader, List<(Vertex, Vertex, Vertex)> triangles)
        {
            var rows = new List<Vertex[]>();
            while (!reader.AtEnd(VertexBits * _verticesPerRow) && rows.Count < 100_000)
            {
                var row = new Vertex[_verticesPerRow];
                for (int i = 0; i < row.Length; i++)
                {
                    var (x, y) = ReadPoint(reader);
                    row[i] = new Vertex(x, y, ReadValues(reader));
                    reader.Align();
                }
                rows.Add(row);
            }
            for (int r = 0; r + 1 < rows.Count; r++)
                for (int i = 0; i + 1 < _verticesPerRow; i++)
                {
                    triangles.Add((rows[r][i], rows[r][i + 1], rows[r + 1][i]));
                    triangles.Add((rows[r][i + 1], rows[r + 1][i + 1], rows[r + 1][i]));
                }
        }

        /// <summary>Coons (6) and tensor-product (7) patches, subdivided into triangles.</summary>
        private void ReadPatches(BitReader reader, List<(Vertex, Vertex, Vertex)> triangles, Matrix toDevice)
        {
            int points = _type == 6 ? 12 : 16;
            var p = new (double X, double Y)[4, 4];
            var colors = new double[4][]; // c00, c03, c33, c30
            bool havePrevious = false;
            int patches = 0;
            while (!reader.AtEnd(_bitsPerFlag) && patches++ < 200_000)
            {
                int flag = (int)reader.Read(_bitsPerFlag);
                if (flag != 0 && !havePrevious) flag = 0;
                int newPoints = flag == 0 ? points : points - 4, newColors = flag == 0 ? 4 : 2;
                if (reader.AtEnd(newPoints * 2 * _bitsPerCoordinate + newColors * ValueCount * _bitsPerComponent)) break;

                // the shared edge of the previous patch becomes p00 p01 p02 p03 (and c00, c03)
                (double X, double Y)[] edge;
                double[][] edgeColors;
                switch (flag)
                {
                    case 1:
                        edge = new[] { p[0, 3], p[1, 3], p[2, 3], p[3, 3] };
                        edgeColors = new[] { colors[1], colors[2] };
                        break;
                    case 2:
                        edge = new[] { p[3, 3], p[3, 2], p[3, 1], p[3, 0] };
                        edgeColors = new[] { colors[2], colors[3] };
                        break;
                    case 3:
                        edge = new[] { p[3, 0], p[2, 0], p[1, 0], p[0, 0] };
                        edgeColors = new[] { colors[3], colors[0] };
                        break;
                    default:
                        edge = Array.Empty<(double, double)>();
                        edgeColors = Array.Empty<double[]>();
                        break;
                }
                // point order: p00 p01 p02 p03 p13 p23 p33 p32 p31 p30 p20 p10 [p11 p12 p22 p21]
                var order = new (int I, int J)[] { (0, 0), (0, 1), (0, 2), (0, 3), (1, 3), (2, 3), (3, 3), (3, 2), (3, 1), (3, 0), (2, 0), (1, 0), (1, 1), (1, 2), (2, 2), (2, 1) };
                for (int k = 0; k < points; k++)
                {
                    var (i, j) = order[k];
                    p[i, j] = flag != 0 && k < 4 ? edge[k] : ReadPoint(reader);
                }
                for (int k = 0; k < 4; k++)
                    colors[k] = flag != 0 && k < 2 ? edgeColors[k] : ReadValues(reader);
                reader.Align();
                havePrevious = true;
                if (_type == 6) CoonsInterior(p);
                Subdivide(p, colors, triangles, toDevice);
            }
        }

        /// <summary>The interior control points of a Coons patch as a tensor-product patch (ISO 32000-2, 8.7.4.5.8).</summary>
        private static void CoonsInterior((double X, double Y)[,] p)
        {
            (double X, double Y) Combine(params (double W, (double X, double Y) P)[] terms) =>
                (terms.Sum(t => t.W * t.P.X) / 9, terms.Sum(t => t.W * t.P.Y) / 9);
            p[1, 1] = Combine((-4, p[0, 0]), (6, p[0, 1]), (6, p[1, 0]), (-2, p[0, 3]), (-2, p[3, 0]), (3, p[3, 1]), (3, p[1, 3]), (-1, p[3, 3]));
            p[1, 2] = Combine((-4, p[0, 3]), (6, p[0, 2]), (6, p[1, 3]), (-2, p[0, 0]), (-2, p[3, 3]), (3, p[3, 2]), (3, p[1, 0]), (-1, p[3, 0]));
            p[2, 2] = Combine((-4, p[3, 3]), (6, p[3, 2]), (6, p[2, 3]), (-2, p[3, 0]), (-2, p[0, 3]), (3, p[2, 0]), (3, p[0, 2]), (-1, p[0, 0]));
            p[2, 1] = Combine((-4, p[3, 0]), (6, p[3, 1]), (6, p[2, 0]), (-2, p[3, 3]), (-2, p[0, 0]), (3, p[2, 3]), (3, p[0, 1]), (-1, p[0, 3]));
        }

        private static double Bernstein(int i, double t) => i switch
        {
            0 => (1 - t) * (1 - t) * (1 - t),
            1 => 3 * t * (1 - t) * (1 - t),
            2 => 3 * t * t * (1 - t),
            _ => t * t * t,
        };

        private void Subdivide((double X, double Y)[,] p, double[][] colors, List<(Vertex, Vertex, Vertex)> triangles, Matrix toDevice)
        {
            // grid density from the patch size in device pixels
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var point in p)
            {
                var (dx, dy) = toDevice.Transform(point.X, point.Y);
                (minX, minY, maxX, maxY) = (Math.Min(minX, dx), Math.Min(minY, dy), Math.Max(maxX, dx), Math.Max(maxY, dy));
            }
            int n = (int)Math.Clamp(Math.Max(maxX - minX, maxY - minY) / 4, 2, 48);
            var grid = new Vertex[n + 1, n + 1];
            int count = ValueCount;
            for (int a = 0; a <= n; a++)
                for (int b = 0; b <= n; b++)
                {
                    double u = (double)a / n, v = (double)b / n, x = 0, y = 0;
                    for (int i = 0; i < 4; i++)
                        for (int j = 0; j < 4; j++)
                        {
                            double w = Bernstein(i, u) * Bernstein(j, v);
                            x += w * p[i, j].X;
                            y += w * p[i, j].Y;
                        }
                    var values = new double[count];
                    for (int k = 0; k < count; k++)
                        values[k] = (1 - u) * (1 - v) * colors[0][k] + (1 - u) * v * colors[1][k] + u * v * colors[2][k] + u * (1 - v) * colors[3][k];
                    grid[a, b] = new Vertex(x, y, values);
                }
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++)
                {
                    triangles.Add((grid[a, b], grid[a + 1, b], grid[a, b + 1]));
                    triangles.Add((grid[a + 1, b], grid[a + 1, b + 1], grid[a, b + 1]));
                }
        }

        /// <summary>Gouraud shading of a device-space triangle (pixel centers inside it, edges inclusive).</summary>
        private void FillTriangle(Layer layer, Vertex a, Vertex b, Vertex c)
        {
            double area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            if (Math.Abs(area) < 1e-12 || !double.IsFinite(area)) return;
            int x0 = Math.Max(layer.X0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(layer.X0 + layer.Width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
            int y0 = Math.Max(layer.Y0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(layer.Y0 + layer.Height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
            int count = ValueCount;
            Span<double> values = stackalloc double[count];
            const double eps = -1e-6;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double wa = ((b.X - px) * (c.Y - py) - (c.X - px) * (b.Y - py)) / area;
                    double wb = ((c.X - px) * (a.Y - py) - (a.X - px) * (c.Y - py)) / area;
                    double wc = 1 - wa - wb;
                    if (wa < eps || wb < eps || wc < eps) continue;
                    for (int k = 0; k < count; k++) values[k] = wa * a.Values[k] + wb * b.Values[k] + wc * c.Values[k];
                    var color = _table != null ? FromTable(_table, _tMin, _tMax, values[0]) : ColorOf(values);
                    layer.Set(layer.IndexOf(x, y), color);
                }
        }
    }
}
