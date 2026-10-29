using System.Text;
using MiPdf.Fonts.TrueType;
using MiPdf.Objects;

namespace MiPdf.Fonts;

/// <summary>
/// An embedded TrueType or OpenType font with full Unicode support. The font is embedded as composite
/// font (Type0, Identity-H). TrueType outlines are subset to the used glyphs; CFF-based OpenType fonts
/// are embedded completely. A ToUnicode map is always written so text remains extractable/searchable.
/// </summary>
/// <remarks>Complex script shaping (Arabic, Indic), ligatures and kerning are not applied.</remarks>
public sealed class PdfTrueTypeFont : PdfFont
{
    private readonly TrueTypeFont _ttf;

    private PdfTrueTypeFont(TrueTypeFont ttf, bool ignoreLicense)
    {
        if (ttf.IsEmbeddingRestricted && !ignoreLicense)
            throw new PdfException($"The license of font '{ttf.PostScriptName}' does not permit embedding.");
        _ttf = ttf;
    }

    /// <summary>Loads a .ttf, .otf or .ttc file.</summary>
    /// <param name="path">Path of the font file.</param>
    /// <param name="collectionIndex">Index of the font inside a TrueType collection (.ttc).</param>
    /// <param name="ignoreLicenseRestrictions">Embed even if the font's license flags forbid it.</param>
    public static PdfTrueTypeFont FromFile(string path, int collectionIndex = 0, bool ignoreLicenseRestrictions = false) =>
        FromBytes(File.ReadAllBytes(path), collectionIndex, ignoreLicenseRestrictions);

    public static PdfTrueTypeFont FromBytes(byte[] data, int collectionIndex = 0, bool ignoreLicenseRestrictions = false) =>
        new(new TrueTypeFont(data, collectionIndex), ignoreLicenseRestrictions);

    public static PdfTrueTypeFont FromStream(Stream stream, int collectionIndex = 0, bool ignoreLicenseRestrictions = false)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return FromBytes(ms.ToArray(), collectionIndex, ignoreLicenseRestrictions);
    }

    public override string Name => _ttf.PostScriptName;

    public string FamilyName => _ttf.FamilyName;

    public bool IsBold => (_ttf.MacStyle & 1) != 0 || _ttf.WeightClass >= 600;

    public bool IsItalic => (_ttf.MacStyle & 2) != 0 || _ttf.ItalicAngle != 0;

    public override double Ascent => _ttf.ToPdfUnits(_ttf.Ascender);

    public override double Descent => _ttf.ToPdfUnits(_ttf.Descender);

    public override double CapHeight => _ttf.ToPdfUnits(_ttf.CapHeight);

    public override double LineGap => _ttf.ToPdfUnits(_ttf.LineGap);

    public override double UnderlinePosition => _ttf.ToPdfUnits(_ttf.UnderlinePosition);

    public override double UnderlineThickness => _ttf.ToPdfUnits(_ttf.UnderlineThickness);

    internal TrueTypeFont Program => _ttf;

    public override double GetCharWidth(int codePoint) => _ttf.ToPdfUnits(_ttf.GetAdvanceWidth(_ttf.GetGlyphId(codePoint)));

    public override bool CanDisplay(int codePoint) => _ttf.GetGlyphId(codePoint) != 0;

    private TrueType.KerningTable? _kerning;

    private TrueType.KerningTable Kerning => _kerning ??= TrueType.KerningTable.Load(_ttf);

    public override bool HasKerning => !Kerning.IsEmpty;

    public override double GetKerning(int left, int right)
    {
        var table = Kerning;
        if (table.IsEmpty) return 0;
        int value = table.Get(_ttf.GetGlyphId(left), _ttf.GetGlyphId(right));
        return value == 0 ? 0 : _ttf.ToPdfUnits(value);
    }

    internal override FontResource CreateResource(PdfDocument document) => new Resource(document, this);

    private sealed class Resource : FontResource
    {
        private readonly PdfTrueTypeFont _font;
        private readonly SortedDictionary<int, string> _glyphToText = new();
        private readonly PdfDictionary _cidFont = new(PdfName.Font);
        private readonly PdfDictionary _descriptor = new(PdfName.FontDescriptor);
        private readonly PdfStream _fontFile = new();
        private readonly PdfStream _toUnicode = new();
        private readonly string _subsetTag;

        public Resource(PdfDocument document, PdfTrueTypeFont font) : base(document, font)
        {
            _font = font;
            _subsetTag = CreateSubsetTag(font.Name, document);
            var ttf = font._ttf;
            string baseName = ttf.IsCff ? font.Name : _subsetTag + "+" + font.Name;

            Dictionary[PdfName.Subtype] = PdfName.Type0;
            Dictionary[PdfName.BaseFont] = PdfName.Get(ttf.IsCff ? font.Name + "-Identity-H" : baseName);
            Dictionary[PdfName.Encoding] = PdfName.IdentityH;
            Dictionary[PdfName.DescendantFonts] = new PdfArray(document.AddObject(_cidFont));
            Dictionary[PdfName.ToUnicode] = document.AddObject(_toUnicode);

            _cidFont[PdfName.Subtype] = ttf.IsCff ? PdfName.CIDFontType0 : PdfName.CIDFontType2;
            _cidFont[PdfName.BaseFont] = PdfName.Get(baseName);
            var systemInfo = new PdfDictionary();
            systemInfo[PdfName.Get("Registry")] = new PdfString("Adobe");
            systemInfo[PdfName.Get("Ordering")] = new PdfString("Identity");
            systemInfo.Set(PdfName.Get("Supplement"), 0);
            _cidFont[PdfName.CIDSystemInfo] = systemInfo;
            _cidFont[PdfName.FontDescriptor] = document.AddObject(_descriptor);
            if (!ttf.IsCff) _cidFont[PdfName.CIDToGIDMap] = PdfName.Identity;

            int flags = 4; // symbolic (glyphs outside the standard Latin character set)
            if (ttf.IsFixedPitch) flags |= 1;
            if (font.IsItalic) flags |= 64;
            _descriptor[PdfName.FontName] = PdfName.Get(baseName);
            _descriptor[PdfName.Get("FontFamily")] = new PdfString(ttf.FamilyName);
            _descriptor.Set(PdfName.Get("Flags"), flags);
            _descriptor[PdfName.Get("FontBBox")] = PdfArray.FromNumbers(
                Math.Round(ttf.ToPdfUnits(ttf.XMin)), Math.Round(ttf.ToPdfUnits(ttf.YMin)),
                Math.Round(ttf.ToPdfUnits(ttf.XMax)), Math.Round(ttf.ToPdfUnits(ttf.YMax)));
            _descriptor.Set(PdfName.Get("ItalicAngle"), ttf.ItalicAngle);
            _descriptor.Set(PdfName.Get("Ascent"), Math.Round(font.Ascent));
            _descriptor.Set(PdfName.Get("Descent"), Math.Round(font.Descent));
            _descriptor.Set(PdfName.Get("CapHeight"), Math.Round(font.CapHeight));
            _descriptor.Set(PdfName.Get("StemV"), 10 + 220 * Math.Max(0, ttf.WeightClass - 50) / 900);
            _descriptor[ttf.IsCff ? PdfName.FontFile3 : PdfName.FontFile2] = document.AddObject(_fontFile);
        }

        public override bool IsMultiByte => true;

        public override byte[] Encode(string text)
        {
            var ttf = _font._ttf;
            var result = new List<byte>(text.Length * 2);
            foreach (var rune in text.EnumerateRunes())
            {
                int gid = ttf.GetGlyphId(rune.Value);
                if (gid != 0 && !_glyphToText.ContainsKey(gid)) _glyphToText[gid] = rune.ToString();
                result.Add((byte)(gid >> 8));
                result.Add((byte)gid);
            }
            return result.ToArray();
        }

        private void WriteWidths()
        {
            var ttf = _font._ttf;
            // widths: W [gid [w ...] ...] grouped by consecutive glyph ids
            var w = new PdfArray();
            PdfArray? run = null;
            int last = -2;
            foreach (int gid in _glyphToText.Keys)
            {
                if (gid != last + 1 || run == null)
                {
                    run = new PdfArray();
                    w.Add(new PdfNumber(gid));
                    w.Add(run);
                }
                run.Add(new PdfNumber(Math.Round(ttf.ToPdfUnits(ttf.GetAdvanceWidth(gid)))));
                last = gid;
            }
            _cidFont[PdfName.W] = w;
            _cidFont.Set(PdfName.Get("DW"), Math.Round(ttf.ToPdfUnits(ttf.GetAdvanceWidth(0))));
        }

        public override void PrepareForReading()
        {
            WriteWidths();
            _toUnicode.SetBytes(ToUnicodeCMap.Build(_glyphToText, 2));
        }

        public override void Finish()
        {
            var ttf = _font._ttf;
            WriteWidths();

            if (ttf.IsCff)
            {
                _fontFile.SetBytes(ttf.Data);
                _fontFile[PdfName.Subtype] = PdfName.Get("OpenType");
            }
            else
            {
                var subset = TrueTypeSubsetter.Subset(ttf, _glyphToText.Keys);
                _fontFile.SetBytes(subset);
                _fontFile.Set(PdfName.Get("Length1"), subset.Length);
            }
            _toUnicode.SetBytes(ToUnicodeCMap.Build(_glyphToText, 2));
            if (Document.Conformance == PdfA.PdfAConformance.PdfA1B && !ttf.IsCff)
            {
                // PDF/A-1 requires a CIDSet identifying the glyphs of a subset CID font
                var bits = new byte[(_glyphToText.Keys.DefaultIfEmpty(0).Max() + 8) / 8];
                bits[0] |= 0x80; // CID 0
                foreach (int gid in _glyphToText.Keys) bits[gid / 8] |= (byte)(0x80 >> (gid % 8));
                _descriptor[PdfName.Get("CIDSet")] = Document.AddObject(new PdfStream(bits));
            }
        }

        private static string CreateSubsetTag(string name, PdfDocument document)
        {
            // six uppercase letters, unique per font and document
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name + document.GetHashCode() + Guid.NewGuid()));
            var sb = new StringBuilder(6);
            for (int i = 0; i < 6; i++) sb.Append((char)('A' + hash[i] % 26));
            return sb.ToString();
        }
    }
}
