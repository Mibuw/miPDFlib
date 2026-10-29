using System.Buffers.Binary;
using System.Text;

namespace MiPdf.Fonts.TrueType;

/// <summary>
/// Creates a subset of a TrueType (glyf-based) font. Glyph ids are kept unchanged (unused glyphs become
/// empty), so the subset can be used with <c>/CIDToGIDMap /Identity</c>.
/// </summary>
internal static class TrueTypeSubsetter
{
    private static readonly string[] TablesToKeep = { "head", "hhea", "maxp", "hmtx", "loca", "glyf", "cvt ", "fpgm", "prep", "OS/2", "name", "post" };

    public static byte[] Subset(TrueTypeFont font, IEnumerable<int> glyphIds)
    {
        if (font.IsCff) throw new InvalidOperationException("CFF fonts cannot be subset by this subsetter.");
        int numGlyphs = font.NumGlyphs;
        var loca = ReadLoca(font, numGlyphs);
        var glyf = font.GetTable("glyf");

        var used = new SortedSet<int> { 0 };
        var pending = new Stack<int>(glyphIds.Where(g => g >= 0 && g < numGlyphs));
        while (pending.Count > 0)
        {
            int gid = pending.Pop();
            if (!used.Add(gid) && gid != 0) continue;
            foreach (int component in GetComponents(glyf, loca, gid))
                if (component < numGlyphs && !used.Contains(component)) pending.Push(component);
        }

        // new glyf and loca (long format)
        using var newGlyf = new MemoryStream();
        var newLoca = new byte[(numGlyphs + 1) * 4];
        for (int gid = 0; gid < numGlyphs; gid++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(gid * 4), (uint)newGlyf.Length);
            if (!used.Contains(gid)) continue;
            int start = (int)loca[gid], end = (int)loca[gid + 1];
            if (end > start && end <= glyf.Length)
            {
                newGlyf.Write(glyf[start..end]);
                while (newGlyf.Length % 4 != 0) newGlyf.WriteByte(0);
            }
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(numGlyphs * 4), (uint)newGlyf.Length);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in TablesToKeep)
        {
            if (!font.HasTable(tag)) continue;
            byte[] data = tag switch
            {
                "glyf" => newGlyf.ToArray(),
                "loca" => newLoca,
                "head" => font.GetTable(tag).ToArray(),
                "post" => TruncatePost(font.GetTable(tag)),
                _ => font.GetTable(tag).ToArray(),
            };
            if (tag == "head")
            {
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 0); // checkSumAdjustment
                BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(50), 1); // long loca
            }
            tables[tag] = data;
        }
        if (!tables.ContainsKey("loca")) tables["loca"] = newLoca;
        if (!tables.ContainsKey("glyf")) tables["glyf"] = newGlyf.ToArray();
        return WriteFont(tables);
    }

    private static byte[] TruncatePost(ReadOnlySpan<byte> post)
    {
        if (post.Length < 32) return post.ToArray();
        var data = post[..32].ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x00030000); // version 3: no glyph names
        return data;
    }

    private static long[] ReadLoca(TrueTypeFont font, int numGlyphs)
    {
        var loca = font.GetTable("loca");
        var result = new long[numGlyphs + 1];
        bool shortFormat = font.IndexToLocFormat == 0;
        for (int i = 0; i <= numGlyphs; i++)
        {
            if (shortFormat)
                result[i] = 2 * i + 2 <= loca.Length ? BinaryPrimitives.ReadUInt16BigEndian(loca[(2 * i)..]) * 2L : result[Math.Max(0, i - 1)];
            else
                result[i] = 4 * i + 4 <= loca.Length ? BinaryPrimitives.ReadUInt32BigEndian(loca[(4 * i)..]) : result[Math.Max(0, i - 1)];
        }
        return result;
    }

    private static List<int> GetComponents(ReadOnlySpan<byte> glyf, long[] loca, int gid)
    {
        var result = new List<int>();
        if (gid + 1 >= loca.Length) return result;
        int start = (int)loca[gid], end = (int)loca[gid + 1];
        if (end - start < 10 || end > glyf.Length) return result;
        short contours = BinaryPrimitives.ReadInt16BigEndian(glyf[start..]);
        if (contours >= 0) return result;
        int p = start + 10;
        while (p + 4 <= end)
        {
            int flags = BinaryPrimitives.ReadUInt16BigEndian(glyf[p..]);
            int component = BinaryPrimitives.ReadUInt16BigEndian(glyf[(p + 2)..]);
            result.Add(component);
            p += 4;
            p += (flags & 0x0001) != 0 ? 4 : 2;        // ARG_1_AND_2_ARE_WORDS
            if ((flags & 0x0008) != 0) p += 2;          // WE_HAVE_A_SCALE
            else if ((flags & 0x0040) != 0) p += 4;     // WE_HAVE_AN_X_AND_Y_SCALE
            else if ((flags & 0x0080) != 0) p += 8;     // WE_HAVE_A_TWO_BY_TWO
            if ((flags & 0x0020) == 0) break;           // MORE_COMPONENTS
        }
        return result;
    }

    private static byte[] WriteFont(SortedDictionary<string, byte[]> tables)
    {
        int numTables = tables.Count;
        int entrySelector = (int)Math.Floor(Math.Log2(numTables));
        int searchRange = (1 << entrySelector) * 16;
        int headerLength = 12 + 16 * numTables;
        using var ms = new MemoryStream();
        var header = new byte[headerLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)(numTables * 16 - searchRange));
        ms.Write(header);
        int index = 0;
        int headOffset = -1;
        foreach (var (tag, data) in tables)
        {
            int offset = (int)ms.Length;
            if (tag == "head") headOffset = offset;
            int rec = 12 + 16 * index++;
            Encoding.ASCII.GetBytes(tag).CopyTo(header, rec);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 12), (uint)data.Length);
            ms.Write(data);
            while (ms.Length % 4 != 0) ms.WriteByte(0);
        }
        var result = ms.ToArray();
        header.CopyTo(result, 0);
        if (headOffset >= 0)
        {
            uint adjustment = unchecked(0xB1B0AFBA - Checksum(result));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(headOffset + 8), adjustment);
        }
        return result;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i)));
        if (i < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data.AsSpan(i).CopyTo(last);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(last));
        }
        return sum;
    }
}
