using System.Text;

namespace MiPdf.Barcodes;

/// <summary>QR Code error correction level: the share of the symbol that can be damaged and still be read.</summary>
public enum QrErrorCorrection
{
    /// <summary>About 7 %.</summary>
    L,

    /// <summary>About 15 % (default).</summary>
    M,

    /// <summary>About 25 %.</summary>
    Q,

    /// <summary>About 30 %.</summary>
    H,
}

/// <summary>Options for <see cref="QrCode.Create(string, QrCodeOptions)"/>.</summary>
public sealed class QrCodeOptions
{
    public QrErrorCorrection ErrorCorrection { get; set; } = QrErrorCorrection.M;

    /// <summary>Smallest version (1-40) to use; larger versions are chosen automatically when the data does not fit.</summary>
    public int MinVersion { get; set; } = 1;

    /// <summary>Largest allowed version (1-40).</summary>
    public int MaxVersion { get; set; } = 40;

    /// <summary>Fixed mask pattern (0-7); null selects the best mask automatically.</summary>
    public int? Mask { get; set; }

    /// <summary>
    /// Adds an ECI designator for UTF-8 when the text contains non-ASCII characters. Off by default because
    /// payment codes (EPC/GiroCode) and most readers expect plain byte mode with UTF-8 data.
    /// </summary>
    public bool Utf8Eci { get; set; }
}

/// <summary>
/// QR Code (ISO/IEC 18004) model 2, versions 1-40, all error correction levels. Numeric, alphanumeric and byte
/// (UTF-8) mode are selected automatically.
/// </summary>
/// <example>
/// <code>
/// var qr = QrCode.Create("https://example.com");
/// canvas.DrawBarcode(qr, 450, 700, 80);   // 80 x 80 pt
/// </code>
/// </example>
public sealed class QrCode : MatrixBarcode
{
    private static readonly int[,] EccPerBlock =
    {
        { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly int[,] BlockCount =
    {
        { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private QrCode(string text, bool[,] modules, int version, QrErrorCorrection ecc, int mask) : base(text, modules, 4)
    {
        Version = version;
        ErrorCorrection = ecc;
        Mask = mask;
    }

    /// <summary>The symbol version (1-40); the size is 17 + 4 * version modules.</summary>
    public int Version { get; }

    public QrErrorCorrection ErrorCorrection { get; }

    /// <summary>The mask pattern used (0-7).</summary>
    public int Mask { get; }

    public static QrCode Create(string text, QrErrorCorrection errorCorrection = QrErrorCorrection.M) =>
        Create(text, new QrCodeOptions { ErrorCorrection = errorCorrection });

    public static QrCode Create(string text, QrCodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ecc = options.ErrorCorrection;
        Mode mode = text.All(char.IsAsciiDigit) ? Mode.Numeric : text.All(c => Alphanumeric.Contains(c)) ? Mode.Alphanumeric : Mode.Byte;
        byte[] bytes = mode == Mode.Byte ? Encoding.UTF8.GetBytes(text) : Encoding.ASCII.GetBytes(text);
        bool eci = mode == Mode.Byte && options.Utf8Eci && text.Any(c => c > 127);

        for (int version = Math.Max(1, options.MinVersion); version <= Math.Min(40, options.MaxVersion); version++)
        {
            int capacityBits = DataCodewords(version, ecc) * 8;
            var bits = EncodeSegment(mode, text, bytes, version, eci);
            if (bits == null || bits.Count > capacityBits) continue;
            var data = Finish(bits, capacityBits);
            var codewords = AddErrorCorrection(data, version, ecc);
            var (modules, mask) = BuildMatrix(codewords, version, ecc, options.Mask);
            return new QrCode(text, modules, version, ecc, mask);
        }
        throw new ArgumentException("The text is too long for a QR Code with the requested options.", nameof(text));
    }

    // ------------------------------------------------------------------ data encoding

    private enum Mode { Numeric = 1, Alphanumeric = 2, Byte = 4 }

    private sealed class BitBuffer : List<bool>
    {
        public void Append(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--) Add(((value >> i) & 1) != 0);
        }
    }

    private static BitBuffer? EncodeSegment(Mode mode, string text, byte[] bytes, int version, bool eci)
    {
        var bits = new BitBuffer();
        if (eci)
        {
            bits.Append(0b0111, 4);
            bits.Append(26, 8); // UTF-8
        }
        bits.Append((int)mode, 4);
        int countBits = mode switch
        {
            Mode.Numeric => version < 10 ? 10 : version < 27 ? 12 : 14,
            Mode.Alphanumeric => version < 10 ? 9 : version < 27 ? 11 : 13,
            _ => version < 10 ? 8 : 16,
        };
        int count = mode == Mode.Byte ? bytes.Length : text.Length;
        if (count >= 1 << countBits) return null;
        bits.Append(count, countBits);
        switch (mode)
        {
            case Mode.Numeric:
                for (int i = 0; i < text.Length; i += 3)
                {
                    int n = Math.Min(3, text.Length - i);
                    bits.Append(int.Parse(text.AsSpan(i, n)), n * 3 + 1);
                }
                break;
            case Mode.Alphanumeric:
                for (int i = 0; i < text.Length; i += 2)
                {
                    if (i + 1 < text.Length) bits.Append(Alphanumeric.IndexOf(text[i]) * 45 + Alphanumeric.IndexOf(text[i + 1]), 11);
                    else bits.Append(Alphanumeric.IndexOf(text[i]), 6);
                }
                break;
            default:
                foreach (byte b in bytes) bits.Append(b, 8);
                break;
        }
        return bits;
    }

    private static byte[] Finish(BitBuffer bits, int capacityBits)
    {
        bits.Append(0, Math.Min(4, capacityBits - bits.Count));
        bits.Append(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) bits.Append(pad, 8);
        var data = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) data[i >> 3] |= (byte)(0x80 >> (i & 7));
        return data;
    }

    private static int RawDataModules(int version)
    {
        int result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            int align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version, QrErrorCorrection ecc) =>
        RawDataModules(version) / 8 - EccPerBlock[(int)ecc, version] * BlockCount[(int)ecc, version];

    private static byte[] AddErrorCorrection(byte[] data, int version, QrErrorCorrection ecc)
    {
        int blocks = BlockCount[(int)ecc, version];
        int eccLength = EccPerBlock[(int)ecc, version];
        int raw = RawDataModules(version) / 8;
        int shortBlocks = blocks - raw % blocks;
        int shortLength = raw / blocks;
        var generator = GaloisField.Qr.Generator(eccLength, 0);
        var blockList = new List<byte[]>();
        for (int i = 0, k = 0; i < blocks; i++)
        {
            int dataLength = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            var block = data.AsSpan(k, dataLength);
            k += dataLength;
            var ecBytes = GaloisField.Qr.Remainder(block, generator);
            var full = new byte[shortLength + 1];
            block.CopyTo(full);
            // short blocks get a placeholder byte so that all blocks have equal length for interleaving
            ecBytes.CopyTo(full, full.Length - eccLength);
            blockList.Add(full);
        }
        var result = new List<byte>(raw);
        for (int i = 0; i < shortLength + 1; i++)
        {
            for (int j = 0; j < blocks; j++)
            {
                if (i != shortLength - eccLength || j >= shortBlocks) result.Add(blockList[j][i]);
            }
        }
        return result.ToArray();
    }

    // ------------------------------------------------------------------ matrix

    private static (bool[,] Modules, int Mask) BuildMatrix(byte[] codewords, int version, QrErrorCorrection ecc, int? fixedMask)
    {
        int size = version * 4 + 17;
        var modules = new bool[size, size];
        var function = new bool[size, size];

        void Set(int x, int y, bool dark)
        {
            modules[y, x] = dark;
            function[y, x] = true;
        }

        for (int i = 0; i < size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }
        void Finder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy)), x = cx + dx, y = cy + dy;
                if (x >= 0 && x < size && y >= 0 && y < size) Set(x, y, dist != 2 && dist != 4);
            }
        }
        Finder(3, 3);
        Finder(size - 4, 3);
        Finder(3, size - 4);

        var align = AlignmentPositions(version);
        for (int i = 0; i < align.Length; i++)
        for (int j = 0; j < align.Length; j++)
        {
            if ((i == 0 && j == 0) || (i == 0 && j == align.Length - 1) || (i == align.Length - 1 && j == 0)) continue;
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                Set(align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        void DrawFormat(int mask)
        {
            int data = FormatBits(ecc) << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) Set(8, i, Bit(i));
            Set(8, 7, Bit(6));
            Set(8, 8, Bit(7));
            Set(7, 8, Bit(8));
            for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));
            for (int i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(i));
            for (int i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(i));
            Set(8, size - 8, true);
        }
        DrawFormat(0);

        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(a, b, bit);
                Set(b, a, bit);
            }
        }

        // data in the zig-zag order, two columns at a time from the right
        int bitIndex = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (function[y, x] || bitIndex >= codewords.Length * 8) continue;
                    modules[y, x] = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                    bitIndex++;
                }
            }
        }

        int best = fixedMask ?? -1;
        if (best < 0)
        {
            int minPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(modules, function, mask);
                DrawFormat(mask);
                int penalty = Penalty(modules);
                if (penalty < minPenalty)
                {
                    minPenalty = penalty;
                    best = mask;
                }
                ApplyMask(modules, function, mask); // undo (XOR)
            }
        }
        ApplyMask(modules, function, best);
        DrawFormat(best);
        return (modules, best);
    }

    private static int FormatBits(QrErrorCorrection ecc) => ecc switch
    {
        QrErrorCorrection.L => 1,
        QrErrorCorrection.M => 0,
        QrErrorCorrection.Q => 3,
        _ => 2,
    };

    private static int[] AlignmentPositions(int version)
    {
        if (version == 1) return Array.Empty<int>();
        int count = version / 7 + 2;
        int size = version * 4 + 17;
        int step = version == 32 ? 26 : (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = size - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private static void ApplyMask(bool[,] modules, bool[,] function, int mask)
    {
        int size = modules.GetLength(0);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (function[y, x]) continue;
            bool invert = mask switch
            {
                0 => (x + y) % 2 == 0,
                1 => y % 2 == 0,
                2 => x % 3 == 0,
                3 => (x + y) % 3 == 0,
                4 => (x / 3 + y / 2) % 2 == 0,
                5 => x * y % 2 + x * y % 3 == 0,
                6 => (x * y % 2 + x * y % 3) % 2 == 0,
                _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
            };
            if (invert) modules[y, x] = !modules[y, x];
        }
    }

    private static int Penalty(bool[,] m)
    {
        int size = m.GetLength(0), penalty = 0, dark = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < size; a++)
            {
                int run = 0;
                bool color = false;
                for (int b = 0; b < size; b++)
                {
                    bool v = pass == 0 ? m[a, b] : m[b, a];
                    if (b > 0 && v == color) run++;
                    else
                    {
                        if (run >= 5) penalty += run - 2;
                        color = v;
                        run = 1;
                    }
                    // finder-like pattern 1011101 with 4 light modules on one side
                    if (b >= 10)
                    {
                        bool P(int k) => pass == 0 ? m[a, b - 10 + k] : m[b - 10 + k, a];
                        bool core = P(0) && !P(1) && P(2) && P(3) && P(4) && !P(5) && P(6);
                        bool coreRight = P(4) && !P(5) && P(6) && P(7) && P(8) && !P(9) && P(10);
                        if (core && !P(7) && !P(8) && !P(9) && !P(10)) penalty += 40;
                        if (coreRight && !P(0) && !P(1) && !P(2) && !P(3)) penalty += 40;
                    }
                }
                if (run >= 5) penalty += run - 2;
            }
        }
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (m[y, x]) dark++;
            if (x < size - 1 && y < size - 1 && m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) penalty += 3;
        }
        int total = size * size;
        int k2 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return penalty + k2 * 10;
    }
}
