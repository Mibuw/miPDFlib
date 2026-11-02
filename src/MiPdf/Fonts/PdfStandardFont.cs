using MiPdf.Objects;

namespace MiPdf.Fonts;

/// <summary>
/// One of the 14 standard Type 1 fonts that every PDF viewer provides (not embedded).
/// Text is encoded with WinAnsiEncoding (Western European characters incl. German umlauts and €).
/// Use <see cref="PdfTrueTypeFont"/> for other scripts or for PDF/A, which requires embedded fonts.
/// </summary>
public sealed class PdfStandardFont : PdfFont
{
    /// <summary>Helvetica (sans serif, metrically compatible with Arial).</summary>
    public static readonly PdfStandardFont Helvetica = new("Helvetica");
    /// <summary>Helvetica Bold.</summary>
    public static readonly PdfStandardFont HelveticaBold = new("Helvetica-Bold");
    /// <summary>Helvetica Oblique (italic).</summary>
    public static readonly PdfStandardFont HelveticaOblique = new("Helvetica-Oblique");
    /// <summary>Helvetica Bold Oblique (bold italic).</summary>
    public static readonly PdfStandardFont HelveticaBoldOblique = new("Helvetica-BoldOblique");
    /// <summary>Times Roman (serif).</summary>
    public static readonly PdfStandardFont TimesRoman = new("Times-Roman");
    /// <summary>Times Bold.</summary>
    public static readonly PdfStandardFont TimesBold = new("Times-Bold");
    /// <summary>Times Italic.</summary>
    public static readonly PdfStandardFont TimesItalic = new("Times-Italic");
    /// <summary>Times Bold Italic.</summary>
    public static readonly PdfStandardFont TimesBoldItalic = new("Times-BoldItalic");
    /// <summary>Courier (monospaced).</summary>
    public static readonly PdfStandardFont Courier = new("Courier");
    /// <summary>Courier Bold.</summary>
    public static readonly PdfStandardFont CourierBold = new("Courier-Bold");
    /// <summary>Courier Oblique (italic).</summary>
    public static readonly PdfStandardFont CourierOblique = new("Courier-Oblique");
    /// <summary>Courier Bold Oblique (bold italic).</summary>
    public static readonly PdfStandardFont CourierBoldOblique = new("Courier-BoldOblique");
    /// <summary>Symbol (Greek letters and mathematical symbols; built-in encoding, see <see cref="IsSymbolic"/>).</summary>
    public static readonly PdfStandardFont Symbol = new("Symbol");
    /// <summary>ZapfDingbats (ornaments and check marks; built-in encoding, see <see cref="IsSymbolic"/>).</summary>
    public static readonly PdfStandardFont ZapfDingbats = new("ZapfDingbats");

    private readonly StandardFontInfo _info;

    private PdfStandardFont(string name) => _info = StandardFontMetrics.Get(name);

    /// <summary>Returns a standard font by its PostScript name, e.g. "Helvetica-Bold".</summary>
    public static PdfStandardFont FromName(string name) => name switch
    {
        "Helvetica" or "Arial" => Helvetica,
        "Helvetica-Bold" or "Arial,Bold" or "Arial-BoldMT" => HelveticaBold,
        "Helvetica-Oblique" or "Arial,Italic" => HelveticaOblique,
        "Helvetica-BoldOblique" or "Arial,BoldItalic" => HelveticaBoldOblique,
        "Times-Roman" or "TimesNewRoman" or "Times New Roman" => TimesRoman,
        "Times-Bold" => TimesBold,
        "Times-Italic" => TimesItalic,
        "Times-BoldItalic" => TimesBoldItalic,
        "Courier" or "CourierNew" or "Courier New" => Courier,
        "Courier-Bold" => CourierBold,
        "Courier-Oblique" => CourierOblique,
        "Courier-BoldOblique" => CourierBoldOblique,
        "Symbol" => Symbol,
        "ZapfDingbats" => ZapfDingbats,
        _ => throw new ArgumentException($"'{name}' is not a standard font.", nameof(name)),
    };

    /// <inheritdoc/>
    public override string Name => _info.Name;

    /// <inheritdoc/>
    public override double Ascent => _info.Ascent;

    /// <inheritdoc/>
    public override double Descent => _info.Descent;

    /// <inheritdoc/>
    public override double CapHeight => _info.CapHeight;

    /// <summary>True for Symbol and ZapfDingbats, which use their built-in encoding (character codes are used as-is).</summary>
    public bool IsSymbolic => _info.Symbolic;

    internal int GetCode(int codePoint)
    {
        if (_info.Symbolic) return codePoint is >= 0 and < 256 ? codePoint : -1;
        return codePoint < 0x10000 ? WinAnsiEncoding.ToCode((char)codePoint) : -1;
    }

    /// <summary>
    /// Advance width of a character in 1/1000 em. Characters the font cannot encode return the width of '?',
    /// which is drawn in their place.
    /// </summary>
    public override double GetCharWidth(int codePoint)
    {
        int code = GetCode(codePoint);
        if (code < 0) code = '?';
        return StandardFontMetrics.GetWidth(_info, code);
    }

    /// <summary>
    /// True if the character is part of WinAnsiEncoding, or for <see cref="IsSymbolic"/> fonts, if the code point is 0-255.
    /// </summary>
    public override bool CanDisplay(int codePoint) => GetCode(codePoint) >= 0;

    internal override FontResource CreateResource(PdfDocument document) => new Resource(document, this);

    private sealed class Resource : FontResource
    {
        private readonly PdfStandardFont _font;

        public Resource(PdfDocument document, PdfStandardFont font) : base(document, font)
        {
            _font = font;
            Dictionary[PdfName.Subtype] = PdfName.Type1;
            Dictionary[PdfName.BaseFont] = PdfName.Get(font.Name);
            if (!font.IsSymbolic) Dictionary[PdfName.Encoding] = PdfName.WinAnsiEncoding;
        }

        public override bool IsMultiByte => false;

        public override byte[] Encode(string text)
        {
            var result = new List<byte>(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                int code = _font.GetCode(rune.Value);
                result.Add((byte)(code < 0 ? '?' : code));
            }
            return result.ToArray();
        }

        public override void Finish()
        {
            Dictionary.Set(PdfName.FirstChar, 32);
            Dictionary.Set(PdfName.LastChar, 255);
            var widths = new PdfArray();
            for (int code = 32; code <= 255; code++) widths.Add(new PdfNumber(StandardFontMetrics.GetWidth(_font._info, code)));
            Dictionary[PdfName.Widths] = widths;
        }
    }
}
