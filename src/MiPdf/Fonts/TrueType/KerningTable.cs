using System.Buffers.Binary;

namespace MiPdf.Fonts.TrueType;

/// <summary>
/// Pair kerning from the OpenType GPOS table (lookup type 2 of the "kern" feature, formats 1 and 2) or the legacy
/// "kern" table (format 0). Values are in font units; negative values move glyphs closer together.
/// </summary>
internal sealed class KerningTable
{
    private readonly Dictionary<(int, int), int> _pairs = new();
    private readonly List<ClassPairs> _classPairs = new();

    private sealed class ClassPairs
    {
        public required HashSet<int> Coverage;
        public required Dictionary<int, int> Class1;
        public required Dictionary<int, int> Class2;
        public required int Class2Count;
        public required short[] Values; // [class1 * class2Count + class2]
    }

    private KerningTable() { }

    public static KerningTable Load(TrueTypeFont font)
    {
        var table = new KerningTable();
        try
        {
            var gpos = font.GetTable("GPOS");
            if (gpos.Length > 0) table.LoadGpos(gpos.ToArray());
            if (table._pairs.Count == 0 && table._classPairs.Count == 0)
            {
                var kern = font.GetTable("kern");
                if (kern.Length > 0) table.LoadKern(kern.ToArray());
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            // malformed tables: no kerning
        }
        return table;
    }

    public bool IsEmpty => _pairs.Count == 0 && _classPairs.Count == 0;

    /// <summary>Kerning adjustment between two glyphs in font units.</summary>
    public int Get(int left, int right)
    {
        if (_pairs.TryGetValue((left, right), out int value)) return value;
        foreach (var cp in _classPairs)
        {
            if (!cp.Coverage.Contains(left)) continue;
            int c1 = cp.Class1.GetValueOrDefault(left), c2 = cp.Class2.GetValueOrDefault(right);
            int index = c1 * cp.Class2Count + c2;
            if (index >= 0 && index < cp.Values.Length && cp.Values[index] != 0) return cp.Values[index];
        }
        return 0;
    }

    private static ushort U16(byte[] d, int p) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p, 2));

    private static short S16(byte[] d, int p) => BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(p, 2));

    private void LoadKern(byte[] d)
    {
        int version = U16(d, 0);
        if (version != 0) return; // Apple format not supported
        int count = U16(d, 2), pos = 4;
        for (int t = 0; t < count && pos + 6 <= d.Length; t++)
        {
            int length = U16(d, pos + 2), coverage = U16(d, pos + 4);
            bool horizontal = (coverage & 1) != 0, minimum = (coverage & 2) != 0, crossStream = (coverage & 4) != 0;
            if ((coverage >> 8) == 0 && horizontal && !minimum && !crossStream)
            {
                int pairs = U16(d, pos + 6);
                for (int i = 0, p = pos + 14; i < pairs && p + 6 <= d.Length; i++, p += 6)
                    _pairs.TryAdd((U16(d, p), U16(d, p + 2)), S16(d, p + 4));
            }
            pos += Math.Max(6, length);
        }
    }

    private void LoadGpos(byte[] d)
    {
        int featureList = U16(d, 6), lookupList = U16(d, 8);
        var lookups = new HashSet<int>();
        int featureCount = U16(d, featureList);
        for (int i = 0; i < featureCount; i++)
        {
            int rec = featureList + 2 + i * 6;
            if (System.Text.Encoding.ASCII.GetString(d, rec, 4) != "kern") continue;
            int feature = featureList + U16(d, rec + 4);
            int lookupCount = U16(d, feature + 2);
            for (int k = 0; k < lookupCount; k++) lookups.Add(U16(d, feature + 4 + k * 2));
        }
        int totalLookups = U16(d, lookupList);
        foreach (int index in lookups.OrderBy(x => x))
        {
            if (index >= totalLookups) continue;
            int lookup = lookupList + U16(d, lookupList + 2 + index * 2);
            int type = U16(d, lookup), subCount = U16(d, lookup + 4);
            for (int s = 0; s < subCount; s++)
            {
                int sub = lookup + U16(d, lookup + 6 + s * 2);
                int subType = type;
                if (type == 9) // extension
                {
                    subType = U16(d, sub + 2);
                    sub += (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(sub + 4, 4));
                }
                if (subType == 2) LoadPairPos(d, sub);
            }
        }
    }

    private void LoadPairPos(byte[] d, int sub)
    {
        int format = U16(d, sub);
        var coverage = ReadCoverage(d, sub + U16(d, sub + 2));
        int vf1 = U16(d, sub + 4), vf2 = U16(d, sub + 6);
        int size1 = ValueSize(vf1), size2 = ValueSize(vf2);
        int xAdvanceOffset = XAdvanceOffset(vf1);
        if (format == 1)
        {
            int setCount = U16(d, sub + 8);
            for (int i = 0; i < setCount && i < coverage.Count; i++)
            {
                int set = sub + U16(d, sub + 10 + i * 2);
                int pairCount = U16(d, set);
                int recordSize = 2 + size1 + size2;
                for (int k = 0; k < pairCount; k++)
                {
                    int rec = set + 2 + k * recordSize;
                    if (xAdvanceOffset < 0) continue;
                    short value = S16(d, rec + 2 + xAdvanceOffset);
                    if (value != 0) _pairs.TryAdd((coverage[i], U16(d, rec)), value);
                }
            }
        }
        else if (format == 2 && xAdvanceOffset >= 0)
        {
            var class1 = ReadClassDef(d, sub + U16(d, sub + 8));
            var class2 = ReadClassDef(d, sub + U16(d, sub + 10));
            int class1Count = U16(d, sub + 12), class2Count = U16(d, sub + 14);
            if ((long)class1Count * class2Count > 1_000_000) return;
            var values = new short[class1Count * class2Count];
            int recordSize = size1 + size2, pos = sub + 16;
            for (int c1 = 0; c1 < class1Count; c1++)
                for (int c2 = 0; c2 < class2Count; c2++, pos += recordSize)
                    values[c1 * class2Count + c2] = S16(d, pos + xAdvanceOffset);
            _classPairs.Add(new ClassPairs { Coverage = coverage.ToHashSet(), Class1 = class1, Class2 = class2, Class2Count = class2Count, Values = values });
        }
    }

    private static int ValueSize(int format) => System.Numerics.BitOperations.PopCount((uint)format & 0xFF) * 2;

    /// <summary>Byte offset of XAdvance within a value record, or -1 if absent.</summary>
    private static int XAdvanceOffset(int format)
    {
        if ((format & 0x0004) == 0) return -1;
        return ((format & 0x0001) != 0 ? 2 : 0) + ((format & 0x0002) != 0 ? 2 : 0);
    }

    private static List<int> ReadCoverage(byte[] d, int pos)
    {
        var glyphs = new List<int>();
        int format = U16(d, pos), count = U16(d, pos + 2);
        if (format == 1)
            for (int i = 0; i < count; i++) glyphs.Add(U16(d, pos + 4 + i * 2));
        else if (format == 2)
            for (int i = 0; i < count; i++)
            {
                int rec = pos + 4 + i * 6;
                int start = U16(d, rec), end = U16(d, rec + 2);
                for (int g = start; g <= end && glyphs.Count < 70000; g++) glyphs.Add(g);
            }
        return glyphs;
    }

    private static Dictionary<int, int> ReadClassDef(byte[] d, int pos)
    {
        var classes = new Dictionary<int, int>();
        int format = U16(d, pos);
        if (format == 1)
        {
            int start = U16(d, pos + 2), count = U16(d, pos + 4);
            for (int i = 0; i < count; i++) classes[start + i] = U16(d, pos + 6 + i * 2);
        }
        else if (format == 2)
        {
            int count = U16(d, pos + 2);
            for (int i = 0; i < count; i++)
            {
                int rec = pos + 4 + i * 6;
                int start = U16(d, rec), end = U16(d, rec + 2), cls = U16(d, rec + 4);
                for (int g = start; g <= end && classes.Count < 70000; g++) classes[g] = cls;
            }
        }
        return classes;
    }
}
