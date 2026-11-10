using MiPdf.Fonts;
using MiPdf.Fonts.TrueType;
using MiPdf.Graphics;
using MiPdf.PostScript.Fonts;
using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// Fonts (PLRM 5): font dictionaries and the font directory, definefont/findfont/makefont, eexec, composite fonts
/// with CMaps and CID-keyed fonts (CIDInit procset, composefont, StartData), and the glyph metrics of Type 3 fonts.
/// Fonts that the program does not define are substituted by the standard 14 fonts (referenced by name in the PDF).
/// </summary>
internal sealed partial class PsInterpreter
{
    /// <summary>The Type 3 glyphs being captured (BuildGlyph/BuildChar may show other Type 3 fonts).</summary>
    private readonly Stack<(Type3Glyph Glyph, PsContentWriter? Writer)> _type3 = new();

    private readonly Dictionary<string, PsObject> _standardFonts = new();

    private void RegisterFontOperators()
    {
        DefineValue("FontDirectory", PsObject.Dictionary(CategoryDict("Font", global: false, create: true)!));
        DefineValue("GlobalFontDirectory", PsObject.Dictionary(CategoryDict("Font", global: true, create: true)!));
        var previousFallback = FindResourceFallback;
        FindResourceFallback = (category, key) => category switch
        {
            "Font" when key.Type == PsType.Name => SubstituteFont(key.AsName.Text),
            "CMap" when key.Type == PsType.Name && key.AsName.Text is "Identity-H" or "Identity-V" => IdentityCMap(key.AsName.Text == "Identity-V"),
            _ => previousFallback?.Invoke(category, key),
        };

        Op("definefont", ip =>
        {
            var font = ip.Pop();
            var key = ip.Pop();
            if (font.Type != PsType.Dictionary) throw new PsError("typecheck");
            ip.Push(ip.DefineFont(key, font.AsDict));
        });
        Op("undefinefont", ip =>
        {
            var key = ip.NormalizeKey(ip.Pop());
            foreach (bool global in new[] { false, true })
            {
                var dict = ip.CategoryDict("Font", global, false);
                if (dict == null || !dict.Map.ContainsKey(key)) continue;
                ip.BeforeChange(dict);
                dict.Map.Remove(key);
            }
        });
        Op("findfont", ip => ip.Push(ip.FindFont(ip.Pop())));
        Op("scalefont", ip =>
        {
            double s = ip.PopReal();
            ip.Push(ip.MakeFont(ip.PopFontDict(), Matrix.Scale(s, s)));
        });
        Op("makefont", ip =>
        {
            var m = ip.PopMatrix();
            ip.Push(ip.MakeFont(ip.PopFontDict(), m));
        });
        Op("setfont", ip => ip.GState.Font = PsObject.Dictionary(ip.PopFontDict()));
        Op("currentfont", ip => ip.Push(ip.CurrentFontObject()));
        Op("rootfont", ip => ip.Push(ip.CurrentFontObject()));
        Op("selectfont", ip =>
        {
            var size = ip.Pop();
            var font = ip.FindFont(ip.Pop()).AsDict;
            var m = size.IsNumber ? Matrix.Scale(size.Number, size.Number) : ToMatrix(size);
            ip.GState.Font = ip.MakeFont(font, m);
        });
        Op("eexec", ip => ip.Eexec());
        Op("composefont", ip =>
        {
            var descendants = ip.PopArray();
            var cmap = ip.Pop();
            var key = ip.Pop();
            ip.Push(ip.ComposeFont(key, cmap, descendants.AsArray));
        });

        // glyph metrics inside BuildGlyph/BuildChar of Type 3 fonts
        Op("setcharwidth", ip =>
        {
            double wy = ip.PopReal(), wx = ip.PopReal();
            if (ip._type3.Count == 0) return;
            var (glyph, _) = ip._type3.Peek();
            glyph.AdvanceX = wx;
            glyph.AdvanceY = wy;
            glyph.Box = null;
        });
        Op("setcachedevice", ip =>
        {
            double ury = ip.PopReal(), urx = ip.PopReal(), lly = ip.PopReal(), llx = ip.PopReal(), wy = ip.PopReal(), wx = ip.PopReal();
            ip.SetCacheDevice(wx, wy, llx, lly, urx, ury);
        });
        Op("setcachedevice2", ip =>
        {
            for (int i = 0; i < 4; i++) ip.PopReal(); // w1x w1y vx vy: vertical metrics are not used
            double ury = ip.PopReal(), urx = ip.PopReal(), lly = ip.PopReal(), llx = ip.PopReal(), wy = ip.PopReal(), wx = ip.PopReal();
            ip.SetCacheDevice(wx, wy, llx, lly, urx, ury);
        });

        RegisterTextOperators();
        RegisterCidInit();
    }

    private void SetCacheDevice(double wx, double wy, double llx, double lly, double urx, double ury)
    {
        if (_type3.Count == 0) return;
        var (glyph, writer) = _type3.Peek();
        glyph.AdvanceX = wx;
        glyph.AdvanceY = wy;
        glyph.Box = new[] { llx, lly, urx, ury };
        // an uncolored glyph: its color is the text color, set when it is shown
        if (writer != null) writer.NoColor = true;
    }

    // ------------------------------------------------------------------ font dictionaries

    private PsObject CurrentFontObject()
    {
        if (GState.Font.Type == PsType.Dictionary) return GState.Font;
        // no font set: use Courier like Ghostscript
        return MakeFont(FindFont(Key("Courier")).AsDict, Matrix.Identity);
    }

    private PsDict PopFontDict()
    {
        var d = PopDict();
        if (!d.Map.TryGetValue(Key("FID"), out var fid) || fid.Type != PsType.FontId) throw new PsError("invalidfont");
        return d;
    }

    public (PsDict Dict, PsFont Font) CurrentFont()
    {
        var d = CurrentFontObject().AsDict;
        return (d, FontOf(d));
    }

    /// <summary>The font behind a font dictionary (its FID).</summary>
    private PsFont FontOf(PsDict dict) =>
        dict.Map.TryGetValue(Key("FID"), out var fid) && fid.Type == PsType.FontId && fid.Ref is PsFont font ? font : throw new PsError("invalidfont");

    private Matrix FontMatrixOf(PsDict dict) =>
        dict.Map.TryGetValue(Key("FontMatrix"), out var m) ? ToMatrix(m) : Matrix.Identity;

    /// <summary><c>definefont</c>: checks the dictionary, builds the font behind it and registers it.</summary>
    public PsObject DefineFont(in PsObject key, PsDict dict, string category = "Font")
    {
        PsFont font;
        if (dict.Map.TryGetValue(Key("FID"), out var existing) && existing.Type == PsType.FontId && existing.Ref is PsFont f) font = f;
        else
        {
            font = BuildFont(dict, key);
            // the FID is added even to a read-only dictionary (as interpreters do)
            BeforeChange(dict);
            dict.Map[Key("FID")] = PsObject.Other(PsType.FontId, font);
        }
        if (dict.Access == PsDictAccess.Unlimited)
        {
            BeforeChange(dict);
            dict.Access = PsDictAccess.ReadOnly;
        }
        var fontObject = PsObject.Dictionary(dict);
        DictPut(CategoryDict(category, dict.Global, create: true)!, key, fontObject);
        return fontObject;
    }

    private PsFont BuildFont(PsDict dict, in PsObject key)
    {
        PsObject Get(string k) => dict.Map.TryGetValue(Key(k), out var v) ? v : PsObject.Null;
        int Int(string k, int fallback) => Get(k) is { Type: PsType.Integer } v ? (int)Math.Clamp(v.Int, int.MinValue, int.MaxValue) : fallback;
        string name = Get("FontName") is { Type: PsType.Name } n ? n.AsName.Text
            : Get("CIDFontName") is { Type: PsType.Name } cn ? cn.AsName.Text
            : key.Type == PsType.Name ? key.AsName.Text : key.Type == PsType.String ? key.AsString.Text : "Font";
        var fontMatrix = Get("FontMatrix").IsArray ? ToMatrix(Get("FontMatrix")) : Matrix.Identity;
        var bbox = new double[] { 0, 0, 1000, 1000 };
        if (Get("FontBBox") is { } b && b.IsArray && b.AsArray.Length == 4 && b.AsArray.Span.ToArray().All(x => x.IsNumber))
            bbox = b.AsArray.Span.ToArray().Select(x => x.Number).ToArray();
        int paintType = Int("PaintType", 0);
        double strokeWidth = Get("StrokeWidth") is { } sw && sw.IsNumber ? sw.Number : 0;
        int cidType = Get("CIDFontType") is { Type: PsType.Integer } ct ? (int)ct.Int : Int("FontType", -1) switch { 9 => 0, 10 => 1, 11 => 2, _ => -1 };
        if (cidType >= 0) return BuildCidFont(dict, name, cidType, fontMatrix, bbox, Get, Int);
        if (Get("FontType").Type != PsType.Integer || !Get("FontMatrix").IsArray) throw new PsError("invalidfont");
        switch (Get("FontType").Int)
        {
            case 0:
                return BuildCompositeFont(dict, name, fontMatrix, Get, Int);
            case 1:
            {
                if (Get("miPDFlib.StandardFont") is { Type: PsType.Name } standard)
                    return new PsFont
                    {
                        Kind = PsFontKind.Standard, Name = name, Dict = dict, BaseMatrix = fontMatrix, FontBBox = bbox,
                        StandardName = standard.AsName.Text, PaintType = paintType, StrokeWidth = strokeWidth,
                    };
                var charStrings = Get("CharStrings");
                if (charStrings.Type != PsType.Dictionary || !Get("Encoding").IsArray && Get("Encoding").Type != PsType.Name)
                    throw new PsError("invalidfont");
                var priv = Get("Private");
                PsDict? privateDict = priv.Type == PsType.Dictionary ? priv.AsDict : null;
                PsArray? subrs = privateDict != null && privateDict.Map.TryGetValue(Key("Subrs"), out var s) && s.IsArray ? s.AsArray : null;
                int lenIV = privateDict != null && privateDict.Map.TryGetValue(Key("lenIV"), out var l) && l.Type == PsType.Integer ? (int)l.Int : 4;
                return new PsFont
                {
                    Kind = PsFontKind.Type1, Name = name, Dict = dict, BaseMatrix = fontMatrix, FontBBox = bbox,
                    CharStrings = charStrings.AsDict, Private = privateDict, Subrs = subrs, LenIV = lenIV,
                    Metrics = Get("Metrics") is { Type: PsType.Dictionary } m ? m.AsDict : null, PaintType = paintType, StrokeWidth = strokeWidth,
                };
            }
            case 3:
            {
                bool buildGlyph = Get("BuildGlyph").IsProcedure;
                if (!buildGlyph && !Get("BuildChar").IsProcedure) throw new PsError("invalidfont");
                if (!Get("Encoding").IsArray) throw new PsError("invalidfont");
                return new PsFont
                {
                    Kind = PsFontKind.Type3, Name = name, Dict = dict, BaseMatrix = fontMatrix, FontBBox = bbox, KeyedByCode = !buildGlyph,
                };
            }
            case 42:
            {
                var charStrings = Get("CharStrings");
                if (!Get("sfnts").IsArray || charStrings.Type != PsType.Dictionary) throw new PsError("invalidfont");
                return new PsFont
                {
                    Kind = PsFontKind.Type42, Name = name, Dict = dict, BaseMatrix = fontMatrix, FontBBox = bbox,
                    CharStrings = charStrings.AsDict, Sfnt = Sfnt(Get("sfnts")), PaintType = paintType, StrokeWidth = strokeWidth,
                };
            }
            default:
                throw new PsError("invalidfont"); // FontType 2 (CFF) is not supported
        }
    }

    private static TrueTypeFont Sfnt(in PsObject sfnts)
    {
        if (!sfnts.IsArray) throw new PsError("invalidfont");
        try
        {
            return new TrueTypeFont(PsFont.SfntData(sfnts.AsArray));
        }
        catch (Exception ex) when (ex is not PsError)
        {
            throw new PsError("invalidfont"); // damaged TrueType data
        }
    }

    /// <summary>A Type 0 font: FMapType, the font numbers of Encoding, the descendants in FDepVector, a CMap for FMapType 9.</summary>
    private PsFont BuildCompositeFont(PsDict dict, string name, Matrix fontMatrix, Func<string, PsObject> get, Func<string, int, int> integer)
    {
        int fmapType = integer("FMapType", -1);
        if (fmapType is not (2 or 3 or 4 or 5 or 8 or 9)) throw new PsError("invalidfont");
        var vector = get("FDepVector");
        if (!vector.IsArray || vector.AsArray.Length == 0) throw new PsError("invalidfont");
        var descendants = vector.AsArray.Span.ToArray();
        foreach (var d in descendants)
        {
            if (d.Type != PsType.Dictionary) throw new PsError("invalidfont");
            var font = FontOf(d.AsDict);
            if (font.Kind == PsFontKind.Composite) throw new PsError("invalidfont"); // nested composite fonts are not supported
        }
        int[] encoding = get("Encoding").IsArray
            ? get("Encoding").AsArray.Span.ToArray().Select(e => e.Type == PsType.Integer ? (int)e.Int : -1).ToArray()
            : Enumerable.Range(0, descendants.Length).ToArray();
        PsCMap? cmap = null;
        if (fmapType == 9)
        {
            var c = get("CMap");
            cmap = c.Type == PsType.Dictionary ? CMapOf(c.AsDict) : null;
            if (cmap == null) throw new PsError("invalidfont");
        }
        return new PsFont
        {
            Kind = PsFontKind.Composite, Name = name, Dict = dict, BaseMatrix = fontMatrix, FMapType = fmapType,
            EncodingMap = encoding, Descendants = descendants, CMap = cmap,
            EscChar = integer("EscChar", 255), ShiftIn = integer("ShiftIn", 15), ShiftOut = integer("ShiftOut", 14),
        };
    }

    /// <summary>CIDFontType 0 (Type 1 charstrings), 1 (BuildGlyph) and 2 (TrueType).</summary>
    private PsFont BuildCidFont(PsDict dict, string name, int cidType, Matrix fontMatrix, double[] bbox, Func<string, PsObject> get, Func<string, int, int> integer)
    {
        int cidCount = integer("CIDCount", 65536);
        switch (cidType)
        {
            case 0:
            {
                var fdArray = get("FDArray");
                if (!fdArray.IsArray || fdArray.AsArray.Length == 0) throw new PsError("invalidfont");
                byte[] glyphData = GlyphDataBytes(get("GlyphData"));
                var fdFonts = new List<PsFont>();
                int index = 0;
                foreach (var fd in fdArray.AsArray.Span.ToArray())
                {
                    if (fd.Type != PsType.Dictionary) throw new PsError("invalidfont");
                    var fdDict = fd.AsDict;
                    var priv = fdDict.Map.TryGetValue(Key("Private"), out var p) && p.Type == PsType.Dictionary ? p.AsDict : NewDict(0);
                    int Private(string k, int fallback) =>
                        priv.Map.TryGetValue(Key(k), out var v) && v.Type == PsType.Integer ? (int)Math.Clamp(v.Int, int.MinValue, int.MaxValue) : fallback;
                    // subroutines: in the private dictionary, or in GlyphData through the subroutine map
                    var subrs = new List<byte[]>();
                    if (priv.Map.TryGetValue(Key("Subrs"), out var subrArray) && subrArray.IsArray)
                        subrs.AddRange(subrArray.AsArray.Span.ToArray().Select(x => x.Type == PsType.String ? x.AsString.ToArray() : Array.Empty<byte>()));
                    else
                    {
                        int offset = Private("SubrMapOffset", -1), sdBytes = Private("SDBytes", 0), count = Private("SubrCount", 0);
                        for (int i = 0; i < count && offset >= 0 && sdBytes > 0; i++)
                        {
                            long at = offset + (long)i * sdBytes;
                            if (at + 2L * sdBytes > glyphData.Length) break;
                            long start = PsFont.ReadNumber(glyphData, (int)at, sdBytes), end = PsFont.ReadNumber(glyphData, (int)(at + sdBytes), sdBytes);
                            subrs.Add(end > start && end <= glyphData.Length ? glyphData.AsSpan((int)start, (int)(end - start)).ToArray() : Array.Empty<byte>());
                        }
                    }
                    var fdMatrix = fdDict.Map.TryGetValue(Key("FontMatrix"), out var fm) && fm.IsArray ? ToMatrix(fm) : new Matrix(0.001, 0, 0, 0.001, 0, 0);
                    fdFonts.Add(new PsFont
                    {
                        Kind = PsFontKind.Type1, Name = name + "-FD" + index++, Dict = fdDict, BaseMatrix = fdMatrix, FontBBox = bbox,
                        Private = priv, LenIV = Private("lenIV", -1), RawSubrList = subrs, RawCharStrings = new Dictionary<string, byte[]>(),
                    });
                }
                return new PsFont
                {
                    Kind = PsFontKind.Cid0, Name = name, Dict = dict, BaseMatrix = fontMatrix, FontBBox = bbox, CidCount = cidCount,
                    GlyphData = glyphData, GlyphDirectory = get("GlyphDirectory"), CidMapOffset = integer("CIDMapOffset", 0),
                    FdBytes = integer("FDBytes", 1), GdBytes = integer("GDBytes", 2), FdFonts = fdFonts.ToArray(),
                };
            }
            case 1:
                if (!get("BuildGlyph").IsProcedure) throw new PsError("invalidfont");
                return new PsFont { Kind = PsFontKind.Cid1, Name = name, Dict = dict, BaseMatrix = fontMatrix, FontBBox = bbox, CidCount = cidCount };
            case 2:
            {
                var sfnt = Sfnt(get("sfnts"));
                int gdBytes = integer("GDBytes", 2);
                var cidMap = get("CIDMap");
                Func<int, int> toGid;
                if (cidMap.Type == PsType.Integer)
                {
                    long add = cidMap.Int;
                    toGid = cid => (int)(cid + add);
                }
                else if (cidMap.Type == PsType.Dictionary)
                {
                    var map = cidMap.AsDict;
                    toGid = cid => map.Map.TryGetValue(PsObject.Integer(cid), out var g) && g.Type == PsType.Integer ? (int)g.Int : 0;
                }
                else if (cidMap.Type == PsType.String || cidMap.IsArray)
                {
                    var bytes = GlyphDataBytes(cidMap);
                    toGid = cid => (long)cid * gdBytes + gdBytes <= bytes.Length ? (int)PsFont.ReadNumber(bytes, cid * gdBytes, gdBytes) : 0;
                }
                else toGid = cid => cid;
                return new PsFont
                {
                    Kind = PsFontKind.Cid2, Name = name, Dict = dict, BaseMatrix = fontMatrix, FontBBox = bbox, CidCount = cidCount,
                    Sfnt = sfnt, CidToGid = toGid,
                };
            }
            default:
                throw new PsError("invalidfont");
        }
    }

    /// <summary>GlyphData (or a CIDMap): a string or an array of strings, concatenated.</summary>
    private static byte[] GlyphDataBytes(in PsObject data)
    {
        if (data.Type == PsType.String) return data.AsString.ToArray();
        if (!data.IsArray) return Array.Empty<byte>();
        var all = new List<byte>();
        foreach (var item in data.AsArray.Span)
            if (item.Type == PsType.String) all.AddRange(item.AsString.Span.ToArray());
        return all.ToArray();
    }

    private PsObject MakeFont(PsDict font, Matrix m)
    {
        var copy = NewDict(font.Count + 1);
        foreach (var (k, v) in font.Map) copy.Map[k] = v;
        copy.Map[Key("FontMatrix")] = MatrixObject(FontMatrixOf(font).Multiply(m));
        copy.Access = PsDictAccess.ReadOnly;
        return PsObject.Dictionary(copy);
    }

    private PsObject FindFont(in PsObject key)
    {
        var found = FindResource("Font", key);
        if (found is { Type: PsType.Dictionary } font) return font;
        throw new PsError("invalidfont");
    }

    /// <summary>composefont: a Type 0 font with FMapType 9 from a CMap and CIDFonts (or base fonts).</summary>
    private PsObject ComposeFont(in PsObject key, in PsObject cmap, PsArray descendants)
    {
        var cmapDict = cmap.Type == PsType.Dictionary ? cmap : FindResource("CMap", cmap) ?? throw new PsError("undefinedresource");
        var fonts = new PsObject[descendants.Length];
        for (int i = 0; i < fonts.Length; i++)
        {
            var d = descendants[i];
            if (d.Type == PsType.Dictionary) fonts[i] = d;
            else fonts[i] = FindResource("CIDFont", d) ?? FindFont(d);
        }
        var dict = NewDict(8);
        dict.Map[Key("FontType")] = PsObject.Integer(0);
        dict.Map[Key("FMapType")] = PsObject.Integer(9);
        dict.Map[Key("FontMatrix")] = MatrixObject(Matrix.Identity);
        dict.Map[Key("FDepVector")] = NewArray(fonts);
        dict.Map[Key("Encoding")] = NewArray(Enumerable.Range(0, fonts.Length).Select(i => PsObject.Integer(i)).ToArray());
        dict.Map[Key("CMap")] = cmapDict;
        if (key.Type == PsType.Name) dict.Map[Key("FontName")] = key;
        return DefineFont(key, dict);
    }

    /// <summary>A font the program asks for but does not define: the closest of the standard 14 fonts.</summary>
    private PsObject? SubstituteFont(string requested)
    {
        string standard = StandardFontFor(requested);
        if (!_standardFonts.TryGetValue(standard, out var font))
        {
            bool global = GlobalMode;
            GlobalMode = true;
            var info = StandardFontMetrics.Get(standard);
            var d = NewDict(12);
            d.Map[Key("FontType")] = PsObject.Integer(1);
            d.Map[Key("FontName")] = PsObject.Name(Name(standard));
            d.Map[Key("FontMatrix")] = NewArray(new[] { 0.001f, 0, 0, 0.001f, 0, 0 }.Select(PsObject.RealValue).ToArray()).WithAttributes(PsAttributes.ReadOnly);
            d.Map[Key("FontBBox")] = NewArray(info.BBox.Select(v => PsObject.RealValue((float)v)).ToArray()).WithAttributes(PsAttributes.ReadOnly);
            d.Map[Key("PaintType")] = PsObject.Integer(0);
            var encoding = standard switch
            {
                "Symbol" => EncodingArray(PsEncodings.Symbol),
                "ZapfDingbats" => EncodingArray(PsEncodings.Dingbats),
                _ => SystemDict.Map[Key("StandardEncoding")],
            };
            d.Map[Key("Encoding")] = encoding;
            // the glyph names (programs test them with known)
            var charStrings = NewDict(256);
            var empty = NewString(0);
            foreach (var name in encoding.AsArray.Span.ToArray().Concat(SystemDict.Map[Key("ISOLatin1Encoding")].AsArray.Span.ToArray()))
                if (name.Type == PsType.Name) charStrings.Map[name] = empty;
            d.Map[Key("CharStrings")] = PsObject.Dictionary(charStrings);
            d.Map[Key("miPDFlib.StandardFont")] = PsObject.Name(Name(standard));
            font = DefineFont(Key(standard), d);
            _standardFonts[standard] = font;
            GlobalMode = global;
        }
        if (requested != standard)
        {
            // the requested name refers to the substitute from now on
            bool global = GlobalMode;
            GlobalMode = true;
            DictPut(CategoryDict("Font", true, create: true)!, Key(requested), font);
            GlobalMode = global;
            DiagnosticSubstitution?.Invoke(requested, standard);
        }
        return font;
    }

    /// <summary>Reports a substituted font (requested, substitute).</summary>
    public Action<string, string>? DiagnosticSubstitution;

    /// <summary>The standard 14 font that best replaces a font name (family, weight and slant from the name).</summary>
    internal static string StandardFontFor(string name)
    {
        if (StandardFontMetrics.IsStandardFont(name)) return name;
        string n = name.ToLowerInvariant();
        if (n.Contains("dingbat") || n.Contains("zapfd")) return "ZapfDingbats";
        if (n.Contains("symbol")) return "Symbol";
        bool bold = n.Contains("bold") || n.Contains("black") || n.Contains("heavy") || n.Contains("demi") || n.Contains("semibold");
        bool italic = n.Contains("italic") || n.Contains("oblique") || n.Contains("slant") || n.Contains("kursiv") || n.EndsWith("-it");
        string family;
        if (n.Contains("courier") || n.Contains("mono") || n.Contains("typewriter") || n.Contains("consol") || n.Contains("fixed")) family = "Courier";
        else if ((n.Contains("times") || n.Contains("roman") || n.Contains("serif") || n.Contains("palatino") || n.Contains("bookman")
                  || n.Contains("century") || n.Contains("garamond") || n.Contains("georgia") || n.Contains("schoolbook")
                  || n.Contains("minion") || n.Contains("cambria") || n.Contains("baskerville")) && !n.Contains("sans"))
            family = "Times";
        else family = "Helvetica";
        return (family, bold, italic) switch
        {
            ("Times", false, false) => "Times-Roman",
            ("Times", true, false) => "Times-Bold",
            ("Times", false, true) => "Times-Italic",
            ("Times", true, true) => "Times-BoldItalic",
            (_, false, false) => family,
            (_, true, false) => family + "-Bold",
            (_, false, true) => family + "-Oblique",
            _ => family + "-BoldOblique",
        };
    }

    // ------------------------------------------------------------------ CMaps and the CIDInit procset

    private readonly Dictionary<bool, PsObject> _identityCMaps = new();

    private PsObject IdentityCMap(bool vertical)
    {
        if (_identityCMaps.TryGetValue(vertical, out var existing)) return existing;
        bool global = GlobalMode;
        GlobalMode = true;
        var d = NewDict(8);
        var cmap = PsCMap.Identity(vertical);
        d.Map[Key("CMapName")] = PsObject.Name(Name(cmap.Name));
        d.Map[Key("WMode")] = PsObject.Integer(cmap.WMode);
        d.Map[Key("CMapType")] = PsObject.Integer(1);
        d.Map[Key("miPDFlib.CMap")] = PsObject.Other(PsType.FontId, cmap);
        d.Access = PsDictAccess.ReadOnly;
        GlobalMode = global;
        var result = PsObject.Dictionary(d);
        DictPut(CategoryDict("CMap", true, create: true)!, Key(cmap.Name), result);
        _identityCMaps[vertical] = result;
        return result;
    }

    private PsCMap? CMapOf(PsDict dict) =>
        dict.Map.TryGetValue(Key("miPDFlib.CMap"), out var c) && c.Ref is PsCMap cmap ? cmap : null;

    /// <summary>The CMap being defined: in the dictionary that begincmap was executed in.</summary>
    private PsCMap CurrentCMap()
    {
        for (int i = DictStack.Count - 1; i >= 0; i--)
            if (CMapOf(DictStack[i]) is { } cmap) return cmap;
        throw new PsError("undefined");
    }

    /// <summary>The CIDInit procset (Adobe Technical Notes 5014 and 5099): CMap definition operators and StartData.</summary>
    private void RegisterCidInit()
    {
        bool global = GlobalMode;
        GlobalMode = true;
        var procset = NewDict(32);
        void Define(string name, Action<PsInterpreter> run) => procset.Map[Key(name)] = PsObject.Operator(new PsOperator(Name(name), run));
        Define("begincmap", ip =>
        {
            var d = ip.CurrentDict;
            ip.BeforeChange(d);
            d.Map[ip.Key("miPDFlib.CMap")] = PsObject.Other(PsType.FontId, new PsCMap());
        });
        Define("endcmap", ip =>
        {
            var cmap = ip.CurrentCMap();
            var d = ip.CurrentDict;
            if (d.Map.TryGetValue(ip.Key("CMapName"), out var n) && n.Type == PsType.Name) cmap.Name = n.AsName.Text;
            if (d.Map.TryGetValue(ip.Key("WMode"), out var w) && w.Type == PsType.Integer) cmap.WMode = (int)w.Int;
        });
        Define("usecmap", ip =>
        {
            var name = ip.Pop();
            var parent = ip.FindResource("CMap", name) ?? throw new PsError("undefinedresource");
            if (ip.CMapOf(parent.AsDict) is { } other) ip.CurrentCMap().Use(other);
        });
        Define("usefont", ip => ip.CurrentCMap().CurrentFont = (int)ip.PopInt());
        foreach (string block in new[] { "codespacerange", "cidrange", "cidchar", "notdefrange", "notdefchar", "bfrange", "bfchar" })
        {
            string kind = block;
            Define("begin" + kind, ip =>
            {
                ip.PopInt();
                ip.Push(PsObject.Mark);
            });
            Define("end" + kind, ip =>
            {
                int n = ip.Ops.CountToMark();
                var items = new PsObject[n];
                for (int i = n - 1; i >= 0; i--) items[i] = ip.Pop();
                ip.Pop();
                ip.CMapBlock(kind, items);
            });
        }
        foreach (string ignored in new[] { "beginrearrangedfont", "endrearrangedfont", "beginusematrix", "endusematrix" })
            Define(ignored, _ => { });
        Define("StartData", ip => ip.StartData());
        GlobalMode = global;
        DictPut(CategoryDict("ProcSet", true, create: true)!, Key("CIDInit"), PsObject.Dictionary(procset));
    }

    private void CMapBlock(string kind, PsObject[] items)
    {
        var cmap = CurrentCMap();
        static byte[] Bytes(in PsObject o) => o.Type == PsType.String ? o.AsString.ToArray() : throw new PsError("typecheck");
        static int Cid(in PsObject o) => o.Type == PsType.Integer ? (int)Math.Clamp(o.Int, 0, int.MaxValue) : throw new PsError("typecheck");
        switch (kind)
        {
            case "codespacerange":
                for (int i = 0; i + 1 < items.Length; i += 2) cmap.AddCodeSpace(Bytes(items[i]), Bytes(items[i + 1]));
                break;
            case "cidrange":
                for (int i = 0; i + 2 < items.Length; i += 3) cmap.AddRange(Bytes(items[i]), Bytes(items[i + 1]), Cid(items[i + 2]));
                break;
            case "cidchar":
                for (int i = 0; i + 1 < items.Length; i += 2) cmap.AddChar(Bytes(items[i]), Cid(items[i + 1]));
                break;
            case "notdefrange":
                for (int i = 0; i + 2 < items.Length; i += 3) cmap.AddNotdef(Bytes(items[i]), Bytes(items[i + 1]), Cid(items[i + 2]));
                break;
            case "notdefchar":
                for (int i = 0; i + 1 < items.Length; i += 2) cmap.AddNotdef(Bytes(items[i]), Bytes(items[i]), Cid(items[i + 1]));
                break;
            // bfrange and bfchar belong to ToUnicode CMaps: not needed for showing text
        }
    }

    /// <summary>
    /// StartData: reads the binary (or hexadecimal) glyph data of a CIDFont from the current file, stores it as
    /// GlyphData and defines the CIDFont; then the font dictionary and the procset are ended (Technical Note 5014).
    /// </summary>
    private void StartData()
    {
        long count = PopInt();
        var kind = PopString().AsString.Text;
        if (count < 0 || count > Limits.MaxStringLength) throw new PsError("limitcheck");
        PsFile? file = null;
        for (int i = Exec.Count - 1; i >= 0 && file == null; i--)
            if (Exec[i] is FileFrame f) file = f.File;
        if (file == null) throw new PsError("ioerror");
        var data = new byte[count];
        int n = 0;
        if (kind == "Hex")
        {
            int high = -1;
            while (n < count)
            {
                int c = file.ReadByte();
                if (c < 0) break;
                int digit = PsAsciiHexDecode.HexDigit(c);
                if (digit < 0) continue;
                if (high < 0) high = digit;
                else
                {
                    data[n++] = (byte)(high << 4 | digit);
                    high = -1;
                }
            }
        }
        else
        {
            while (n < count)
            {
                int c = file.ReadByte();
                if (c < 0) break;
                data[n++] = (byte)c;
            }
        }
        var dict = CurrentDict;
        DictPut(dict, Key("GlyphData"), NewString(data));
        var name = dict.Map.TryGetValue(Key("CIDFontName"), out var cn) ? cn : Key("CIDFont");
        DefineFont(name, dict, "CIDFont");
        for (int i = 0; i < 2 && DictStack.Count > 3; i++) DictStack.RemoveAt(DictStack.Count - 1);
    }

    // ------------------------------------------------------------------ eexec

    /// <summary><c>eexec</c>: decrypts the file (or string) and executes it with systemdict on the dictionary stack.</summary>
    private void Eexec()
    {
        var source = Pop();
        PsFile file = source.Type switch
        {
            PsType.File => source.AsFile,
            PsType.String => new PsMemoryFile(source.AsString.ToArray()),
            _ => throw new PsError("typecheck"),
        };
        var decrypted = new PsEexecFile(file);
        if (DictStack.Count >= Limits.MaxDictStack) throw new PsError("dictstackoverflow");
        DictStack.Add(SystemDict);
        PushFrame(new ContinuationFrame(ip =>
        {
            int i = ip.DictStack.LastIndexOf(ip.SystemDict);
            if (i >= 3) ip.DictStack.RemoveAt(i);
        }));
        PushFrame(new FileFrame(decrypted));
    }
}

/// <summary>eexec decryption of a file: binary or hexadecimal (decided by the first four characters).</summary>
internal sealed class PsEexecFile : PsDecodeFilter
{
    private int _r = Type1Charstrings.EexecKey;
    private bool? _hex;
    private int _skip = 4;
    private readonly Queue<int> _lookahead = new();

    public PsEexecFile(PsFile source) : base(source) { }

    private int NextCipher()
    {
        if (_hex == null)
        {
            // skip whitespace before the data, then look at four characters
            int c;
            do c = Source.ReadByte(); while (c is ' ' or '\t' or '\r' or '\n');
            if (c >= 0) _lookahead.Enqueue(c);
            for (int i = 0; i < 3; i++)
            {
                int d = Source.ReadByte();
                if (d >= 0) _lookahead.Enqueue(d);
            }
            _hex = _lookahead.Count == 4 && _lookahead.All(b => PsAsciiHexDecode.HexDigit(b) >= 0);
        }
        int Raw() => _lookahead.Count > 0 ? _lookahead.Dequeue() : Source.ReadByte();
        if (_hex == false) return Raw();
        int high = -1;
        while (true)
        {
            int c = Raw();
            if (c < 0) return -1;
            int digit = PsAsciiHexDecode.HexDigit(c);
            if (digit < 0)
            {
                if (PsScanner.IsWhitespace(c)) continue;
                return -1; // the end of the hexadecimal data
            }
            if (high < 0) high = digit;
            else return (high << 4) | digit;
        }
    }

    protected override int Decode()
    {
        while (true)
        {
            int c = NextCipher();
            if (c < 0) return -1;
            int p = c ^ (_r >> 8);
            _r = ((c + _r) * 52845 + 22719) & 0xFFFF;
            if (_skip > 0)
            {
                _skip--;
                continue;
            }
            return p;
        }
    }
}
