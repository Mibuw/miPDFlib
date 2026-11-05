using System.Buffers.Binary;
using MiPdf.Rendering.Raster;

namespace MiPdf.Rendering.Fonts;

/// <summary>
/// Reads glyph outlines from TrueType and OpenType fonts as embedded in PDF files (often subsets with only some
/// tables): glyph data (glyf/loca, also composite glyphs), the cmap subtables, glyph names (post) and the CFF table
/// of OpenType fonts with PostScript outlines. Outlines are returned in font units.
/// </summary>
internal sealed class SfntFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private int[]? _loca;
    private Dictionary<string, int>? _postNames;

    public SfntFont(byte[] data, int collectionIndex = 0)
    {
        _data = data;
        int offset = 0;
        if (Tag(0) == "ttcf")
        {
            int count = (int)U32(8);
            offset = (int)U32(12 + 4 * Math.Clamp(collectionIndex, 0, Math.Max(0, count - 1)));
        }
        int tables = U16(offset + 4);
        for (int i = 0; i < tables && offset + 12 + 16 * i + 16 <= data.Length; i++)
        {
            int record = offset + 12 + 16 * i;
            int tableOffset = (int)U32(record + 8), length = (int)U32(record + 12);
            if (tableOffset >= 0 && length >= 0 && (long)tableOffset + length <= data.Length) _tables[Tag(record)] = (tableOffset, length);
        }
        if (_tables.TryGetValue("head", out var head) && head.Length >= 54)
        {
            UnitsPerEm = Math.Max(16, (int)U16(head.Offset + 18));
            LongLoca = S16(head.Offset + 50) == 1;
        }
        if (_tables.TryGetValue("maxp", out var maxp) && maxp.Length >= 6) GlyphCount = U16(maxp.Offset + 4);
        if (_tables.TryGetValue("CFF ", out var cff)) Cff = new CffFont(data.AsSpan(cff.Offset, cff.Length).ToArray());
    }

    public int UnitsPerEm { get; } = 1000;

    public int GlyphCount { get; private set; }

    private bool LongLoca { get; }

    /// <summary>The CFF outlines of an OpenType font with PostScript outlines, otherwise null.</summary>
    public CffFont? Cff { get; }

    public bool HasGlyf => _tables.ContainsKey("glyf") && _tables.ContainsKey("loca");

    /// <summary>The advance width of a glyph in font units (hhea/hmtx), or 0 if unknown.</summary>
    public int AdvanceWidth(int glyph)
    {
        if (glyph < 0 || !_tables.TryGetValue("hhea", out var hhea) || hhea.Length < 36 || !_tables.TryGetValue("hmtx", out var hmtx)) return 0;
        int count = U16(hhea.Offset + 34);
        if (count == 0) return 0;
        int index = Math.Min(glyph, count - 1);
        return 4 * index + 2 <= hmtx.Length ? U16(hmtx.Offset + 4 * index) : 0;
    }

    private string Tag(int pos) => pos + 4 <= _data.Length ? System.Text.Encoding.ASCII.GetString(_data, pos, 4) : "";

    private ushort U16(int pos) => pos + 2 <= _data.Length ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(pos)) : (ushort)0;

    private short S16(int pos) => pos + 2 <= _data.Length ? BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(pos)) : (short)0;

    private uint U32(int pos) => pos + 4 <= _data.Length ? BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(pos)) : 0;

    // ------------------------------------------------------------------ cmap

    /// <summary>The glyph for a character code in the cmap subtable (platform, encoding), or -1.</summary>
    public int MapCode(int platform, int encoding, int code)
    {
        if (!_tables.TryGetValue("cmap", out var cmap)) return -1;
        int count = U16(cmap.Offset + 2);
        for (int i = 0; i < count; i++)
        {
            int record = cmap.Offset + 4 + 8 * i;
            if (U16(record) != platform || U16(record + 2) != encoding) continue;
            int sub = cmap.Offset + (int)U32(record + 4);
            int gid = Lookup(sub, code);
            if (gid >= 0) return gid;
        }
        return -1;
    }

    public bool HasCmap(int platform, int encoding)
    {
        if (!_tables.TryGetValue("cmap", out var cmap)) return false;
        int count = U16(cmap.Offset + 2);
        for (int i = 0; i < count; i++)
            if (U16(cmap.Offset + 4 + 8 * i) == platform && U16(cmap.Offset + 4 + 8 * i + 2) == encoding) return true;
        return false;
    }

    private int Lookup(int sub, int code)
    {
        switch (U16(sub))
        {
            case 0:
                return code is >= 0 and < 256 ? _data.ElementAtOrDefault(sub + 6 + code) : -1;
            case 4:
            {
                int segments = U16(sub + 6) / 2;
                int ends = sub + 14, starts = ends + 2 * segments + 2, deltas = starts + 2 * segments, ranges = deltas + 2 * segments;
                for (int s = 0; s < segments; s++)
                {
                    int end = U16(ends + 2 * s);
                    if (code > end) continue;
                    int start = U16(starts + 2 * s);
                    if (code < start) return -1;
                    int delta = S16(deltas + 2 * s), rangeOffset = U16(ranges + 2 * s);
                    if (rangeOffset == 0) return (code + delta) & 0xFFFF;
                    int glyph = U16(ranges + 2 * s + rangeOffset + 2 * (code - start));
                    return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
                }
                return -1;
            }
            case 6:
            {
                int first = U16(sub + 6), count = U16(sub + 8);
                return code >= first && code < first + count ? U16(sub + 10 + 2 * (code - first)) : -1;
            }
            case 12:
            {
                int groups = (int)U32(sub + 12);
                for (int g = 0; g < groups; g++)
                {
                    int group = sub + 16 + 12 * g;
                    uint start = U32(group), end = U32(group + 4);
                    if (code >= start && code <= end) return (int)(U32(group + 8) + (code - start));
                }
                return -1;
            }
            default:
                return -1;
        }
    }

    // ------------------------------------------------------------------ glyph names (post format 2)

    public int GlyphByName(string name)
    {
        if (_postNames == null)
        {
            _postNames = new Dictionary<string, int>(StringComparer.Ordinal);
            if (_tables.TryGetValue("post", out var post) && U32(post.Offset) == 0x00020000)
            {
                int count = U16(post.Offset + 32);
                int namesStart = post.Offset + 34 + 2 * count;
                var custom = new List<string>();
                for (int p = namesStart; p < post.Offset + post.Length && p < _data.Length;)
                {
                    int length = _data[p];
                    if (p + 1 + length > _data.Length) break;
                    custom.Add(System.Text.Encoding.Latin1.GetString(_data, p + 1, length));
                    p += 1 + length;
                }
                for (int gid = 0; gid < count; gid++)
                {
                    int index = U16(post.Offset + 34 + 2 * gid);
                    string? glyphName = index < 258 ? MacGlyphNames.Get(index) : index - 258 < custom.Count ? custom[index - 258] : null;
                    if (glyphName != null) _postNames.TryAdd(glyphName, gid);
                }
            }
        }
        return _postNames.TryGetValue(name, out int glyph) ? glyph : -1;
    }

    // ------------------------------------------------------------------ outlines

    /// <summary>The outline of a glyph in font units (quadratic curves converted to cubic), or null.</summary>
    public PathGeometry? GetOutline(int glyph)
    {
        if (Cff != null) return Cff.GetOutline(glyph);
        var path = new PathGeometry();
        AddGlyph(path, glyph, Graphics.Matrix.Identity, 0);
        return path;
    }

    private bool TryGetGlyphRange(int glyph, out int start, out int end)
    {
        start = end = 0;
        if (!_tables.TryGetValue("glyf", out var glyf) || !_tables.TryGetValue("loca", out var loca)) return false;
        if (_loca == null)
        {
            int count = GlyphCount > 0 ? GlyphCount : (LongLoca ? loca.Length / 4 : loca.Length / 2) - 1;
            _loca = new int[Math.Max(0, count) + 1];
            for (int i = 0; i < _loca.Length; i++)
                _loca[i] = LongLoca ? (int)U32(loca.Offset + 4 * i) : U16(loca.Offset + 2 * i) * 2;
        }
        if (glyph < 0 || glyph + 1 >= _loca.Length) return false;
        start = glyf.Offset + _loca[glyph];
        end = glyf.Offset + _loca[glyph + 1];
        return end > start && end <= glyf.Offset + glyf.Length;
    }

    private void AddGlyph(PathGeometry path, int glyph, Graphics.Matrix matrix, int depth)
    {
        if (depth > 8 || !TryGetGlyphRange(glyph, out int start, out int end)) return;
        int contours = S16(start);
        if (contours >= 0) AddSimpleGlyph(path, start, end, contours, matrix);
        else AddCompositeGlyph(path, start + 10, end, matrix, depth);
    }

    private void AddSimpleGlyph(PathGeometry path, int start, int end, int contours, Graphics.Matrix matrix)
    {
        int p = start + 10;
        var endPoints = new int[contours];
        for (int i = 0; i < contours; i++) endPoints[i] = U16(p + 2 * i);
        p += 2 * contours;
        int points = contours > 0 ? endPoints[^1] + 1 : 0;
        if (points <= 0 || points > 65536) return;
        p += 2 + U16(p); // instructions
        var flags = new byte[points];
        for (int i = 0; i < points && p < end;)
        {
            byte flag = _data[p++];
            flags[i++] = flag;
            if ((flag & 8) != 0 && p < end)
            {
                int repeat = _data[p++];
                for (int r = 0; r < repeat && i < points; r++) flags[i++] = flag;
            }
        }
        var xs = new int[points];
        var ys = new int[points];
        int value = 0;
        for (int i = 0; i < points; i++)
        {
            byte flag = flags[i];
            if ((flag & 2) != 0) value += (flag & 16) != 0 ? _data.ElementAtOrDefault(p++) : -_data.ElementAtOrDefault(p++);
            else if ((flag & 16) == 0) { value += S16(p); p += 2; }
            xs[i] = value;
        }
        value = 0;
        for (int i = 0; i < points; i++)
        {
            byte flag = flags[i];
            if ((flag & 4) != 0) value += (flag & 32) != 0 ? _data.ElementAtOrDefault(p++) : -_data.ElementAtOrDefault(p++);
            else if ((flag & 32) == 0) { value += S16(p); p += 2; }
            ys[i] = value;
        }
        int first = 0;
        foreach (int last in endPoints)
        {
            if (last < first || last >= points) break;
            AddContour(path, xs, ys, flags, first, last, matrix);
            first = last + 1;
        }
    }

    /// <summary>A contour of on-curve and off-curve (quadratic control) points.</summary>
    private static void AddContour(PathGeometry path, int[] xs, int[] ys, byte[] flags, int first, int last, Graphics.Matrix matrix)
    {
        int count = last - first + 1;
        if (count < 2) return;
        (double X, double Y) Point(int i) => matrix.Transform(xs[first + (i % count)], ys[first + (i % count)]);
        bool On(int i) => (flags[first + (i % count)] & 1) != 0;
        // start at an on-curve point (or the midpoint of two off-curve points)
        int startIndex = Enumerable.Range(0, count).FirstOrDefault(On, -1);
        (double X, double Y) start = startIndex >= 0 ? Point(startIndex) : Mid(Point(0), Point(1));
        if (startIndex < 0) startIndex = 0;
        path.MoveTo(start.X, start.Y);
        var current = start;
        (double X, double Y)? control = null;
        for (int k = 1; k <= count; k++)
        {
            int i = startIndex + k;
            var point = Point(i);
            if (On(i))
            {
                if (control is { } c) Quad(path, current, c, point);
                else path.LineTo(point.X, point.Y);
                current = point;
                control = null;
            }
            else
            {
                if (control is { } c)
                {
                    var mid = Mid(c, point);
                    Quad(path, current, c, mid);
                    current = mid;
                }
                control = point;
            }
        }
        if (control is { } last2) Quad(path, current, last2, start);
        path.Close();
    }

    private static (double X, double Y) Mid((double X, double Y) a, (double X, double Y) b) => ((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static void Quad(PathGeometry path, (double X, double Y) p0, (double X, double Y) c, (double X, double Y) p1) =>
        path.CurveTo(p0.X + 2.0 / 3 * (c.X - p0.X), p0.Y + 2.0 / 3 * (c.Y - p0.Y), p1.X + 2.0 / 3 * (c.X - p1.X), p1.Y + 2.0 / 3 * (c.Y - p1.Y), p1.X, p1.Y);

    private void AddCompositeGlyph(PathGeometry path, int p, int end, Graphics.Matrix matrix, int depth)
    {
        while (p + 4 <= end)
        {
            int flags = U16(p), glyph = U16(p + 2);
            p += 4;
            double dx, dy;
            if ((flags & 1) != 0) { dx = S16(p); dy = S16(p + 2); p += 4; }
            else { dx = (sbyte)_data[p]; dy = (sbyte)_data[p + 1]; p += 2; }
            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & 8) != 0) { a = d = F2Dot14(p); p += 2; }
            else if ((flags & 0x40) != 0) { a = F2Dot14(p); d = F2Dot14(p + 2); p += 4; }
            else if ((flags & 0x80) != 0) { a = F2Dot14(p); b = F2Dot14(p + 2); c = F2Dot14(p + 4); d = F2Dot14(p + 6); p += 8; }
            // flag 2: offsets are x/y values (point matching is not supported)
            var component = new Graphics.Matrix(a, b, c, d, (flags & 2) != 0 ? dx : 0, (flags & 2) != 0 ? dy : 0);
            AddGlyph(path, glyph, component.Multiply(matrix), depth + 1);
            if ((flags & 0x20) == 0) break;
        }
    }

    private double F2Dot14(int pos) => S16(pos) / 16384.0;
}
