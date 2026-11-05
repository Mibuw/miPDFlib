using MiPdf.Fonts;
using MiPdf.Objects;

namespace MiPdf.Text;

/// <summary>A decoded character code of a shown string.</summary>
internal readonly record struct DecodedGlyph(int Code, string Text, double Width, bool IsSingleByteSpace, int Length = 1, int Cid = -1);

/// <summary>
/// Decodes strings shown with a font dictionary into Unicode text and glyph widths
/// (ISO 32000-2, 9.10 "Extraction of text content").
/// </summary>
internal sealed class FontDecoder
{
    private readonly bool _composite;
    private readonly CMap? _toUnicode;
    private readonly CMap _encodingCMap;
    private readonly string?[] _simpleUnicode = new string?[256];
    private readonly double[] _simpleWidths = new double[256];
    private readonly Dictionary<int, double> _cidWidths = new();
    private readonly double _defaultWidth;
    private readonly double _widthScale = 0.001;
    private readonly bool _identityUnicodeFallback;
    private readonly PdfStream? _embeddedTrueType;
    private Dictionary<int, string>? _glyphToUnicode;

    public FontDecoder(PdfDictionary font)
    {
        var subtype = font.GetName(PdfName.Subtype)?.Value;
        _composite = subtype == "Type0";
        if (font.GetStream(PdfName.ToUnicode) is { } tu)
        {
            try { _toUnicode = CMap.Parse(tu.GetBytes()); }
            catch (PdfException ex)
            {
                DiagnosticContext.Warning(PdfDiagnosticCode.ToUnicodeDamaged,
                    $"The ToUnicode map of font {font.GetName(PdfName.BaseFont)?.Value ?? "(unnamed)"} cannot be read ({ex.Message}).", font);
            }
        }

        if (_composite)
        {
            _encodingCMap = font.Get(PdfName.Encoding) is PdfStream encStream ? SafeParse(encStream) : CMap.Identity2Byte();
            if (!_encodingCMap.HasCodeSpaces) _encodingCMap = CMap.Identity2Byte();
            var descendant = font.GetArray(PdfName.DescendantFonts)?.GetDictionary(0);
            _defaultWidth = descendant?.GetDouble(PdfName.Get("DW"), 1000) ?? 1000;
            if (descendant?.GetArray(PdfName.W) is { } w) ReadCidWidths(w);
            var ordering = descendant?.GetDictionary(PdfName.CIDSystemInfo)?.GetText(PdfName.Get("Ordering"));
            _identityUnicodeFallback = ordering is null or "Identity" or "UCS";
            _embeddedTrueType = descendant?.GetDictionary(PdfName.FontDescriptor)?.GetStream(PdfName.FontFile2);
            return;
        }

        _encodingCMap = new CMap();
        if (subtype == "Type3" && font.GetArray(PdfName.Get("FontMatrix")) is { Count: 6 } fm) _widthScale = fm.GetDouble(0);
        string baseFont = font.GetName(PdfName.BaseFont)?.Value ?? "";
        string plainName = baseFont.Contains('+') ? baseFont[(baseFont.IndexOf('+') + 1)..] : baseFont;
        var descriptor = font.GetDictionary(PdfName.FontDescriptor);
        bool symbolic = (descriptor?.GetInt(PdfName.Get("Flags")) & 4) != 0;

        // encoding: base encoding + differences
        string? baseEncoding = null;
        PdfArray? differences = null;
        switch (font.Get(PdfName.Encoding))
        {
            case PdfName n:
                baseEncoding = n.Value;
                break;
            case PdfDictionary d:
                baseEncoding = d.GetName(PdfName.BaseEncoding)?.Value;
                differences = d.GetArray(PdfName.Differences);
                break;
        }
        bool builtInSymbol = baseEncoding == null && (plainName.StartsWith("Symbol", StringComparison.Ordinal) || plainName.StartsWith("ZapfDingbats", StringComparison.Ordinal));
        if (baseEncoding == null && subtype == "TrueType" && !symbolic) baseEncoding = "WinAnsiEncoding";
        for (int code = 0; code < 256; code++)
        {
            char c = builtInSymbol ? '\0' : GlyphNames.Decode(baseEncoding, code);
            if (c == '\0' && symbolic && baseEncoding == null && code >= 32) c = (char)code; // symbolic font without encoding
            if (c != '\0') _simpleUnicode[code] = c.ToString();
        }
        if (builtInSymbol && plainName.StartsWith("ZapfDingbats", StringComparison.Ordinal))
            for (int code = 0; code < 256; code++)
                if (ZapfDingbatsUnicode(code) is > 0 and var u) _simpleUnicode[code] = ((char)u).ToString();
        if (differences != null)
        {
            int code = 0;
            foreach (var item in differences)
            {
                switch (item.Resolve())
                {
                    case PdfNumber num:
                        code = num.IntValue;
                        break;
                    case PdfName glyph:
                        if (code is >= 0 and < 256) _simpleUnicode[code] = GlyphNames.ToUnicode(glyph.Value) ?? _simpleUnicode[code];
                        code++;
                        break;
                }
            }
        }

        // widths
        double missing = descriptor?.GetDouble(PdfName.Get("MissingWidth")) ?? 0;
        if (font.GetArray(PdfName.Widths) is { } widths)
        {
            int first = font.GetInt(PdfName.FirstChar);
            for (int code = 0; code < 256; code++)
            {
                int index = code - first;
                _simpleWidths[code] = index >= 0 && index < widths.Count ? widths.GetDouble(index) : missing;
            }
        }
        else if (StandardFontMetrics.IsStandardFont(StandardName(plainName)))
        {
            var info = StandardFontMetrics.Get(StandardName(plainName));
            for (int code = 0; code < 256; code++)
            {
                // the metrics are indexed by WinAnsi code; map via Unicode for other encodings
                int winAnsi = _simpleUnicode[code] is { Length: 1 } u ? WinAnsiEncoding.ToCode(u[0]) : -1;
                if (info.Symbolic) winAnsi = code;
                _simpleWidths[code] = winAnsi >= 32 ? StandardFontMetrics.GetWidth(info, winAnsi) : 500;
            }
        }
        else
        {
            Array.Fill(_simpleWidths, missing > 0 ? missing : 500);
            HasKnownWidths = false;
        }
    }

    /// <summary>
    /// Unicode of a code in ZapfDingbats' built-in encoding (Adobe's zapfdingbats glyph list); 0 if unused. The Unicode
    /// Dingbats block was derived from this font, so most codes map to U+2700 + (code - 0x20); the gaps of that block
    /// hold glyphs Unicode already had elsewhere.
    /// </summary>
    internal static int ZapfDingbatsUnicode(int code) => code switch
    {
        0x20 => 0x20,
        0x25 => 0x260E,
        0x2A => 0x261B,
        0x2B => 0x261E,
        0x48 => 0x2605,
        0x6C => 0x25CF,
        0x6E => 0x25A0,
        0x73 => 0x25B2,
        0x74 => 0x25BC,
        0x75 => 0x25C6,
        0x77 => 0x25D7,
        >= 0x21 and <= 0x7E => 0x2700 + code - 0x20,
        >= 0x80 and <= 0x8D => 0x2768 + code - 0x80,
        >= 0xA1 and <= 0xA7 => 0x2761 + code - 0xA1,
        0xA8 => 0x2663,
        0xA9 => 0x2666,
        0xAA => 0x2665,
        0xAB => 0x2660,
        >= 0xAC and <= 0xB5 => 0x2460 + code - 0xAC,
        >= 0xB6 and <= 0xD4 => 0x2776 + code - 0xB6,
        0xD5 => 0x2192,
        0xD6 => 0x2194,
        0xD7 => 0x2195,
        >= 0xD8 and <= 0xEF => 0x2798 + code - 0xD8,
        >= 0xF1 and <= 0xFE => 0x27B1 + code - 0xF1,
        _ => 0,
    };

    private static CMap SafeParse(PdfStream s)
    {
        try { return CMap.Parse(s.GetBytes()); }
        catch (PdfException) { return CMap.Identity2Byte(); }
    }

    private static string StandardName(string name) => name switch
    {
        "Arial" or "ArialMT" => "Helvetica",
        "Arial,Bold" or "Arial-BoldMT" => "Helvetica-Bold",
        "Arial,Italic" or "Arial-ItalicMT" => "Helvetica-Oblique",
        "Arial,BoldItalic" or "Arial-BoldItalicMT" => "Helvetica-BoldOblique",
        "TimesNewRoman" or "TimesNewRomanPSMT" => "Times-Roman",
        "TimesNewRoman,Bold" or "TimesNewRomanPS-BoldMT" => "Times-Bold",
        "CourierNew" or "CourierNewPSMT" => "Courier",
        _ => name,
    };

    private void ReadCidWidths(PdfArray w)
    {
        int i = 0;
        while (i < w.Count)
        {
            int first = w.GetInt(i);
            if (i + 1 < w.Count && w.Get(i + 1) is PdfArray list)
            {
                for (int k = 0; k < list.Count; k++) _cidWidths[first + k] = list.GetDouble(k);
                i += 2;
            }
            else if (i + 2 < w.Count)
            {
                int last = w.GetInt(i + 1);
                double width = w.GetDouble(i + 2);
                for (int cid = first; cid <= last && cid - first < 65536; cid++) _cidWidths[cid] = width;
                i += 3;
            }
            else break;
        }
    }

    /// <summary>
    /// Without ToUnicode map: for Identity-encoded embedded TrueType fonts the CID is the glyph id, which can
    /// be mapped back to Unicode through the font's own cmap table.
    /// </summary>
    private string FallbackUnicode(int cid)
    {
        if (!_identityUnicodeFallback || _embeddedTrueType == null) return "";
        if (_glyphToUnicode == null)
        {
            _glyphToUnicode = new Dictionary<int, string>();
            try
            {
                var ttf = new Fonts.TrueType.TrueTypeFont(_embeddedTrueType.GetBytes());
                foreach (var (codePoint, gid) in ttf.CharacterMap)
                    if (codePoint is >= 0x20 and <= 0x10FFFF and (< 0xD800 or > 0xDFFF))
                        _glyphToUnicode.TryAdd(gid, char.ConvertFromUtf32(codePoint));
            }
            catch (Exception ex) when (ex is PdfException or ArgumentException or IndexOutOfRangeException) { }
        }
        return _glyphToUnicode.TryGetValue(cid, out var s) ? s : "";
    }

    /// <summary>False for a simple font without /Widths that is not a standard 14 font (its widths are guessed).</summary>
    public bool HasKnownWidths { get; } = true;

    /// <summary>Width of the space character in text space units (1/1000 em scaled by FontMatrix for Type3).</summary>
    public double SpaceWidth
    {
        get
        {
            if (!_composite && _simpleWidths[32] > 0 && (_toUnicode?.GetUnicode(32) ?? _simpleUnicode[32]) == " ")
                return _simpleWidths[32] * _widthScale;
            if (_composite && _cidWidths.TryGetValue(3, out var w3) && _toUnicode?.GetUnicode(3) == " ") return w3 * 0.001;
            return 0.25; // fallback: quarter em
        }
    }

    public IEnumerable<DecodedGlyph> Decode(byte[] bytes)
    {
        int pos = 0;
        while (pos < bytes.Length)
        {
            if (_composite)
            {
                int code = _encodingCMap.ReadCode(bytes, ref pos, out int length);
                int cid = _encodingCMap.GetCid(code);
                string text = _toUnicode?.GetUnicode(code) ?? FallbackUnicode(cid);
                double width = (_cidWidths.TryGetValue(cid, out var w) ? w : _defaultWidth) * 0.001;
                // a one-byte code 32 counts as space for the word spacing (Tw) also in composite fonts
                yield return new DecodedGlyph(code, text, width, length == 1 && code == 32, Math.Max(1, length), cid);
            }
            else
            {
                int code = bytes[pos++];
                string text = _toUnicode?.GetUnicode(code) ?? _simpleUnicode[code] ?? "";
                yield return new DecodedGlyph(code, text, _simpleWidths[code] * _widthScale, code == 32);
            }
        }
    }
}
