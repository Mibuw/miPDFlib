using MiPdf.Content;
using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.Objects;
using MiPdf.Fonts.Programs;
using MiPdf.Rendering.Fonts;
using MiPdf.Rendering.Raster;

namespace MiPdf.Rendering;

/// <summary>
/// Interprets content streams (ISO 32000-2, 8, 9 and 11) and paints them on a <see cref="Surface"/>: paths (fill,
/// stroke, clip), text (all rendering modes, embedded and substituted fonts, Type 3 fonts), colors, tiling and shading
/// patterns, shadings, opacity, blend modes, soft masks, transparency groups, images, image masks, form XObjects and
/// optional content. The matrix passed in maps user space to device pixels.
/// </summary>
internal sealed class ContentRenderer
{
    private const int MaxDepth = 16;
    private const double Tolerance = 0.25; // flattening tolerance in device pixels

    private Surface _surface; // swapped while transparency groups, soft masks and pattern cells are rendered
    private readonly PdfDocument _document;
    private readonly HashSet<PdfStream> _activeForms = new(ReferenceEqualityComparer.Instance);
    private Rasterizer _rasterizer = new(); // replaced while a pattern cell is rendered during a fill
    private readonly Dictionary<PdfDictionary, FontOutlines> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfDictionary, Shading?> _shadings = new(ReferenceEqualityComparer.Instance);
    private readonly List<((PdfStream Pattern, Matrix Matrix, Rgb? Color) Key, Tile Tile)> _tiles = new();
    private readonly List<((PdfDictionary Mask, Matrix Ctm, int Width) Key, byte[] Mask)> _softMasks = new();
    private int _depth, _groupDepth;
    private double _minLineWidth = 1; // device pixels (more while a supersampled pattern cell is rendered)

    public ContentRenderer(Surface surface, PdfDocument document)
    {
        _surface = surface;
        _document = document;
    }

    private sealed class GraphicsState
    {
        public Matrix Ctm;
        public byte[]? Clip;
        public (int X0, int Y0, int X1, int Y1)? ClipBounds; // device pixels covered by Clip (inclusive)
        public ColorSpace FillSpace = ColorSpace.Gray, StrokeSpace = ColorSpace.Gray;
        public double[] FillColor = { 0 }, StrokeColor = { 0 };
        public double LineWidth = 1, MiterLimit = 10, DashPhase;
        public int LineCap, LineJoin;
        public double[] Dash = Array.Empty<double>();
        public double FillAlpha = 1, StrokeAlpha = 1;
        public BlendMode BlendMode;
        public byte[]? SoftMask;          // per device pixel, like Clip
        public PdfDictionary? FillPattern, StrokePattern;
        public Matrix BaseCtm;            // the default space of the content stream (pattern space parent)
        public PdfDictionary? Resources;  // of the content stream (for shading color spaces)

        // text state (ISO 32000-2, 9.3)
        public PdfDictionary? Font;
        public double FontSize = 1, CharSpacing, WordSpacing, HorizontalScale = 1, Leading, Rise;
        public int RenderMode;

        public GraphicsState Clone() => (GraphicsState)MemberwiseClone();

        public Rgb Fill => FillSpace.ToRgb(Pad(FillColor, FillSpace.Components));

        public Rgb Stroke => StrokeSpace.ToRgb(Pad(StrokeColor, StrokeSpace.Components));

        private static double[] Pad(double[] values, int n) => values.Length >= n ? values : values.Concat(new double[n - values.Length]).ToArray();
    }

    public void Render(byte[] content, PdfDictionary? resources, Matrix ctm, byte[]? clip) =>
        Render(content, resources, new GraphicsState { Ctm = ctm, BaseCtm = ctm, Clip = clip }, 0);

    /// <summary>Renders a form XObject (e.g. an annotation appearance) with <paramref name="ctm"/> as the current matrix.</summary>
    public void RenderForm(PdfStream form, PdfDictionary? resources, Matrix ctm) =>
        DrawForm(form, resources, new GraphicsState { Ctm = ctm, BaseCtm = ctm }, 0);

    private void Render(byte[] content, PdfDictionary? resources, GraphicsState gs, int depth)
    {
        if (depth > MaxDepth)
        {
            DiagnosticContext.Warning(PdfDiagnosticCode.NestingTooDeep, $"Content is nested more than {MaxDepth} levels deep; the inner levels are not drawn.");
            return;
        }
        int outerDepth = _depth;
        _depth = depth;
        try
        {
            RenderOperators(content, resources, gs, depth);
        }
        finally
        {
            _depth = outerDepth;
        }
    }

    private void RenderOperators(byte[] content, PdfDictionary? resources, GraphicsState gs, int depth)
    {
        gs.Resources = resources;
        var stack = new Stack<GraphicsState>();
        var path = new PathGeometry();
        int? pendingClip = null; // 0 nonzero, 1 even-odd
        var marked = new Stack<bool>();
        int hidden = 0;
        Matrix tm = Matrix.Identity, tlm = Matrix.Identity;
        PathGeometry? textClip = null; // glyph outlines of clipping text modes, applied at ET

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
                    gs.StrokePattern = gs.StrokeSpace.IsPattern ? PatternOf(o, resources) : null;
                    break;
                case "sc" or "scn":
                    gs.FillColor = Numbers(o);
                    gs.FillPattern = gs.FillSpace.IsPattern ? PatternOf(o, resources) : null;
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
                    if (pendingClip is { } rule) ApplyClip(path, gs, rule == 1);
                    pendingClip = null;
                    path.Clear();
                    break;
                case "W":
                    pendingClip = 0;
                    break;
                case "W*":
                    pendingClip = 1;
                    break;

                // shadings
                case "sh":
                    if (hidden == 0 && o.Count >= 1 && o[0] is PdfName shadingName
                        && GetShading(resources?.GetDictionary(PdfName.Get("Shading"))?.Get(shadingName), resources) is { } shading)
                        PaintShading(shading, gs);
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

                // text
                case "BT":
                    tm = tlm = Matrix.Identity;
                    textClip = null;
                    break;
                case "ET":
                    if (textClip != null) ApplyClip(textClip, gs, evenOdd: false);
                    textClip = null;
                    break;
                case "Tf":
                    if (o.Count >= 2 && o[0] is PdfName fontName)
                    {
                        gs.Font = resources?.GetDictionary(PdfName.Font)?.GetDictionary(fontName);
                        gs.FontSize = N(o, 1);
                    }
                    break;
                case "Tc": gs.CharSpacing = N(o, 0); break;
                case "Tw": gs.WordSpacing = N(o, 0); break;
                case "Tz": gs.HorizontalScale = N(o, 0) / 100; break;
                case "TL": gs.Leading = N(o, 0); break;
                case "Ts": gs.Rise = N(o, 0); break;
                case "Tr": gs.RenderMode = (int)N(o, 0); break;
                case "Td":
                    tm = tlm = Matrix.Translate(N(o, 0), N(o, 1)).Multiply(tlm);
                    break;
                case "TD":
                    gs.Leading = -N(o, 1);
                    tm = tlm = Matrix.Translate(N(o, 0), N(o, 1)).Multiply(tlm);
                    break;
                case "Tm":
                    if (o.Count >= 6) tm = tlm = M(o);
                    break;
                case "T*":
                    tm = tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                    break;
                case "Tj":
                    if (o.Count >= 1 && o[0] is PdfString text) ShowText(text.Bytes, gs, resources, ref tm, ref textClip, hidden > 0, depth);
                    break;
                case "'":
                    tm = tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                    if (o.Count >= 1 && o[^1] is PdfString text1) ShowText(text1.Bytes, gs, resources, ref tm, ref textClip, hidden > 0, depth);
                    break;
                case "\"":
                    if (o.Count >= 3)
                    {
                        gs.WordSpacing = N(o, 0);
                        gs.CharSpacing = N(o, 1);
                    }
                    tm = tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                    if (o.Count >= 1 && o[^1] is PdfString text2) ShowText(text2.Bytes, gs, resources, ref tm, ref textClip, hidden > 0, depth);
                    break;
                case "TJ":
                    if (o.Count >= 1 && o[0] is PdfArray parts)
                        foreach (var part in parts)
                        {
                            if (part is PdfString piece) ShowText(piece.Bytes, gs, resources, ref tm, ref textClip, hidden > 0, depth);
                            else if (part is PdfNumber adjust)
                                tm = Matrix.Translate(-adjust.Value / 1000 * gs.FontSize * gs.HorizontalScale, 0).Multiply(tm);
                        }
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

    /// <summary>The pattern named by the last operand of scn/SCN.</summary>
    private static PdfDictionary? PatternOf(IReadOnlyList<PdfObject> operands, PdfDictionary? resources) =>
        operands.Count > 0 && operands[^1] is PdfName name ? resources?.GetDictionary(PdfName.Get("Pattern"))?.GetDictionary(name) : null;

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
                case "BM":
                    // a name or an array of names: the first one known
                    var names = value is PdfArray modes ? modes.Select(m => (m.Resolve() as PdfName)?.Value) : new[] { (value as PdfName)?.Value };
                    gs.BlendMode = Surface.ParseBlendMode(names.FirstOrDefault(n => n != null && Surface.IsKnownBlendMode(n)));
                    break;
                case "SMask":
                    gs.SoftMask = value is PdfDictionary smask ? SoftMask(smask, gs) : null;
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ text

    private FontOutlines? GetFont(PdfDictionary? font)
    {
        if (font == null) return null;
        if (!_fonts.TryGetValue(font, out var renderFont)) _fonts[font] = renderFont = FontOutlines.Load(font, SystemFonts.Find);
        return renderFont;
    }

    /// <summary>
    /// Shows a string: glyph outlines placed with the text rendering matrix (ISO 32000-2, 9.4.4), filled, stroked and/or
    /// added to the clip according to the rendering mode; the text matrix advances by the glyph widths.
    /// </summary>
    private void ShowText(byte[] bytes, GraphicsState gs, PdfDictionary? resources, ref Matrix tm, ref PathGeometry? textClip, bool hidden, int depth)
    {
        var font = GetFont(gs.Font);
        if (font == null) return;
        int mode = gs.RenderMode;
        bool paint = !hidden && mode != 3 && mode != 7;
        bool clip = mode >= 4;
        var outlines = new PathGeometry();
        double scale = gs.FontSize * gs.HorizontalScale;
        foreach (var glyph in font.Decoder.Decode(bytes))
        {
            // text space -> user space for this glyph
            var trm = new Matrix(scale, 0, 0, gs.FontSize, 0, gs.Rise).Multiply(tm);
            if (font.Type3 != null)
            {
                if (paint) DrawType3Glyph(font, glyph, trm, gs, resources, depth);
            }
            else if ((paint || clip) && font.GetGlyph(glyph) is { IsEmpty: false } outline)
                AppendTransformed(outlines, outline, font.FontMatrix.Multiply(trm));
            double width = font.Advance?.Invoke(glyph) ?? glyph.Width;
            double advance = (width * gs.FontSize + gs.CharSpacing + (glyph.IsSingleByteSpace ? gs.WordSpacing : 0)) * gs.HorizontalScale;
            tm = Matrix.Translate(advance, 0).Multiply(tm);
        }
        if (outlines.IsEmpty) return;
        if (paint)
        {
            if (mode is 0 or 2 or 4 or 6) Fill(outlines, gs, evenOdd: false);
            if (mode is 1 or 2 or 5 or 6) Stroke(outlines, gs);
        }
        if (clip)
        {
            textClip ??= new PathGeometry();
            AppendTransformed(textClip, outlines, Matrix.Identity);
        }
    }

    private static void AppendTransformed(PathGeometry target, PathGeometry source, Matrix m)
    {
        foreach (var seg in source.Segments)
        {
            switch (seg.Kind)
            {
                case PathGeometry.Kind.Move:
                {
                    var p = m.Transform(seg.X1, seg.Y1);
                    target.MoveTo(p.X, p.Y);
                    break;
                }
                case PathGeometry.Kind.Line:
                {
                    var p = m.Transform(seg.X1, seg.Y1);
                    target.LineTo(p.X, p.Y);
                    break;
                }
                case PathGeometry.Kind.Curve:
                {
                    var p1 = m.Transform(seg.X1, seg.Y1);
                    var p2 = m.Transform(seg.X2, seg.Y2);
                    var p3 = m.Transform(seg.X3, seg.Y3);
                    target.CurveTo(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
                    break;
                }
                case PathGeometry.Kind.Close:
                    target.Close();
                    break;
            }
        }
    }

    /// <summary>A glyph of a Type 3 font: its glyph procedure drawn with the font matrix and the text rendering matrix.</summary>
    private void DrawType3Glyph(FontOutlines font, Text.DecodedGlyph glyph, Matrix trm, GraphicsState gs, PdfDictionary? resources, int depth)
    {
        var type3 = font.Type3!;
        string? name = glyph.Code is >= 0 and < 256 ? font.CodeNames[glyph.Code] : null;
        if (name == null || type3.GetDictionary(PdfName.Get("CharProcs"))?.GetStream(PdfName.Get(name)) is not { } procedure) return;
        if (!_activeForms.Add(procedure)) return;
        try
        {
            var inner = gs.Clone();
            inner.Ctm = font.FontMatrix.Multiply(trm).Multiply(gs.Ctm);
            inner.BaseCtm = inner.Ctm;
            Render(procedure.GetBytes(), type3.GetDictionary(PdfName.Resources) ?? resources, inner, depth + 1);
        }
        finally
        {
            _activeForms.Remove(procedure);
        }
    }

    // ------------------------------------------------------------------ painting

    private void Fill(PathGeometry path, GraphicsState gs, bool evenOdd)
    {
        _rasterizer.Clear();
        foreach (var (points, _) in path.Flatten(gs.Ctm, Tolerance))
            if (points.Count >= 2) _rasterizer.AddPolygon(points);
        PaintCoverage(evenOdd, gs, stroke: false);
    }

    private void Stroke(PathGeometry path, GraphicsState gs)
    {
        var ctm = gs.Ctm;
        double scale = Math.Sqrt(Math.Abs(ctm.A * ctm.D - ctm.B * ctm.C));
        if (scale <= 0 || !double.IsFinite(scale)) return;
        // lines are at least one device pixel wide (also width 0, the thinnest line the device can render)
        double width = Math.Max(gs.LineWidth, _minLineWidth / scale);
        var style = new StrokeStyle(width, gs.LineCap, gs.LineJoin, gs.MiterLimit, gs.Dash, gs.DashPhase);
        var polygons = Stroker.Stroke(path.Flatten(Matrix.Identity, Tolerance / scale), style, Tolerance / scale);
        _rasterizer.Clear();
        foreach (var polygon in polygons) _rasterizer.AddPolygon(polygon.Select(pt => ctm.Transform(pt.X, pt.Y)).ToList());
        PaintCoverage(evenOdd: false, gs, stroke: true);
    }

    /// <summary>Paints the rasterizer's polygons with the fill or stroke color (or pattern) of the graphics state.</summary>
    private void PaintCoverage(bool evenOdd, GraphicsState gs, bool stroke)
    {
        double alpha = stroke ? gs.StrokeAlpha : gs.FillAlpha;
        if (alpha <= 0 || _rasterizer.IsEmpty) return;
        if ((stroke ? gs.StrokeSpace : gs.FillSpace).IsNone) return; // the colorant None leaves no marks
        Layer? layer = null;
        if ((stroke ? gs.StrokeSpace : gs.FillSpace).IsPattern)
        {
            layer = PatternLayer(gs, stroke, Intersect(DeviceRegion(_rasterizer.Bounds), gs.ClipBounds));
            if (layer == null) return;
        }
        PaintRows(evenOdd, gs, stroke ? gs.Stroke : gs.Fill, alpha, layer);
    }

    private void PaintRows(bool evenOdd, GraphicsState gs, Rgb color, double alpha, Layer? layer)
    {
        var surface = _surface;
        var clip = gs.Clip;
        var softMask = gs.SoftMask;
        var mode = gs.BlendMode;
        _rasterizer.Rasterize(surface.Width, surface.Height, evenOdd, (y, x0, x1, coverage) =>
        {
            long row = (long)y * surface.Width;
            for (int x = x0; x <= x1; x++)
            {
                double shape = coverage[x];
                if (shape <= 0) continue;
                long i = row + x;
                if (clip != null) shape *= clip[i] / 255.0;
                double opacity = softMask != null ? alpha * softMask[i] / 255.0 : alpha;
                if (shape <= 0 || opacity <= 0 && !surface.Knockout) continue;
                if (layer == null)
                {
                    surface.Paint(i, color.R, color.G, color.B, shape, opacity, mode);
                    continue;
                }
                long li = layer.IndexOf(x, y);
                if (li < 0 || layer.Rgba[li + 3] == 0) continue;
                surface.Paint(i, layer.Rgba[li], layer.Rgba[li + 1], layer.Rgba[li + 2], shape, opacity * layer.Rgba[li + 3] / 255.0, mode);
            }
        });
    }

    /// <summary>A device rectangle (inclusive pixel bounds clipped to the surface) around a floating-point box.</summary>
    private (int X0, int Y0, int X1, int Y1) DeviceRegion((double MinX, double MinY, double MaxX, double MaxY) box) =>
        (Math.Max(0, (int)Math.Floor(box.MinX)), Math.Max(0, (int)Math.Floor(box.MinY)),
            Math.Min(_surface.Width - 1, (int)Math.Ceiling(box.MaxX)), Math.Min(_surface.Height - 1, (int)Math.Ceiling(box.MaxY)));

    /// <summary>Intersects the clip of the graphics state with the path (mask and bounding rectangle).</summary>
    private void ApplyClip(PathGeometry path, GraphicsState gs, bool evenOdd)
    {
        var mask = new byte[_surface.Width * _surface.Height];
        _rasterizer.Clear();
        foreach (var (points, _) in path.Flatten(gs.Ctm, Tolerance))
            if (points.Count >= 2) _rasterizer.AddPolygon(points);
        var bounds = _rasterizer.IsEmpty ? (0, 0, -1, -1) : Intersect(DeviceRegion(_rasterizer.Bounds), gs.ClipBounds);
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
        gs.Clip = mask;
        gs.ClipBounds = bounds;
    }

    private static (int X0, int Y0, int X1, int Y1) Intersect((int X0, int Y0, int X1, int Y1) a, (int X0, int Y0, int X1, int Y1)? b) =>
        b is { } c ? (Math.Max(a.X0, c.X0), Math.Max(a.Y0, c.Y0), Math.Min(a.X1, c.X1), Math.Min(a.Y1, c.Y1)) : a;

    // ------------------------------------------------------------------ patterns and shadings

    /// <summary>The colors of the current pattern over a device region, or null if the pattern cannot be painted.</summary>
    private Layer? PatternLayer(GraphicsState gs, bool stroke, (int X0, int Y0, int X1, int Y1) region)
    {
        var pattern = stroke ? gs.StrokePattern : gs.FillPattern;
        if (pattern == null || region.X1 < region.X0 || region.Y1 < region.Y0) return null;
        var layer = new Layer(region.X0, region.Y0, region.X1 - region.X0 + 1, region.Y1 - region.Y0 + 1);
        var toDevice = MatrixMath.FromArray(pattern.GetArray(PdfName.Get("Matrix"))).Multiply(gs.BaseCtm);
        switch (pattern.GetInt(PdfName.Get("PatternType")))
        {
            case 1 when pattern is PdfStream tiling:
            {
                var space = stroke ? gs.StrokeSpace : gs.FillSpace;
                Rgb? uncolored = null;
                if (tiling.GetInt(PdfName.Get("PaintType"), 1) == 2)
                {
                    var underlying = space.Underlying ?? ColorSpace.Gray;
                    var values = stroke ? gs.StrokeColor : gs.FillColor;
                    uncolored = underlying.ToRgb(values.Length >= underlying.Components ? values : values.Concat(new double[underlying.Components - values.Length]).ToArray());
                }
                return PaintTiling(tiling, toDevice, uncolored, layer) ? layer : null;
            }
            case 2:
            {
                var shading = GetShading(pattern.Get(PdfName.Get("Shading")), gs.Resources);
                if (shading == null) return null;
                shading.Render(layer, toDevice);
                if (shading.Background is { } background)
                    for (long i = 0; i < layer.Rgba.Length; i += 4)
                        if (layer.Rgba[i + 3] == 0) layer.Set(i, background);
                return layer;
            }
            default:
                DiagnosticContext.Warning(PdfDiagnosticCode.PaintNotSupported,
                    $"A pattern of type {pattern.GetInt(PdfName.Get("PatternType"), 0)} is invalid or unsupported; it is not painted.", pattern);
                return null;
        }
    }

    private Shading? GetShading(PdfObject? value, PdfDictionary? resources)
    {
        if (value?.Resolve() is not PdfDictionary dict) return null;
        if (!_shadings.TryGetValue(dict, out var shading))
        {
            shading = Shading.Load(dict, resources);
            if (shading == null)
                DiagnosticContext.Warning(PdfDiagnosticCode.PaintNotSupported,
                    $"A shading of type {dict.GetInt(PdfName.Get("ShadingType"), 0)} is invalid or unsupported; it is not painted.", dict);
            if (_shadings.Count < 256) _shadings[dict] = shading;
        }
        return shading;
    }

    /// <summary>The sh operator: the shading painted over the clip region (and its bounding box) in user space.</summary>
    private void PaintShading(Shading shading, GraphicsState gs)
    {
        if (gs.FillAlpha <= 0) return;
        _rasterizer.Clear();
        if (shading.BBox is { } bbox)
        {
            var box = new PathGeometry();
            box.Rectangle(bbox.Left, bbox.Bottom, bbox.Width, bbox.Height);
            foreach (var (points, _) in box.Flatten(gs.Ctm, Tolerance)) _rasterizer.AddPolygon(points);
        }
        else
        {
            // the whole clip region (its bounding rectangle; the mask does the rest)
            var (x0, y0, x1, y1) = gs.ClipBounds ?? (0, 0, _surface.Width - 1, _surface.Height - 1);
            if (x1 < x0 || y1 < y0) return;
            _rasterizer.AddPolygon(new[] { ((double)x0, (double)y0), (x1 + 1.0, (double)y0), (x1 + 1.0, y1 + 1.0), ((double)x0, y1 + 1.0) });
        }
        var region = Intersect(DeviceRegion(_rasterizer.Bounds), gs.ClipBounds);
        if (region.X1 < region.X0 || region.Y1 < region.Y0) return;
        var layer = new Layer(region.X0, region.Y0, region.X1 - region.X0 + 1, region.Y1 - region.Y0 + 1);
        shading.Render(layer, gs.Ctm);
        PaintRows(evenOdd: false, gs, Rgb.Black, gs.FillAlpha, layer);
    }

    /// <summary>
    /// One cell of a tiling pattern, rendered with <see cref="Supersampling"/> times the device resolution and sampled
    /// with wrap-around: each device pixel averages a grid of samples, which gives the coverage of thin lines.
    /// </summary>
    private sealed class Tile(Surface cell, double xStep, double yStep)
    {
        public const int Supersampling = 4;

        public void Sample(Layer layer, Matrix toDevice)
        {
            if (MatrixMath.Invert(toDevice) is not { } inverse) return;
            const int n = Supersampling;
            double sx = cell.Width / xStep, sy = cell.Height / yStep;
            for (int y = layer.Y0; y < layer.Y0 + layer.Height; y++)
                for (int x = layer.X0; x < layer.X0 + layer.Width; x++)
                {
                    double r = 0, g = 0, b = 0, a = 0;
                    for (int j = 0; j < n; j++)
                        for (int i = 0; i < n; i++)
                        {
                            var (px, py) = inverse.Transform(x + (i + 0.5) / n, y + (j + 0.5) / n);
                            double u = px % xStep, v = py % yStep;
                            if (u < 0) u += xStep;
                            if (v < 0) v += yStep;
                            int cx = Math.Min(cell.Width - 1, (int)(u * sx)), cy = Math.Min(cell.Height - 1, (int)(v * sy));
                            long s = ((long)cy * cell.Width + cx) * 4;
                            double pa = cell.Pixels[s + 3];
                            if (pa == 0) continue;
                            r += cell.Pixels[s] * pa;
                            g += cell.Pixels[s + 1] * pa;
                            b += cell.Pixels[s + 2] * pa;
                            a += pa;
                        }
                    if (a <= 0) continue;
                    long target = layer.IndexOf(x, y);
                    layer.Rgba[target] = (byte)Math.Round(r / a);
                    layer.Rgba[target + 1] = (byte)Math.Round(g / a);
                    layer.Rgba[target + 2] = (byte)Math.Round(b / a);
                    layer.Rgba[target + 3] = (byte)Math.Round(a / (n * n));
                }
        }
    }

    /// <summary>
    /// Fills the layer with a tiling pattern: few tiles (large steps) are drawn directly at device resolution, many
    /// small ones through a cached cell.
    /// </summary>
    private bool PaintTiling(PdfStream pattern, Matrix toDevice, Rgb? uncolored, Layer layer)
    {
        double xStep = Math.Abs(pattern.GetDouble(PdfName.Get("XStep"))), yStep = Math.Abs(pattern.GetDouble(PdfName.Get("YStep")));
        if (!(xStep > 0) || !(yStep > 0) || pattern.GetRectangle(PdfName.BBox) is not { } bbox) return false;
        if (MatrixMath.Invert(toDevice) is not { } inverse) return false;

        // the tiles whose bounding box can reach the layer
        var corners = new[]
        {
            inverse.Transform(layer.X0, layer.Y0), inverse.Transform(layer.X0 + layer.Width, layer.Y0),
            inverse.Transform(layer.X0, layer.Y0 + layer.Height), inverse.Transform(layer.X0 + layer.Width, layer.Y0 + layer.Height),
        };
        double minU = corners.Min(c => c.X), maxU = corners.Max(c => c.X), minV = corners.Min(c => c.Y), maxV = corners.Max(c => c.Y);
        double i0 = Math.Floor((minU - bbox.Right) / xStep), i1 = Math.Ceiling((maxU - bbox.Left) / xStep);
        double j0 = Math.Floor((minV - bbox.Top) / yStep), j1 = Math.Ceiling((maxV - bbox.Bottom) / yStep);
        if ((i1 - i0 + 1) * (j1 - j0 + 1) <= 16)
        {
            var surface = new Surface(layer.Width, layer.Height, null);
            var offset = Matrix.Translate(-layer.X0, -layer.Y0);
            for (double j = j0; j <= j1; j++)
                for (double i = i0; i <= i1; i++)
                    RenderTile(pattern, bbox, Matrix.Translate(i * xStep, j * yStep).Multiply(toDevice).Multiply(offset), surface);
            if (uncolored is { } c) Recolor(surface, c);
            Array.Copy(surface.Pixels, layer.Rgba, layer.Rgba.Length);
            return true;
        }
        var tile = TileCell(pattern, bbox, xStep, yStep, toDevice, uncolored);
        if (tile == null) return false;
        tile.Sample(layer, toDevice);
        return true;
    }

    private static void Recolor(Surface surface, Rgb color)
    {
        var px = surface.Pixels;
        for (long p = 0; p < px.Length; p += 4) (px[p], px[p + 1], px[p + 2]) = (color.R, color.G, color.B);
    }

    /// <summary>Draws the pattern's content once on a surface, clipped to its bounding box.</summary>
    private void RenderTile(PdfStream pattern, PdfRectangle bbox, Matrix ctm, Surface target, double minLineWidth = 1)
    {
        if (!_activeForms.Add(pattern)) return; // a pattern that uses itself
        var parent = _surface;
        var parentRasterizer = _rasterizer;
        double parentMinLineWidth = _minLineWidth;
        _surface = target;
        _rasterizer = new Rasterizer();
        _minLineWidth = minLineWidth;
        try
        {
            var resources = pattern.GetDictionary(PdfName.Resources);
            var gs = new GraphicsState { Ctm = ctm, BaseCtm = ctm, Resources = resources };
            var box = new PathGeometry();
            box.Rectangle(bbox.Left, bbox.Bottom, bbox.Width, bbox.Height);
            ApplyClip(box, gs, evenOdd: false);
            Render(pattern.GetBytes(), resources, gs, _depth + 1);
        }
        finally
        {
            _surface = parent;
            _rasterizer = parentRasterizer;
            _minLineWidth = parentMinLineWidth;
            _activeForms.Remove(pattern);
        }
    }

    private Tile? TileCell(PdfStream pattern, PdfRectangle bbox, double xStep, double yStep, Matrix toDevice, Rgb? uncolored)
    {
        var key = (pattern, toDevice, uncolored);
        foreach (var (k, cached) in _tiles)
            if (k.Equals(key)) return cached;
        // cell resolution: the device length of the pattern space axes, supersampled
        double sx = Math.Sqrt(toDevice.A * toDevice.A + toDevice.B * toDevice.B), sy = Math.Sqrt(toDevice.C * toDevice.C + toDevice.D * toDevice.D);
        int width = (int)Math.Clamp(Math.Ceiling(xStep * sx * Tile.Supersampling), 1, 4096);
        int height = (int)Math.Clamp(Math.Ceiling(yStep * sy * Tile.Supersampling), 1, 4096);
        if ((long)width * height > 4_000_000) (width, height) = ((int)Math.Max(1, width * Math.Sqrt(4e6 / ((long)width * height))), (int)Math.Max(1, height * Math.Sqrt(4e6 / ((long)width * height))));
        var cell = new Surface(width, height, null);
        var cellMatrix = Matrix.Scale(width / xStep, height / yStep);
        // cell pixels per device pixel (for the minimum line width)
        double cellScale = Math.Max(1, Math.Min(width / (xStep * sx), height / (yStep * sy)));
        // the cell and its neighbors, so that content reaching over the cell boundary is included
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
                RenderTile(pattern, bbox, Matrix.Translate(i * xStep, j * yStep).Multiply(cellMatrix), cell, cellScale * _minLineWidth);
        if (uncolored is { } color) Recolor(cell, color);
        var tile = new Tile(cell, xStep, yStep);
        if (_tiles.Count >= 16) _tiles.RemoveAt(0);
        _tiles.Add((key, tile));
        return tile;
    }

    // ------------------------------------------------------------------ transparency

    /// <summary>
    /// A soft mask from an ExtGState /SMask dictionary (ISO 32000-2, 11.6.5.2): the group rendered in the current
    /// coordinate system, its alpha or luminosity (over the backdrop color) as mask values, through /TR.
    /// </summary>
    private byte[]? SoftMask(PdfDictionary smask, GraphicsState gs)
    {
        if (smask.GetStream(PdfName.Get("G")) is not { } group) return null;
        foreach (var (k, cached) in _softMasks)
            if (ReferenceEquals(k.Mask, smask) && k.Ctm == gs.Ctm && k.Width == _surface.Width) return cached;
        bool luminosity = smask.GetName(PdfName.S)?.Value == "Luminosity";
        var backdrop = Rgb.Black;
        if (luminosity && smask.GetArray(PdfName.Get("BC")) is { Count: > 0 } bc)
        {
            var groupSpace = ColorSpace.Resolve(group.GetDictionary(PdfName.Get("Group"))?.Get(PdfName.Get("CS")), group.GetDictionary(PdfName.Resources));
            var values = bc.Select(v => (v.Resolve() as PdfNumber)?.Value ?? 0).ToArray();
            backdrop = groupSpace.ToRgb(values.Length >= groupSpace.Components ? values : values.Concat(new double[groupSpace.Components - values.Length]).ToArray());
        }
        var target = new Surface(_surface.Width, _surface.Height, luminosity ? (backdrop.R, backdrop.G, backdrop.B) : null);
        var parent = _surface;
        _surface = target;
        try
        {
            DrawForm(group, gs.Resources, new GraphicsState { Ctm = gs.Ctm, BaseCtm = gs.Ctm, Resources = gs.Resources }, _depth + 1);
        }
        finally
        {
            _surface = parent;
        }
        byte[]? transfer = null;
        if (PdfFunction.Load(smask.Get(PdfName.Get("TR"))) is { Inputs: 1, Outputs: >= 1 } tr)
        {
            transfer = new byte[256];
            Span<double> output = stackalloc double[tr.Outputs];
            Span<double> input = stackalloc double[1];
            for (int v = 0; v < 256; v++)
            {
                input[0] = v / 255.0;
                tr.Evaluate(input, output);
                transfer[v] = (byte)Math.Round(Math.Clamp(output[0], 0, 1) * 255);
            }
        }
        var mask = new byte[target.Width * target.Height];
        var px = target.Pixels;
        for (int i = 0; i < mask.Length; i++)
        {
            int value = luminosity ? (int)Math.Round(0.3 * px[4 * i] + 0.59 * px[4 * i + 1] + 0.11 * px[4 * i + 2]) : px[4 * i + 3];
            mask[i] = transfer != null ? transfer[value] : (byte)value;
        }
        if (_softMasks.Count >= 8) _softMasks.RemoveAt(0);
        _softMasks.Add(((smask, gs.Ctm, _surface.Width), mask));
        return mask;
    }

    private void DrawForm(PdfStream form, PdfDictionary? parentResources, GraphicsState gs, int depth)
    {
        if (!_activeForms.Add(form))
        {
            DiagnosticContext.Warning(PdfDiagnosticCode.NestingTooDeep, "A form XObject uses itself; the inner use is not drawn.", form);
            return;
        }
        try
        {
            var inner = gs.Clone();
            inner.Ctm = MatrixMath.FromArray(form.GetArray(PdfName.Get("Matrix"))).Multiply(gs.Ctm);
            inner.BaseCtm = inner.Ctm;
            var resources = form.GetDictionary(PdfName.Resources) ?? parentResources;
            inner.Resources = resources;
            var bbox = form.GetRectangle(PdfName.BBox);
            if (bbox is { } box)
            {
                var path = new PathGeometry();
                path.Rectangle(box.Left, box.Bottom, box.Width, box.Height);
                ApplyClip(path, inner, evenOdd: false);
            }
            // a transparency group is composited as a whole (isolated) when it has opacity, a blend mode or a soft
            // mask, is a knockout group or is an element of one; other groups are drawn directly (non-isolated)
            var groupDict = form.GetDictionary(PdfName.Get("Group"));
            bool group = groupDict?.GetName(PdfName.S)?.Value == "Transparency";
            bool knockout = group && groupDict!.GetBool(PdfName.Get("K"));
            bool plain = gs.FillAlpha >= 1 && gs.BlendMode == BlendMode.Normal && gs.SoftMask == null;
            if (group && (!plain || knockout || _surface.Knockout) && _groupDepth < 8)
            {
                inner.FillAlpha = inner.StrokeAlpha = 1;
                inner.BlendMode = BlendMode.Normal;
                inner.SoftMask = null;
                // a non-isolated knockout group without effects starts from (and knocks out to) what is below it
                bool fromBackdrop = knockout && plain && !_surface.Knockout && !groupDict!.GetBool(PdfName.Get("I"));
                var target = fromBackdrop
                    ? new Surface(_surface) { Knockout = true, Backdrop = (byte[])_surface.Pixels.Clone() }
                    : new Surface(_surface.Width, _surface.Height, null) { Knockout = knockout, Shape = _surface.Knockout ? new byte[_surface.Width * _surface.Height] : null };
                var parent = _surface;
                _surface = target;
                _groupDepth++;
                try
                {
                    Render(form.GetBytes(), resources, inner, depth + 1);
                }
                finally
                {
                    _surface = parent;
                    _groupDepth--;
                }
                if (fromBackdrop) Array.Copy(target.Pixels, parent.Pixels, parent.Pixels.Length);
                else CompositeGroup(target, gs);
                return;
            }
            Render(form.GetBytes(), resources, inner, depth + 1);
        }
        finally
        {
            _activeForms.Remove(form);
        }
    }

    private void CompositeGroup(Surface group, GraphicsState gs)
    {
        var px = group.Pixels;
        var softMask = gs.SoftMask;
        long count = (long)group.Width * group.Height;
        for (long i = 0; i < count; i++)
        {
            byte a = px[4 * i + 3];
            // the group's shape and opacity (its alpha divided by its shape) when the shape was recorded
            double shape = group.Shape != null ? group.Shape[i] / 255.0 : a / 255.0;
            if (a == 0 && (group.Shape == null || !_surface.Knockout) || shape <= 0) continue;
            double opacity = group.Shape != null ? Math.Min(1, a / 255.0 / shape) : 1;
            opacity *= softMask != null ? gs.FillAlpha * softMask[i] / 255.0 : gs.FillAlpha;
            _surface.Paint(i, px[4 * i], px[4 * i + 1], px[4 * i + 2], shape, opacity, gs.BlendMode);
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
        var ctm = gs.Ctm;
        if (MatrixMath.Invert(ctm) is not { } inverse) return;
        var corners = new[] { ctm.Transform(0, 0), ctm.Transform(1, 0), ctm.Transform(0, 1), ctm.Transform(1, 1) };
        var region = Intersect(DeviceRegion((corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y))), gs.ClipBounds);
        if (region.X1 < region.X0 || region.Y1 < region.Y0) return;
        // a stencil mask painted with a pattern takes the pattern's colors
        Layer? layer = null;
        if (stencil && gs.FillSpace.IsNone) return;
        if (stencil && gs.FillSpace.IsPattern && (layer = PatternLayer(gs, stroke: false, region)) == null) return;
        var fill = gs.Fill;
        for (int y = region.Y0; y <= region.Y1; y++)
        {
            for (int x = region.X0; x <= region.X1; x++)
            {
                var (u, v) = inverse.Transform(x + 0.5, y + 0.5);
                if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
                int ix = Math.Min(img.Width - 1, (int)(u * img.Width)), iy = Math.Min(img.Height - 1, (int)((1 - v) * img.Height));
                int p = iy * img.Width + ix;
                // stencil masks and masked images: the mask is shape; the alpha constant and the soft mask are opacity
                long index = (long)y * _surface.Width + x;
                double shape = gs.Clip != null ? gs.Clip[index] / 255.0 : 1;
                if (stencil && img.Alpha != null) shape *= img.Alpha[p] / 255.0;
                double opacity = gs.FillAlpha * (!stencil && img.Alpha != null ? img.Alpha[p] / 255.0 : 1);
                if (gs.SoftMask != null) opacity *= gs.SoftMask[index] / 255.0;
                if (shape <= 0 || opacity <= 0 && !_surface.Knockout) continue;
                if (layer != null)
                {
                    long li = layer.IndexOf(x, y);
                    if (li >= 0 && layer.Rgba[li + 3] > 0)
                        _surface.Paint(index, layer.Rgba[li], layer.Rgba[li + 1], layer.Rgba[li + 2], shape, opacity * layer.Rgba[li + 3] / 255.0, gs.BlendMode);
                }
                else if (stencil) _surface.Paint(index, fill.R, fill.G, fill.B, shape, opacity, gs.BlendMode);
                else _surface.Paint(index, img.Rgb[p * 3], img.Rgb[p * 3 + 1], img.Rgb[p * 3 + 2], shape, opacity, gs.BlendMode);
            }
        }
    }
}
