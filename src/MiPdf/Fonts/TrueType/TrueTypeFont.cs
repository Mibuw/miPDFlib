using System.Buffers.Binary;
using System.Text;

namespace MiPdf.Fonts.TrueType;

/// <summary>
/// Parser for TrueType (glyf) and OpenType (CFF) font files including TrueType collections.
/// Provides metrics, character mapping and access to raw tables for embedding/subsetting.
/// </summary>
internal sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private ushort[] _advanceWidths = Array.Empty<ushort>();
    private readonly Dictionary<int, int> _cmap = new();

    public TrueTypeFont(byte[] data, int collectionIndex = 0)
    {
        _data = data;
        int offset = 0;
        if (Tag(0) == "ttcf")
        {
            int numFonts = (int)U32(8);
            if (collectionIndex < 0 || collectionIndex >= numFonts) throw new PdfException("Invalid font index in TrueType collection.");
            offset = (int)U32(12 + 4 * collectionIndex);
        }
        uint version = U32(offset);
        if (version != 0x00010000 && Tag(offset) != "OTTO" && Tag(offset) != "true")
            throw new PdfException("Unsupported font format (expected TrueType or OpenType).");
        int numTables = U16(offset + 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = offset + 12 + 16 * i;
            _tables[Tag(rec)] = ((int)U32(rec + 8), (int)U32(rec + 12));
        }
        IsCff = _tables.ContainsKey("CFF ") || _tables.ContainsKey("CFF2");
        ReadHead();
        ReadHhea();
        ReadMaxp();
        ReadHmtx();
        ReadOs2();
        ReadPost();
        ReadName();
        ReadCmap();
    }

    public byte[] Data => _data;

    public bool IsCff { get; }

    public int UnitsPerEm { get; private set; } = 1000;

    public short XMin { get; private set; }
    public short YMin { get; private set; }
    public short XMax { get; private set; }
    public short YMax { get; private set; }

    public int MacStyle { get; private set; }

    public int IndexToLocFormat { get; private set; }

    public short Ascender { get; private set; }

    public short Descender { get; private set; }

    public short LineGap { get; private set; }

    public short CapHeight { get; private set; }

    public short XHeight { get; private set; }

    public int WeightClass { get; private set; } = 400;

    public int FsType { get; private set; }

    public double ItalicAngle { get; private set; }

    public bool IsFixedPitch { get; private set; }

    public short UnderlinePosition { get; private set; }

    public short UnderlineThickness { get; private set; }

    public int NumGlyphs { get; private set; }

    public string PostScriptName { get; private set; } = "Font";

    public string FamilyName { get; private set; } = "";

    /// <summary>True if the font license forbids embedding (OS/2 fsType "restricted license").</summary>
    public bool IsEmbeddingRestricted => (FsType & 0x000F) == 0x0002;

    public bool IsSymbolic { get; private set; }

    public bool HasTable(string tag) => _tables.ContainsKey(tag);

    public ReadOnlySpan<byte> GetTable(string tag) =>
        _tables.TryGetValue(tag, out var t) && t.Offset + t.Length <= _data.Length ? _data.AsSpan(t.Offset, t.Length) : ReadOnlySpan<byte>.Empty;

    public IEnumerable<string> TableTags => _tables.Keys;

    /// <summary>Glyph id for a Unicode code point (0 = .notdef).</summary>
    public int GetGlyphId(int codePoint)
    {
        if (_cmap.TryGetValue(codePoint, out int gid)) return gid;
        if (IsSymbolic && codePoint < 0x100 && _cmap.TryGetValue(0xF000 + codePoint, out gid)) return gid;
        return 0;
    }

    public IReadOnlyDictionary<int, int> CharacterMap => _cmap;

    /// <summary>Advance width in font units.</summary>
    public int GetAdvanceWidth(int glyphId)
    {
        if (_advanceWidths.Length == 0) return UnitsPerEm / 2;
        return glyphId < _advanceWidths.Length ? _advanceWidths[glyphId] : _advanceWidths[^1];
    }

    /// <summary>Converts font units to 1/1000 em (PDF glyph space).</summary>
    public double ToPdfUnits(double fontUnits) => fontUnits * 1000.0 / UnitsPerEm;

    // ------------------------------------------------------------------ table readers

    private void ReadHead()
    {
        var t = Offset("head");
        if (t < 0) throw new PdfException("Font has no head table.");
        UnitsPerEm = Math.Max((int)U16(t + 18), 16);
        XMin = S16(t + 36);
        YMin = S16(t + 38);
        XMax = S16(t + 40);
        YMax = S16(t + 42);
        MacStyle = U16(t + 44);
        IndexToLocFormat = S16(t + 50);
    }

    private void ReadHhea()
    {
        var t = Offset("hhea");
        if (t < 0) return;
        Ascender = S16(t + 4);
        Descender = S16(t + 6);
        LineGap = S16(t + 8);
        _numberOfHMetrics = U16(t + 34);
    }

    private int _numberOfHMetrics;

    private void ReadMaxp()
    {
        var t = Offset("maxp");
        NumGlyphs = t < 0 ? 0 : U16(t + 4);
    }

    private void ReadHmtx()
    {
        var t = Offset("hmtx");
        if (t < 0 || _numberOfHMetrics == 0) return;
        int count = Math.Max(NumGlyphs, _numberOfHMetrics);
        _advanceWidths = new ushort[count];
        ushort last = 0;
        for (int i = 0; i < count; i++)
        {
            if (i < _numberOfHMetrics && t + 4 * i + 2 <= _data.Length) last = U16(t + 4 * i);
            _advanceWidths[i] = last;
        }
    }

    private void ReadOs2()
    {
        var t = Offset("OS/2");
        CapHeight = (short)(Ascender * 0.7);
        XHeight = (short)(Ascender * 0.5);
        if (t < 0) return;
        int version = U16(t);
        WeightClass = U16(t + 4);
        FsType = U16(t + 8);
        short typoAscender = S16(t + 68), typoDescender = S16(t + 70);
        if (Ascender == 0)
        {
            Ascender = typoAscender;
            Descender = typoDescender;
        }
        if (version >= 2 && _tables["OS/2"].Length >= 90)
        {
            XHeight = S16(t + 86);
            CapHeight = S16(t + 88);
        }
    }

    private void ReadPost()
    {
        var t = Offset("post");
        if (t < 0) return;
        ItalicAngle = S16(t + 4) + U16(t + 6) / 65536.0;
        UnderlinePosition = S16(t + 8);
        UnderlineThickness = S16(t + 10);
        IsFixedPitch = U32(t + 12) != 0;
    }

    private void ReadName()
    {
        var t = Offset("name");
        if (t < 0) return;
        int count = U16(t + 2);
        int stringOffset = t + U16(t + 4);
        string? ps = null, family = null;
        for (int i = 0; i < count; i++)
        {
            int rec = t + 6 + 12 * i;
            int platform = U16(rec), encoding = U16(rec + 2), language = U16(rec + 4), nameId = U16(rec + 6);
            int length = U16(rec + 8), offset = U16(rec + 10);
            if (nameId != 6 && nameId != 1) continue;
            if (stringOffset + offset + length > _data.Length) continue;
            string value;
            if (platform == 3 || platform == 0) value = Encoding.BigEndianUnicode.GetString(_data, stringOffset + offset, length);
            else if (platform == 1 && encoding == 0) value = Encoding.Latin1.GetString(_data, stringOffset + offset, length);
            else continue;
            bool preferred = platform == 3 && (language == 0x409 || language == 0);
            if (nameId == 6 && (ps == null || preferred)) ps = value;
            if (nameId == 1 && (family == null || preferred)) family = value;
        }
        if (!string.IsNullOrWhiteSpace(ps))
            PostScriptName = new string(ps.Where(c => c > 32 && c < 127 && "[](){}<>/%".IndexOf(c) < 0).ToArray());
        FamilyName = family ?? PostScriptName;
    }

    private void ReadCmap()
    {
        var t = Offset("cmap");
        if (t < 0) return;
        int count = U16(t + 2);
        int best = -1, bestScore = -1;
        for (int i = 0; i < count; i++)
        {
            int rec = t + 4 + 8 * i;
            int platform = U16(rec), encoding = U16(rec + 2);
            int sub = t + (int)U32(rec + 4);
            if (sub >= _data.Length) continue;
            int format = U16(sub);
            int score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 6,
                (0, _, 12) => 5,
                (3, 1, 4) => 4,
                (0, _, 4) => 3,
                (3, 0, 4) => 2, // symbol
                (1, 0, 0) => 1,
                _ => -1,
            };
            if (score > bestScore)
            {
                bestScore = score;
                best = sub;
                IsSymbolic = platform == 3 && encoding == 0;
            }
        }
        if (best < 0) return;
        switch (U16(best))
        {
            case 4: ReadCmapFormat4(best); break;
            case 12: ReadCmapFormat12(best); break;
            case 0:
                for (int c = 0; c < 256; c++) _cmap[c] = _data[best + 6 + c];
                break;
        }
    }

    private void ReadCmapFormat4(int t)
    {
        int segCountX2 = U16(t + 6);
        int segCount = segCountX2 / 2;
        int endCodes = t + 14;
        int startCodes = endCodes + segCountX2 + 2;
        int idDeltas = startCodes + segCountX2;
        int idRangeOffsets = idDeltas + segCountX2;
        for (int s = 0; s < segCount; s++)
        {
            int end = U16(endCodes + 2 * s), start = U16(startCodes + 2 * s);
            short delta = S16(idDeltas + 2 * s);
            int rangeOffsetPos = idRangeOffsets + 2 * s;
            int rangeOffset = U16(rangeOffsetPos);
            if (start > end) continue;
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int gid;
                if (rangeOffset == 0) gid = (c + delta) & 0xFFFF;
                else
                {
                    int addr = rangeOffsetPos + rangeOffset + 2 * (c - start);
                    if (addr + 2 > _data.Length) continue;
                    gid = U16(addr);
                    if (gid != 0) gid = (gid + delta) & 0xFFFF;
                }
                if (gid != 0 && (gid < NumGlyphs || NumGlyphs == 0)) _cmap.TryAdd(c, gid);
            }
        }
    }

    private void ReadCmapFormat12(int t)
    {
        uint groups = U32(t + 12);
        for (uint g = 0; g < groups; g++)
        {
            int rec = t + 16 + (int)(12 * g);
            if (rec + 12 > _data.Length) break;
            uint start = U32(rec), end = U32(rec + 4), gid = U32(rec + 8);
            if (end - start > 0x10FFFF) continue;
            for (uint c = start; c <= end; c++) _cmap.TryAdd((int)c, (int)(gid + c - start));
        }
    }

    // ------------------------------------------------------------------ binary helpers

    internal int Offset(string tag) => _tables.TryGetValue(tag, out var t) && t.Offset < _data.Length ? t.Offset : -1;

    internal ushort U16(int pos) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(pos, 2));

    internal short S16(int pos) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(pos, 2));

    internal uint U32(int pos) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(pos, 4));

    private string Tag(int pos) => Encoding.ASCII.GetString(_data, pos, 4);
}
