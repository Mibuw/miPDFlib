using System.Buffers.Binary;
using MiPdf.Fonts;
using MiPdf.Fonts.TrueType;
using MiPdf.Graphics;
using MiPdf.PostScript.Graphics;
using MiPdf.PostScript.Interpreter;

namespace MiPdf.PostScript.Fonts;

internal enum PsFontKind
{
    Type1,
    Standard,
    Type3,
    Type42,
    Composite,
    Cid0,
    Cid1,
    Cid2,
}

/// <summary>A Type 3 glyph as captured from BuildGlyph/BuildChar: its metrics and content in glyph space.</summary>
internal sealed class Type3Glyph
{
    public double AdvanceX, AdvanceY;
    public double[]? Box; // setcachedevice: llx lly urx ury (uncolored glyph, d1); null for setcharwidth (d0)
    public byte[] Content = Array.Empty<byte>();
    public List<PsSegment>? Outline; // for charpath
}

/// <summary>
/// The font behind a font dictionary (its FID): the kind, the font program and caches of glyph metrics and outlines.
/// Dictionaries made by scalefont/makefont share it; their FontMatrix and Encoding are taken from the dictionary.
/// </summary>
internal sealed class PsFont
{
    public PsFontKind Kind { get; init; }
    public string Name { get; init; } = "Font";
    public PsDict Dict { get; init; } = null!;
    public Matrix BaseMatrix { get; init; } = new(0.001, 0, 0, 0.001, 0, 0);
    public double[] FontBBox { get; init; } = { 0, 0, 1000, 1000 };
    public int PaintType { get; init; }
    public double StrokeWidth { get; init; }

    // Type 1
    public PsDict? CharStrings { get; init; }
    public PsDict? Private { get; init; }
    public PsArray? Subrs { get; init; }
    public int LenIV { get; init; } = 4;
    public PsDict? Metrics { get; init; }
    private readonly Dictionary<string, GlyphOutline?> _type1 = new();

    /// <summary>Charstrings and subroutines given directly (the font dictionaries of CIDFontType 0 fonts).</summary>
    public Dictionary<string, byte[]>? RawCharStrings { get; init; }
    public List<byte[]>? RawSubrList { get; init; }

    // composite fonts (Type 0)
    public int FMapType { get; init; }
    public int[] EncodingMap { get; init; } = Array.Empty<int>();
    public PsObject[] Descendants { get; init; } = Array.Empty<PsObject>();
    public PsCMap? CMap { get; init; }
    public int EscChar { get; init; } = 255;
    public int ShiftIn { get; init; } = 15;
    public int ShiftOut { get; init; } = 14;

    // CID-keyed fonts
    public int CidCount { get; init; }
    public byte[] GlyphData { get; init; } = Array.Empty<byte>();
    public PsObject GlyphDirectory { get; init; } = PsObject.Null;
    public int CidMapOffset { get; init; }
    public int FdBytes { get; init; } = 1;
    public int GdBytes { get; init; } = 2;
    public PsFont[] FdFonts { get; init; } = Array.Empty<PsFont>();
    public Func<int, int>? CidToGid { get; init; }

    // standard fonts without program
    public string? StandardName { get; init; }
    private Dictionary<string, int>? _standardWidths;

    // Type 42
    public TrueTypeFont? Sfnt { get; init; }

    // Type 3: glyphs captured by the interpreter
    public readonly Dictionary<string, Type3Glyph> Type3Glyphs = new();

    /// <summary>The resources of the PDF Type 3 font (fonts and graphics states used by the glyphs).</summary>
    public MiPdf.Objects.PdfDictionary? Type3Resources;

    public bool IsSymbolicStandard => StandardName is "Symbol" or "ZapfDingbats";

    /// <summary>A Type 3 font without BuildGlyph: BuildChar gets codes, so the glyphs are kept per code.</summary>
    public bool KeyedByCode { get; init; }

    /// <summary>The key (and PDF glyph name) of a Type 3 glyph kept per code.</summary>
    public static string CodeKey(int code) => "c" + code.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The advance of a glyph in glyph space (Type 3 glyphs come from the interpreter).</summary>
    public (double X, double Y) Advance(string name, PsInterpreter ip)
    {
        if (Metrics != null && Metrics.Map.TryGetValue(ip.Key(name), out var m))
        {
            if (m.IsNumber) return (m.Number, 0);
            if (m.IsArray && m.AsArray.Length == 2 && m.AsArray[0].IsNumber) return (m.AsArray[1].Number, 0);
            if (m.IsArray && m.AsArray.Length == 4 && m.AsArray[2].IsNumber && m.AsArray[3].IsNumber) return (m.AsArray[2].Number, m.AsArray[3].Number);
        }
        switch (Kind)
        {
            case PsFontKind.Type1:
            {
                var g = Type1Glyph(name, ip);
                return g == null ? (0, 0) : (g.AdvanceX, g.AdvanceY);
            }
            case PsFontKind.Standard:
                return (StandardWidth(name), 0);
            case PsFontKind.Type42:
            {
                int gid = GlyphId(name, ip);
                return (Sfnt!.GetAdvanceWidth(gid) / (double)Sfnt.UnitsPerEm, 0);
            }
            default:
                return (0, 0);
        }
    }

    /// <summary>The outline of a glyph in glyph space, null if not available (standard fonts have no program).</summary>
    public GlyphOutline? Outline(string name, PsInterpreter ip) => Kind switch
    {
        PsFontKind.Type1 => Type1Glyph(name, ip),
        PsFontKind.Type42 => TrueTypeOutline(GlyphId(name, ip)),
        _ => null,
    };

    // ------------------------------------------------------------------ Type 1

    /// <summary>The charstring of a glyph as stored (still charstring-encrypted unless lenIV is -1); null if missing.</summary>
    public byte[]? RawCharstring(string name, PsInterpreter ip)
    {
        if (RawCharStrings != null) return RawCharStrings.TryGetValue(name, out var raw) ? raw : null;
        if (CharStrings == null || !CharStrings.Map.TryGetValue(ip.Key(name), out var cs) || cs.Type != PsType.String) return null;
        return cs.AsString.ToArray();
    }

    public int SubrCount => RawSubrList?.Count ?? Subrs?.Length ?? 0;

    /// <summary>A subroutine as stored; null if missing.</summary>
    public byte[]? RawSubr(int i)
    {
        if (RawSubrList != null) return i >= 0 && i < RawSubrList.Count ? RawSubrList[i] : null;
        if (Subrs == null || i < 0 || i >= Subrs.Length || Subrs[i].Type != PsType.String) return null;
        return Subrs[i].AsString.ToArray();
    }

    /// <summary>The charstring of a glyph, decrypted; null if the font does not have the glyph.</summary>
    public byte[]? Charstring(string name, PsInterpreter ip)
    {
        var raw = RawCharstring(name, ip);
        return raw == null ? null : Type1Charstrings.Decrypt(raw, Type1Charstrings.CharstringKey, LenIV);
    }

    public bool HasGlyph(string name, PsInterpreter ip) => Kind switch
    {
        PsFontKind.Type1 => RawCharStrings?.ContainsKey(name) ?? CharStrings?.Map.ContainsKey(ip.Key(name)) == true,
        PsFontKind.Type42 => CharStrings?.Map.ContainsKey(ip.Key(name)) == true,
        _ => true,
    };

    // ------------------------------------------------------------------ CIDFontType 0

    /// <summary>
    /// The glyph of a CID in a CIDFontType 0 font: the font of its FDArray entry and the name under which the charstring
    /// is kept there ("g" and the CID); null if the CID has no glyph.
    /// </summary>
    public (PsFont Font, string Name)? Cid0Glyph(int cid, PsInterpreter ip)
    {
        if (cid < 0 || cid >= Math.Max(CidCount, 1) || FdFonts.Length == 0) return null;
        string name = "g" + cid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int fd;
        byte[]? charstring;
        if (GlyphDirectory.Type == PsType.Dictionary || GlyphDirectory.IsArray)
        {
            PsObject entry = PsObject.Null;
            if (GlyphDirectory.Type == PsType.Dictionary) GlyphDirectory.AsDict.Map.TryGetValue(PsObject.Integer(cid), out entry);
            else if (cid < GlyphDirectory.AsArray.Length) entry = GlyphDirectory.AsArray[cid];
            if (entry.Type != PsType.String || entry.AsString.Length < FdBytes) return null;
            var bytes = entry.AsString.Span;
            fd = (int)ReadNumber(bytes, 0, FdBytes);
            charstring = bytes[FdBytes..].ToArray();
        }
        else
        {
            int entrySize = FdBytes + GdBytes;
            long at = CidMapOffset + (long)cid * entrySize;
            if (at + 2L * entrySize > GlyphData.Length) return null;
            fd = (int)ReadNumber(GlyphData, (int)at, FdBytes);
            long start = ReadNumber(GlyphData, (int)at + FdBytes, GdBytes);
            long end = ReadNumber(GlyphData, (int)(at + entrySize + FdBytes), GdBytes);
            if (end <= start || end > GlyphData.Length) return null;
            charstring = GlyphData.AsSpan((int)start, (int)(end - start)).ToArray();
        }
        if (fd < 0 || fd >= FdFonts.Length) return null;
        var font = FdFonts[fd];
        font.RawCharStrings!.TryAdd(name, charstring);
        return (font, name);
    }

    public static long ReadNumber(ReadOnlySpan<byte> data, int position, int bytes)
    {
        long v = 0;
        for (int i = 0; i < bytes; i++) v = (v << 8) | data[position + i];
        return v;
    }

    private GlyphOutline? Type1Glyph(string name, PsInterpreter ip)
    {
        if (_type1.TryGetValue(name, out var cached)) return cached;
        var code = Charstring(name, ip) ?? (name != ".notdef" ? Charstring(".notdef", ip) : null);
        GlyphOutline? outline = null;
        if (code != null)
        {
            outline = Type1Charstrings.Run(code, i =>
            {
                var raw = RawSubr(i);
                return raw == null ? null : Type1Charstrings.Decrypt(raw, Type1Charstrings.CharstringKey, LenIV);
            }, n => Charstring(n, ip));
        }
        if (_type1.Count < 65536) _type1[name] = outline;
        return outline;
    }

    // ------------------------------------------------------------------ standard fonts

    private double StandardWidth(string name)
    {
        var info = StandardFontMetrics.Get(StandardName!);
        if (_standardWidths == null)
        {
            var widths = new Dictionary<string, int>();
            if (IsSymbolicStandard)
            {
                var encoding = StandardName == "Symbol" ? PsEncodings.Symbol : PsEncodings.Dingbats;
                for (int code = 32; code < 256; code++)
                    if (encoding[code] != ".notdef") widths.TryAdd(encoding[code], StandardFontMetrics.GetWidth(info, code));
            }
            else
            {
                for (int code = 32; code < 256; code++)
                {
                    char c = WinAnsiEncoding.ToUnicode(code);
                    if (c == '\0') continue;
                    string? glyph = GlyphNames.NameOf(c);
                    if (glyph != null) widths.TryAdd(glyph, StandardFontMetrics.GetWidth(info, code));
                }
            }
            _standardWidths = widths;
        }
        if (_standardWidths.TryGetValue(name, out int w) && w > 0) return w;
        if (name == ".notdef") return 0;
        // glyphs outside the metrics (fi, fl, ...): monospaced width or the width of n
        return info.FixedWidth > 0 ? info.FixedWidth : _standardWidths.TryGetValue("n", out int n) ? n : 500;
    }

    // ------------------------------------------------------------------ Type 42

    /// <summary>The glyph index of a glyph name (CharStrings maps names to indices); 0 (.notdef) if missing.</summary>
    public int GlyphId(string name, PsInterpreter ip)
    {
        if (CharStrings != null && CharStrings.Map.TryGetValue(ip.Key(name), out var gid) && gid.Type == PsType.Integer
            && gid.Int >= 0 && gid.Int < (Sfnt?.NumGlyphs ?? 0))
            return (int)gid.Int;
        return 0;
    }

    /// <summary>The TrueType outline of a glyph in glyph space (1 em = 1), quadratic splines as cubic curves.</summary>
    public GlyphOutline? TrueTypeOutline(int gid)
    {
        if (Sfnt == null) return null;
        var outline = new GlyphOutline { AdvanceX = Sfnt.GetAdvanceWidth(gid) / (double)Sfnt.UnitsPerEm };
        double scale = 1.0 / Sfnt.UnitsPerEm;
        AddGlyph(outline, gid, Matrix.Scale(scale, scale), 0);
        return outline;
    }

    private void AddGlyph(GlyphOutline outline, int gid, Matrix m, int depth)
    {
        if (depth > 8 || gid < 0 || gid >= Sfnt!.NumGlyphs) return;
        var loca = Sfnt.GetTable("loca");
        var glyf = Sfnt.GetTable("glyf");
        int start, end;
        if (Sfnt.IndexToLocFormat == 0)
        {
            if ((gid + 1) * 2 + 2 > loca.Length) return;
            start = BinaryPrimitives.ReadUInt16BigEndian(loca[(gid * 2)..]) * 2;
            end = BinaryPrimitives.ReadUInt16BigEndian(loca[(gid * 2 + 2)..]) * 2;
        }
        else
        {
            if ((gid + 1) * 4 + 4 > loca.Length) return;
            start = (int)BinaryPrimitives.ReadUInt32BigEndian(loca[(gid * 4)..]);
            end = (int)BinaryPrimitives.ReadUInt32BigEndian(loca[(gid * 4 + 4)..]);
        }
        if (end <= start || end > glyf.Length) return;
        var g = glyf[start..end];
        short contours = BinaryPrimitives.ReadInt16BigEndian(g);
        if (contours >= 0) SimpleGlyph(outline, g, contours, m);
        else CompositeGlyph(outline, g, m, depth);
    }

    private static void SimpleGlyph(GlyphOutline outline, ReadOnlySpan<byte> g, int contours, Matrix m)
    {
        int p = 10;
        var ends = new int[contours];
        for (int i = 0; i < contours; i++, p += 2) ends[i] = BinaryPrimitives.ReadUInt16BigEndian(g[p..]);
        int points = contours == 0 ? 0 : ends[^1] + 1;
        int instructions = BinaryPrimitives.ReadUInt16BigEndian(g[p..]);
        p += 2 + instructions;
        var flags = new byte[points];
        for (int i = 0; i < points;)
        {
            byte f = g[p++];
            flags[i++] = f;
            if ((f & 8) != 0)
            {
                int repeat = g[p++];
                for (int r = 0; r < repeat && i < points; r++) flags[i++] = f;
            }
        }
        var xs = new int[points];
        var ys = new int[points];
        int v = 0;
        for (int i = 0; i < points; i++)
        {
            byte f = flags[i];
            if ((f & 2) != 0) v += (f & 16) != 0 ? g[p++] : -g[p++];
            else if ((f & 16) == 0)
            {
                v += BinaryPrimitives.ReadInt16BigEndian(g[p..]);
                p += 2;
            }
            xs[i] = v;
        }
        v = 0;
        for (int i = 0; i < points; i++)
        {
            byte f = flags[i];
            if ((f & 4) != 0) v += (f & 32) != 0 ? g[p++] : -g[p++];
            else if ((f & 32) == 0)
            {
                v += BinaryPrimitives.ReadInt16BigEndian(g[p..]);
                p += 2;
            }
            ys[i] = v;
        }
        int first = 0;
        foreach (int last in ends)
        {
            Contour(outline, xs, ys, flags, first, last, m);
            first = last + 1;
        }
    }

    /// <summary>One closed contour of on- and off-curve points (implied on-curve points between two off-curve ones).</summary>
    private static void Contour(GlyphOutline outline, int[] xs, int[] ys, byte[] flags, int first, int last, Matrix m)
    {
        int n = last - first + 1;
        if (n < 2) return;
        bool On(int i) => (flags[first + (i % n + n) % n] & 1) != 0;
        (double X, double Y) P(int i) => (xs[first + (i % n + n) % n], ys[first + (i % n + n) % n]);
        // start at an on-curve point (or the midpoint of the first two off-curve points)
        int start = Enumerable.Range(0, n).FirstOrDefault(On, -1);
        (double X, double Y) startPoint = start >= 0 ? P(start) : ((P(0).X + P(1).X) / 2, (P(0).Y + P(1).Y) / 2);
        if (start < 0) start = 0;
        var (sx, sy) = m.Transform(startPoint.X, startPoint.Y);
        outline.Segments.Add(PsSegment.Move(sx, sy));
        var current = startPoint;
        for (int k = 1; k <= n; k++)
        {
            int i = start + k;
            var point = P(i);
            if (On(i))
            {
                var (x, y) = m.Transform(point.X, point.Y);
                outline.Segments.Add(PsSegment.Line(x, y));
                current = point;
                continue;
            }
            // an off-curve control point: the curve ends at the next on-curve point or the implied midpoint
            var next = P(i + 1);
            var end = On(i + 1) ? next : ((point.X + next.X) / 2, (point.Y + next.Y) / 2);
            if (On(i + 1)) k++;
            var c1 = (current.X + 2.0 / 3 * (point.X - current.X), current.Y + 2.0 / 3 * (point.Y - current.Y));
            var c2 = (end.Item1 + 2.0 / 3 * (point.X - end.Item1), end.Item2 + 2.0 / 3 * (point.Y - end.Item2));
            var (x1, y1) = m.Transform(c1.Item1, c1.Item2);
            var (x2, y2) = m.Transform(c2.Item1, c2.Item2);
            var (x3, y3) = m.Transform(end.Item1, end.Item2);
            outline.Segments.Add(new PsSegment(PsSegmentKind.CurveTo, x1, y1, x2, y2, x3, y3));
            current = end;
        }
        outline.Segments.Add(PsSegment.Close);
    }

    private void CompositeGlyph(GlyphOutline outline, ReadOnlySpan<byte> g, Matrix m, int depth)
    {
        int p = 10;
        while (p + 4 <= g.Length)
        {
            int flags = BinaryPrimitives.ReadUInt16BigEndian(g[p..]);
            int component = BinaryPrimitives.ReadUInt16BigEndian(g[(p + 2)..]);
            p += 4;
            double dx, dy;
            if ((flags & 1) != 0)
            {
                dx = BinaryPrimitives.ReadInt16BigEndian(g[p..]);
                dy = BinaryPrimitives.ReadInt16BigEndian(g[(p + 2)..]);
                p += 4;
            }
            else
            {
                dx = (sbyte)g[p];
                dy = (sbyte)g[p + 1];
                p += 2;
            }
            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & 8) != 0)
            {
                a = d = F2Dot14(g, p);
                p += 2;
            }
            else if ((flags & 0x40) != 0)
            {
                a = F2Dot14(g, p);
                d = F2Dot14(g, p + 2);
                p += 4;
            }
            else if ((flags & 0x80) != 0)
            {
                a = F2Dot14(g, p);
                b = F2Dot14(g, p + 2);
                c = F2Dot14(g, p + 4);
                d = F2Dot14(g, p + 6);
                p += 8;
            }
            // offsets as point numbers (flag 2 clear) are not supported: the component is placed at the origin
            if ((flags & 2) == 0) dx = dy = 0;
            AddGlyph(outline, component, new Matrix(a, b, c, d, dx, dy).Multiply(m), depth + 1);
            if ((flags & 0x20) == 0) break;
        }
    }

    private static double F2Dot14(ReadOnlySpan<byte> g, int p) => BinaryPrimitives.ReadInt16BigEndian(g[p..]) / 16384.0;

    /// <summary>The font data of a Type 42 font: the strings of sfnts, each without its padding byte if odd.</summary>
    public static byte[] SfntData(PsArray sfnts)
    {
        var data = new List<byte>();
        foreach (var item in sfnts.Span)
        {
            if (item.Type != PsType.String) throw new PsError("invalidfont");
            var bytes = item.AsString.Span;
            data.AddRange((bytes.Length % 2 == 1 ? bytes[..^1] : bytes).ToArray());
        }
        return data.ToArray();
    }
}
