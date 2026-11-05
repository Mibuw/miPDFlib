using System.Collections.Concurrent;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.Rendering.Raster;
using MiPdf.Text;

namespace MiPdf.Rendering.Fonts;

/// <summary>
/// A font prepared for rendering: the glyph outlines of the embedded font program (TrueType, CFF, Type 1) or of a
/// system font as substitute, selected per shown code as ISO 32000-2, 9.6.5 and 9.7.4 describe. Type 3 fonts are
/// drawn from their glyph procedures by the content renderer.
/// </summary>
internal sealed class RenderFont
{
    private readonly Dictionary<int, PathGeometry?> _glyphs = new();
    private readonly Func<DecodedGlyph, PathGeometry?> _outline;

    private RenderFont(FontDecoder decoder, Matrix fontMatrix, Func<DecodedGlyph, PathGeometry?> outline, PdfDictionary? type3 = null)
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

    // ------------------------------------------------------------------ loading

    public static RenderFont Load(PdfDictionary font)
    {
        var decoder = new FontDecoder(font);
        string subtype = font.GetName(PdfName.Subtype)?.Value ?? "";
        if (subtype == "Type3")
        {
            var matrix = font.GetArray(PdfName.Get("FontMatrix")) is { Count: 6 } m
                ? new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5))
                : new Matrix(0.001, 0, 0, 0.001, 0, 0);
            return new RenderFont(decoder, matrix, _ => null, font) { CodeNames = CodeNamesOf(font, null) };
        }
        return subtype == "Type0" ? LoadComposite(font, decoder) : LoadSimple(font, decoder, subtype);
    }

    private static readonly Matrix Thousandth = new(0.001, 0, 0, 0.001, 0, 0);

    private static Matrix UnitsMatrix(SfntFont sfnt) => sfnt.Cff?.FontMatrix ?? new Matrix(1.0 / sfnt.UnitsPerEm, 0, 0, 1.0 / sfnt.UnitsPerEm, 0, 0);

    private static RenderFont LoadComposite(PdfDictionary font, FontDecoder decoder)
    {
        var descendant = font.GetArray(PdfName.DescendantFonts)?.GetDictionary(0);
        var descriptor = descendant?.GetDictionary(PdfName.FontDescriptor);
        var program = LoadProgram(descriptor);
        switch (program)
        {
            case SfntFont sfnt:
            {
                int[]? cidToGid = ReadCidToGid(descendant);
                return new RenderFont(decoder, UnitsMatrix(sfnt), g =>
                {
                    int gid = sfnt.Cff != null ? sfnt.Cff.GlyphByCid(g.Cid)
                        : cidToGid != null ? (g.Cid >= 0 && g.Cid < cidToGid.Length ? cidToGid[g.Cid] : 0) : g.Cid;
                    return gid > 0 ? sfnt.GetOutline(gid) : null;
                });
            }
            case CffFont cff:
                return new RenderFont(decoder, cff.FontMatrix, g => cff.GlyphByCid(g.Cid) is > 0 and var gid ? cff.GetOutline(gid) : null);
        }
        // not embedded: a system font, glyphs by Unicode
        var substitute = Substitute(font, font.GetName(PdfName.BaseFont)?.Value ?? descendant?.GetName(PdfName.BaseFont)?.Value ?? "", descriptor, program);
        return ByUnicode(decoder, substitute);
    }

    private static RenderFont LoadSimple(PdfDictionary font, FontDecoder decoder, string subtype)
    {
        var descriptor = font.GetDictionary(PdfName.FontDescriptor);
        var program = LoadProgram(descriptor);
        bool symbolic = ((descriptor?.GetInt(PdfName.Get("Flags")) ?? 0) & 4) != 0;
        switch (program)
        {
            case Type1Font type1:
            {
                var names = CodeNamesOf(font, type1.NameForCode);
                return new RenderFont(decoder, type1.FontMatrix, g =>
                {
                    string? name = g.Code is >= 0 and < 256 ? names[g.Code] : null;
                    return name != null && type1.HasGlyph(name) ? type1.GetOutline(name)
                        : type1.NameForCode(g.Code) is { } builtIn ? type1.GetOutline(builtIn) : null;
                }) { CodeNames = names };
            }
            case CffFont cff:
            {
                var names = CodeNamesOf(font, null);
                return new RenderFont(decoder, cff.FontMatrix, g =>
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
                return new RenderFont(decoder, sfnt.Cff.FontMatrix, g =>
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
                return new RenderFont(decoder, UnitsMatrix(sfnt), g =>
                {
                    string? name = g.Code is >= 0 and < 256 ? names[g.Code] : null;
                    int gid = TrueTypeGlyph(sfnt, g.Code, name, symbolic && font.Get(PdfName.Encoding) == null);
                    return gid > 0 ? sfnt.GetOutline(gid) : null;
                }) { CodeNames = names };
            }
        }
        // not embedded (standard 14 fonts and others): a system font, glyphs by Unicode
        var substitute = Substitute(font, font.GetName(PdfName.BaseFont)?.Value ?? "", descriptor, program);
        return ByUnicode(decoder, substitute, CodeNamesOf(font, null));
    }

    /// <summary>The installed substitute for a font without (usable) embedded program, reported as a diagnostic.</summary>
    private static SfntFont? Substitute(PdfDictionary font, string baseFont, PdfDictionary? descriptor, object? program)
    {
        var substitute = SystemFonts.Find(baseFont, descriptor);
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

    private static RenderFont ByUnicode(FontDecoder decoder, SfntFont? substitute, string?[]? names = null)
    {
        if (substitute == null) return new RenderFont(decoder, Thousandth, _ => null);
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
        return new RenderFont(decoder, UnitsMatrix(substitute), g => Glyph(g) is > 0 and var gid ? substitute.GetOutline(gid) : null)
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

/// <summary>System fonts as substitutes for fonts that are not embedded (standard 14 fonts and others).</summary>
internal static class SystemFonts
{
    private static readonly ConcurrentDictionary<string, SfntFont?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Directories = BuildDirectories();

    private static string[] BuildDirectories()
    {
        var list = new List<string>();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(windows)) list.Add(windows);
        list.AddRange(new[] { "/usr/share/fonts", "/usr/local/share/fonts", "/System/Library/Fonts", "/Library/Fonts" });
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) list.Add(Path.Combine(home, ".fonts"));
        return list.Where(Directory.Exists).ToArray();
    }

    // candidates per style: Windows, Liberation (metric compatible), DejaVu
    private static readonly Dictionary<string, string[]> Candidates = new(StringComparer.Ordinal)
    {
        ["sans"] = new[] { "arial.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf" },
        ["sans-bold"] = new[] { "arialbd.ttf", "LiberationSans-Bold.ttf", "DejaVuSans-Bold.ttf" },
        ["sans-italic"] = new[] { "ariali.ttf", "LiberationSans-Italic.ttf", "DejaVuSans-Oblique.ttf" },
        ["sans-bolditalic"] = new[] { "arialbi.ttf", "LiberationSans-BoldItalic.ttf", "DejaVuSans-BoldOblique.ttf" },
        ["serif"] = new[] { "times.ttf", "LiberationSerif-Regular.ttf", "DejaVuSerif.ttf" },
        ["serif-bold"] = new[] { "timesbd.ttf", "LiberationSerif-Bold.ttf", "DejaVuSerif-Bold.ttf" },
        ["serif-italic"] = new[] { "timesi.ttf", "LiberationSerif-Italic.ttf", "DejaVuSerif-Italic.ttf" },
        ["serif-bolditalic"] = new[] { "timesbi.ttf", "LiberationSerif-BoldItalic.ttf", "DejaVuSerif-BoldItalic.ttf" },
        ["mono"] = new[] { "cour.ttf", "LiberationMono-Regular.ttf", "DejaVuSansMono.ttf" },
        ["mono-bold"] = new[] { "courbd.ttf", "LiberationMono-Bold.ttf", "DejaVuSansMono-Bold.ttf" },
        ["mono-italic"] = new[] { "couri.ttf", "LiberationMono-Italic.ttf", "DejaVuSansMono-Oblique.ttf" },
        ["mono-bolditalic"] = new[] { "courbi.ttf", "LiberationMono-BoldItalic.ttf", "DejaVuSansMono-BoldOblique.ttf" },
        ["symbol"] = new[] { "symbol.ttf" },
        ["dingbats"] = new[] { "seguisym.ttf", "DejaVuSans.ttf" },
    };

    /// <summary>A substitute for a font by its name and font descriptor (sans, serif or monospaced; bold, italic).</summary>
    public static SfntFont? Find(string baseFont, PdfDictionary? descriptor)
    {
        string name = baseFont.Contains('+') ? baseFont[(baseFont.IndexOf('+') + 1)..] : baseFont;
        int flags = descriptor?.GetInt(PdfName.Get("Flags")) ?? 0;
        string lower = name.ToLowerInvariant();
        string family =
            lower.StartsWith("symbol", StringComparison.Ordinal) ? "symbol"
            : lower.Contains("dingbat") ? "dingbats"
            : lower.Contains("courier") || lower.Contains("mono") || (flags & 1) != 0 ? "mono"
            : lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("georgia") || lower.Contains("garamond") || (flags & 2) != 0 && !lower.Contains("arial") && !lower.Contains("helvetica") ? "serif"
            : "sans";
        bool bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || (descriptor?.GetDouble(PdfName.Get("FontWeight")) ?? 400) >= 600 || (flags & (1 << 18)) != 0;
        bool italic = lower.Contains("italic") || lower.Contains("oblique") || (flags & 64) != 0 || (descriptor?.GetDouble(PdfName.Get("ItalicAngle")) ?? 0) != 0;
        string key = family is "symbol" or "dingbats" ? family : family + (bold && italic ? "-bolditalic" : bold ? "-bold" : italic ? "-italic" : "");
        return Cache.GetOrAdd(key, Load) ?? (key != "sans" ? Cache.GetOrAdd("sans", Load) : null);
    }

    private static SfntFont? Load(string key)
    {
        foreach (var file in Candidates[key])
        {
            foreach (var directory in Directories)
            {
                string? path = File.Exists(Path.Combine(directory, file)) ? Path.Combine(directory, file)
                    : SafeSearch(directory, file);
                if (path == null) continue;
                try
                {
                    return new SfntFont(File.ReadAllBytes(path)) { FileName = Path.GetFileName(path) };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfException or IndexOutOfRangeException)
                {
                    // try the next candidate
                }
            }
        }
        return null;
    }

    private static string? SafeSearch(string directory, string file)
    {
        try
        {
            return Directory.EnumerateFiles(directory, file, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true, MaxRecursionDepth = 4 }).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
