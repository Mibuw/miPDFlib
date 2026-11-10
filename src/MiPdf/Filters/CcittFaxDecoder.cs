using MiPdf.Objects;

namespace MiPdf.Filters;

/// <summary>
/// CCITTFaxDecode (ISO 32000-2, 7.4.6): Group 3 one-dimensional (K = 0), Group 3 two-dimensional (K &gt; 0) and Group 4
/// (K &lt; 0) fax data, as in scanned and faxed documents. The result has one bit per pixel, rows padded to whole bytes;
/// black pixels are 0 bits unless /BlackIs1 is true. Damaged data ends the image: the remaining rows stay white.
/// </summary>
internal static class CcittFaxDecoder
{
    private const int White = 0, Black = 1;
    private const int MaxColumns = 1_000_000;

    // ITU-T T.4 code tables: "code:run length"
    private const string WhiteCodes =
        "00110101:0 000111:1 0111:2 1000:3 1011:4 1100:5 1110:6 1111:7 10011:8 10100:9 00111:10 01000:11 001000:12 " +
        "000011:13 110100:14 110101:15 101010:16 101011:17 0100111:18 0001100:19 0001000:20 0010111:21 0000011:22 " +
        "0000100:23 0101000:24 0101011:25 0010011:26 0100100:27 0011000:28 00000010:29 00000011:30 00011010:31 " +
        "00011011:32 00010010:33 00010011:34 00010100:35 00010101:36 00010110:37 00010111:38 00101000:39 00101001:40 " +
        "00101010:41 00101011:42 00101100:43 00101101:44 00000100:45 00000101:46 00001010:47 00001011:48 01010010:49 " +
        "01010011:50 01010100:51 01010101:52 00100100:53 00100101:54 01011000:55 01011001:56 01011010:57 01011011:58 " +
        "01001010:59 01001011:60 00110010:61 00110011:62 00110100:63 " +
        "11011:64 10010:128 010111:192 0110111:256 00110110:320 00110111:384 01100100:448 01100101:512 01101000:576 " +
        "01100111:640 011001100:704 011001101:768 011010010:832 011010011:896 011010100:960 011010101:1024 " +
        "011010110:1088 011010111:1152 011011000:1216 011011001:1280 011011010:1344 011011011:1408 010011000:1472 " +
        "010011001:1536 010011010:1600 011000:1664 010011011:1728";

    private const string BlackCodes =
        "0000110111:0 010:1 11:2 10:3 011:4 0011:5 0010:6 00011:7 000101:8 000100:9 0000100:10 0000101:11 0000111:12 " +
        "00000100:13 00000111:14 000011000:15 0000010111:16 0000011000:17 0000001000:18 00001100111:19 00001101000:20 " +
        "00001101100:21 00000110111:22 00000101000:23 00000010111:24 00000011000:25 000011001010:26 000011001011:27 " +
        "000011001100:28 000011001101:29 000001101000:30 000001101001:31 000001101010:32 000001101011:33 " +
        "000011010010:34 000011010011:35 000011010100:36 000011010101:37 000011010110:38 000011010111:39 " +
        "000001101100:40 000001101101:41 000011011010:42 000011011011:43 000001010100:44 000001010101:45 " +
        "000001010110:46 000001010111:47 000001100100:48 000001100101:49 000001010010:50 000001010011:51 " +
        "000000100100:52 000000110111:53 000000111000:54 000000100111:55 000000101000:56 000001011000:57 " +
        "000001011001:58 000000101011:59 000000101100:60 000001011010:61 000001100110:62 000001100111:63 " +
        "0000001111:64 000011001000:128 000011001001:192 000001011011:256 000000110011:320 000000110100:384 " +
        "000000110101:448 0000001101100:512 0000001101101:576 0000001001010:640 0000001001011:704 0000001001100:768 " +
        "0000001001101:832 0000001110010:896 0000001110011:960 0000001110100:1024 0000001110101:1088 " +
        "0000001110110:1152 0000001110111:1216 0000001010010:1280 0000001010011:1344 0000001010100:1408 " +
        "0000001010101:1472 0000001011010:1536 0000001011011:1600 0000001100100:1664 0000001100101:1728";

    // extended make-up codes, the same for both colors
    private const string CommonCodes =
        "00000001000:1792 00000001100:1856 00000001101:1920 000000010010:1984 000000010011:2048 000000010100:2112 " +
        "000000010101:2176 000000010110:2240 000000010111:2304 000000011100:2368 000000011101:2432 000000011110:2496 " +
        "000000011111:2560";

    private enum Mode { Pass, Horizontal, Vertical, End }

    // two-dimensional mode codes: code -> (mode, vertical offset)
    private static readonly Dictionary<(int Length, int Code), (Mode Mode, int Offset)> ModeCodes = new()
    {
        [(4, 0b0001)] = (Mode.Pass, 0),
        [(3, 0b001)] = (Mode.Horizontal, 0),
        [(1, 0b1)] = (Mode.Vertical, 0),
        [(3, 0b011)] = (Mode.Vertical, 1),
        [(6, 0b000011)] = (Mode.Vertical, 2),
        [(7, 0b0000011)] = (Mode.Vertical, 3),
        [(3, 0b010)] = (Mode.Vertical, -1),
        [(6, 0b000010)] = (Mode.Vertical, -2),
        [(7, 0b0000010)] = (Mode.Vertical, -3),
    };

    private static readonly Dictionary<(int Length, int Code), int>[] RunCodes = { Table(WhiteCodes), Table(BlackCodes) };

    private static Dictionary<(int Length, int Code), int> Table(string codes)
    {
        var table = new Dictionary<(int, int), int>();
        foreach (var entry in (codes + " " + CommonCodes).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            table[(parts[0].Length, Convert.ToInt32(parts[0], 2))] = int.Parse(parts[1]);
        }
        return table;
    }

    public static byte[] Decode(byte[] data, PdfDictionary? parms)
    {
        int k = parms?.GetInt(PdfName.Get("K")) ?? 0;
        int columns = parms?.GetInt(PdfName.Get("Columns"), 1728) ?? 1728;
        int rows = Math.Max(0, parms?.GetInt(PdfName.Get("Rows")) ?? 0);
        bool byteAlign = parms?.GetBool(PdfName.Get("EncodedByteAlign")) ?? false;
        bool blackIs1 = parms?.GetBool(PdfName.Get("BlackIs1")) ?? false;
        if (columns <= 0 || columns > MaxColumns) throw new PdfException($"Invalid CCITT /Columns {columns}.");
        int rowBytes = (columns + 7) / 8;
        if (rows > 0) PdfLimits.Current.CheckDecodedSize((long)rows * rowBytes);

        var reader = new BitReader(data);
        var output = new List<byte[]>();
        var reference = new List<int>(); // changing elements of the previous row (all white at the start)
        var coding = new List<int>();
        while (rows == 0 || output.Count < rows)
        {
            bool twoDimensional = k < 0;
            if (k >= 0)
            {
                // Group 3: an optional end-of-line code (with fill bits) before each row
                bool endOfLine = SkipEndOfLine(reader);
                if (endOfLine && IsEndOfLine(reader)) break; // RTC: several EOLs end the data
                if (!endOfLine && byteAlign) reader.AlignToByte();
                if (k > 0) twoDimensional = reader.Read(1) == 0;
            }
            else if (byteAlign) reader.AlignToByte();
            if (reader.AtEnd) break;
            coding.Clear();
            bool ok = twoDimensional ? DecodeTwoDimensional(reader, reference, coding, columns) : DecodeOneDimensional(reader, coding, columns);
            if (!ok)
            {
                // end of block (EOFB) or damaged data: keep the rows decoded so far
                break;
            }
            output.Add(Pixels(coding, columns, rowBytes, blackIs1));
            PdfLimits.Current.CheckDecodedSize((long)output.Count * rowBytes);
            (reference, coding) = (coding, reference);
        }

        int count = rows > 0 ? rows : output.Count;
        var result = new byte[(long)count * rowBytes];
        if (!blackIs1) Array.Fill(result, (byte)0xFF);
        for (int i = 0; i < output.Count && i < count; i++) output[i].CopyTo(result, i * rowBytes);
        return result;
    }

    /// <summary>
    /// MMR (Group 4) data inside a JBIG2 stream (ITU-T T.88, 6.2.6): <paramref name="rows"/> rows starting at byte
    /// <paramref name="offset"/>, one byte per pixel (1 = black). An end-of-block code after the rows is skipped;
    /// <paramref name="end"/> is the byte position after the data.
    /// </summary>
    public static byte[] DecodeMmr(byte[] data, int offset, int length, int columns, int rows, out int end)
    {
        // collective bitmaps of JBIG2 symbol dictionaries can be very wide; the decoded size limit applies
        if (columns <= 0 || rows < 0) throw new PdfException("Invalid MMR bitmap size.");
        PdfLimits.Current.CheckDecodedSize((long)columns * rows);
        var pixels = new byte[(long)columns * rows];
        var slice = data.AsSpan(offset, Math.Max(0, Math.Min(length, data.Length - offset))).ToArray();
        var reader = new BitReader(slice);
        var reference = new List<int>();
        var coding = new List<int>();
        for (int y = 0; y < rows; y++)
        {
            coding.Clear();
            if (!DecodeTwoDimensional(reader, reference, coding, columns)) break;
            // the changing elements are the starts of the black and white runs, starting with black
            for (int i = 0; i < coding.Count; i += 2)
            {
                int from = coding[i], to = i + 1 < coding.Count ? coding[i + 1] : columns;
                for (int x = Math.Max(0, from); x < Math.Min(columns, to); x++) pixels[(long)y * columns + x] = 1;
            }
            (reference, coding) = (coding, reference);
        }
        // end of block: two EOL codes (000000000001 000000000001)
        if (reader.PeekBits(24) == 0x001001) reader.Read(24);
        end = offset + (int)((reader.Position + 7) / 8);
        return pixels;
    }

    /// <summary>A row with changing elements at the given positions (the row starts white).</summary>
    private static byte[] Pixels(List<int> changes, int columns, int rowBytes, bool blackIs1)
    {
        var row = new byte[rowBytes];
        if (!blackIs1) Array.Fill(row, (byte)0xFF);
        for (int i = 0; i < changes.Count; i += 2)
        {
            int start = changes[i], end = i + 1 < changes.Count ? changes[i + 1] : columns;
            for (int x = start; x < end; x++)
            {
                byte bit = (byte)(0x80 >> (x & 7));
                if (blackIs1) row[x >> 3] |= bit;
                else row[x >> 3] &= (byte)~bit;
            }
        }
        return row;
    }

    private static void AddChange(List<int> changes, int position, int columns)
    {
        position = Math.Clamp(position, changes.Count > 0 ? changes[^1] : 0, columns);
        if (position < columns) changes.Add(position);
    }

    private static bool DecodeOneDimensional(BitReader reader, List<int> coding, int columns)
    {
        int position = 0, color = White;
        while (position < columns)
        {
            int run = ReadRun(reader, color);
            if (run < 0) return false;
            position += run;
            AddChange(coding, position, columns);
            color ^= 1;
        }
        return true;
    }

    private static bool DecodeTwoDimensional(BitReader reader, List<int> reference, List<int> coding, int columns)
    {
        int a0 = -1, color = White, first = 0;
        for (int guard = 0; a0 < columns; guard++)
        {
            if (guard > 2 * columns + 16) return false; // no progress: damaged data
            var (mode, offset) = ReadMode(reader);
            // b1: first changing element of the reference row right of a0 that changes to the opposite color. a0 only
            // moves right, so the first element right of it is found from the previous one (rows can have millions of
            // changes in JBIG2 collective bitmaps); the elements do not decrease, so b1 is that one or the next.
            while (first < reference.Count && reference[first] <= a0) first++;
            int index = first < reference.Count && (first & 1) != color ? first + 1 : first;
            int b1 = index < reference.Count ? reference[index] : columns;
            int b2 = index + 1 < reference.Count ? reference[index + 1] : columns;
            switch (mode)
            {
                case Mode.Pass:
                    a0 = b2;
                    break;
                case Mode.Horizontal:
                    int start = Math.Max(a0, 0);
                    int run1 = ReadRun(reader, color), run2 = run1 < 0 ? -1 : ReadRun(reader, color ^ 1);
                    if (run2 < 0) return false;
                    AddChange(coding, start + run1, columns);
                    AddChange(coding, start + run1 + run2, columns);
                    a0 = start + run1 + run2;
                    break;
                case Mode.Vertical:
                    int a1 = Math.Clamp(b1 + offset, Math.Max(a0, 0), columns);
                    AddChange(coding, a1, columns);
                    a0 = a1;
                    color ^= 1;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static (Mode Mode, int Offset) ReadMode(BitReader reader)
    {
        int code = 0;
        for (int length = 1; length <= 7; length++)
        {
            int bit = reader.Read(1);
            if (bit < 0) return (Mode.End, 0);
            code = (code << 1) | bit;
            if (ModeCodes.TryGetValue((length, code), out var mode)) return mode;
        }
        return (Mode.End, 0); // EOL/EOFB, extensions or damaged data
    }

    /// <summary>Reads make-up codes and the terminating code of one run; -1 for invalid or missing data.</summary>
    private static int ReadRun(BitReader reader, int color)
    {
        int total = 0;
        for (int guard = 0; guard < 1000; guard++)
        {
            int code = 0, run = -1;
            for (int length = 1; length <= 13 && run < 0; length++)
            {
                int bit = reader.Read(1);
                if (bit < 0) return -1;
                code = (code << 1) | bit;
                if (length >= 2 && RunCodes[color].TryGetValue((length, code), out int value)) run = value;
            }
            if (run < 0) return -1;
            total += run;
            if (run < 64) return total;
        }
        return -1;
    }

    /// <summary>Skips fill bits and an end-of-line code (000000000001) if present; returns true if one was skipped.</summary>
    private static bool SkipEndOfLine(BitReader reader)
    {
        if (!IsEndOfLine(reader)) return false;
        while (reader.PeekAt(0) == 0) reader.Read(1);
        reader.Read(1);
        return true;
    }

    /// <summary>True if the next bits are an end-of-line code, possibly preceded by fill bits.</summary>
    private static bool IsEndOfLine(BitReader reader)
    {
        int zeros = 0;
        while (zeros < 64)
        {
            int bit = reader.PeekAt(zeros);
            if (bit != 0) return bit == 1 && zeros >= 11;
            zeros++;
        }
        return false;
    }

    private sealed class BitReader(byte[] data)
    {
        private long _position; // in bits

        public long Position => _position;

        public bool AtEnd => _position >= (long)data.Length * 8;

        public int PeekBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = PeekAt(i);
                if (bit < 0) return -1;
                value = (value << 1) | bit;
            }
            return value;
        }

        public int PeekAt(int offset)
        {
            long bit = _position + offset;
            if (bit >= (long)data.Length * 8) return -1;
            return (data[bit >> 3] >> (7 - (int)(bit & 7))) & 1;
        }

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = PeekAt(0);
                if (bit < 0) return -1;
                value = (value << 1) | bit;
                _position++;
            }
            return value;
        }

        public void AlignToByte() => _position = (_position + 7) & ~7L;
    }
}
