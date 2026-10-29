using MiPdf.Objects;

namespace MiPdf.Fonts;

/// <summary>
/// One of the 14 standard Type 1 fonts that every PDF viewer provides (not embedded).
/// Text is encoded with WinAnsiEncoding (Western European characters incl. German umlauts and €).
/// Use <see cref="PdfTrueTypeFont"/> for other scripts or for PDF/A, which requires embedded fonts.
/// </summary>
public sealed class PdfStandardFont : PdfFont
{
    public static readonly PdfStandardFont Helvetica = new("Helvetica");
    public static readonly PdfStandardFont HelveticaBold = new("Helvetica-Bold");
    public static readonly PdfStandardFont HelveticaOblique = new("Helvetica-Oblique");
    public static readonly PdfStandardFont HelveticaBoldOblique = new("Helvetica-BoldOblique");
    public static readonly PdfStandardFont TimesRoman = new("Times-Roman");
    public static readonly PdfStandardFont TimesBold = new("Times-Bold");
    public static readonly PdfStandardFont TimesItalic = new("Times-Italic");
    public static readonly PdfStandardFont TimesBoldItalic = new("Times-BoldItalic");
    public static readonly PdfStandardFont Courier = new("Courier");
    public static readonly PdfStandardFont CourierBold = new("Courier-Bold");
    public static readonly PdfStandardFont CourierOblique = new("Courier-Oblique");
    public static readonly PdfStandardFont CourierBoldOblique = new("Courier-BoldOblique");
    public static readonly PdfStandardFont Symbol = new("Symbol");
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

    public override string Name => _info.Name;

    public override double Ascent => _info.Ascent;

    public override double Descent => _info.Descent;

    public override double CapHeight => _info.CapHeight;

    /// <summary>True for Symbol and ZapfDingbats, which use their built-in encoding (character codes are used as-is).</summary>
    public bool IsSymbolic => _info.Symbolic;

    internal int GetCode(int codePoint)
    {
        if (_info.Symbolic) return codePoint is >= 0 and < 256 ? codePoint : -1;
        return codePoint < 0x10000 ? WinAnsiEncoding.ToCode((char)codePoint) : -1;
    }

    public override double GetCharWidth(int codePoint)
    {
        int code = GetCode(codePoint);
        if (code < 0) code = '?';
        return StandardFontMetrics.GetWidth(_info, code);
    }

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
