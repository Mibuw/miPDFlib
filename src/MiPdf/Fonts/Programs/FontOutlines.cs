using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.Text;

namespace MiPdf.Fonts.Programs;

/// <summary>
/// The glyph outlines of a font: those of its embedded font program (TrueType, CFF, Type 1) or, for rendering, of a
/// system font as substitute, selected per shown code as ISO 32000-2, 9.6.5 and 9.7.4 describe. Type 3 glyphs are
/// content streams (their procedures), not outlines.
/// </summary>
internal sealed class FontOutlines
{
    private readonly Dictionary<int, PathGeometry?> _glyphs = new();
    private readonly Func<DecodedGlyph, PathGeometry?> _outline;

    private FontOutlines(FontDecoder decoder, Matrix fontMatrix, Func<DecodedGlyph, PathGeometry?> outline, PdfDictionary? type3 = null)
    {
        Decoder = decoder;
        FontMatrix = fontMatrix;
        _outline = outline;
        Type3 = type3;
    }

    public FontDecoder Decoder { get; }

    /// <summary>Glyph space to text space.</summary>
    public Matrix FontMatrix { get; }

    /// <summary>The font dictionary of a Type 3 font (glyphs are content streams), otherwise null.</summary>
    public PdfDictionary? Type3 { get; }

    /// <summary>The names of the codes of simple fonts (encoding and differences); used for Type 3 fonts.</summary>
    public string?[] CodeNames { get; private init; } = new string?[256];

    /// <summary>
    /// The advance of a glyph in text space from the font program, for fonts whose PDF widths are missing (a simple
    /// font without /Widths); null to use the PDF widths.
    /// </summary>
    public Func<DecodedGlyph, double>? Advance { get; private init; }

    /// <summary>The outline of a shown glyph in glyph space, or null (nothing to draw).</summary>
    public PathGeometry? GetGlyph(DecodedGlyph glyph)
    {
        int key = glyph.Cid >= 0 ? glyph.Cid : glyph.Code;
        if (_glyphs.TryGetValue(key, out var path)) return path;
        try
        {
            path = _outline(glyph);
        }
        catch (Exception ex) when (ex is PdfException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            path = null;
        }
        if (_glyphs.Count < 65536) _glyphs[key] = path;
        return path;
    }

    // ------------------------------------------------------------------ glyph boxes

    private readonly Dictionary<int, PdfRectangle?> _boxes = new();

    // the boxes of a standard font that is not embedded (from its font metrics), in text space
    private Func<DecodedGlyph, PdfRectangle?>? _metricBox;

    /// <summary>
    /// The box a shown glyph covers, in text space (1 = the font size, before the text matrix): the bounds of its
    /// outline, of the standard font's metrics for a standard font that is not embedded, or of the glyph procedure's
    /// d1 operator for a Type 3 font. Null if the glyph draws nothing or its shape is not known.
    /// </summary>
    public PdfRectangle? GetGlyphBox(DecodedGlyph glyph)
    {
        int key = glyph.Cid >= 0 ? glyph.Cid : glyph.Code;
        if (_boxes.TryGetValue(key, out var box)) return box;
        try
        {
            box = Type3 != null ? Type3Box(glyph)
                : _metricBox != null ? _metricBox(glyph)
                : GetGlyph(glyph)?.Bounds() is { } bounds ? Transform(bounds, FontMatrix) : null;
        }
        catch (Exception ex) when (ex is PdfException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidCastException)
        {
            box = null;
        }
        if (box is { } b && (b.Width <= 0 && b.Height <= 0 || !double.IsFinite(b.Left + b.Right + b.Bottom + b.Top))) box = null;
        if (_boxes.Count < 65536) _boxes[key] = box;
        return box;
    }

    /// <summary>
    /// The box of a Type 3 glyph: the bounds its procedure declares with d1, or, for a colored glyph (d0), the bounds
    /// of the paths it paints.
    /// </summary>
    private PdfRectangle? Type3Box(DecodedGlyph glyph)
    {
        if (glyph.Code is < 0 or > 255 || CodeNames[glyph.Code] is not { } name) return null;
        if (Type3?.GetDictionary(PdfName.Get("CharProcs"))?.GetStream(PdfName.Get(name)) is not { } procedure) return null;
        var content = procedure.GetBytes();
        var first = Content.ContentParser.Parse(content).FirstOrDefault();
        if (first?.Operator == "d1" && first.Operands.Count >= 6 && first.Operands.All(o => o is PdfNumber))
        {
            double N(int i) => ((PdfNumber)first.Operands[i]).Value;
            var r = new PdfRectangle(Math.Min(N(2), N(4)), Math.Min(N(3), N(5)), Math.Max(N(2), N(4)), Math.Max(N(3), N(5)));
            return Transform(r, FontMatrix);
        }
        var resources = procedure.GetDictionary(PdfName.Resources) ?? Type3!.GetDictionary(PdfName.Resources);
        return Content.PathCollector.PaintedBounds(content, resources) is { } painted ? Transform(painted, FontMatrix) : null;
    }

    private static PdfRectangle Transform(PdfRectangle r, Matrix m)
    {
        var a = m.Transform(r.Left, r.Bottom);
        var b = m.Transform(r.Right, r.Bottom);
        var c = m.Transform(r.Right, r.Top);
        var d = m.Transform(r.Left, r.Top);
        return new PdfRectangle(Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
            Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)), Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)));
    }

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// Loads the outlines of a font. <paramref name="substitute"/> finds a replacement for a font without (usable)
    /// embedded program (by base font name and descriptor); without it such a font has no outlines.
    /// </summary>
    public static FontOutlines Load(PdfDictionary font, Func<string, PdfDictionary?, SfntFont?>? substitute = null)
    {
        var decoder = new FontDecoder(font);
        string subtype = font.GetName(PdfName.Subtype)?.Value ?? "";
        if (subtype == "Type3")
        {
            var matrix = font.GetArray(PdfName.Get("FontMatrix")) is { Count: 6 } m
                ? new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5))
                : new Matrix(0.001, 0, 0, 0.001, 0, 0);
            return new FontOutlines(decoder, matrix, _ => null, font) { CodeNames = CodeNamesOf(font, null) };
        }
        return subtype == "Type0" ? LoadComposite(font, decoder, substitute) : LoadSimple(font, decoder, subtype, substitute);
    }

    private static readonly Matrix Thousandth = new(0.001, 0, 0, 0.001, 0, 0);

    private static Matrix UnitsMatrix(SfntFont sfnt) => sfnt.Cff?.FontMatrix ?? new Matrix(1.0 / sfnt.UnitsPerEm, 0, 0, 1.0 / sfnt.UnitsPerEm, 0, 0);

    private static FontOutlines LoadComposite(PdfDictionary font, FontDecoder decoder, Func<string, PdfDictionary?, SfntFont?>? find)
    {
        var descendant = font.GetArray(PdfName.DescendantFonts)?.GetDictionary(0);
        var descriptor = descendant?.GetDictionary(PdfName.FontDescriptor);
        var program = LoadProgram(descriptor);
        switch (program)
        {
            case SfntFont sfnt:
            {
                int[]? cidToGid = ReadCidToGid(descendant);
                return new FontOutlines(decoder, UnitsMatrix(sfnt), g =>
                {
                    // a CIDToGIDMap applies also when the program has CFF outlines (an OpenType font as FontFile2)
                    int gid = cidToGid != null ? (g.Cid >= 0 && g.Cid < cidToGid.Length ? cidToGid[g.Cid] : 0)
                        : sfnt.Cff != null ? sfnt.Cff.GlyphByCid(g.Cid) : g.Cid;
                    return gid > 0 ? sfnt.GetOutline(gid) : null;
                });
            }
            case CffFont cff:
                return new FontOutlines(decoder, cff.FontMatrix, g => cff.GlyphByCid(g.Cid) is > 0 and var gid ? cff.GetOutline(gid) : null);
        }
        // not embedded: a system font, glyphs by Unicode
        var substitute = Substitute(font, font.GetName(PdfName.BaseFont)?.Value ?? descendant?.GetName(PdfName.BaseFont)?.Value ?? "", descriptor, program, find);
        return ByUnicode(decoder, substitute);
    }

    private static FontOutlines LoadSimple(PdfDictionary font, FontDecoder decoder, string subtype, Func<string, PdfDictionary?, SfntFont?>? find)
    {
        var descriptor = font.GetDictionary(PdfName.FontDescriptor);
        var program = LoadProgram(descriptor);
        bool symbolic = ((descriptor?.GetInt(PdfName.Get("Flags")) ?? 0) & 4) != 0;
        switch (program)
        {
            case Type1Font type1:
            {
                var names = CodeNamesOf(font, type1.NameForCode);
                return new FontOutlines(decoder, type1.FontMatrix, g =>
                {
                    string? name = g.Code is >= 0 and < 256 ? names[g.Code] : null;
                    return name != null && type1.HasGlyph(name) ? type1.GetOutline(name)
                        : type1.NameForCode(g.Code) is { } builtIn ? type1.GetOutline(builtIn) : null;
                }) { CodeNames = names };
            }
            case CffFont cff:
            {
                var names = CodeNamesOf(font, null);
                return new FontOutlines(decoder, cff.FontMatrix, g =>
                {
                    string? name = g.Code is >= 0 and < 256 ? names[g.Code] : null;
                    int gid = name != null ? cff.GlyphByName(name) : -1;
                    if (gid < 0) gid = cff.GlyphForCode(g.Code);
                    return gid > 0 ? cff.GetOutline(gid) : null;
                }) { CodeNames = names };
            }
            case SfntFont sfnt when sfnt.Cff != null:
            {
                var names = CodeNamesOf(font, null);
                return new FontOutlines(decoder, sfnt.Cff.FontMatrix, g =>
                {
                    string? name = g.Code is >= 0 and < 256 ? names[g.Code] : null;
                    int gid = name != null ? sfnt.Cff.GlyphByName(name) : -1;
                    if (gid < 0) gid = sfnt.Cff.GlyphForCode(g.Code);
                    if (gid < 0) gid = TrueTypeGlyph(sfnt, g.Code, name, symbolic);
                    return gid > 0 ? sfnt.Cff.GetOutline(gid) : null;
                }) { CodeNames = names };
            }
            case SfntFont sfnt:
            {
                var names = CodeNamesOf(font, null, defaultEncoding: subtype == "TrueType" && !symbolic ? "WinAnsiEncoding" : null);
                return new FontOutlines(decoder, UnitsMatrix(sfnt), g =>
                {
                    string? name = g.Code is >= 0 and < 256 ? names[g.Code] : null;
                    int gid = TrueTypeGlyph(sfnt, g.Code, name, symbolic && font.Get(PdfName.Encoding) == null);
                    return gid > 0 ? sfnt.GetOutline(gid) : null;
                }) { CodeNames = names };
            }
        }
        // not embedded (standard 14 fonts and others): a system font, glyphs by Unicode
        string baseFont = font.GetName(PdfName.BaseFont)?.Value ?? "";
        var substitute = Substitute(font, baseFont, descriptor, program, find);
        var outlines = ByUnicode(decoder, substitute, CodeNamesOf(font, null));
        // the boxes of a standard font from its metrics: glyph names by its built-in encoding, the PDF encoding and differences
        if (program == null && StandardFontMetrics.GetGlyphBoxes(baseFont) is { } metrics)
        {
            var names = CodeNamesOf(font, code => metrics.BuiltIn[code]);
            outlines._metricBox = g => g.Code is >= 0 and < 256 && names[g.Code] is { } name && metrics.ByName.TryGetValue(name, out var v)
                && (v.Right > v.Left || v.Top > v.Bottom)
                ? new PdfRectangle(v.Left / 1000.0, v.Bottom / 1000.0, v.Right / 1000.0, v.Top / 1000.0) : null;
        }
        return outlines;
    }

    /// <summary>The installed substitute for a font without (usable) embedded program, reported as a diagnostic.</summary>
    private static SfntFont? Substitute(PdfDictionary font, string baseFont, PdfDictionary? descriptor, object? program, Func<string, PdfDictionary?, SfntFont?>? find)
    {
        if (find == null) return null;
        var substitute = find(baseFont, descriptor);
        string name = baseFont.Length > 0 ? baseFont : "(unnamed)";
        if (substitute == null)
            DiagnosticContext.Warning(PdfDiagnosticCode.FontMissing, $"Font {name} is not embedded and no substitute is installed; its text is not drawn.", font);
        else if (program == null && descriptor?.GetStream(PdfName.FontFile2) == null && descriptor?.GetStream(PdfName.Get("FontFile3")) == null && descriptor?.GetStream(PdfName.Get("FontFile")) == null)
            DiagnosticContext.Information(PdfDiagnosticCode.FontSubstituted, $"Font {name} is not embedded; it is drawn with {substitute.FileName}.", font);
        else
            DiagnosticContext.Warning(PdfDiagnosticCode.FontDamaged, $"The embedded program of font {name} cannot be read; it is drawn with {substitute.FileName}.", font);
        return substitute;
    }

    /// <summary>The glyph of a simple TrueType font (ISO 32000-2, 9.6.5.4).</summary>
    private static int TrueTypeGlyph(SfntFont sfnt, int code, string? name, bool symbolic)
    {
        int gid = -1;
        if (!symbolic && name != null && sfnt.HasCmap(3, 1) && GlyphNames.ToUnicode(name) is { Length: 1 } unicode)
            gid = sfnt.MapCode(3, 1, unicode[0]);
        if (gid <= 0 && sfnt.HasCmap(3, 0))
        {
            gid = sfnt.MapCode(3, 0, code);
            if (gid <= 0) gid = sfnt.MapCode(3, 0, 0xF000 + code);
            if (gid <= 0) gid = sfnt.MapCode(3, 0, 0xF100 + code);
            if (gid <= 0) gid = sfnt.MapCode(3, 0, 0xF200 + code);
        }
        if (gid <= 0 && sfnt.HasCmap(1, 0)) gid = sfnt.MapCode(1, 0, code);
        if (gid <= 0 && name != null) gid = sfnt.GlyphByName(name);
        if (gid <= 0 && sfnt.HasCmap(3, 1) && name == null) gid = sfnt.MapCode(3, 1, code);
        if (gid < 0 && !sfnt.HasCmap(3, 1) && !sfnt.HasCmap(3, 0) && !sfnt.HasCmap(1, 0)) gid = code; // no cmap: code = glyph
        return gid;
    }

    private static FontOutlines ByUnicode(FontDecoder decoder, SfntFont? substitute, string?[]? names = null)
    {
        if (substitute == null) return new FontOutlines(decoder, Thousandth, _ => null);
        bool symbol = substitute.HasCmap(3, 0) && !substitute.HasCmap(3, 1);
        int Glyph(DecodedGlyph g)
        {
            int gid = -1;
            if (symbol) gid = substitute.MapCode(3, 0, 0xF000 + (g.Code & 0xFF));
            string? text = g.Text;
            if ((string.IsNullOrEmpty(text) || text == "�") && names != null && g.Code is >= 0 and < 256 && names[g.Code] is { } name)
                text = GlyphNames.ToUnicode(name);
            if (gid <= 0 && !string.IsNullOrEmpty(text))
            {
                int codePoint = char.ConvertToUtf32(text, 0);
                gid = substitute.MapCode(3, 10, codePoint);
                if (gid <= 0) gid = substitute.MapCode(3, 1, codePoint);
                if (gid <= 0) gid = substitute.MapCode(0, 3, codePoint);
            }
            return gid;
        }
        return new FontOutlines(decoder, UnitsMatrix(substitute), g => Glyph(g) is > 0 and var gid ? substitute.GetOutline(gid) : null)
        {
            CodeNames = names ?? new string?[256],
            Advance = decoder.HasKnownWidths || substitute.Cff != null ? null
                : g => Glyph(g) is > 0 and var gid ? (double)substitute.AdvanceWidth(gid) / substitute.UnitsPerEm : g.Width,
        };
    }

    /// <summary>The embedded font program: Type1Font, CffFont or SfntFont, or null.</summary>
    private static object? LoadProgram(PdfDictionary? descriptor)
    {
        if (descriptor == null) return null;
        try
        {
            if (descriptor.GetStream(PdfName.FontFile2) is { } ff2) return new SfntFont(ff2.GetBytes());
            if (descriptor.GetStream(PdfName.Get("FontFile3")) is { } ff3)
            {
                var data = ff3.GetBytes();
                return ff3.GetName(PdfName.Subtype)?.Value == "OpenType" || data.Length > 4 && (data[0] == 'O' && data[1] == 'T' || data[0] == 0 && data[1] == 1)
                    ? new SfntFont(data) : new CffFont(data);
            }
            if (descriptor.GetStream(PdfName.Get("FontFile")) is { } ff1) return new Type1Font(ff1.GetBytes(), ff1.GetInt(PdfName.Get("Length1")));
        }
        catch (Exception ex) when (ex is PdfException or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
        {
            // a damaged font program: use a substitute
        }
        return null;
    }

    private static int[]? ReadCidToGid(PdfDictionary? descendant)
    {
        if (descendant?.GetStream(PdfName.Get("CIDToGIDMap")) is not { } map) return null;
        var data = map.GetBytes();
        var result = new int[data.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = (data[2 * i] << 8) | data[2 * i + 1];
        return result;
    }

    /// <summary>Glyph names per code: the font's built-in encoding, then the PDF encoding and its differences.</summary>
    private static string?[] CodeNamesOf(PdfDictionary font, Func<int, string?>? builtIn, string? defaultEncoding = null)
    {
        var names = new string?[256];
        if (builtIn != null) for (int code = 0; code < 256; code++) names[code] = builtIn(code);
        string? baseEncoding = defaultEncoding;
        PdfArray? differences = null;
        switch (font.Get(PdfName.Encoding))
        {
            case PdfName n:
                baseEncoding = n.Value;
                break;
            case PdfDictionary d:
                baseEncoding = d.GetName(PdfName.BaseEncoding)?.Value ?? baseEncoding;
                differences = d.GetArray(PdfName.Differences);
                break;
        }
        if (baseEncoding != null)
            for (int code = 0; code < 256; code++)
            {
                char c = GlyphNames.Decode(baseEncoding, code);
                if (c != '\0' && GlyphNames.NameOf(c) is { } name) names[code] = name;
            }
        if (differences != null)
        {
            int code = 0;
            foreach (var item in differences)
            {
                switch (item.Resolve())
                {
                    case PdfNumber number:
                        code = number.IntValue;
                        break;
                    case PdfName glyph:
                        if (code is >= 0 and < 256) names[code] = glyph.Value;
                        code++;
                        break;
                }
            }
        }
        return names;
    }
}
