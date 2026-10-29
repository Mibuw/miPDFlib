using System.Text;
using MiPdf.Content;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Text;

/// <summary>A word with its position on the page (user space, axis-aligned bounds).</summary>
public sealed record TextWord(string Text, PdfRectangle Bounds, double FontSize, string FontName);

/// <summary>A line of text.</summary>
public sealed record TextLineInfo(string Text, PdfRectangle Bounds, IReadOnlyList<TextWord> Words);

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
    private readonly Dictionary<PdfDictionary, FontDecoder> _decoders = new(ReferenceEqualityComparer.Instance);

    private sealed class State
    {
        public Matrix Ctm = Matrix.Identity;
        public PdfDictionary? Font;
        public double FontSize = 1, CharSpacing, WordSpacing, HorizontalScale = 1, Leading, Rise;
        public State Clone() => (State)MemberwiseClone();
    }

    public GlyphCollector(TextExtractionOptions options) => _options = options;

    public List<TextGlyph> Collect(PdfPage page)
    {
        page.Document.PrepareFontsForReading();
        var resources = page.GetInheritable(PdfName.Resources) as PdfDictionary;
        Process(page.GetContentBytes(), resources, new State(), 0);
        if (_options.IncludeAnnotations)
        {
            foreach (var annot in page.Annotations)
            {
                if (annot.Dictionary.GetDictionary(PdfName.AP)?.Get(PdfName.N) is not PdfStream ap) continue;
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
                        xobject.GetName(PdfName.Subtype) == PdfName.Form)
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

    private void Show(byte[] bytes, State gs, ref Matrix tm)
    {
        var decoder = GetDecoder(gs.Font);
        if (decoder == null) return;
        string fontName = gs.Font?.GetName(PdfName.BaseFont)?.Value ?? "";
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
                var pts = new[]
                {
                    (start.X - ax * 0.2, start.Y - ay * 0.2), (end.X - ax * 0.2, end.Y - ay * 0.2),
                    (start.X + ax * 0.8, start.Y + ay * 0.8), (end.X + ax * 0.8, end.Y + ay * 0.8),
                };
                _glyphs.Add(new TextGlyph
                {
                    Text = glyph.Text,
                    X0 = start.X, Y0 = start.Y, X1 = end.X, Y1 = end.Y,
                    DirX = dirX, DirY = dirY,
                    FontSize = size,
                    SpaceWidth = decoder.SpaceWidth * size * gs.HorizontalScale,
                    FontName = fontName.Contains('+') ? fontName[(fontName.IndexOf('+') + 1)..] : fontName,
                    Bounds = new PdfRectangle(pts.Min(p => p.Item1), pts.Min(p => p.Item2), pts.Max(p => p.Item1), pts.Max(p => p.Item2)),
                    Order = _glyphs.Count,
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
