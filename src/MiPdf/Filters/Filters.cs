using System.IO.Compression;
using MiPdf.Objects;

namespace MiPdf.Filters;

/// <summary>FlateDecode (zlib/deflate).</summary>
public static class FlateFilter
{
    public static byte[] Encode(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var z = new ZLibStream(output, level, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    /// <summary>
    /// Decodes zlib data. Tolerates missing/invalid zlib headers, bad checksums and truncated data
    /// (returns whatever could be decompressed), which are common in real-world files.
    /// </summary>
    public static byte[] Decode(byte[] data)
    {
        if (data.Length == 0) return data;
        bool hasZlibHeader = data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;
        if (hasZlibHeader)
        {
            var result = Inflate(new ZLibStream(new MemoryStream(data), CompressionMode.Decompress), out bool ok);
            if (ok || result.Length > 0) return result;
        }
        var raw = Inflate(new DeflateStream(new MemoryStream(data, hasZlibHeader ? 2 : 0, data.Length - (hasZlibHeader ? 2 : 0)),
            CompressionMode.Decompress), out _);
        return raw;
    }

    private static byte[] Inflate(Stream z, out bool ok)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        var limits = PdfLimits.Current;
        ok = true;
        try
        {
            using (z)
            {
                int n;
                while ((n = z.Read(buffer, 0, buffer.Length)) > 0)
                {
                    limits.CheckDecodedSize(output.Length + n); // before writing: keeps the buffer from growing beyond the limit
                    output.Write(buffer, 0, n);
                }
            }
        }
        catch (InvalidDataException)
        {
            ok = false;
        }
        return output.ToArray();
    }
}

/// <summary>PNG and TIFF predictors for Flate/LZW (ISO 32000-2, 7.4.4.4).</summary>
public static class Predictor
{
    public static byte[] Apply(byte[] data, PdfDictionary? parms)
    {
        if (parms is null) return data;
        int predictor = parms.GetInt(PdfName.Predictor, 1);
        if (predictor <= 1) return data;
        long colors = Math.Clamp(parms.GetLong(PdfName.Colors, 1), 1, 32);
        long bpc = Math.Clamp(parms.GetLong(PdfName.BitsPerComponent, 8), 1, 16);
        long columns = Math.Max(1, parms.GetLong(PdfName.Columns, 1));
        long bytesPerRow = (colors * bpc * Math.Min(columns, int.MaxValue) + 7) / 8;
        if (bytesPerRow > data.Length) return data; // inconsistent parameters: leave the data as it is
        int bpp = (int)Math.Max(1, (colors * bpc + 7) / 8);
        return predictor == 2 ? Tiff(data, (int)bytesPerRow, bpp, (int)bpc) : Png(data, (int)bytesPerRow, bpp);
    }

    private static byte[] Png(byte[] data, int rowLength, int bpp)
    {
        int rows = data.Length / (rowLength + 1);
        var output = new byte[rows * rowLength];
        var prior = new byte[rowLength];
        int pos = 0;
        for (int r = 0; r < rows; r++)
        {
            int type = data[pos++];
            var row = output.AsSpan(r * rowLength, rowLength);
            data.AsSpan(pos, rowLength).CopyTo(row);
            pos += rowLength;
            switch (type)
            {
                case 1: // Sub
                    for (int i = bpp; i < rowLength; i++) row[i] += row[i - bpp];
                    break;
                case 2: // Up
                    for (int i = 0; i < rowLength; i++) row[i] += prior[i];
                    break;
                case 3: // Average
                    for (int i = 0; i < rowLength; i++)
                        row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + prior[i]) / 2);
                    break;
                case 4: // Paeth
                    for (int i = 0; i < rowLength; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
                        row[i] += (byte)Paeth(a, b, c);
                    }
                    break;
            }
            row.CopyTo(prior);
        }
        return output;
    }

    internal static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Tiff(byte[] data, int rowLength, int bpp, int bpc)
    {
        if (bpc != 8) return data; // only the common 8-bit case is supported
        var output = (byte[])data.Clone();
        for (int rowStart = 0; rowStart + rowLength <= output.Length; rowStart += rowLength)
            for (int i = bpp; i < rowLength; i++)
                output[rowStart + i] += output[rowStart + i - bpp];
        return output;
    }
}

/// <summary>LZWDecode.</summary>
public static class LzwFilter
{
    public static byte[] Decode(byte[] data, int earlyChange = 1)
    {
        var output = new MemoryStream(data.Length * 3);
        var limits = PdfLimits.Current;
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < 256; i++) table.Add(new[] { (byte)i });
            table.Add(Array.Empty<byte>()); // 256 clear
            table.Add(Array.Empty<byte>()); // 257 EOD
        }
        Reset();
        int codeLength = 9;
        long bitBuffer = 0;
        int bitCount = 0;
        byte[]? previous = null;
        foreach (byte b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
            while (bitCount >= codeLength)
            {
                int code = (int)((bitBuffer >> (bitCount - codeLength)) & ((1 << codeLength) - 1));
                bitCount -= codeLength;
                if (code == 256)
                {
                    Reset();
                    codeLength = 9;
                    previous = null;
                    continue;
                }
                if (code == 257) return output.ToArray();
                byte[] entry;
                if (code < table.Count) entry = table[code];
                else if (previous != null && code == table.Count) entry = [.. previous, previous[0]];
                else return output.ToArray(); // corrupt data
                output.Write(entry);
                limits.CheckDecodedSize(output.Length);
                if (previous != null && table.Count < 4096) table.Add([.. previous, entry[0]]);
                previous = entry;
                int next = table.Count + earlyChange;
                codeLength = next >= 2048 ? 12 : next >= 1024 ? 11 : next >= 512 ? 10 : 9;
            }
        }
        return output.ToArray();
    }
}

/// <summary>ASCII85Decode.</summary>
public static class Ascii85Filter
{
    private static readonly byte[] ZeroGroup = new byte[4];

    public static byte[] Decode(byte[] data)
    {
        var output = new MemoryStream(data.Length * 4 / 5 + 4);
        Span<int> group = stackalloc int[5];
        int n = 0;
        int start = 0;
        if (data.Length >= 2 && data[0] == '<' && data[1] == '~') start = 2;
        for (int i = start; i < data.Length; i++)
        {
            byte c = data[i];
            if (c == '~') break;
            if (c is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0) continue;
            if (c == 'z' && n == 0)
            {
                output.Write(ZeroGroup);
                continue;
            }
            if (c < '!' || c > 'u') continue;
            group[n++] = c - '!';
            if (n == 5)
            {
                WriteGroup(output, group, 4);
                n = 0;
            }
        }
        if (n > 1)
        {
            for (int i = n; i < 5; i++) group[i] = 84;
            WriteGroup(output, group, n - 1);
        }
        return output.ToArray();
    }

    private static void WriteGroup(Stream output, Span<int> g, int count)
    {
        uint v = 0;
        for (int i = 0; i < 5; i++) v = unchecked(v * 85 + (uint)g[i]);
        Span<byte> b = stackalloc byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        output.Write(b[..count]);
    }
}

/// <summary>ASCIIHexDecode.</summary>
public static class AsciiHexFilter
{
    public static byte[] Decode(byte[] data)
    {
        var output = new MemoryStream(data.Length / 2 + 1);
        int high = -1;
        foreach (byte c in data)
        {
            if (c == '>') break;
            int v = HexValue(c);
            if (v < 0) continue;
            if (high < 0) high = v;
            else
            {
                output.WriteByte((byte)((high << 4) | v));
                high = -1;
            }
        }
        if (high >= 0) output.WriteByte((byte)(high << 4));
        return output.ToArray();
    }

    public static byte[] Encode(byte[] data)
    {
        var hex = Convert.ToHexString(data);
        return System.Text.Encoding.ASCII.GetBytes(hex + ">");
    }

    internal static int HexValue(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}

/// <summary>RunLengthDecode.</summary>
public static class RunLengthFilter
{
    public static byte[] Decode(byte[] data)
    {
        var output = new MemoryStream(data.Length * 2);
        var limits = PdfLimits.Current;
        int i = 0;
        while (i < data.Length)
        {
            limits.CheckDecodedSize(output.Length);
            int len = data[i++];
            if (len == 128) break;
            if (len < 128)
            {
                int count = Math.Min(len + 1, data.Length - i);
                output.Write(data, i, count);
                i += count;
            }
            else if (i < data.Length)
            {
                byte b = data[i++];
                for (int k = 0; k < 257 - len; k++) output.WriteByte(b);
            }
        }
        return output.ToArray();
    }
}
