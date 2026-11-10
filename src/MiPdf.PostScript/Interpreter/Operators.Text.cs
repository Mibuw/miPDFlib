using System.Text;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.PostScript.Fonts;
using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// The show operators, stringwidth, charpath, cshow and kshow (PLRM 5.2, 5.10). A string becomes a list of glyphs: for
/// composite fonts each with its descendant (or, for CIDFontType 0, its FDArray font), the transformation from glyph
/// to user space and the advance. Painted glyphs are written as text in the PDF font made for their font.
/// </summary>
internal sealed partial class PsInterpreter
{
    /// <summary>A glyph to show.</summary>
    private sealed class GlyphItem
    {
        public int Code;                // the character code in the font that has the glyph
        public int ShowCode;            // the code from the string (for widthshow and cshow)
        public int Cid = -1;            // CID-keyed fonts
        public string Name = ".notdef"; // glyph name (simple fonts), key of the glyph otherwise
        public PsDict Dict = null!;     // the font dictionary (of the descendant)
        public PsFont Font = null!;     // the font that has the glyph (CIDFontType 0: the FDArray font)
        public Matrix GlyphToUser;
        public Matrix TextFromGlyph;    // glyph space to the text space of the PDF font
        public (double X, double Y) Advance; // in glyph space
        public string? Unicode;         // the text of the glyph when the codes are Unicode
        public bool ByName;             // glyphshow
    }

    private void RegisterTextOperators()
    {
        Op("show", ip => ip.ShowItems(ip.Glyphs(ip.PopString().AsString.ToArray()), null));
        Op("ashow", ip =>
        {
            var s = ip.PopString();
            double ay = ip.PopReal(), ax = ip.PopReal();
            ip.ShowItems(ip.Glyphs(s.AsString.ToArray()), (_, _, w) => (w.X + ax, w.Y + ay));
        });
        Op("widthshow", ip =>
        {
            var s = ip.PopString();
            long c = ip.PopInt();
            double cy = ip.PopReal(), cx = ip.PopReal();
            ip.ShowItems(ip.Glyphs(s.AsString.ToArray()), (_, code, w) => code == c ? (w.X + cx, w.Y + cy) : w);
        });
        Op("awidthshow", ip =>
        {
            var s = ip.PopString();
            double ay = ip.PopReal(), ax = ip.PopReal();
            long c = ip.PopInt();
            double cy = ip.PopReal(), cx = ip.PopReal();
            ip.ShowItems(ip.Glyphs(s.AsString.ToArray()), (_, code, w) => code == c ? (w.X + cx + ax, w.Y + cy + ay) : (w.X + ax, w.Y + ay));
        });
        Op("xshow", ip => ip.ShowWithDisplacements(xy: (true, false)));
        Op("yshow", ip => ip.ShowWithDisplacements(xy: (false, true)));
        Op("xyshow", ip => ip.ShowWithDisplacements(xy: (true, true)));
        Op("kshow", ip =>
        {
            var s = ip.PopString();
            var proc = ip.PopProc();
            if (ip.CurrentFont().Font.Kind == PsFontKind.Composite) throw new PsError("invalidfont");
            ip.PushFrame(new KshowFrame(s.AsString.ToArray(), proc));
        });
        Op("cshow", ip =>
        {
            var s = ip.PopString();
            var proc = ip.PopProc();
            ip.PushFrame(new CshowFrame(ip.Glyphs(s.AsString.ToArray()), proc));
        });
        Op("glyphshow", ip =>
        {
            var name = ip.Pop();
            var (dict, font) = ip.CurrentFont();
            if (font.Kind == PsFontKind.Composite) throw new PsError("invalidfont");
            if (name.Type != PsType.Name) throw new PsError("typecheck");
            var item = ip.SimpleGlyph(dict, font, ip.FontMatrixOf(dict), -1, name.AsName.Text);
            item.ByName = true;
            ip.ShowItems(new List<GlyphItem> { item }, null);
        });
        Op("stringwidth", ip =>
        {
            double wx = 0, wy = 0;
            foreach (var item in ip.Glyphs(ip.PopString().AsString.ToArray()))
            {
                var (ax, ay) = item.GlyphToUser.TransformDistance(item.Advance.X, item.Advance.Y);
                wx += ax;
                wy += ay;
            }
            ip.PushReal(wx);
            ip.PushReal(wy);
        });
        Op("charpath", ip =>
        {
            ip.PopBool();
            ip.CharPath(ip.Glyphs(ip.PopString().AsString.ToArray()));
        });
    }

    // ------------------------------------------------------------------ glyphs of a string

    /// <summary>The glyphs of a string in the current font.</summary>
    private List<GlyphItem> Glyphs(byte[] s)
    {
        var (dict, font) = CurrentFont();
        var fm = FontMatrixOf(dict);
        if (font.Kind == PsFontKind.Composite) return CompositeGlyphs(font, fm, s);
        var items = new List<GlyphItem>(s.Length);
        foreach (byte code in s)
        {
            CountOperation();
            items.Add(SimpleGlyph(dict, font, fm, code, null));
        }
        return items;
    }

    /// <summary>A glyph of a base font (by code, or by name for glyphshow).</summary>
    private GlyphItem SimpleGlyph(PsDict dict, PsFont font, Matrix fm, int code, string? name)
    {
        name ??= GlyphName(dict, code);
        var item = new GlyphItem { Code = code, ShowCode = code, Name = name, Dict = dict, Font = font, GlyphToUser = fm };
        switch (font.Kind)
        {
            case PsFontKind.Type3:
            {
                var glyph = Type3Glyph(dict, font, font.KeyedByCode ? PsFont.CodeKey(code) : name, PsObject.Name(Name(name)), code);
                item.Advance = (glyph.AdvanceX, glyph.AdvanceY);
                item.TextFromGlyph = font.BaseMatrix;
                break;
            }
            case PsFontKind.Type42:
                item.Advance = font.Advance(name, this);
                item.TextFromGlyph = Matrix.Identity; // TrueType glyph space is the text space (1 em)
                break;
            default:
                item.Advance = font.Advance(name, this);
                item.TextFromGlyph = font.BaseMatrix;
                break;
        }
        return item;
    }

    /// <summary>The glyphs of a string in a composite font (FMapType 2, 3, 4, 5, 8 and 9).</summary>
    private List<GlyphItem> CompositeGlyphs(PsFont root, Matrix rootMatrix, byte[] s)
    {
        var items = new List<GlyphItem>();
        int current = 0; // escape and shift modes: the selected descendant
        int p = 0;
        while (p < s.Length)
        {
            CountOperation();
            int fontNumber, code;
            uint fullCode;
            int cid = -1;
            switch (root.FMapType)
            {
                case 9:
                {
                    var (c, f, value, length) = root.CMap!.Decode(s, p);
                    p += length;
                    cid = c;
                    fontNumber = f;
                    fullCode = value;
                    code = (int)value;
                    break;
                }
                case 2:
                    if (p + 1 >= s.Length) return items;
                    fontNumber = s[p];
                    code = s[p + 1];
                    fullCode = (uint)(s[p] << 8 | s[p + 1]);
                    p += 2;
                    break;
                case 4:
                    fontNumber = s[p] >> 7;
                    code = s[p] & 0x7F;
                    fullCode = s[p++];
                    break;
                case 5:
                {
                    if (p + 1 >= s.Length) return items;
                    int v = s[p] << 8 | s[p + 1];
                    p += 2;
                    fontNumber = v >> 7;
                    code = v & 0x7F;
                    fullCode = (uint)v;
                    break;
                }
                case 3:
                    if (s[p] == root.EscChar)
                    {
                        if (p + 1 >= s.Length) return items;
                        current = s[p + 1];
                        p += 2;
                        continue;
                    }
                    fontNumber = current;
                    code = s[p];
                    fullCode = s[p++];
                    break;
                case 8:
                    if (s[p] == root.ShiftIn || s[p] == root.ShiftOut)
                    {
                        current = s[p] == root.ShiftOut ? 1 : 0;
                        p++;
                        continue;
                    }
                    fontNumber = current;
                    code = s[p];
                    fullCode = s[p++];
                    break;
                default:
                    throw new PsError("invalidfont");
            }
            int index = root.FMapType == 9 ? fontNumber : fontNumber < root.EncodingMap.Length ? root.EncodingMap[fontNumber] : -1;
            if (index < 0 || index >= root.Descendants.Length) continue;
            var descendant = root.Descendants[index].AsDict;
            var descendantFont = FontOf(descendant);
            var chain = FontMatrixOf(descendant).Multiply(rootMatrix);
            string? unicode = root.CMap is { IsUnicode: true } ? UnicodeOf(fullCode) : null;
            GlyphItem item;
            if (descendantFont.Kind is PsFontKind.Cid0 or PsFontKind.Cid1 or PsFontKind.Cid2)
                item = CidGlyph(descendant, descendantFont, chain, cid >= 0 ? cid : code);
            else
                item = SimpleGlyph(descendant, descendantFont, chain, (cid >= 0 ? cid : code) & 0xFF, null);
            item.ShowCode = (int)fullCode;
            item.Unicode = unicode;
            items.Add(item);
        }
        return items;
    }

    private static string? UnicodeOf(uint code) =>
        code is >= 0xD800 and <= 0xDFFF ? null : code <= 0xFFFF ? ((char)code).ToString() : null;

    /// <summary>A glyph of a CID-keyed font.</summary>
    private GlyphItem CidGlyph(PsDict dict, PsFont font, Matrix chain, int cid)
    {
        var item = new GlyphItem { Cid = cid, Code = cid, Dict = dict, Font = font, GlyphToUser = chain };
        switch (font.Kind)
        {
            case PsFontKind.Cid2:
            {
                int gid = font.CidToGid!(cid);
                if (gid < 0 || gid >= font.Sfnt!.NumGlyphs) gid = 0;
                item.Name = gid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                item.Advance = (font.Sfnt!.GetAdvanceWidth(gid) / (double)font.Sfnt.UnitsPerEm, 0);
                item.TextFromGlyph = Matrix.Identity;
                break;
            }
            case PsFontKind.Cid0:
            {
                var glyph = font.Cid0Glyph(cid, this) ?? font.Cid0Glyph(0, this);
                if (glyph is not var (fdFont, name))
                {
                    item.Advance = (0, 0);
                    item.TextFromGlyph = Matrix.Identity;
                    break;
                }
                item.Font = fdFont;
                item.Name = name;
                item.GlyphToUser = fdFont.BaseMatrix.Multiply(chain);
                item.Advance = fdFont.Advance(name, this);
                item.TextFromGlyph = fdFont.BaseMatrix;
                break;
            }
            default:
            {
                // CIDFontType 1: BuildGlyph with the CID, like a Type 3 font
                string key = "cid" + cid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var glyph = Type3Glyph(dict, font, key, PsObject.Integer(cid), cid);
                item.Name = key;
                item.Advance = (glyph.AdvanceX, glyph.AdvanceY);
                item.TextFromGlyph = font.BaseMatrix;
                break;
            }
        }
        return item;
    }

    /// <summary>The glyph name of a code in a font's encoding.</summary>
    private string GlyphName(PsDict dict, int code)
    {
        if (dict.Map.TryGetValue(Key("Encoding"), out var e) && e.IsArray && code >= 0 && code < e.AsArray.Length && e.AsArray[code].Type == PsType.Name)
            return e.AsArray[code].AsName.Text;
        return ".notdef";
    }

    // ------------------------------------------------------------------ showing

    private delegate (double X, double Y) Displacement(int index, int code, (double X, double Y) width);

    /// <summary>xshow, yshow, xyshow: the displacements come from an array of numbers instead of the glyph widths.</summary>
    private void ShowWithDisplacements((bool X, bool Y) xy)
    {
        var numbers = Pop();
        var s = PopString();
        if (numbers.Type == PsType.String) throw new PsError("rangecheck"); // encoded number strings are not supported
        if (!numbers.IsArray) throw new PsError("typecheck");
        var values = numbers.AsArray.Span.ToArray();
        if (values.Any(v => !v.IsNumber)) throw new PsError("typecheck");
        int per = xy.X && xy.Y ? 2 : 1;
        var items = Glyphs(s.AsString.ToArray());
        if (values.Length < items.Count * per) throw new PsError("rangecheck");
        ShowItems(items, (i, _, _) =>
        {
            double dx = xy.X ? values[i * per].Number : 0;
            double dy = xy.Y ? values[i * per + (xy.X ? 1 : 0)].Number : 0;
            return (dx, dy);
        });
    }

    /// <summary>
    /// Shows glyphs at the current point: consecutive glyphs of the same PDF font that advance by their own widths are
    /// written as one text string; the current point moves along.
    /// </summary>
    private void ShowItems(List<GlyphItem> items, Displacement? displacement)
    {
        var path = RequirePoint();
        var ctm = GState.Ctm;
        double x = path.CurrentX, y = path.CurrentY;
        var device = Device;
        var fonts = device?.Document != null ? device.Fonts ??= new PsPdfFonts(device.Document, this) : null;
        var color = device != null ? CurrentDeviceColor() : null;
        var run = new List<byte>();
        PdfFontEntry? runFont = null;
        Matrix runMatrix = default;
        double runX = x, runY = y;
        int runMode = 0;
        double runWidth = 0;

        void Flush()
        {
            if (run.Count > 0 && runFont != null && color is { } c && device != null)
                device.Text(GState, c, runFont, new Matrix(runMatrix.A, runMatrix.B, runMatrix.C, runMatrix.D, runX, runY), run.ToArray(), runMode, runWidth);
            run.Clear();
            runFont = null;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (fonts != null && color != null && PdfGlyph(fonts, item) is var (entry, bytes))
            {
                // text space to device space
                var textToUser = item.TextFromGlyph.TryInvert(out var inverse) ? inverse.Multiply(item.GlyphToUser) : item.GlyphToUser;
                var tm = textToUser.Multiply(ctm);
                if (runFont != null && (runFont != entry || !SameLinear(runMatrix, tm))) Flush();
                if (runFont == null)
                {
                    runFont = entry;
                    runMatrix = tm;
                    runX = x;
                    runY = y;
                    runMode = item.Font.PaintType == 2 ? 1 : 0;
                    var g = item.GlyphToUser.Multiply(ctm);
                    runWidth = item.Font.StrokeWidth * Math.Sqrt(Math.Abs(g.A * g.D - g.B * g.C));
                }
                run.AddRange(bytes);
            }
            var user = item.GlyphToUser.TransformDistance(item.Advance.X, item.Advance.Y);
            var d = displacement?.Invoke(i, item.ShowCode, user) ?? user;
            var (dx, dy) = ctm.TransformDistance(d.X, d.Y);
            x += dx;
            y += dy;
            // a displacement other than the width breaks the run (the PDF text positions by widths)
            if (displacement != null && (Math.Abs(d.X - user.X) > 1e-9 || Math.Abs(d.Y - user.Y) > 1e-9)) Flush();
        }
        Flush();
        path.MoveTo(x, y);
    }

    private static bool SameLinear(Matrix a, Matrix b) =>
        Math.Abs(a.A - b.A) < 1e-9 && Math.Abs(a.B - b.B) < 1e-9 && Math.Abs(a.C - b.C) < 1e-9 && Math.Abs(a.D - b.D) < 1e-9;

    /// <summary>The PDF font of a glyph and the bytes that show it there.</summary>
    private (PdfFontEntry Entry, byte[] Bytes)? PdfGlyph(PsPdfFonts fonts, GlyphItem item)
    {
        var font = item.Font;
        switch (font.Kind)
        {
            case PsFontKind.Type42:
            case PsFontKind.Cid2:
            {
                var entry = fonts.Get(font, PsObject.Null);
                int gid = font.Kind == PsFontKind.Cid2 ? int.Parse(item.Name, System.Globalization.CultureInfo.InvariantCulture) : font.GlyphId(item.Name, this);
                entry.Used.Add(gid);
                entry.GlyphNamesById[gid] = item.Unicode != null ? UnicodeName(item.Unicode) : item.Name;
                return (entry, new[] { (byte)(gid >> 8), (byte)gid });
            }
            case PsFontKind.Cid1:
            case PsFontKind.Type1 when font.RawCharStrings != null:
            {
                // glyphs of CID-keyed fonts get codes as they are shown
                var (entry, code) = fonts.ForGlyph(font, item.Name);
                entry.UnicodeNames[code] = item.Unicode != null ? UnicodeName(item.Unicode) : ".notdef";
                entry.Used.Add(code);
                return (entry, new[] { (byte)code });
            }
            default:
            {
                if (item.ByName)
                {
                    var (e, c) = fonts.ForGlyph(font, font.Kind == PsFontKind.Type3 && font.KeyedByCode ? PsFont.CodeKey(item.Code) : item.Name);
                    e.UnicodeNames[c] = item.Name;
                    e.Used.Add(c);
                    return (e, new[] { (byte)c });
                }
                var entry = fonts.Get(font, item.Dict.Map.TryGetValue(Key("Encoding"), out var enc) ? enc : PsObject.Null);
                int code = item.Code & 0xFF;
                if (item.Unicode != null) entry.UnicodeNames[code] = UnicodeName(item.Unicode);
                entry.Used.Add(code);
                return (entry, new[] { (byte)code });
            }
        }
    }

    /// <summary>A glyph name for a text (uniXXXX), so that the ToUnicode map of the font gives it.</summary>
    private static string UnicodeName(string text) => "uni" + string.Concat(text.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ Type 3 glyphs

    /// <summary>A Type 3 (or CIDFontType 1) glyph, captured from BuildGlyph or BuildChar on first use.</summary>
    private Type3Glyph Type3Glyph(PsDict dict, PsFont font, string key, PsObject glyphArgument, int code)
    {
        if (font.Type3Glyphs.TryGetValue(key, out var glyph)) return glyph;
        glyph = new Type3Glyph();
        font.Type3Glyphs[key] = glyph; // also stops recursion through the glyph itself
        var parent = Device;
        PsContentWriter? writer = null;
        if (parent?.Document != null)
        {
            font.Type3Resources ??= new PdfDictionary();
            writer = new PsContentWriter(new PsDictionaryResources(parent.Document, font.Type3Resources), glyphSpace: true);
        }
        var saved = GState;
        GState = saved.Clone();
        GState.Device = writer != null ? new PsGlyphDevice(parent, writer) : new PsNullDevice();
        GState.Ctm = Matrix.Identity;
        GState.Path = new PsPath();
        GState.Clip = null;
        GState.Path.MoveTo(0, 0);
        _type3.Push((glyph, writer));
        try
        {
            RunBuildProcedure(dict, glyphArgument, code);
        }
        catch (PsError)
        {
            // a failing glyph procedure leaves an empty glyph
        }
        finally
        {
            _type3.Pop();
            GState = saved;
        }
        if (writer != null)
        {
            string header = glyph.Box is { } b
                ? $"{N(glyph.AdvanceX)} {N(glyph.AdvanceY)} {N(b[0])} {N(b[1])} {N(b[2])} {N(b[3])} d1\n"
                : $"{N(glyph.AdvanceX)} {N(glyph.AdvanceY)} d0\n";
            var body = writer.ToArray();
            var content = new byte[header.Length + body.Length];
            Encoding.ASCII.GetBytes(header, content);
            body.CopyTo(content, header.Length);
            glyph.Content = content;
        }
        return glyph;
    }

    private static string N(double v) => PsContentWriter.Format(v, 4);

    /// <summary>Calls BuildGlyph (font, name or CID) or BuildChar (font, code).</summary>
    private void RunBuildProcedure(PsDict dict, PsObject glyphArgument, int code)
    {
        var fontObject = PsObject.Dictionary(dict);
        if (dict.Map.TryGetValue(Key("BuildGlyph"), out var buildGlyph) && buildGlyph.IsProcedure)
            CallProcedure(buildGlyph, new[] { fontObject, glyphArgument });
        else if (dict.Map.TryGetValue(Key("BuildChar"), out var buildChar) && buildChar.IsProcedure)
            CallProcedure(buildChar, new[] { fontObject, PsObject.Integer(code < 0 ? 0 : code) });
    }

    // ------------------------------------------------------------------ charpath

    /// <summary><c>charpath</c>: the glyph outlines are added to the current path; the current point moves along.</summary>
    private void CharPath(List<GlyphItem> items)
    {
        var path = RequirePoint();
        var ctm = GState.Ctm;
        double x = path.CurrentX, y = path.CurrentY;
        foreach (var item in items)
        {
            var toDevice = item.GlyphToUser.Multiply(ctm);
            var placed = new Matrix(toDevice.A, toDevice.B, toDevice.C, toDevice.D, x, y);
            IEnumerable<PsSegment>? segments = null;
            switch (item.Font.Kind)
            {
                case PsFontKind.Type3:
                case PsFontKind.Cid1:
                    segments = Type3Outline(item, placed);
                    break;
                case PsFontKind.Cid2:
                    segments = item.Font.TrueTypeOutline(int.Parse(item.Name, System.Globalization.CultureInfo.InvariantCulture)) is { } o ? TransformSegments(o.Segments, placed) : null;
                    break;
                default:
                    if (item.Font.Outline(item.Name, this) is { } outline) segments = TransformSegments(outline.Segments, placed);
                    else DiagnosticMissingOutline?.Invoke(item.Font.Name);
                    break;
            }
            if (segments != null) AppendSegments(path, segments);
            var user = item.GlyphToUser.TransformDistance(item.Advance.X, item.Advance.Y);
            var (dx, dy) = ctm.TransformDistance(user.X, user.Y);
            x += dx;
            y += dy;
        }
        path.MoveTo(x, y);
    }

    /// <summary>Reports a font without outlines (charpath with a font that is not embedded).</summary>
    public Action<string>? DiagnosticMissingOutline;

    private List<PsSegment> Type3Outline(GlyphItem item, Matrix placed)
    {
        var collector = new PsPathDevice(Device);
        var saved = GState;
        GState = saved.Clone();
        GState.Device = collector;
        GState.Ctm = placed;
        GState.Path = new PsPath();
        GState.Clip = null;
        _type3.Push((new Type3Glyph(), null));
        try
        {
            var argument = item.Font.Kind == PsFontKind.Cid1 ? PsObject.Integer(item.Cid) : PsObject.Name(Name(item.Name));
            RunBuildProcedure(item.Dict, argument, item.Code);
        }
        catch (PsError)
        {
        }
        finally
        {
            _type3.Pop();
            GState = saved;
        }
        return collector.Collected;
    }

    private static IEnumerable<PsSegment> TransformSegments(IEnumerable<PsSegment> segments, Matrix m)
    {
        foreach (var s in segments)
        {
            if (s.Kind == PsSegmentKind.ClosePath)
            {
                yield return s;
                continue;
            }
            var (x3, y3) = m.Transform(s.X3, s.Y3);
            if (s.Kind == PsSegmentKind.CurveTo)
            {
                var (x1, y1) = m.Transform(s.X1, s.Y1);
                var (x2, y2) = m.Transform(s.X2, s.Y2);
                yield return new PsSegment(s.Kind, x1, y1, x2, y2, x3, y3);
            }
            else yield return s with { X3 = x3, Y3 = y3 };
        }
    }

    private static void AppendSegments(PsPath path, IEnumerable<PsSegment> segments)
    {
        foreach (var s in segments)
        {
            switch (s.Kind)
            {
                case PsSegmentKind.MoveTo: path.MoveTo(s.X3, s.Y3); break;
                case PsSegmentKind.LineTo: path.LineTo(s.X3, s.Y3); break;
                case PsSegmentKind.CurveTo: path.CurveTo(s.X1, s.Y1, s.X2, s.Y2, s.X3, s.Y3); break;
                default: path.ClosePath(); break;
            }
        }
    }

    /// <summary>kshow: shows each glyph and calls the procedure with the codes of each pair in between.</summary>
    private sealed class KshowFrame(byte[] codes, PsObject proc) : LoopFrame
    {
        private int _index;

        public override bool Next(PsInterpreter ip)
        {
            if (_index >= codes.Length) return false;
            int i = _index++;
            ip.ShowItems(ip.Glyphs(new[] { codes[i] }), null);
            if (i + 1 < codes.Length)
            {
                ip.Push(codes[i]);
                ip.Push(codes[i + 1]);
                ip.PushProcedure(proc);
            }
            return true;
        }
    }

    /// <summary>cshow: calls the procedure with each code and its width; nothing is painted.</summary>
    private sealed class CshowFrame(List<GlyphItem> items, PsObject proc) : LoopFrame
    {
        private int _index;

        public override bool Next(PsInterpreter ip)
        {
            if (_index >= items.Count) return false;
            var item = items[_index++];
            var (wx, wy) = item.GlyphToUser.TransformDistance(item.Advance.X, item.Advance.Y);
            ip.Push(item.ShowCode);
            ip.PushReal(wx);
            ip.PushReal(wy);
            ip.PushProcedure(proc);
            return true;
        }
    }
}
