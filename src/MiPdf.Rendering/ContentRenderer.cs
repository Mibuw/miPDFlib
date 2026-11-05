using MiPdf.Content;
using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.Objects;
using MiPdf.Rendering.Raster;

namespace MiPdf.Rendering;

/// <summary>
/// Interprets content streams (ISO 32000-2, 8) and paints them on a <see cref="Surface"/>: paths (fill, stroke, clip),
/// colors, opacity, images, image masks, form XObjects and optional content. The matrix passed in maps user space to
/// device pixels.
/// </summary>
internal sealed class ContentRenderer
{
    private const int MaxDepth = 16;
    private const double Tolerance = 0.25; // flattening tolerance in device pixels

    private readonly Surface _surface;
    private readonly PdfDocument _document;
    private readonly HashSet<PdfStream> _activeForms = new(ReferenceEqualityComparer.Instance);
    private readonly Rasterizer _rasterizer = new();

    public ContentRenderer(Surface surface, PdfDocument document)
    {
        _surface = surface;
        _document = document;
    }

    private sealed class GraphicsState
    {
        public Matrix Ctm;
        public byte[]? Clip;
        public ColorSpace FillSpace = ColorSpace.Gray, StrokeSpace = ColorSpace.Gray;
        public double[] FillColor = { 0 }, StrokeColor = { 0 };
        public double LineWidth = 1, MiterLimit = 10, DashPhase;
        public int LineCap, LineJoin;
        public double[] Dash = Array.Empty<double>();
        public double FillAlpha = 1, StrokeAlpha = 1;

        public GraphicsState Clone() => (GraphicsState)MemberwiseClone();

        public Rgb Fill => FillSpace.ToRgb(Pad(FillColor, FillSpace.Components));

        public Rgb Stroke => StrokeSpace.ToRgb(Pad(StrokeColor, StrokeSpace.Components));

        private static double[] Pad(double[] values, int n) => values.Length >= n ? values : values.Concat(new double[n - values.Length]).ToArray();
    }

    public void Render(byte[] content, PdfDictionary? resources, Matrix ctm, byte[]? clip) =>
        Render(content, resources, new GraphicsState { Ctm = ctm, Clip = clip }, 0);

    /// <summary>Renders a form XObject (e.g. an annotation appearance) with <paramref name="ctm"/> as the current matrix.</summary>
    public void RenderForm(PdfStream form, PdfDictionary? resources, Matrix ctm) =>
        DrawForm(form, resources, new GraphicsState { Ctm = ctm }, 0);

    private void Render(byte[] content, PdfDictionary? resources, GraphicsState gs, int depth)
    {
        if (depth > MaxDepth) return;
        var stack = new Stack<GraphicsState>();
        var path = new PathGeometry();
        int? pendingClip = null; // 0 nonzero, 1 even-odd
        var marked = new Stack<bool>();
        int hidden = 0;

        foreach (var op in ContentParser.Parse(content))
        {
            var o = op.Operands;
            switch (op.Operator)
            {
                // graphics state
                case "q":
                    if (stack.Count < 256) stack.Push(gs.Clone());
                    break;
                case "Q":
                    if (stack.Count > 0) gs = stack.Pop();
                    break;
                case "cm":
                    if (o.Count >= 6) gs.Ctm = M(o).Multiply(gs.Ctm);
                    break;
                case "w":
                    gs.LineWidth = N(o, 0);
                    break;
                case "J":
                    gs.LineCap = (int)N(o, 0);
                    break;
                case "j":
                    gs.LineJoin = (int)N(o, 0);
                    break;
                case "M":
                    gs.MiterLimit = N(o, 0);
                    break;
                case "d":
                    if (o.Count >= 2 && o[0] is PdfArray dash)
                    {
                        gs.Dash = dash.Select(v => (v.Resolve() as PdfNumber)?.Value ?? 0).ToArray();
                        gs.DashPhase = N(o, 1);
                    }
                    break;
                case "gs":
                    if (o.Count >= 1 && o[0] is PdfName gsName && resources?.GetDictionary(PdfName.ExtGState)?.GetDictionary(gsName) is { } ext)
                        ApplyExtGState(ext, gs);
                    break;

                // colors
                case "CS":
                    gs.StrokeSpace = ColorSpace.Resolve(o.Count > 0 ? o[0] : null, resources);
                    gs.StrokeColor = gs.StrokeSpace.Initial;
                    break;
                case "cs":
                    gs.FillSpace = ColorSpace.Resolve(o.Count > 0 ? o[0] : null, resources);
                    gs.FillColor = gs.FillSpace.Initial;
                    break;
                case "SC" or "SCN":
                    gs.StrokeColor = Numbers(o);
                    break;
                case "sc" or "scn":
                    gs.FillColor = Numbers(o);
                    break;
                case "G":
                    (gs.StrokeSpace, gs.StrokeColor) = (ColorSpace.Gray, Numbers(o));
                    break;
                case "g":
                    (gs.FillSpace, gs.FillColor) = (ColorSpace.Gray, Numbers(o));
                    break;
                case "RG":
                    (gs.StrokeSpace, gs.StrokeColor) = (ColorSpace.Rgb, Numbers(o));
                    break;
                case "rg":
                    (gs.FillSpace, gs.FillColor) = (ColorSpace.Rgb, Numbers(o));
                    break;
                case "K":
                    (gs.StrokeSpace, gs.StrokeColor) = (ColorSpace.Cmyk, Numbers(o));
                    break;
                case "k":
                    (gs.FillSpace, gs.FillColor) = (ColorSpace.Cmyk, Numbers(o));
                    break;

                // path construction
                case "m":
                    path.MoveTo(N(o, 0), N(o, 1));
                    break;
                case "l":
                    path.LineTo(N(o, 0), N(o, 1));
                    break;
                case "c":
                    path.CurveTo(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5));
                    break;
                case "v":
                    path.CurveTo(path.Current.X, path.Current.Y, N(o, 0), N(o, 1), N(o, 2), N(o, 3));
                    break;
                case "y":
                    path.CurveTo(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 2), N(o, 3));
                    break;
                case "h":
                    path.Close();
                    break;
                case "re":
                    path.Rectangle(N(o, 0), N(o, 1), N(o, 2), N(o, 3));
                    break;

                // path painting
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n":
                    string p = op.Operator;
                    if (p is "s" or "b" or "b*") path.Close();
                    if (hidden == 0)
                    {
                        if (p is "f" or "F" or "B" or "b") Fill(path, gs, evenOdd: false);
                        else if (p is "f*" or "B*" or "b*") Fill(path, gs, evenOdd: true);
                        if (p is "S" or "s" or "B" or "B*" or "b" or "b*") Stroke(path, gs);
                    }
                    if (pendingClip is { } rule) gs.Clip = ClipMask(path, gs, rule == 1);
                    pendingClip = null;
                    path.Clear();
                    break;
                case "W":
                    pendingClip = 0;
                    break;
                case "W*":
                    pendingClip = 1;
                    break;

                // XObjects and images
                case "Do":
                    if (hidden > 0 || o.Count < 1 || o[0] is not PdfName name) break;
                    if (resources?.GetDictionary(PdfName.XObject)?.GetStream(name) is { } xobject && !_document.IsHiddenContent(xobject[PdfName.Get("OC")]))
                    {
                        if (xobject.GetName(PdfName.Subtype)?.Value == "Form") DrawForm(xobject, resources, gs, depth);
                        else if (xobject.GetName(PdfName.Subtype)?.Value == "Image") DrawImage(xobject, gs);
                    }
                    break;
                case "BI":
                    if (hidden == 0 && op.InlineImage is { } inline) DrawImage(GraphicsWalker.ExpandInline(inline), gs);
                    break;

                // optional content
                case "BMC":
                    marked.Push(false);
                    break;
                case "BDC":
                    bool hides = o.Count >= 2 && o[0] is PdfName { Value: "OC" } && o[1] is PdfName property
                        && _document.IsHiddenContent(resources?.GetDictionary(PdfName.Get("Properties"))?[property]);
                    marked.Push(hides);
                    if (hides) hidden++;
                    break;
                case "EMC":
                    if (marked.Count > 0 && marked.Pop()) hidden--;
                    break;
            }
        }
    }

    private static Matrix M(IReadOnlyList<PdfObject> o) => new(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5));

    private static double N(IReadOnlyList<PdfObject> operands, int index) =>
        index < operands.Count && operands[index] is PdfNumber n ? n.Value : 0;

    private static double[] Numbers(IReadOnlyList<PdfObject> operands) =>
        operands.OfType<PdfNumber>().Select(n => n.Value).ToArray();

    private void ApplyExtGState(PdfDictionary ext, GraphicsState gs)
    {
        foreach (var (key, raw) in ext)
        {
            var value = raw.Resolve();
            switch (key.Value)
            {
                case "LW" when value is PdfNumber n: gs.LineWidth = n.Value; break;
                case "LC" when value is PdfNumber n: gs.LineCap = n.IntValue; break;
                case "LJ" when value is PdfNumber n: gs.LineJoin = n.IntValue; break;
                case "ML" when value is PdfNumber n: gs.MiterLimit = n.Value; break;
                case "CA" when value is PdfNumber n: gs.StrokeAlpha = Math.Clamp(n.Value, 0, 1); break;
                case "ca" when value is PdfNumber n: gs.FillAlpha = Math.Clamp(n.Value, 0, 1); break;
                case "D" when value is PdfArray { Count: >= 2 } d && d[0].Resolve() is PdfArray dash:
                    gs.Dash = dash.Select(v => (v.Resolve() as PdfNumber)?.Value ?? 0).ToArray();
                    gs.DashPhase = (d[1].Resolve() as PdfNumber)?.Value ?? 0;
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ painting

    private void Fill(PathGeometry path, GraphicsState gs, bool evenOdd)
    {
        if (gs.FillSpace.IsPattern) return; // patterns follow with shadings
        _rasterizer.Clear();
        foreach (var (points, _) in path.Flatten(gs.Ctm, Tolerance))
            if (points.Count >= 2) _rasterizer.AddPolygon(points);
        Paint(evenOdd, gs.Fill, gs.FillAlpha, gs.Clip);
    }

    private void Stroke(PathGeometry path, GraphicsState gs)
    {
        if (gs.StrokeSpace.IsPattern) return;
        var ctm = gs.Ctm;
        double scale = Math.Sqrt(Math.Abs(ctm.A * ctm.D - ctm.B * ctm.C));
        if (scale <= 0 || !double.IsFinite(scale)) return;
        // lines are at least one device pixel wide (also width 0, the thinnest line the device can render)
        double width = Math.Max(gs.LineWidth, 1 / scale);
        var style = new StrokeStyle(width, gs.LineCap, gs.LineJoin, gs.MiterLimit, gs.Dash, gs.DashPhase);
        var polygons = Stroker.Stroke(path.Flatten(Matrix.Identity, Tolerance / scale), style, Tolerance / scale);
        _rasterizer.Clear();
        foreach (var polygon in polygons) _rasterizer.AddPolygon(polygon.Select(pt => ctm.Transform(pt.X, pt.Y)).ToList());
        Paint(evenOdd: false, gs.Stroke, gs.StrokeAlpha, gs.Clip);
    }

    private void Paint(bool evenOdd, Rgb color, double alpha, byte[]? clip)
    {
        if (alpha <= 0) return;
        _rasterizer.Rasterize(_surface.Width, _surface.Height, evenOdd, (y, x0, x1, coverage) => _surface.BlendRow(y, x0, x1, coverage, clip, color, alpha));
    }

    /// <summary>The clip mask after intersecting the current one with the path.</summary>
    private byte[] ClipMask(PathGeometry path, GraphicsState gs, bool evenOdd)
    {
        var mask = new byte[_surface.Width * _surface.Height];
        _rasterizer.Clear();
        foreach (var (points, _) in path.Flatten(gs.Ctm, Tolerance))
            if (points.Count >= 2) _rasterizer.AddPolygon(points);
        var previous = gs.Clip;
        int width = _surface.Width;
        _rasterizer.Rasterize(_surface.Width, _surface.Height, evenOdd, (y, x0, x1, coverage) =>
        {
            for (int x = x0; x <= x1; x++)
            {
                int i = y * width + x;
                double value = coverage[x] * 255;
                if (previous != null) value = value * previous[i] / 255;
                mask[i] = (byte)Math.Round(value);
            }
        });
        return mask;
    }

    private void DrawForm(PdfStream form, PdfDictionary? parentResources, GraphicsState gs, int depth)
    {
        if (!_activeForms.Add(form)) return; // recursive form
        try
        {
            var inner = gs.Clone();
            if (form.GetArray(PdfName.Get("Matrix")) is { Count: 6 } m)
                inner.Ctm = new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5)).Multiply(gs.Ctm);
            if (form.GetRectangle(PdfName.BBox) is { } bbox)
            {
                var box = new PathGeometry();
                box.Rectangle(bbox.Left, bbox.Bottom, bbox.Width, bbox.Height);
                inner.Clip = ClipMask(box, inner, evenOdd: false);
            }
            Render(form.GetBytes(), form.GetDictionary(PdfName.Resources) ?? parentResources, inner, depth + 1);
        }
        finally
        {
            _activeForms.Remove(form);
        }
    }

    /// <summary>Draws an image XObject: the unit square in user space, sampled per device pixel (nearest neighbor).</summary>
    private void DrawImage(PdfStream image, GraphicsState gs)
    {
        bool stencil = image.GetBool(PdfName.Get("ImageMask"));
        (int Width, int Height, byte[] Rgb, byte[]? Alpha)? decoded;
        try
        {
            decoded = ImageDecoder.Decode(image);
        }
        catch (PdfException)
        {
            return;
        }
        if (decoded is not { } img || img.Width <= 0 || img.Height <= 0) return;
        if (stencil && gs.FillSpace.IsPattern) return;
        var ctm = gs.Ctm;
        double det = ctm.A * ctm.D - ctm.B * ctm.C;
        if (Math.Abs(det) < 1e-12) return;
        // inverse of the CTM: device pixel -> unit square
        var inverse = new Matrix(ctm.D / det, -ctm.B / det, -ctm.C / det, ctm.A / det,
            (ctm.C * ctm.F - ctm.D * ctm.E) / det, (ctm.B * ctm.E - ctm.A * ctm.F) / det);
        var corners = new[] { ctm.Transform(0, 0), ctm.Transform(1, 0), ctm.Transform(0, 1), ctm.Transform(1, 1) };
        int x0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.X))), x1 = Math.Min(_surface.Width - 1, (int)Math.Ceiling(corners.Max(c => c.X)));
        int y0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y))), y1 = Math.Min(_surface.Height - 1, (int)Math.Ceiling(corners.Max(c => c.Y)));
        var fill = gs.Fill;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var (u, v) = inverse.Transform(x + 0.5, y + 0.5);
                if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
                int ix = Math.Min(img.Width - 1, (int)(u * img.Width)), iy = Math.Min(img.Height - 1, (int)((1 - v) * img.Height));
                int p = iy * img.Width + ix;
                double alpha = gs.FillAlpha * (img.Alpha != null ? img.Alpha[p] / 255.0 : 1);
                long index = (long)y * _surface.Width + x;
                if (gs.Clip != null) alpha *= gs.Clip[index] / 255.0;
                if (alpha <= 0) continue;
                if (stencil) _surface.Blend(index, fill.R, fill.G, fill.B, alpha);
                else _surface.Blend(index, img.Rgb[p * 3], img.Rgb[p * 3 + 1], img.Rgb[p * 3 + 2], alpha);
            }
        }
    }
}
