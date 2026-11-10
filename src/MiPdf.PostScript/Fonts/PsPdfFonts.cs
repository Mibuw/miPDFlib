using System.Globalization;
using System.Text;
using MiPdf.Fonts;
using MiPdf.Fonts.TrueType;
using MiPdf.Objects;
using MiPdf.PostScript.Interpreter;

namespace MiPdf.PostScript.Fonts;

/// <summary>A PDF font made for a PostScript font with a particular encoding; it collects the characters used.</summary>
internal sealed class PdfFontEntry
{
    public required PsFont Font { get; init; }
    public required PdfReference Reference { get; init; }
    public required PdfDictionary Dictionary { get; init; }

    /// <summary>Glyph names of the codes (simple fonts) as written to the PDF font.</summary>
    public string[] CodeNames { get; init; } = new string[256];

    /// <summary>Glyph names of the codes for ToUnicode (they differ for Type 3 fonts keyed by code).</summary>
    public string[] UnicodeNames { get; init; } = new string[256];

    /// <summary>Codes used so far (simple fonts) or glyph indices (Type 42 as Type 0 font).</summary>
    public readonly SortedSet<int> Used = new();

    /// <summary>Type 42: glyph names of the glyph indices (for ToUnicode).</summary>
    public readonly Dictionary<int, string> GlyphNamesById = new();

    /// <summary>Codes assigned to glyph names by glyphshow (the encoding is built from them).</summary>
    public Dictionary<string, int>? DynamicCodes { get; init; }

    public bool IsComposite => Font.Kind is PsFontKind.Type42 or PsFontKind.Cid2;
}

/// <summary>
/// The PDF fonts of a document: Type 1 fonts are embedded as subsets rebuilt from the font dictionaries, standard
/// fonts without program are referenced by name, Type 3 fonts get their captured glyph procedures, Type 42 fonts
/// become Type 0 fonts with a TrueType subset. All get a ToUnicode map from the glyph names.
/// </summary>
internal sealed class PsPdfFonts
{
    private readonly PdfDocument _document;
    private readonly PsInterpreter _ip;
    private readonly Dictionary<(PsFont, object), PdfFontEntry> _entries = new();
    private int _subsetCounter;

    public PsPdfFonts(PdfDocument document, PsInterpreter ip)
    {
        _document = document;
        _ip = ip;
    }

    /// <summary>The PDF font for a font and its encoding array.</summary>
    public PdfFontEntry Get(PsFont font, PsObject encoding)
    {
        object key = encoding.IsArray ? encoding.AsArray.Storage : "none";
        if (_entries.TryGetValue((font, key), out var entry)) return entry;
        var names = new string[256];
        for (int i = 0; i < 256; i++)
            names[i] = encoding.IsArray && i < encoding.AsArray.Length && encoding.AsArray[i].Type == PsType.Name
                ? encoding.AsArray[i].AsName.Text : ".notdef";
        // Type 3 fonts with BuildChar get their glyphs per code
        var pdfNames = font.Kind == PsFontKind.Type3 && font.KeyedByCode ? Enumerable.Range(0, 256).Select(PsFont.CodeKey).ToArray() : names;
        entry = NewEntry(font, pdfNames, null, names);
        _entries[(font, key)] = entry;
        return entry;
    }

    private readonly Dictionary<PsFont, PdfFontEntry> _glyphShow = new();

    /// <summary>A PDF font for glyphshow: codes are assigned to the glyph names as they are shown.</summary>
    public (PdfFontEntry Entry, int Code) ForGlyph(PsFont font, string name)
    {
        if (_glyphShow.TryGetValue(font, out var entry))
        {
            if (entry.DynamicCodes!.TryGetValue(name, out int existing)) return (entry, existing);
            if (entry.DynamicCodes.Count >= 255) entry = null;
        }
        if (entry == null)
        {
            var names = Enumerable.Repeat(".notdef", 256).ToArray();
            entry = NewEntry(font, names, new Dictionary<string, int>(), (string[])names.Clone());
            _entries[(font, Guid.NewGuid())] = entry;
            _glyphShow[font] = entry;
        }
        int code = entry.DynamicCodes!.Count + 1;
        entry.DynamicCodes[name] = code;
        entry.CodeNames[code] = name;
        return (entry, code);
    }

    private PdfFontEntry NewEntry(PsFont font, string[] names, Dictionary<string, int>? dynamicCodes, string[] unicodeNames)
    {
        var dict = new PdfDictionary { [PdfName.Type] = PdfName.Font };
        return new PdfFontEntry
        {
            Font = font, Dictionary = dict, Reference = _document.AddObject(dict), CodeNames = names, DynamicCodes = dynamicCodes,
            UnicodeNames = unicodeNames,
        };
    }

    /// <summary>Writes the font dictionaries and programs.</summary>
    public void Finish()
    {
        foreach (var entry in _entries.Values.Distinct())
        {
            if (entry.Used.Count == 0) entry.Used.Add(entry.IsComposite ? 0 : 32);
            try
            {
                switch (entry.Font.Kind)
                {
                    case PsFontKind.Standard:
                        WriteStandard(entry);
                        break;
                    case PsFontKind.Type1:
                        WriteType1(entry);
                        break;
                    case PsFontKind.Type3:
                    case PsFontKind.Cid1:
                        WriteType3(entry);
                        break;
                    case PsFontKind.Type42:
                    case PsFontKind.Cid2:
                        WriteType42(entry);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // damaged font data: the text is kept with a standard font
                WriteFallback(entry);
                FontFailed?.Invoke(entry.Font.Name);
            }
        }
    }

    /// <summary>Reports a font that could not be written and was replaced by Helvetica.</summary>
    public Action<string>? FontFailed;

    private void WriteFallback(PdfFontEntry entry)
    {
        var d = entry.Dictionary;
        foreach (var key in d.Select(kv => kv.Key).ToList()) d.Remove(key);
        d[PdfName.Type] = PdfName.Font;
        d[PdfName.Subtype] = PdfName.Get("Type1");
        d[PdfName.Get("BaseFont")] = PdfName.Get("Helvetica");
        if (entry.IsComposite)
        {
            // two-byte codes cannot be shown with a simple font: an empty font
            d[PdfName.Get("FirstChar")] = new PdfNumber(0);
            d[PdfName.Get("LastChar")] = new PdfNumber(0);
            d[PdfName.Get("Widths")] = new PdfArray(new PdfNumber(0));
            return;
        }
        int first = entry.Used.Min, last = entry.Used.Max;
        d[PdfName.Get("FirstChar")] = new PdfNumber(first);
        d[PdfName.Get("LastChar")] = new PdfNumber(last);
        d[PdfName.Get("Widths")] = new PdfArray(Enumerable.Range(first, last - first + 1).Select(_ => (PdfObject)new PdfNumber(500)));
    }

    private string SubsetName(string name)
    {
        int n = _subsetCounter++;
        var tag = new char[6];
        for (int i = 5; i >= 0; i--)
        {
            tag[i] = (char)('A' + n % 26);
            n /= 26;
        }
        return new string(tag) + "+" + Sanitize(name);
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name) sb.Append(c is > ' ' and < (char)127 and not ('/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%') ? c : '-');
        return sb.Length == 0 ? "Font" : sb.ToString();
    }

    // ------------------------------------------------------------------ simple fonts

    private void SimpleFontBasics(PdfFontEntry entry, Func<string, double> width)
    {
        int first = entry.Used.Min, last = entry.Used.Max;
        entry.Dictionary[PdfName.Get("FirstChar")] = new PdfNumber(first);
        entry.Dictionary[PdfName.Get("LastChar")] = new PdfNumber(last);
        var widths = new PdfArray();
        for (int code = first; code <= last; code++)
            widths.Add(new PdfNumber(entry.Used.Contains(code) ? Math.Round(width(entry.CodeNames[code]), 3) : 0));
        entry.Dictionary[PdfName.Get("Widths")] = widths;
        // the encoding: the glyph names of the used codes
        var differences = new PdfArray();
        int previous = -2;
        foreach (int code in entry.Used)
        {
            if (code != previous + 1) differences.Add(new PdfNumber(code));
            differences.Add(PdfName.Get(entry.CodeNames[code]));
            previous = code;
        }
        entry.Dictionary[PdfName.Get("Encoding")] = new PdfDictionary
        {
            [PdfName.Type] = PdfName.Get("Encoding"),
            [PdfName.Get("Differences")] = differences,
        };
        entry.Dictionary[PdfName.Get("ToUnicode")] = ToUnicode(entry.Used.Select(c => (c, entry.UnicodeNames[c])), oneByte: true);
    }

    private void WriteStandard(PdfFontEntry entry)
    {
        var font = entry.Font;
        entry.Dictionary[PdfName.Subtype] = PdfName.Get("Type1");
        entry.Dictionary[PdfName.Get("BaseFont")] = PdfName.Get(font.StandardName!);
        SimpleFontBasics(entry, name => font.Advance(name, _ip).X);
        if (font.IsSymbolicStandard)
        {
            // the built-in encoding applies unless the names differ from it
            var builtIn = font.StandardName == "Symbol" ? PsEncodings.Symbol : PsEncodings.Dingbats;
            if (entry.Used.All(c => entry.CodeNames[c] == builtIn[c])) entry.Dictionary.Remove(PdfName.Get("Encoding"));
        }
    }

    private void WriteType3(PdfFontEntry entry)
    {
        var font = entry.Font;
        var d = entry.Dictionary;
        d[PdfName.Subtype] = PdfName.Get("Type3");
        var m = font.BaseMatrix;
        d[PdfName.Get("FontMatrix")] = PdfArray.FromNumbers(m.A, m.B, m.C, m.D, m.E, m.F);
        var procs = new PdfDictionary();
        double[] box = { 0, 0, 0, 0 };
        bool any = false;
        foreach (int code in entry.Used)
        {
            string name = entry.CodeNames[code];
            if (procs.ContainsKey(PdfName.Get(name)) || !font.Type3Glyphs.TryGetValue(name, out var glyph)) continue;
            procs[PdfName.Get(name)] = _document.AddObject(new PdfStream(glyph.Content));
            if (glyph.Box is { } b)
            {
                if (!any) Array.Copy(b, box, 4);
                else
                {
                    box[0] = Math.Min(box[0], b[0]);
                    box[1] = Math.Min(box[1], b[1]);
                    box[2] = Math.Max(box[2], b[2]);
                    box[3] = Math.Max(box[3], b[3]);
                }
                any = true;
            }
        }
        d[PdfName.Get("CharProcs")] = procs;
        d[PdfName.Get("FontBBox")] = PdfArray.FromNumbers(any ? box : font.FontBBox);
        d[PdfName.Resources] = font.Type3Resources ?? new PdfDictionary();
        SimpleFontBasics(entry, name => font.Type3Glyphs.TryGetValue(name, out var g) ? g.AdvanceX : 0);
    }

    private void WriteType1(PdfFontEntry entry)
    {
        var font = entry.Font;
        string baseName = SubsetName(font.Name);
        entry.Dictionary[PdfName.Subtype] = PdfName.Get("Type1");
        entry.Dictionary[PdfName.Get("BaseFont")] = PdfName.Get(baseName);
        double scale = font.BaseMatrix.A * 1000; // glyph space to the 1/1000 text space units of Widths
        SimpleFontBasics(entry, name => font.Advance(name, _ip).X * scale);

        // the glyphs to embed: the used ones, .notdef and the parts of seac accented characters
        var glyphs = new SortedSet<string>(StringComparer.Ordinal) { ".notdef" };
        foreach (int code in entry.Used)
        {
            string name = entry.CodeNames[code];
            if (!font.HasGlyph(name, _ip)) continue;
            glyphs.Add(name);
            if (font.Outline(name, _ip) is { } outline)
            {
                if (outline.SeacBase != null) glyphs.Add(outline.SeacBase);
                if (outline.SeacAccent != null) glyphs.Add(outline.SeacAccent);
            }
        }
        var (program, length1, length2, length3) = Type1Writer.Write(font, glyphs, baseName, _ip);
        var file = new PdfStream(program);
        file[PdfName.Get("Length1")] = new PdfNumber(length1);
        file[PdfName.Get("Length2")] = new PdfNumber(length2);
        file[PdfName.Get("Length3")] = new PdfNumber(length3);
        var descriptor = Descriptor(baseName, font, scale);
        descriptor[PdfName.Get("FontFile")] = _document.AddObject(file);
        entry.Dictionary[PdfName.Get("FontDescriptor")] = _document.AddObject(descriptor);
    }

    private PdfDictionary Descriptor(string name, PsFont font, double scale)
    {
        var b = font.FontBBox;
        double llx = b[0] * scale, lly = b[1] * scale, urx = b[2] * scale, ury = b[3] * scale;
        return new PdfDictionary
        {
            [PdfName.Type] = PdfName.Get("FontDescriptor"),
            [PdfName.Get("FontName")] = PdfName.Get(name),
            [PdfName.Get("Flags")] = new PdfNumber(4), // symbolic: the encoding is given completely
            [PdfName.Get("FontBBox")] = PdfArray.FromNumbers(llx, lly, urx, ury),
            [PdfName.Get("ItalicAngle")] = new PdfNumber(0),
            [PdfName.Get("Ascent")] = new PdfNumber(Math.Round(ury)),
            [PdfName.Get("Descent")] = new PdfNumber(Math.Round(Math.Min(0, lly))),
            [PdfName.Get("CapHeight")] = new PdfNumber(Math.Round(ury)),
            [PdfName.Get("StemV")] = new PdfNumber(80),
        };
    }

    // ------------------------------------------------------------------ Type 42 as Type 0

    private void WriteType42(PdfFontEntry entry)
    {
        var font = entry.Font;
        var sfnt = font.Sfnt!;
        string baseName = SubsetName(font.Name);
        var program = TrueTypeSubsetter.Subset(sfnt, entry.Used);
        var file = new PdfStream(program);
        file[PdfName.Get("Length1")] = new PdfNumber(program.Length);
        double scale = 1000.0 / sfnt.UnitsPerEm;
        var descriptor = Descriptor(baseName, font, 1000);
        descriptor[PdfName.Get("FontBBox")] = PdfArray.FromNumbers(sfnt.XMin * scale, sfnt.YMin * scale, sfnt.XMax * scale, sfnt.YMax * scale);
        descriptor[PdfName.Get("Ascent")] = new PdfNumber(Math.Round(sfnt.Ascender * scale));
        descriptor[PdfName.Get("Descent")] = new PdfNumber(Math.Round(sfnt.Descender * scale));
        descriptor[PdfName.Get("CapHeight")] = new PdfNumber(Math.Round((sfnt.CapHeight != 0 ? sfnt.CapHeight : sfnt.Ascender) * scale));
        descriptor[PdfName.Get("FontFile2")] = _document.AddObject(file);
        var widths = new PdfArray();
        foreach (int gid in entry.Used)
        {
            widths.Add(new PdfNumber(gid));
            widths.Add(new PdfArray(new PdfNumber(Math.Round(sfnt.GetAdvanceWidth(gid) * scale, 3))));
        }
        var cidFont = new PdfDictionary
        {
            [PdfName.Type] = PdfName.Font,
            [PdfName.Subtype] = PdfName.Get("CIDFontType2"),
            [PdfName.Get("BaseFont")] = PdfName.Get(baseName),
            [PdfName.Get("CIDSystemInfo")] = new PdfDictionary
            {
                [PdfName.Get("Registry")] = new PdfString("Adobe"),
                [PdfName.Get("Ordering")] = new PdfString("Identity"),
                [PdfName.Get("Supplement")] = new PdfNumber(0),
            },
            [PdfName.Get("FontDescriptor")] = _document.AddObject(descriptor),
            [PdfName.Get("W")] = widths,
            [PdfName.Get("CIDToGIDMap")] = PdfName.Get("Identity"),
        };
        var d = entry.Dictionary;
        d[PdfName.Subtype] = PdfName.Get("Type0");
        d[PdfName.Get("BaseFont")] = PdfName.Get(baseName);
        d[PdfName.Get("Encoding")] = PdfName.Get("Identity-H");
        d[PdfName.Get("DescendantFonts")] = new PdfArray(_document.AddObject(cidFont));
        d[PdfName.Get("ToUnicode")] = ToUnicode(entry.Used.Select(g => (g, entry.GlyphNamesById.TryGetValue(g, out var n) ? n : "")), oneByte: false);
    }

    // ------------------------------------------------------------------ ToUnicode

    private PdfReference ToUnicode(IEnumerable<(int Code, string Name)> codes, bool oneByte)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append(oneByte ? "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" : "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var mapped = codes.Select(c => (c.Code, Text: GlyphNames.ToUnicode(c.Name))).Where(c => !string.IsNullOrEmpty(c.Text)).ToList();
        for (int i = 0; i < mapped.Count; i += 100)
        {
            var chunk = mapped.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (var (code, text) in chunk)
            {
                sb.Append('<').Append(code.ToString(oneByte ? "X2" : "X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (char ch in text!) sb.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return _document.AddObject(new PdfStream(Encoding.ASCII.GetBytes(sb.ToString())));
    }
}
