using System.Text;
using System.Text.RegularExpressions;
using MiPdf.Content;
using MiPdf.Fonts;
using MiPdf.Forms;
using MiPdf.Graphics;
using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.Text;

namespace MiPdf.Redaction;

/// <summary>Options for redaction.</summary>
public sealed class RedactionOptions
{
    /// <summary>Color of the boxes drawn over redacted areas (default black); null draws nothing.</summary>
    public PdfColor? FillColor { get; init; } = PdfColor.Black;

    /// <summary>Optional text drawn into each box (e.g. "REDACTED").</summary>
    public string? OverlayText { get; init; }

    public PdfFont OverlayFont { get; init; } = PdfStandardFont.Helvetica;

    public PdfColor OverlayTextColor { get; init; } = PdfColor.White;

    /// <summary>Remove annotations and form fields that intersect a redacted area (default true).</summary>
    public bool RemoveAnnotations { get; init; } = true;

    /// <summary>Remove vector graphics (paths) that lie completely inside a redacted area (default true).</summary>
    public bool RemoveVectorGraphics { get; init; } = true;

    /// <summary>A glyph is removed when this share of its box overlaps a redacted area (default 0.3).</summary>
    public double GlyphOverlapThreshold { get; init; } = 0.3;
}

/// <summary>What a redaction removed.</summary>
public sealed class RedactionResult
{
    public int GlyphsRemoved { get; internal set; }

    /// <summary>Images removed completely (inside an area, or not decodable for partial redaction, e.g. JPEG).</summary>
    public int ImagesRemoved { get; internal set; }

    /// <summary>Images whose pixels inside the areas were blacked out.</summary>
    public int ImagesModified { get; internal set; }

    public int PathsRemoved { get; internal set; }

    public int AnnotationsRemoved { get; internal set; }

    public List<string> Warnings { get; } = new();

    internal void Add(RedactionResult other)
    {
        GlyphsRemoved += other.GlyphsRemoved;
        ImagesRemoved += other.ImagesRemoved;
        ImagesModified += other.ImagesModified;
        PathsRemoved += other.PathsRemoved;
        AnnotationsRemoved += other.AnnotationsRemoved;
        Warnings.AddRange(other.Warnings);
    }

    public override string ToString() =>
        $"glyphs={GlyphsRemoved}, images removed={ImagesRemoved}, images modified={ImagesModified}, paths={PathsRemoved}, annotations={AnnotationsRemoved}";
}

/// <summary>
/// True redaction: the content inside the given areas is removed from the page content (text glyphs, image pixels,
/// vector graphics, annotations, form fields, also inside form XObjects) and covered with boxes. The document must
/// then be saved completely (not incrementally), so that the removed content is not kept in older revisions.
/// </summary>
/// <example>
/// <code>
/// using var doc = PdfDocument.Open("contract.pdf");
/// doc.RedactText("John Smith");
/// doc.RedactText(new Regex(@"AT\d{2}(?: ?\d{4}){4}"));        // IBANs
/// doc.GetPage(1).Redact(new[] { new PdfRectangle(72, 700, 300, 720) });
/// doc.Save("contract-redacted.pdf");
/// </code>
/// </example>
public static class PdfRedactor
{
    /// <summary>Redacts areas (in default user space, i.e. page coordinates) of a page.</summary>
    public static RedactionResult Redact(PdfPage page, IEnumerable<PdfRectangle> areas, RedactionOptions? options = null)
    {
        options ??= new RedactionOptions();
        var list = areas.Where(a => a.Width > 0 && a.Height > 0).ToList();
        var result = new RedactionResult();
        if (list.Count == 0) return result;
        page.Document.PrepareFontsForReading();
        page.Document.MarkRedacted();

        var resources = page.Resources;
        var redactor = new ContentRedactor(page.Document, list, options, result);
        var content = redactor.Rewrite(page.GetContentBytes(), resources, Matrix.Identity, 0);
        page.SetContent(content);

        if (options.RemoveAnnotations) RemoveAnnotations(page, list, result);

        if (options.FillColor is { } fill || options.OverlayText != null)
        {
            using var canvas = page.GetCanvas();
            canvas.SaveState();
            foreach (var area in list)
            {
                if (options.FillColor is { } f) canvas.SetFillColor(f).Rectangle(area).Fill();
                if (options.OverlayText is { Length: > 0 } text)
                {
                    double size = Math.Min(area.Height * 0.7, area.Width / Math.Max(1, options.OverlayFont.GetTextWidth(text, 1)) * 0.9);
                    if (size >= 3)
                        canvas.DrawText(text, options.OverlayFont, size, area.Left + area.Width / 2,
                            area.Bottom + (area.Height - options.OverlayFont.CapHeight * size / 1000) / 2, options.OverlayTextColor, TextAlignment.Center);
                }
            }
            canvas.RestoreState();
        }
        return result;
    }

    /// <summary>Redacts every occurrence of <paramref name="text"/> in the document.</summary>
    public static RedactionResult RedactText(PdfDocument document, string text, StringComparison comparison = StringComparison.Ordinal, RedactionOptions? options = null) =>
        RedactMatches(document, page => page.FindText(text, comparison), options);

    /// <summary>Redacts every match of <paramref name="pattern"/> (matched per text line).</summary>
    public static RedactionResult RedactText(PdfDocument document, Regex pattern, RedactionOptions? options = null) =>
        RedactMatches(document, page => page.FindText(pattern), options);

    private static RedactionResult RedactMatches(PdfDocument document, Func<PdfPage, IReadOnlyList<PdfRectangle>> find, RedactionOptions? options)
    {
        var total = new RedactionResult();
        foreach (var page in document.Pages)
        {
            var hits = find(page).Select(r => new PdfRectangle(r.Left - 0.5, r.Bottom - 0.5, r.Right + 0.5, r.Top + 0.5)).ToList();
            if (hits.Count > 0) total.Add(Redact(page, hits, options));
        }
        return total;
    }

    /// <summary>Marks an area for redaction with a Redact annotation (reviewable before <see cref="ApplyRedactions"/>).</summary>
    public static PdfAnnotation MarkForRedaction(PdfPage page, PdfRectangle area, string? overlayText = null)
    {
        var dict = new PdfDictionary();
        dict[PdfName.Subtype] = PdfName.Get("Redact");
        dict[PdfName.Rect] = PdfArray.FromRectangle(area);
        dict[PdfName.Get("QuadPoints")] = PdfArray.FromNumbers(area.Left, area.Top, area.Right, area.Top, area.Left, area.Bottom, area.Right, area.Bottom);
        dict[PdfName.Get("IC")] = PdfArray.FromNumbers(0, 0, 0);
        dict[PdfName.C] = PdfArray.FromNumbers(1, 0, 0);
        if (overlayText != null) dict.SetText(PdfName.Get("OverlayText"), overlayText);
        dict.Set(PdfName.F, 4);
        return page.AddAnnotation(dict);
    }

    /// <summary>Applies all Redact annotations of the document and removes them.</summary>
    public static RedactionResult ApplyRedactions(PdfDocument document, RedactionOptions? options = null)
    {
        var total = new RedactionResult();
        foreach (var page in document.Pages)
        {
            var marks = page.Annotations.Where(a => a.Subtype == "Redact").ToList();
            if (marks.Count == 0) continue;
            var areas = new List<PdfRectangle>();
            foreach (var mark in marks)
            {
                if (mark.Dictionary.GetArray(PdfName.Get("QuadPoints")) is { Count: >= 8 } q)
                {
                    for (int i = 0; i + 7 < q.Count; i += 8)
                    {
                        var xs = new[] { q.GetDouble(i), q.GetDouble(i + 2), q.GetDouble(i + 4), q.GetDouble(i + 6) };
                        var ys = new[] { q.GetDouble(i + 1), q.GetDouble(i + 3), q.GetDouble(i + 5), q.GetDouble(i + 7) };
                        areas.Add(new PdfRectangle(xs.Min(), ys.Min(), xs.Max(), ys.Max()));
                    }
                }
                else areas.Add(mark.Rect);
                page.RemoveAnnotation(mark);
            }
            string? overlay = marks.Select(m => m.Dictionary.GetText(PdfName.Get("OverlayText"))).FirstOrDefault(t => t != null);
            var pageOptions = options ?? new RedactionOptions { OverlayText = overlay };
            total.Add(Redact(page, areas, pageOptions));
        }
        return total;
    }

    private static void RemoveAnnotations(PdfPage page, List<PdfRectangle> areas, RedactionResult result)
    {
        var form = page.Document.GetForm();
        foreach (var annotation in page.Annotations.ToList())
        {
            if (!areas.Any(a => Intersects(a, annotation.Rect))) continue;
            if (annotation.Subtype == "Widget" && form != null)
            {
                var field = form.Fields.FirstOrDefault(f => f.Widgets.Any(w => ReferenceEquals(w.Dictionary, annotation.Dictionary)));
                if (field != null)
                {
                    form.RemoveField(field);
                    result.AnnotationsRemoved++;
                    continue;
                }
            }
            page.RemoveAnnotation(annotation);
            result.AnnotationsRemoved++;
        }
    }

    internal static bool Intersects(PdfRectangle a, PdfRectangle b) =>
        a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;
}

/// <summary>Rewrites a content stream without the content inside the redacted areas.</summary>
internal sealed class ContentRedactor
{
    private readonly PdfDocument _document;
    private readonly List<PdfRectangle> _areas;
    private readonly RedactionOptions _options;
    private readonly RedactionResult _result;
    private readonly Dictionary<PdfDictionary, FontDecoder> _decoders = new(ReferenceEqualityComparer.Instance);
    private readonly PdfObjectSerializer _serializer = new();

    public ContentRedactor(PdfDocument document, List<PdfRectangle> areas, RedactionOptions options, RedactionResult result)
    {
        _document = document;
        _areas = areas;
        _options = options;
        _result = result;
    }

    private sealed class State
    {
        public Matrix Ctm = Matrix.Identity;
        public PdfDictionary? Font;
        public double FontSize = 1, CharSpacing, WordSpacing, HorizontalScale = 1, Leading, Rise;
        public State Clone() => (State)MemberwiseClone();
    }

    public byte[] Rewrite(byte[] content, PdfDictionary? resources, Matrix ctm, int depth)
    {
        var output = new MemoryStream();
        var gs = new State { Ctm = ctm };
        var stack = new Stack<State>();
        Matrix tm = Matrix.Identity, tlm = Matrix.Identity;
        var path = new List<(double X, double Y)>();
        var pathOps = new List<ContentOperation>();
        var usedNames = new HashSet<PdfName>();
        var replacedNames = new HashSet<PdfName>();

        foreach (var op in ContentParser.Parse(content))
        {
            var o = op.Operands;
            switch (op.Operator)
            {
                case "q":
                    stack.Push(gs.Clone());
                    break;
                case "Q":
                    if (stack.Count > 0) gs = stack.Pop();
                    break;
                case "cm":
                    if (o.Count >= 6) gs.Ctm = new Matrix(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5)).Multiply(gs.Ctm);
                    break;
                case "BT":
                    tm = tlm = Matrix.Identity;
                    break;
                case "Tf":
                    if (o.Count >= 2)
                    {
                        gs.Font = o[0] is PdfName fn ? resources?.GetDictionary(PdfName.Font)?.GetDictionary(fn) : null;
                        gs.FontSize = N(o, 1);
                    }
                    break;
                case "Tc": gs.CharSpacing = N(o, 0); break;
                case "Tw": gs.WordSpacing = N(o, 0); break;
                case "Tz": gs.HorizontalScale = N(o, 0) / 100; break;
                case "TL": gs.Leading = N(o, 0); break;
                case "Ts": gs.Rise = N(o, 0); break;
                case "Td":
                    tlm = Matrix.Translate(N(o, 0), N(o, 1)).Multiply(tlm);
                    tm = tlm;
                    break;
                case "TD":
                    gs.Leading = -N(o, 1);
                    tlm = Matrix.Translate(N(o, 0), N(o, 1)).Multiply(tlm);
                    tm = tlm;
                    break;
                case "Tm":
                    if (o.Count >= 6) tm = tlm = new Matrix(N(o, 0), N(o, 1), N(o, 2), N(o, 3), N(o, 4), N(o, 5));
                    break;
                case "T*":
                    tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                    tm = tlm;
                    break;
                case "Tj":
                case "'":
                case "\"":
                case "TJ":
                    WriteText(output, op, gs, ref tm, ref tlm);
                    continue;
                case "Do":
                    if (o.Count >= 1 && o[0] is PdfName name && resources?.GetDictionary(PdfName.XObject)?.GetStream(name) is { } xobject)
                    {
                        var replacement = RedactXObject(name, xobject, resources, gs.Ctm, depth);
                        if (replacement != name) replacedNames.Add(name);
                        if (replacement == null) continue; // removed
                        usedNames.Add(replacement);
                        WriteOp(output, "Do", replacement);
                        continue;
                    }
                    break;
                case "BI":
                    if (op.InlineImage != null && Covered(UnitSquare(gs.Ctm)) != Coverage.None)
                    {
                        _result.ImagesRemoved++;
                        continue;
                    }
                    break;
                // path construction
                case "m":
                case "l":
                    if (o.Count >= 2) path.Add(gs.Ctm.Transform(N(o, 0), N(o, 1)));
                    pathOps.Add(op);
                    continue;
                case "c":
                    if (o.Count >= 6) for (int i = 0; i < 6; i += 2) path.Add(gs.Ctm.Transform(N(o, i), N(o, i + 1)));
                    pathOps.Add(op);
                    continue;
                case "v":
                case "y":
                    if (o.Count >= 4) for (int i = 0; i < 4; i += 2) path.Add(gs.Ctm.Transform(N(o, i), N(o, i + 1)));
                    pathOps.Add(op);
                    continue;
                case "re":
                    if (o.Count >= 4)
                    {
                        double x = N(o, 0), y = N(o, 1), w = N(o, 2), h = N(o, 3);
                        path.Add(gs.Ctm.Transform(x, y));
                        path.Add(gs.Ctm.Transform(x + w, y));
                        path.Add(gs.Ctm.Transform(x, y + h));
                        path.Add(gs.Ctm.Transform(x + w, y + h));
                    }
                    pathOps.Add(op);
                    continue;
                case "h":
                case "W":
                case "W*":
                    pathOps.Add(op);
                    continue;
                case "S":
                case "s":
                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                case "n":
                    foreach (var p in pathOps) WriteOperation(output, p);
                    bool remove = op.Operator != "n" && _options.RemoveVectorGraphics && path.Count > 0 && Covered(Bounds(path)) == Coverage.Full;
                    if (remove) _result.PathsRemoved++;
                    if (remove) output.Write("n\n"u8);
                    else WriteOperation(output, op);
                    path.Clear();
                    pathOps.Clear();
                    continue;
            }
            WriteOperation(output, op);
        }
        foreach (var p in pathOps) WriteOperation(output, p);

        // drop resources that are no longer drawn, otherwise the original (unredacted) objects stay in the file
        if (resources?.GetDictionary(PdfName.XObject) is { } xobjects)
            foreach (var name in replacedNames)
                if (!usedNames.Contains(name)) xobjects.Remove(name);
        return output.ToArray();
    }

    // ------------------------------------------------------------------ text

    private void WriteText(MemoryStream output, ContentOperation op, State gs, ref Matrix tm, ref Matrix tlm)
    {
        var o = op.Operands;
        var prefix = new List<string>();
        var parts = new List<PdfObject>();
        switch (op.Operator)
        {
            case "Tj":
                if (o.Count >= 1) parts.Add(o[0]);
                break;
            case "'":
                tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                tm = tlm;
                prefix.Add("T*");
                if (o.Count >= 1) parts.Add(o[^1]);
                break;
            case "\"":
                if (o.Count >= 3)
                {
                    gs.WordSpacing = N(o, 0);
                    gs.CharSpacing = N(o, 1);
                    prefix.Add($"{PdfNumber.FormatReal(gs.WordSpacing)} Tw");
                    prefix.Add($"{PdfNumber.FormatReal(gs.CharSpacing)} Tc");
                }
                tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                tm = tlm;
                prefix.Add("T*");
                if (o.Count >= 1) parts.Add(o[^1]);
                break;
            default:
                if (o.Count >= 1 && o[0] is PdfArray array) parts.AddRange(array);
                break;
        }

        var decoder = GetDecoder(gs.Font);
        var newArray = new PdfArray();
        bool changed = false;
        double pendingAdjust = 0;
        foreach (var part in parts)
        {
            if (part is PdfNumber adjust)
            {
                tm = Matrix.Translate(-adjust.Value / 1000 * gs.FontSize * gs.HorizontalScale, 0).Multiply(tm);
                pendingAdjust += adjust.Value;
                continue;
            }
            if (part is not PdfString str) continue;
            if (decoder == null)
            {
                FlushAdjust(newArray, ref pendingAdjust);
                newArray.Add(str);
                continue;
            }
            var kept = new List<byte>();
            int pos = 0;
            foreach (var glyph in decoder.Decode(str.Bytes))
            {
                double tx = (glyph.Width * gs.FontSize + gs.CharSpacing + (glyph.IsSingleByteSpace ? gs.WordSpacing : 0)) * gs.HorizontalScale;
                bool remove = IsGlyphRedacted(glyph, gs, tm, tx);
                int length = Math.Min(glyph.Length, str.Bytes.Length - pos);
                if (remove)
                {
                    changed = true;
                    _result.GlyphsRemoved++;
                    if (kept.Count > 0)
                    {
                        FlushAdjust(newArray, ref pendingAdjust);
                        newArray.Add(new PdfString(kept.ToArray(), str.IsHex));
                        kept.Clear();
                    }
                    if (gs.FontSize != 0 && gs.HorizontalScale != 0) pendingAdjust -= tx * 1000 / (gs.FontSize * gs.HorizontalScale);
                }
                else
                {
                    if (kept.Count == 0) FlushAdjust(newArray, ref pendingAdjust);
                    kept.AddRange(str.Bytes.AsSpan(pos, length).ToArray());
                }
                pos += length;
                tm = Matrix.Translate(tx, 0).Multiply(tm);
            }
            if (kept.Count > 0)
            {
                FlushAdjust(newArray, ref pendingAdjust);
                newArray.Add(new PdfString(kept.ToArray(), str.IsHex));
            }
        }

        if (!changed)
        {
            WriteOperation(output, op);
            return;
        }
        foreach (var p in prefix) output.Write(Encoding.ASCII.GetBytes(p + "\n"));
        FlushAdjust(newArray, ref pendingAdjust);
        WriteOp(output, "TJ", newArray);
    }

    private static void FlushAdjust(PdfArray array, ref double adjust)
    {
        if (Math.Abs(adjust) > 1e-6) array.Add(new PdfNumber(Math.Round(adjust, 3)));
        adjust = 0;
    }

    private bool IsGlyphRedacted(DecodedGlyph glyph, State gs, Matrix tm, double tx)
    {
        var trm = tm.Multiply(gs.Ctm);
        double size = gs.FontSize;
        // glyph box in text space: advance horizontally, 0.25 em below to 0.85 em above the baseline
        double width = Math.Abs(tx) > 1e-6 ? tx : glyph.Width * size * gs.HorizontalScale;
        var corners = new[]
        {
            trm.Transform(0, gs.Rise - 0.25 * size), trm.Transform(width, gs.Rise - 0.25 * size),
            trm.Transform(0, gs.Rise + 0.85 * size), trm.Transform(width, gs.Rise + 0.85 * size),
        };
        var box = Bounds(corners);
        double area = box.Width * box.Height;
        if (area <= 1e-9)
        {
            var (cx, cy) = (box.Left + box.Width / 2, box.Bottom + box.Height / 2);
            return _areas.Any(a => cx >= a.Left && cx <= a.Right && cy >= a.Bottom && cy <= a.Top);
        }
        foreach (var a in _areas)
        {
            double ix = Math.Min(a.Right, box.Right) - Math.Max(a.Left, box.Left);
            double iy = Math.Min(a.Top, box.Top) - Math.Max(a.Bottom, box.Bottom);
            if (ix > 0 && iy > 0 && ix * iy >= area * _options.GlyphOverlapThreshold) return true;
        }
        return false;
    }

    private FontDecoder? GetDecoder(PdfDictionary? font)
    {
        if (font == null) return null;
        if (!_decoders.TryGetValue(font, out var decoder))
        {
            decoder = new FontDecoder(font);
            _decoders[font] = decoder;
        }
        return decoder;
    }

    // ------------------------------------------------------------------ XObjects

    /// <summary>Returns the name to draw (original or redacted copy), or null if the XObject is removed.</summary>
    private PdfName? RedactXObject(PdfName name, PdfStream xobject, PdfDictionary resources, Matrix ctm, int depth)
    {
        if (xobject.GetName(PdfName.Subtype) == PdfName.Image)
        {
            var coverage = Covered(UnitSquare(ctm));
            if (coverage == Coverage.None) return name;
            if (coverage == Coverage.Full)
            {
                _result.ImagesRemoved++;
                return null;
            }
            var blanked = BlankImage(xobject, ctm);
            if (blanked == null)
            {
                _result.ImagesRemoved++;
                _result.Warnings.Add($"Image /{name.Value} could not be decoded for partial redaction and was removed completely.");
                return null;
            }
            _result.ImagesModified++;
            return AddXObject(resources, _document.AddObject(blanked));
        }

        if (xobject.GetName(PdfName.Subtype) != PdfName.Form || depth > 8) return name;
        var formMatrix = xobject.GetArray(PdfName.Get("Matrix")) is { Count: 6 } m
            ? new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5))
            : Matrix.Identity;
        var total = formMatrix.Multiply(ctm);
        var bbox = xobject.GetRectangle(PdfName.BBox);
        if (bbox is { } b && Covered(Bounds(new[] { total.Transform(b.Left, b.Bottom), total.Transform(b.Right, b.Bottom), total.Transform(b.Left, b.Top), total.Transform(b.Right, b.Top) })) == Coverage.None)
            return name;

        var copy = new PdfStream(Array.Empty<byte>());
        foreach (var (key, value) in xobject)
            if (key != PdfName.Length && key != PdfName.Filter && key != PdfName.DecodeParms) copy[key] = value.Clone();
        var innerResources = (xobject.GetDictionary(PdfName.Resources) ?? resources).Clone() as PdfDictionary ?? new PdfDictionary();
        copy[PdfName.Resources] = innerResources;
        var before = _result.GlyphsRemoved + _result.ImagesRemoved + _result.ImagesModified + _result.PathsRemoved;
        var content = Rewrite(xobject.GetBytes(), innerResources, total, depth + 1);
        if (_result.GlyphsRemoved + _result.ImagesRemoved + _result.ImagesModified + _result.PathsRemoved == before) return name;
        copy.SetBytes(content, compress: true);
        return AddXObject(resources, _document.AddObject(copy));
    }

    private static PdfName AddXObject(PdfDictionary resources, PdfReference reference)
    {
        var xobjects = resources.GetDictionary(PdfName.XObject);
        if (xobjects == null) resources[PdfName.XObject] = xobjects = new PdfDictionary();
        for (int i = 1; ; i++)
        {
            var name = PdfName.Get("Rd" + i);
            if (xobjects.ContainsKey(name)) continue;
            xobjects[name] = reference;
            return name;
        }
    }

    /// <summary>Copy of an image with the pixels inside the areas set to black (or transparent for stencil masks); null if not decodable.</summary>
    private PdfStream? BlankImage(PdfStream image, Matrix ctm)
    {
        byte[] data;
        try
        {
            data = Filters.StreamFilters.Decode(image.RawBytes, image, out var remaining);
            if (remaining != null) return null; // JPEG, JPEG 2000, JBIG2, CCITT
        }
        catch (PdfException)
        {
            return null;
        }
        int width = image.GetInt(PdfName.Get("Width")), height = image.GetInt(PdfName.Get("Height"));
        bool mask = image.GetBool(PdfName.Get("ImageMask"));
        int bpc = mask ? 1 : image.GetInt(PdfName.BitsPerComponent, 8);
        int components = mask ? 1 : ColorComponents(image.Get(PdfName.ColorSpace));
        if (width <= 0 || height <= 0 || components <= 0 || bpc is not (1 or 8 or 16)) return null;
        long rowBytes = ((long)width * components * bpc + 7) / 8;
        if (rowBytes * height > data.Length) return null;
        if (!Invert(ctm, out var inverse)) return null;

        bool invertedMask = mask && image.GetArray(PdfName.Decode) is { Count: >= 2 } d && d.GetDouble(0) == 1;
        byte maskFill = (byte)(invertedMask ? 0x00 : 0xFF); // stencil masks: make the area unpainted
        bool cmyk = components == 4;
        foreach (var area in _areas)
        {
            var pts = new[] { inverse.Transform(area.Left, area.Bottom), inverse.Transform(area.Right, area.Bottom), inverse.Transform(area.Left, area.Top), inverse.Transform(area.Right, area.Top) };
            double u0 = pts.Min(p => p.X), u1 = pts.Max(p => p.X), v0 = pts.Min(p => p.Y), v1 = pts.Max(p => p.Y);
            // every pixel touched by the area (floor/ceil) is cleared
            int c0 = Math.Clamp((int)Math.Floor(u0 * width), 0, width), c1 = Math.Clamp((int)Math.Ceiling(u1 * width), 0, width);
            int r0 = Math.Clamp((int)Math.Floor((1 - v1) * height), 0, height), r1 = Math.Clamp((int)Math.Ceiling((1 - v0) * height), 0, height);
            for (int row = r0; row < r1; row++)
            {
                long rowStart = row * rowBytes;
                for (int col = c0; col < c1; col++)
                {
                    if (bpc == 1)
                    {
                        long bit = (long)col * components;
                        for (int k = 0; k < components; k++, bit++)
                        {
                            ref byte b = ref data[rowStart + bit / 8];
                            byte flag = (byte)(0x80 >> (int)(bit % 8));
                            b = (mask ? maskFill : (byte)0) != 0 ? (byte)(b | flag) : (byte)(b & ~flag);
                        }
                        continue;
                    }
                    int bytesPerSample = bpc / 8;
                    long offset = rowStart + (long)col * components * bytesPerSample;
                    for (int k = 0; k < components * bytesPerSample; k++)
                        data[offset + k] = cmyk && k / bytesPerSample == 3 ? (byte)0xFF : (byte)0;
                }
            }
        }
        var copy = new PdfStream(Array.Empty<byte>());
        foreach (var (key, value) in image)
            if (key != PdfName.Length && key != PdfName.Filter && key != PdfName.DecodeParms) copy[key] = value.Clone();
        copy.SetBytes(data, compress: true);
        return copy;
    }

    private static int ColorComponents(PdfObject? colorSpace)
    {
        switch (colorSpace?.Resolve())
        {
            case PdfName n:
                return n.Value switch { "DeviceGray" or "G" or "CalGray" => 1, "DeviceRGB" or "RGB" or "CalRGB" or "Lab" => 3, "DeviceCMYK" or "CMYK" => 4, _ => -1 };
            case PdfArray a when a.Count > 0 && a[0].Resolve() is PdfName family:
                return family.Value switch
                {
                    "ICCBased" => (a.Count > 1 ? (a[1].Resolve() as PdfStream)?.GetInt(PdfName.N, -1) : -1) ?? -1,
                    "Indexed" or "I" or "Separation" => 1,
                    "CalGray" => 1,
                    "CalRGB" or "Lab" => 3,
                    "DeviceN" => a.Count > 1 && a[1].Resolve() is PdfArray names ? names.Count : -1,
                    _ => -1,
                };
            default:
                return -1;
        }
    }

    // ------------------------------------------------------------------ geometry and output

    private enum Coverage { None, Partial, Full }

    private Coverage Covered(PdfRectangle box)
    {
        bool any = false;
        foreach (var a in _areas)
        {
            if (box.Left >= a.Left - 0.01 && box.Right <= a.Right + 0.01 && box.Bottom >= a.Bottom - 0.01 && box.Top <= a.Top + 0.01) return Coverage.Full;
            if (PdfRedactor.Intersects(a, box) || (box.Width == 0 || box.Height == 0) && box.Left <= a.Right && box.Right >= a.Left && box.Bottom <= a.Top && box.Top >= a.Bottom)
                any = true;
        }
        return any ? Coverage.Partial : Coverage.None;
    }

    private static PdfRectangle UnitSquare(Matrix ctm) =>
        Bounds(new[] { ctm.Transform(0, 0), ctm.Transform(1, 0), ctm.Transform(0, 1), ctm.Transform(1, 1) });

    private static PdfRectangle Bounds(IReadOnlyCollection<(double X, double Y)> points) =>
        new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));

    private static bool Invert(Matrix m, out Matrix inverse)
    {
        double det = m.A * m.D - m.B * m.C;
        if (Math.Abs(det) < 1e-12)
        {
            inverse = Matrix.Identity;
            return false;
        }
        inverse = new Matrix(m.D / det, -m.B / det, -m.C / det, m.A / det, (m.C * m.F - m.D * m.E) / det, (m.B * m.E - m.A * m.F) / det);
        return true;
    }

    private static double N(IReadOnlyList<PdfObject> operands, int index) =>
        index < operands.Count && operands[index] is PdfNumber n ? n.Value : 0;

    private void WriteOp(MemoryStream output, string op, PdfObject operand)
    {
        _serializer.Write(operand, new PdfOutput(output));
        output.Write(Encoding.ASCII.GetBytes(" " + op + "\n"));
    }

    private void WriteOperation(MemoryStream output, ContentOperation op)
    {
        if (op.Operator == "BI" && op.InlineImage is { } image)
        {
            output.Write("BI"u8);
            foreach (var (key, value) in image)
            {
                if (key == PdfName.Length) continue;
                output.WriteByte((byte)' ');
                _serializer.Write(key, new PdfOutput(output));
                output.WriteByte((byte)' ');
                _serializer.Write(value, new PdfOutput(output));
            }
            output.Write(" ID "u8);
            output.Write(image.RawBytes);
            output.Write("\nEI\n"u8);
            return;
        }
        foreach (var operand in op.Operands)
        {
            _serializer.Write(operand, new PdfOutput(output));
            output.WriteByte((byte)' ');
        }
        output.Write(Encoding.Latin1.GetBytes(op.Operator));
        output.WriteByte((byte)'\n');
    }
}
