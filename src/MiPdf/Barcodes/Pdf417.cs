using System.Numerics;
using System.Text;

namespace MiPdf.Barcodes;

/// <summary>Options for <see cref="Pdf417.Create(string, Pdf417Options)"/>.</summary>
public sealed class Pdf417Options
{
    /// <summary>
    /// Error correction level 0-8 (2^(level+1) error correction codewords); null chooses the level ISO/IEC 15438
    /// recommends for the amount of data (2 to 5).
    /// </summary>
    public int? ErrorCorrectionLevel { get; set; }

    /// <summary>Number of data columns (1-30); null chooses a symbol about twice as wide as high.</summary>
    public int? Columns { get; set; }

    /// <summary>Height of a row in modules, 2-10 (default 3, the minimum the standard recommends).</summary>
    public int RowHeight { get; set; } = 3;
}

/// <summary>
/// PDF417 (ISO/IEC 15438), the stacked barcode of ID documents, boarding passes and shipping labels. Text, numeric and
/// byte compaction are chosen automatically; text with characters outside ASCII is encoded as UTF-8 with an ECI
/// designator.
/// </summary>
public sealed class Pdf417 : MatrixBarcode
{
    private const int TextLatch = 900, ByteLatch = 901, NumericLatch = 902, ByteLatchSix = 924, Eci = 927;
    private const int StartPattern = 0x1FEA8, StopPattern = 0x3FA29; // 17 and 18 modules

    private Pdf417(string text, bool[,] modules, int level, int columns, int rows) : base(text, modules, 2)
    {
        ErrorCorrectionLevel = level;
        DataColumns = columns;
        CodewordRows = rows;
    }

    /// <summary>The error correction level used (0-8).</summary>
    public int ErrorCorrectionLevel { get; }

    /// <summary>The number of data columns (codewords per row).</summary>
    public int DataColumns { get; }

    /// <summary>The number of rows of codewords (each <see cref="Pdf417Options.RowHeight"/> modules high).</summary>
    public int CodewordRows { get; }

    /// <summary>Encodes text (control characters such as the separators of driver's licence data are allowed).</summary>
    public static Pdf417 Create(string text, Pdf417Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var codewords = new List<int>();
        if (text.Any(c => c > 0x7F))
        {
            // UTF-8 with ECI 000026
            codewords.Add(Eci);
            codewords.Add(26);
            EncodeBytes(Encoding.UTF8.GetBytes(text), codewords);
        }
        else
        {
            EncodeHighLevel(Encoding.ASCII.GetBytes(text), codewords);
        }
        return Build(text, codewords, options ?? new Pdf417Options());
    }

    /// <summary>Encodes binary data (byte compaction, without a character set designator).</summary>
    public static Pdf417 Create(byte[] data, Pdf417Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var codewords = new List<int>();
        EncodeBytes(data, codewords);
        return Build(Convert.ToHexString(data), codewords, options ?? new Pdf417Options());
    }

    // ------------------------------------------------------------------ high-level encoding

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static bool IsText(byte b) => b is >= 32 and <= 126 or 9 or 10 or 13;

    private static int DigitRun(byte[] data, int start)
    {
        int i = start;
        while (i < data.Length && IsDigit(data[i])) i++;
        return i - start;
    }

    /// <summary>The length of the text run at <paramref name="start"/>, ending before 13 or more digits.</summary>
    private static int TextRun(byte[] data, int start)
    {
        int i = start;
        while (i < data.Length && IsText(data[i]))
        {
            int digits = DigitRun(data, i);
            if (digits >= 13) break;
            i += digits > 0 ? digits : 1;
        }
        return i - start;
    }

    /// <summary>Numeric compaction for 13 or more digits, text compaction for 5 or more text characters, else bytes.</summary>
    private static void EncodeHighLevel(byte[] data, List<int> codewords)
    {
        bool textMode = true; // the symbol starts in text compaction
        int submode = 0;
        int p = 0;
        while (p < data.Length)
        {
            int digits = DigitRun(data, p);
            if (digits >= 13)
            {
                codewords.Add(NumericLatch);
                EncodeNumeric(data, p, digits, codewords);
                textMode = false;
                p += digits;
                continue;
            }
            int text = TextRun(data, p);
            if (text >= 5 || (text > 0 && p + text == data.Length && p == 0))
            {
                if (!textMode)
                {
                    codewords.Add(TextLatch);
                    textMode = true;
                    submode = 0;
                }
                submode = EncodeText(data, p, text, codewords, submode);
                p += text;
                continue;
            }
            // bytes up to the next run of 13 digits or 5 text characters
            int end = p;
            while (end < data.Length)
            {
                if (DigitRun(data, end) >= 13 || TextRun(data, end) >= 5) break;
                end++;
            }
            if (end == p) end = p + 1;
            EncodeBytes(data[p..end], codewords);
            textMode = false;
            p = end;
        }
    }

    private const int Alpha = 0, Lower = 1, Mixed = 2, Punctuation = 3;
    private const string MixedChars = "0123456789&\r\t,:#-.$/+%*=^";
    private const string PunctuationChars = ";<>@[\\]_`~!\r\t,:\n-.$/\"|*()?{}'";

    private static bool IsAlpha(byte b) => b is >= (byte)'A' and <= (byte)'Z' || b == ' ';

    private static bool IsLower(byte b) => b is >= (byte)'a' and <= (byte)'z' || b == ' ';

    private static bool IsMixed(byte b) => MixedChars.IndexOf((char)b) >= 0;

    private static bool IsPunctuation(byte b) => PunctuationChars.IndexOf((char)b) >= 0;

    /// <summary>Text compaction with the sub-modes alpha, lower, mixed and punctuation; returns the sub-mode at the end.</summary>
    private static int EncodeText(byte[] data, int start, int count, List<int> codewords, int submode)
    {
        var values = new List<int>();
        for (int i = start; i < start + count; i++)
        {
            byte c = data[i];
            switch (submode)
            {
                case Alpha:
                    if (IsAlpha(c)) values.Add(c == ' ' ? 26 : c - 'A');
                    else if (IsLower(c)) { values.Add(27); submode = Lower; i--; }
                    else if (IsMixed(c)) { values.Add(28); submode = Mixed; i--; }
                    else { values.Add(29); values.Add(PunctuationChars.IndexOf((char)c)); } // shift
                    break;
                case Lower:
                    if (IsLower(c)) values.Add(c == ' ' ? 26 : c - 'a');
                    else if (c is >= (byte)'A' and <= (byte)'Z') { values.Add(27); values.Add(c - 'A'); } // shift to alpha
                    else if (IsMixed(c)) { values.Add(28); submode = Mixed; i--; }
                    else { values.Add(29); values.Add(PunctuationChars.IndexOf((char)c)); }
                    break;
                case Mixed:
                    if (IsMixed(c)) values.Add(MixedChars.IndexOf((char)c));
                    else if (c == ' ') values.Add(26);
                    else if (c is >= (byte)'A' and <= (byte)'Z') { values.Add(28); submode = Alpha; i--; }
                    else if (c is >= (byte)'a' and <= (byte)'z') { values.Add(27); submode = Lower; i--; }
                    else if (i + 1 < start + count && !IsMixed(data[i + 1]) && IsPunctuation(data[i + 1]))
                    {
                        values.Add(25); // latch to punctuation for two or more punctuation characters
                        submode = Punctuation;
                        i--;
                    }
                    else { values.Add(29); values.Add(PunctuationChars.IndexOf((char)c)); }
                    break;
                default:
                    if (IsPunctuation(c)) values.Add(PunctuationChars.IndexOf((char)c));
                    else { values.Add(29); submode = Alpha; i--; } // latch back to alpha
                    break;
            }
        }
        if (values.Count % 2 == 1) values.Add(29); // padding
        for (int i = 0; i < values.Count; i += 2) codewords.Add(30 * values[i] + values[i + 1]);
        return submode;
    }

    /// <summary>Numeric compaction: groups of up to 44 digits as base-900 numbers (with a leading 1).</summary>
    private static void EncodeNumeric(byte[] data, int start, int count, List<int> codewords)
    {
        for (int p = start; p < start + count; p += 44)
        {
            int length = Math.Min(44, start + count - p);
            var value = BigInteger.Parse("1" + Encoding.ASCII.GetString(data, p, length));
            var group = new List<int>();
            while (value > 0)
            {
                group.Add((int)(value % 900));
                value /= 900;
            }
            group.Reverse();
            codewords.AddRange(group);
        }
    }

    /// <summary>Byte compaction: six bytes as five base-900 codewords, remaining bytes one codeword each.</summary>
    private static void EncodeBytes(byte[] data, List<int> codewords)
    {
        codewords.Add(data.Length % 6 == 0 ? ByteLatchSix : ByteLatch);
        int p = 0;
        for (; p + 6 <= data.Length; p += 6)
        {
            long value = 0;
            for (int i = 0; i < 6; i++) value = (value << 8) | data[p + i];
            var group = new int[5];
            for (int i = 4; i >= 0; i--)
            {
                group[i] = (int)(value % 900);
                value /= 900;
            }
            codewords.AddRange(group);
        }
        for (; p < data.Length; p++) codewords.Add(data[p]);
    }

    // ------------------------------------------------------------------ symbol

    private static int RecommendedLevel(int dataCodewords) => dataCodewords switch
    {
        <= 40 => 2,
        <= 160 => 3,
        <= 320 => 4,
        _ => 5,
    };

    private static Pdf417 Build(string text, List<int> data, Pdf417Options options)
    {
        if (options.RowHeight is < 2 or > 10) throw new ArgumentOutOfRangeException(nameof(options), "RowHeight must be 2-10.");
        int level = options.ErrorCorrectionLevel ?? RecommendedLevel(data.Count + 1);
        if (level is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(options), "ErrorCorrectionLevel must be 0-8.");
        if (options.Columns is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(options), "Columns must be 1-30.");
        int ecCount = 2 << level;
        int needed = data.Count + 1 + ecCount; // with the symbol length descriptor
        if (needed > 928) throw new ArgumentException("The data does not fit into a PDF417 symbol.");

        int columns = 0, rows = 0;
        double best = double.MaxValue;
        for (int c = options.Columns ?? 1; c <= (options.Columns ?? 30); c++)
        {
            int r = Math.Max(3, (needed + c - 1) / c);
            if (r > 90 || r * c > 928) continue;
            // a symbol about twice as wide as high (in modules), with as few padding codewords as possible
            double ratio = (17.0 * c + 69) / (r * options.RowHeight);
            double score = Math.Abs(Math.Log(ratio / 2)) + (r * c - needed) / 100.0;
            if (score < best)
            {
                best = score;
                columns = c;
                rows = r;
            }
        }
        if (columns == 0) throw new ArgumentException("The data does not fit into a PDF417 symbol with this number of columns.");

        // symbol length descriptor, data, padding, error correction
        int dataCount = rows * columns - ecCount;
        var codewords = new List<int>(rows * columns) { dataCount };
        codewords.AddRange(data);
        while (codewords.Count < dataCount) codewords.Add(TextLatch);
        codewords.AddRange(ErrorCorrection(codewords, ecCount));

        int width = 17 * (columns + 4) + 1;
        var modules = new bool[rows * options.RowHeight, width];
        for (int r = 0; r < rows; r++)
        {
            int cluster = r % 3;
            int left = 30 * (r / 3) + cluster switch
            {
                0 => (rows - 1) / 3,
                1 => level * 3 + (rows - 1) % 3,
                _ => columns - 1,
            };
            int right = 30 * (r / 3) + cluster switch
            {
                0 => columns - 1,
                1 => (rows - 1) / 3,
                _ => level * 3 + (rows - 1) % 3,
            };
            var table = Pdf417Patterns.Codewords[cluster];
            int x = 0;
            void Put(int pattern, int length)
            {
                for (int i = length - 1; i >= 0; i--, x++)
                    if (((pattern >> i) & 1) != 0)
                        for (int y = 0; y < options.RowHeight; y++) modules[r * options.RowHeight + y, x] = true;
            }
            Put(StartPattern, 17);
            Put(table[left], 17);
            for (int c = 0; c < columns; c++) Put(table[codewords[r * columns + c]], 17);
            Put(table[right], 17);
            Put(StopPattern, 18);
        }
        return new Pdf417(text, modules, level, columns, rows);
    }

    /// <summary>Reed-Solomon error correction over GF(929) with the generator roots 3^1 ... 3^k (ISO/IEC 15438, 4.10).</summary>
    internal static int[] ErrorCorrection(List<int> data, int count)
    {
        // generator polynomial (x - 3)(x - 3^2)...(x - 3^k), coefficients lowest degree first (g[count] = 1)
        var g = new int[count + 1];
        g[0] = 1;
        int root = 1;
        for (int i = 1; i <= count; i++)
        {
            root = root * 3 % 929;
            for (int j = i; j >= 0; j--)
                g[j] = ((j > 0 ? g[j - 1] : 0) - g[j] * root % 929 + 929) % 929;
        }
        var e = new int[count];
        foreach (int d in data)
        {
            int t1 = (d + e[count - 1]) % 929;
            for (int j = count - 1; j >= 1; j--) e[j] = (e[j - 1] + 929 - t1 * g[j] % 929) % 929;
            e[0] = (929 - t1 * g[0] % 929) % 929;
        }
        var result = new int[count];
        for (int j = count - 1; j >= 0; j--) result[count - 1 - j] = e[j] == 0 ? 0 : 929 - e[j];
        return result;
    }
}
