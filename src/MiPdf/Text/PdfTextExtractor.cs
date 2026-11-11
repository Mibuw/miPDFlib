using System.Text;
using MiPdf.Content;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Text;

/// <summary>A word with its position on the page (user space, axis-aligned bounds).</summary>
public sealed record TextWord(string Text, PdfRectangle Bounds, double FontSize, string FontName);

/// <summary>A line of text.</summary>
public sealed record TextLineInfo(string Text, PdfRectangle Bounds, IReadOnlyList<TextWord> Words);

/// <summary>
/// A character on the page as it is drawn: one glyph (or several characters, for a ligature or a replacement text from
/// <c>/ActualText</c>) with its position, font, colors and rendering mode. Coordinates are user space of the page
/// (points, origin bottom-left).
/// </summary>
public sealed record TextLetter
{
    /// <summary>The character(s) the glyph stands for.</summary>
    public required string Text { get; init; }

    /// <summary>Axis-aligned bounds of the glyph cell: the advance along the baseline, 0.8 em above and 0.2 em below it.</summary>
    public PdfRectangle Bounds { get; init; }

    /// <summary>Where the glyph starts on the baseline.</summary>
    public (double X, double Y) BaselineStart { get; init; }

    /// <summary>Where the glyph ends on the baseline (start plus the advance, including character and word spacing).</summary>
    public (double X, double Y) BaselineEnd { get; init; }

    /// <summary>Direction of the baseline in degrees, counterclockwise from the x axis (0 for normal text, 90 for text running upwards).</summary>
    public double Angle { get; init; }

    /// <summary>The size the text appears in, in points (font size scaled by the text and transformation matrices).</summary>
    public double FontSize { get; init; }

    /// <summary>The font name without a subset prefix, e.g. <c>Helvetica-Bold</c>.</summary>
    public string FontName { get; init; } = "";

    /// <summary>True for a bold font (by its descriptor's weight or flags, or its name).</summary>
    public bool IsBold { get; init; }

    /// <summary>True for an italic or oblique font (by its descriptor's italic angle or flags, or its name).</summary>
    public bool IsItalic { get; init; }

    /// <summary>
    /// The fill color: gray, RGB and CMYK as in the file (ICC-based colors by their number of components), other color
    /// spaces converted to RGB; null for a pattern.
    /// </summary>
    public PdfColor? FillColor { get; init; }

    /// <summary>The stroke color (used by the rendering modes that stroke), like <see cref="FillColor"/>.</summary>
    public PdfColor? StrokeColor { get; init; }

    /// <summary>The text rendering mode (0 fill, 1 stroke, 2 fill and stroke, 3 invisible, 4–7 the same and add to the clip).</summary>
    public int RenderingMode { get; init; }

    /// <summary>False for invisible text (rendering mode 3 or 7), e.g. the text layer of a scanned page made by OCR.</summary>
    public bool IsVisible => RenderingMode is not (3 or 7);
}

/// <summary>Options for <see cref="PdfTextExtractor"/>.</summary>
public sealed class TextExtractionOptions
{
    /// <summary>Sort text by position (reading order) instead of content stream order. Default: true.</summary>
    public bool SortByPosition { get; set; } = true;

    /// <summary>Insert an empty line when the vertical gap between lines is large (paragraphs).</summary>
    public bool DetectParagraphs { get; set; }

    /// <summary>Include text of form XObjects (e.g. stamps, letterheads). Default: true.</summary>
    public bool IncludeXObjects { get; set; } = true;

    /// <summary>Include text in annotation appearances (e.g. filled form fields). Default: false.</summary>
    public bool IncludeAnnotations { get; set; }

    /// <summary>
    /// Include text of layers that are hidden when the document is opened (default true: all text, as in the file;
    /// false: only the text a viewer shows, see <see cref="PdfDocument.Layers"/>).
    /// </summary>
    public bool IncludeHiddenLayers { get; set; } = true;
}

/// <summary>Extracts text, words and their positions from pages.</summary>
public static class PdfTextExtractor
{
    /// <summary>Returns the text of a page; lines are separated by '\n'.</summary>
    public static string ExtractText(PdfPage page, TextExtractionOptions? options = null)
    {
        options ??= new TextExtractionOptions();
        var lines = ExtractLines(page, options);
        var sb = new StringBuilder();
        TextLineInfo? previous = null;
        foreach (var line in lines)
        {
            if (previous != null)
            {
                sb.Append('\n');
                double gap = previous.Bounds.Bottom - line.Bounds.Top;
                if (options.DetectParagraphs && gap > previous.Bounds.Height * 0.8) sb.Append('\n');
            }
            sb.Append(line.Text);
            previous = line;
        }
        return sb.ToString();
    }

    /// <summary>Returns the text of all pages separated by form feeds ('\f').</summary>
    public static string ExtractText(PdfDocument document, TextExtractionOptions? options = null) =>
        string.Join('\f', document.Pages.Select(p => ExtractText(p, options)));

    /// <summary>Returns the lines of a page with words and positions.</summary>
    public static IReadOnlyList<TextLineInfo> ExtractLines(PdfPage page, TextExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        using var diagnostics = DiagnosticContext.Enter(page);
        options ??= new TextExtractionOptions();
        try
        {
            var glyphs = new GlyphCollector(options).Collect(page);
            return LineBuilder.Build(glyphs, options.SortByPosition);
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex))
        {
            throw new PdfException("Text extraction failed (damaged content): " + ex.Message, ex);
        }
    }

    /// <summary>Returns all words of a page with their bounding boxes.</summary>
    public static IReadOnlyList<TextWord> ExtractWords(PdfPage page, TextExtractionOptions? options = null) =>
        ExtractLines(page, options).SelectMany(l => l.Words).ToList();

    /// <summary>
    /// Returns every character of a page with its position, font, colors and rendering mode, in the order of
    /// <see cref="ExtractLines"/> (reading order, or content order without <see cref="TextExtractionOptions.SortByPosition"/>).
    /// Glyphs drawn twice at the same place to look bold are returned once.
    /// </summary>
    public static IReadOnlyList<TextLetter> ExtractLetters(PdfPage page, TextExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        using var diagnostics = DiagnosticContext.Enter(page);
        options ??= new TextExtractionOptions();
        try
        {
            var glyphs = new GlyphCollector(options).Collect(page);
            return LineBuilder.BuildGlyphLines(glyphs, options.SortByPosition).SelectMany(line => line.Select(e => e.Glyph.ToLetter())).ToList();
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex))
        {
            throw new PdfException("Text extraction failed (damaged content): " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Finds all occurrences of <paramref name="search"/> on a page (within a line) and returns their bounding boxes,
    /// e.g. to place a signature or stamp at a placeholder text.
    /// </summary>
    public static IReadOnlyList<PdfRectangle> FindText(PdfPage page, string search, StringComparison comparison = StringComparison.Ordinal)
    {
        if (string.IsNullOrEmpty(search)) return new List<PdfRectangle>();
        return FindMatches(page, s =>
        {
            var hits = new List<(int, int)>();
            int index = 0;
            while ((index = s.IndexOf(search, index, comparison)) >= 0)
            {
                hits.Add((index, search.Length));
                index += search.Length;
            }
            return hits;
        });
    }

    /// <summary>Returns the bounds of all matches of <paramref name="pattern"/> (matched per text line), e.g. IBANs or e-mail addresses.</summary>
    public static IReadOnlyList<PdfRectangle> FindText(PdfPage page, System.Text.RegularExpressions.Regex pattern) =>
        FindMatches(page, s => pattern.Matches(s).Where(m => m.Length > 0).Select(m => (m.Index, m.Length)).ToList());

    private static IReadOnlyList<PdfRectangle> FindMatches(PdfPage page, Func<string, List<(int Index, int Length)>> matcher)
    {
        var result = new List<PdfRectangle>();
        var glyphs = new GlyphCollector(new TextExtractionOptions()).Collect(page);
        foreach (var line in LineBuilder.BuildGlyphLines(glyphs, sortByPosition: true))
        {
            var text = new StringBuilder();
            var owners = new List<TextGlyph?>();
            foreach (var (glyph, insertSpace) in line)
            {
                if (insertSpace)
                {
                    text.Append(' ');
                    owners.Add(null);
                }
                foreach (char c in glyph.Text)
                {
                    text.Append(c);
                    owners.Add(glyph);
                }
            }
            foreach (var (index, length) in matcher(text.ToString()))
            {
                var covered = owners.Skip(index).Take(length).OfType<TextGlyph>().ToList();
                if (covered.Count > 0) result.Add(Union(covered.Select(g => g.Bounds)));
            }
        }
        return result;
    }

    internal static PdfRectangle Union(IEnumerable<PdfRectangle> rects)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        foreach (var x in rects)
        {
            l = Math.Min(l, x.Left);
            b = Math.Min(b, x.Bottom);
            r = Math.Max(r, x.Right);
            t = Math.Max(t, x.Top);
        }
        return l == double.MaxValue ? default : new PdfRectangle(l, b, r, t);
    }
}

/// <summary>A positioned glyph in user space.</summary>
internal sealed class TextGlyph
{
    public required string Text;
    public double X0, Y0, X1, Y1;   // baseline start and end
    public double DirX, DirY;       // unit direction of the baseline
    public double FontSize;         // effective size in user space
    public double SpaceWidth;       // in user space
    public required string FontName;
    public PdfRectangle Bounds;
    public int Order;
    public bool Bold, Italic;
    public PdfColor? Fill, Stroke;
    public int RenderingMode;

    public TextLetter ToLetter() => new()
    {
        Text = Text,
        Bounds = Bounds,
        BaselineStart = (X0, Y0),
        BaselineEnd = (X1, Y1),
        Angle = Math.Round(Math.Atan2(DirY, DirX) * 180 / Math.PI, 2),
        FontSize = Math.Round(FontSize, 2),
        FontName = FontName,
        IsBold = Bold,
        IsItalic = Italic,
        FillColor = Fill,
        StrokeColor = Stroke,
        RenderingMode = RenderingMode,
    };

    public double Perpendicular => DirX * Y0 - DirY * X0;

    public double ParallelStart => DirX * X0 + DirY * Y0;

    public double ParallelEnd => DirX * X1 + DirY * Y1;

    public int Orientation => (int)Math.Round(Math.Atan2(DirY, DirX) * 180 / Math.PI);
}

/// <summary>Executes content streams and records glyph positions.</summary>
internal sealed class GlyphCollector
{
    private readonly TextExtractionOptions _options;
    private readonly List<TextGlyph> _glyphs = new();
    private PdfDocument? _document;
    // nesting depth of marked content of hidden layers; glyphs inside are positioned but not collected
    private int _hidden;
    private readonly Dictionary<PdfDictionary, FontDecoder> _decoders = new(ReferenceEqualityComparer.Instance);

    private sealed class State
    {
        public Matrix Ctm = Matrix.Identity;
        public PdfDictionary? Font;
        public double FontSize = 1, CharSpacing, WordSpacing, HorizontalScale = 1, Leading, Rise;
        public int RenderingMode;
        public ColorSpace FillSpace = ColorSpace.Gray, StrokeSpace = ColorSpace.Gray;
        public PdfColor? Fill = PdfColor.Black, Stroke = PdfColor.Black;
        public State Clone() => (State)MemberwiseClone();
    }

    /// <summary>
    /// A color as <see cref="TextLetter"/> reports it: device colors (and ICC-based colors by their number of
    /// components) as they are, other spaces converted to RGB, patterns null.
    /// </summary>
    private static PdfColor? ToColor(ColorSpace space, IReadOnlyList<PdfObject> operands)
    {
        // a damaged color space or tint function costs the color, not the text
        try
        {
            return Convert(space, operands);
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex) || ex is PdfException)
        {
            return null;
        }
    }

    private static ColorSpace ResolveSpace(PdfObject operand, PdfDictionary? resources)
    {
        try
        {
            return ColorSpace.Resolve(operand, resources);
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex) || ex is PdfException)
        {
            return ColorSpace.Gray;
        }
    }

    private static PdfColor? Convert(ColorSpace space, IReadOnlyList<PdfObject> operands)
    {
        if (space.IsPattern) return null;
        var c = new double[space.Components];
        for (int i = 0; i < c.Length && i < operands.Count; i++) c[i] = operands[i] is PdfNumber n ? n.Value : 0;
        if (ReferenceEquals(space, ColorSpace.Gray)) return PdfColor.Gray(Unit(c[0]));
        if (ReferenceEquals(space, ColorSpace.Rgb)) return PdfColor.Rgb(Unit(c[0]), Unit(c[1]), Unit(c[2]));
        if (ReferenceEquals(space, ColorSpace.Cmyk)) return PdfColor.Cmyk(Unit(c[0]), Unit(c[1]), Unit(c[2]), Unit(c[3]));
        var rgb = space.ToRgb(c);
        return PdfColor.FromRgb(rgb.R, rgb.G, rgb.B);

        // rounded to the precision of PDF numbers (0.3 is read as 0.30000000000000004)
        static double Unit(double v) => double.IsFinite(v) ? Math.Round(Math.Clamp(v, 0, 1), 5) : 0;
    }

    /// <summary>The initial color of a color space (black for device spaces), as <see cref="ToColor"/> reports it.</summary>
    private static PdfColor? InitialColor(ColorSpace space) =>
        ToColor(space, space.Initial.Select(v => (PdfObject)new PdfNumber(v)).ToList());

    private readonly Dictionary<PdfDictionary, (bool Bold, bool Italic)> _styles = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Bold and italic from the font descriptor (weight 600 or more or the ForceBold flag; an italic angle or the
    /// Italic flag), for a composite font that of its descendant, and from the font name.
    /// </summary>
    private (bool Bold, bool Italic) Style(PdfDictionary? font)
    {
        if (font == null) return (false, false);
        if (_styles.TryGetValue(font, out var style)) return style;
        var descriptor = font.GetDictionary(PdfName.Get("FontDescriptor"))
            ?? (font.GetArray(PdfName.Get("DescendantFonts"))?.Count > 0 ? (font.GetArray(PdfName.Get("DescendantFonts"))![0].Resolve() as PdfDictionary)?.GetDictionary(PdfName.Get("FontDescriptor")) : null);
        string name = font.GetName(PdfName.BaseFont)?.Value ?? "";
        int flags = (int)(descriptor?.GetDouble(PdfName.Get("Flags")) ?? 0);
        double weight = descriptor?.GetDouble(PdfName.Get("FontWeight")) ?? 0;
        double angle = descriptor?.GetDouble(PdfName.Get("ItalicAngle")) ?? 0;
        bool bold = weight >= 600 || (flags & 0x40000) != 0
            || name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || name.Contains("Semibold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Demi", StringComparison.OrdinalIgnoreCase);
        bool italic = Math.Abs(angle) > 0.5 || (flags & 0x40) != 0
            || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        return _styles[font] = (bold, italic);
    }

    public GlyphCollector(TextExtractionOptions options) => _options = options;

    public List<TextGlyph> Collect(PdfPage page)
    {
        page.Document.PrepareFontsForReading();
        _document = page.Document;
        var resources = page.GetInheritable(PdfName.Resources) as PdfDictionary;
        Process(page.GetContentBytes(), resources, new State(), 0);
        if (_options.IncludeAnnotations)
        {
            foreach (var annot in page.Annotations)
            {
                if (annot.Dictionary.GetDictionary(PdfName.AP)?.Get(PdfName.N) is not PdfStream ap || IsHidden(annot.Dictionary)) continue;
                var rect = annot.Rect;
                var bbox = ap.GetRectangle(PdfName.BBox) ?? rect;
                var state = new State
                {
                    Ctm = Matrix.Translate(rect.Left - bbox.Left, rect.Bottom - bbox.Bottom),
                };
                Process(ap.GetBytes(), ap.GetDictionary(PdfName.Resources) ?? resources, state, 1);
            }
        }
        return _glyphs;
    }

    private void Process(byte[] content, PdfDictionary? resources, State initial, int depth)
    {
        if (depth > 16) return;
        var stack = new Stack<State>();
        // per marked-content sequence: does it hide its content, and its replacement text (/ActualText) with the
        // number of glyphs collected before it
        var marked = new Stack<(bool Hides, string? ActualText, int Start)>();
        var gs = initial;
        Matrix tm = Matrix.Identity, tlm = Matrix.Identity;
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
                case "BMC":
                    marked.Push((false, null, _glyphs.Count));
                    break;
                case "BDC":
                {
                    bool hides = !_options.IncludeHiddenLayers && o.Count >= 2 && o[0] is PdfName { Value: "OC" } && o[1] is PdfName property
                        && _document != null && _document.IsHiddenContent(resources?.GetDictionary(PdfName.Get("Properties"))?[property]);
                    var properties = o.Count >= 2 ? o[1] switch
                    {
                        PdfDictionary inline => inline,
                        PdfName named => resources?.GetDictionary(PdfName.Get("Properties"))?.GetDictionary(named),
                        _ => null,
                    } : null;
                    // the outermost replacement text wins
                    string? actual = marked.Any(m => m.ActualText != null) ? null : properties?.GetText(PdfName.ActualText);
                    marked.Push((hides, actual, _glyphs.Count));
                    if (hides) _hidden++;
                    break;
                }
                case "EMC":
                    if (marked.Count > 0)
                    {
                        var (hides, actual, start) = marked.Pop();
                        if (hides) _hidden--;
                        if (actual != null) ReplaceWithActualText(start, actual);
                    }
                    break;
                case "Tf":
                    if (o.Count >= 2)
                    {
                        gs.Font = o[0] is PdfName fn ? resources?.GetDictionary(PdfName.Font)?.GetDictionary(fn) : null;
                        gs.FontSize = N(o, 1);
                    }
                    break;
                case "Tr": gs.RenderingMode = Math.Clamp((int)N(o, 0), 0, 7); break;
                // colors (only for TextLetter)
                case "g":
                    gs.FillSpace = ColorSpace.Gray;
                    gs.Fill = ToColor(gs.FillSpace, o);
                    break;
                case "G":
                    gs.StrokeSpace = ColorSpace.Gray;
                    gs.Stroke = ToColor(gs.StrokeSpace, o);
                    break;
                case "rg":
                    gs.FillSpace = ColorSpace.Rgb;
                    gs.Fill = ToColor(gs.FillSpace, o);
                    break;
                case "RG":
                    gs.StrokeSpace = ColorSpace.Rgb;
                    gs.Stroke = ToColor(gs.StrokeSpace, o);
                    break;
                case "k":
                    gs.FillSpace = ColorSpace.Cmyk;
                    gs.Fill = ToColor(gs.FillSpace, o);
                    break;
                case "K":
                    gs.StrokeSpace = ColorSpace.Cmyk;
                    gs.Stroke = ToColor(gs.StrokeSpace, o);
                    break;
                case "cs":
                    if (o.Count >= 1)
                    {
                        gs.FillSpace = ResolveSpace(o[0], resources);
                        gs.Fill = InitialColor(gs.FillSpace);
                    }
                    break;
                case "CS":
                    if (o.Count >= 1)
                    {
                        gs.StrokeSpace = ResolveSpace(o[0], resources);
                        gs.Stroke = InitialColor(gs.StrokeSpace);
                    }
                    break;
                case "sc" or "scn":
                    gs.Fill = ToColor(gs.FillSpace, o);
                    break;
                case "SC" or "SCN":
                    gs.Stroke = ToColor(gs.StrokeSpace, o);
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
                    if (o.Count >= 1 && o[0] is PdfString s) Show(s.Bytes, gs, ref tm);
                    break;
                case "'":
                    tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                    tm = tlm;
                    if (o.Count >= 1 && o[^1] is PdfString s1) Show(s1.Bytes, gs, ref tm);
                    break;
                case "\"":
                    if (o.Count >= 3)
                    {
                        gs.WordSpacing = N(o, 0);
                        gs.CharSpacing = N(o, 1);
                    }
                    tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                    tm = tlm;
                    if (o.Count >= 1 && o[^1] is PdfString s2) Show(s2.Bytes, gs, ref tm);
                    break;
                case "TJ":
                    if (o.Count >= 1 && o[0] is PdfArray array)
                    {
                        foreach (var item in array)
                        {
                            if (item is PdfString part) Show(part.Bytes, gs, ref tm);
                            else if (item is PdfNumber adjust)
                                tm = Matrix.Translate(-adjust.Value / 1000 * gs.FontSize * gs.HorizontalScale, 0).Multiply(tm);
                        }
                    }
                    break;
                case "Do":
                    if (!_options.IncludeXObjects || o.Count < 1 || o[0] is not PdfName name) break;
                    if (resources?.GetDictionary(PdfName.XObject)?.GetStream(name) is { } xobject &&
                        xobject.GetName(PdfName.Subtype) == PdfName.Form && !IsHidden(xobject))
                    {
                        var inner = gs.Clone();
                        if (xobject.GetArray(PdfName.Get("Matrix")) is { Count: 6 } m)
                            inner.Ctm = new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5)).Multiply(gs.Ctm);
                        Process(xobject.GetBytes(), xobject.GetDictionary(PdfName.Resources) ?? resources, inner, depth + 1);
                    }
                    break;
            }
        }
    }

    private static double N(IReadOnlyList<PdfObject> operands, int index) =>
        index < operands.Count && operands[index] is PdfNumber n ? n.Value : 0;

    /// <summary>
    /// Replaces the glyphs collected since <paramref name="start"/> by one glyph with the replacement text of the
    /// marked content (ISO 32000-2, 14.9.4), e.g. right-to-left text in logical order or ligatures.
    /// </summary>
    private void ReplaceWithActualText(int start, string text)
    {
        if (start >= _glyphs.Count) return; // nothing shown (or hidden)
        var parts = _glyphs.GetRange(start, _glyphs.Count - start);
        _glyphs.RemoveRange(start, parts.Count);
        var first = parts[0];
        var low = parts.MinBy(g => Math.Min(g.ParallelStart, g.ParallelEnd))!;
        var high = parts.MaxBy(g => Math.Max(g.ParallelStart, g.ParallelEnd))!;
        bool lowStarts = low.ParallelStart <= low.ParallelEnd, highEnds = high.ParallelEnd >= high.ParallelStart;
        _glyphs.Add(new TextGlyph
        {
            Text = text,
            X0 = lowStarts ? low.X0 : low.X1, Y0 = lowStarts ? low.Y0 : low.Y1,
            X1 = highEnds ? high.X1 : high.X0, Y1 = highEnds ? high.Y1 : high.Y0,
            DirX = first.DirX, DirY = first.DirY,
            FontSize = first.FontSize,
            SpaceWidth = first.SpaceWidth,
            FontName = first.FontName,
            Bounds = new PdfRectangle(parts.Min(g => g.Bounds.Left), parts.Min(g => g.Bounds.Bottom), parts.Max(g => g.Bounds.Right), parts.Max(g => g.Bounds.Top)),
            Order = start,
            Bold = first.Bold,
            Italic = first.Italic,
            Fill = first.Fill,
            Stroke = first.Stroke,
            RenderingMode = first.RenderingMode,
        });
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

    /// <summary>True if hidden text is excluded and the object (form XObject, annotation) belongs to a hidden layer.</summary>
    private bool IsHidden(PdfDictionary dict) =>
        !_options.IncludeHiddenLayers && _document != null && _document.IsHiddenContent(dict[PdfName.Get("OC")]);

    private void Show(byte[] bytes, State gs, ref Matrix tm)
    {
        var decoder = GetDecoder(gs.Font);
        if (decoder == null) return;
        string fontName = gs.Font?.GetName(PdfName.BaseFont)?.Value ?? "";
        if (fontName.Contains('+')) fontName = fontName[(fontName.IndexOf('+') + 1)..]; // subset prefix
        var (bold, italic) = Style(gs.Font);
        foreach (var glyph in decoder.Decode(bytes))
        {
            double tx = (glyph.Width * gs.FontSize + gs.CharSpacing + (glyph.IsSingleByteSpace ? gs.WordSpacing : 0)) * gs.HorizontalScale;
            var trm = new Matrix(gs.FontSize * gs.HorizontalScale, 0, 0, gs.FontSize, 0, gs.Rise).Multiply(tm).Multiply(gs.Ctm);
            var start = trm.Transform(0, 0);
            var endMatrix = Matrix.Translate(tx, 0).Multiply(tm).Multiply(gs.Ctm);
            var end = endMatrix.Transform(0, gs.Rise);
            var up = trm.Transform(0, 1);
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double dirX, dirY;
            if (len > 1e-6)
            {
                dirX = dx / len;
                dirY = dy / len;
            }
            else
            {
                var unit = trm.Transform(1, 0);
                double ul = Math.Max(1e-9, Math.Sqrt((unit.X - start.X) * (unit.X - start.X) + (unit.Y - start.Y) * (unit.Y - start.Y)));
                dirX = (unit.X - start.X) / ul;
                dirY = (unit.Y - start.Y) / ul;
            }
            double size = Math.Sqrt((up.X - start.X) * (up.X - start.X) + (up.Y - start.Y) * (up.Y - start.Y));
            if (glyph.Text.Length > 0)
            {
                // bounding box: baseline from start to end, ascent 0.8 em, descent 0.2 em
                double ax = -dirY * size, ay = dirX * size;
                double x0 = start.X - ax * 0.2, x1 = end.X - ax * 0.2, x2 = start.X + ax * 0.8, x3 = end.X + ax * 0.8;
                double y0 = start.Y - ay * 0.2, y1 = end.Y - ay * 0.2, y2 = start.Y + ay * 0.8, y3 = end.Y + ay * 0.8;
                if (_hidden == 0)
                    _glyphs.Add(new TextGlyph
                    {
                        Text = glyph.Text,
                        X0 = start.X, Y0 = start.Y, X1 = end.X, Y1 = end.Y,
                        DirX = dirX, DirY = dirY,
                        FontSize = size,
                        SpaceWidth = decoder.SpaceWidth * size * gs.HorizontalScale,
                        FontName = fontName,
                        Bounds = new PdfRectangle(Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)), Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)),
                            Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)), Math.Max(Math.Max(y0, y1), Math.Max(y2, y3))),
                        Order = _glyphs.Count,
                        Bold = bold,
                        Italic = italic,
                        Fill = gs.Fill,
                        Stroke = gs.Stroke,
                        RenderingMode = gs.RenderingMode,
                    });
            }
            tm = Matrix.Translate(tx, 0).Multiply(tm);
        }
    }
}

/// <summary>Groups glyphs into lines and words.</summary>
internal static class LineBuilder
{
    /// <summary>Returns lines as glyph sequences; the flag marks glyphs that need a space before them.</summary>
    public static List<List<(TextGlyph Glyph, bool SpaceBefore)>> BuildGlyphLines(List<TextGlyph> glyphs, bool sortByPosition)
    {
        var result = new List<List<(TextGlyph, bool)>>();
        if (glyphs.Count == 0) return result;
        List<List<TextGlyph>> lines;
        if (sortByPosition)
        {
            lines = new List<List<TextGlyph>>();
            foreach (var group in glyphs.GroupBy(g => g.Orientation).OrderBy(g => Math.Abs(g.Key)))
            {
                var sorted = group.OrderByDescending(g => g.Perpendicular).ThenBy(g => g.ParallelStart).ToList();
                List<TextGlyph>? current = null;
                double linePerp = 0;
                foreach (var g in sorted)
                {
                    double tolerance = Math.Max(1, g.FontSize * 0.4);
                    if (current == null || Math.Abs(linePerp - g.Perpendicular) > tolerance)
                    {
                        current = new List<TextGlyph>();
                        lines.Add(current);
                        linePerp = g.Perpendicular;
                    }
                    current.Add(g);
                }
            }
            foreach (var line in lines) line.Sort((a, b) => a.ParallelStart.CompareTo(b.ParallelStart));
        }
        else
        {
            // content order; a new line starts when the baseline changes noticeably or text goes backwards
            lines = new List<List<TextGlyph>>();
            List<TextGlyph>? current = null;
            foreach (var g in glyphs.OrderBy(g => g.Order))
            {
                var last = current?[^1];
                bool newLine = last == null || last.Orientation != g.Orientation ||
                               Math.Abs(last.Perpendicular - g.Perpendicular) > Math.Max(1, g.FontSize * 0.4) ||
                               g.ParallelStart < last.ParallelStart - g.FontSize;
                if (newLine)
                {
                    current = new List<TextGlyph>();
                    lines.Add(current);
                }
                current!.Add(g);
            }
        }

        foreach (var line in lines)
        {
            var entries = new List<(TextGlyph, bool)>();
            TextGlyph? previous = null;
            foreach (var g in line)
            {
                bool space = false;
                if (previous != null)
                {
                    double gap = g.ParallelStart - previous.ParallelEnd;
                    double threshold = Math.Max(previous.SpaceWidth, g.SpaceWidth) * 0.5;
                    if (threshold <= 0) threshold = g.FontSize * 0.15;
                    threshold = Math.Min(threshold, g.FontSize * 0.2);
                    bool hasSpace = previous.Text.EndsWith(' ') || g.Text.StartsWith(' ');
                    space = !hasSpace && gap > threshold;
                    // overlapping duplicates (fake bold): skip identical glyph at the same position
                    if (g.Text == previous.Text && Math.Abs(gap + (previous.ParallelEnd - previous.ParallelStart)) < g.FontSize * 0.05)
                        continue;
                }
                entries.Add((g, space));
                previous = g;
            }
            if (entries.Count > 0) result.Add(entries);
        }
        return result;
    }

    public static IReadOnlyList<TextLineInfo> Build(List<TextGlyph> glyphs, bool sortByPosition)
    {
        var lines = new List<TextLineInfo>();
        foreach (var line in BuildGlyphLines(glyphs, sortByPosition))
        {
            var words = new List<TextWord>();
            var wordText = new StringBuilder();
            var wordGlyphs = new List<TextGlyph>();
            var lineText = new StringBuilder();

            void FlushWord()
            {
                if (wordText.Length == 0) return;
                var first = wordGlyphs[0];
                words.Add(new TextWord(wordText.ToString(), PdfTextExtractor.Union(wordGlyphs.Select(g => g.Bounds)), Math.Round(first.FontSize, 2), first.FontName));
                wordText.Clear();
                wordGlyphs.Clear();
            }

            foreach (var (g, spaceBefore) in line)
            {
                if (spaceBefore)
                {
                    lineText.Append(' ');
                    FlushWord();
                }
                lineText.Append(g.Text);
                foreach (char c in g.Text)
                {
                    if (char.IsWhiteSpace(c)) FlushWord();
                    else
                    {
                        wordText.Append(c);
                        if (wordGlyphs.Count == 0 || !ReferenceEquals(wordGlyphs[^1], g)) wordGlyphs.Add(g);
                    }
                }
            }
            FlushWord();
            var text = lineText.ToString().TrimEnd();
            if (text.Length == 0) continue;
            lines.Add(new TextLineInfo(text, PdfTextExtractor.Union(line.Select(e => e.Glyph.Bounds)), words));
        }
        return lines;
    }
}
