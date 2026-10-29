using MiPdf.Fonts;
using MiPdf.Objects;

namespace MiPdf.Text;

/// <summary>A decoded character code of a shown string.</summary>
internal readonly record struct DecodedGlyph(int Code, string Text, double Width, bool IsSingleByteSpace, int Length = 1);

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
            catch (PdfException) { }
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
        {
            // common form symbols (check marks, radio dots, ...)
            foreach (var (code, text) in new[] { (0x34, "✔"), (0x33, "✓"), (0x6C, "●"), (0x6E, "■"), (0x75, "◆"), (0x48, "★"), (0x38, "✖"), (0x20, " ") })
                _simpleUnicode[code] = text;
        }
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
        else Array.Fill(_simpleWidths, missing > 0 ? missing : 500);
    }

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
                yield return new DecodedGlyph(code, text, width, false, Math.Max(1, length));
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
