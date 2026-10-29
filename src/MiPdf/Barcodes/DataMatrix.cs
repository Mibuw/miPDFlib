using System.Text;

namespace MiPdf.Barcodes;

/// <summary>Symbol shape selection for <see cref="DataMatrix"/>.</summary>
public enum DataMatrixShape
{
    /// <summary>Square symbols only (10x10 to 144x144, default).</summary>
    Square,

    /// <summary>Rectangular symbols only (8x18 to 16x48).</summary>
    Rectangle,

    /// <summary>The smallest square or rectangular symbol.</summary>
    Any,
}

/// <summary>
/// Data Matrix ECC 200 (ISO/IEC 16022). Text is encoded in ASCII mode (digit pairs compressed), binary/UTF-8
/// content in Base 256 mode. GS1 Data Matrix is supported via <see cref="CreateGs1"/>.
/// </summary>
public sealed class DataMatrix : MatrixBarcode
{
    // rows, columns, region rows, region columns (data area per region), data codewords, ecc codewords, blocks
    private static readonly (int Rows, int Cols, int RegionRows, int RegionCols, int Data, int Ecc, int Blocks)[] Symbols =
    {
        (10, 10, 8, 8, 3, 5, 1), (12, 12, 10, 10, 5, 7, 1), (8, 18, 6, 16, 5, 7, 1), (14, 14, 12, 12, 8, 10, 1),
        (8, 32, 6, 14, 10, 11, 1), (16, 16, 14, 14, 12, 12, 1), (12, 26, 10, 24, 16, 14, 1), (18, 18, 16, 16, 18, 14, 1),
        (20, 20, 18, 18, 22, 18, 1), (12, 36, 10, 16, 22, 18, 1), (22, 22, 20, 20, 30, 20, 1), (16, 36, 14, 16, 32, 24, 1),
        (24, 24, 22, 22, 36, 24, 1), (26, 26, 24, 24, 44, 28, 1), (16, 48, 14, 22, 49, 28, 1), (32, 32, 14, 14, 62, 36, 1),
        (36, 36, 16, 16, 86, 42, 1), (40, 40, 18, 18, 114, 48, 1), (44, 44, 20, 20, 144, 56, 1), (48, 48, 22, 22, 174, 68, 1),
        (52, 52, 24, 24, 204, 84, 2), (64, 64, 14, 14, 280, 112, 2), (72, 72, 16, 16, 368, 144, 4), (80, 80, 18, 18, 456, 192, 4),
        (88, 88, 20, 20, 576, 224, 4), (96, 96, 22, 22, 696, 272, 4), (104, 104, 24, 24, 816, 336, 6), (120, 120, 18, 18, 1050, 408, 6),
        (132, 132, 20, 20, 1304, 496, 8), (144, 144, 22, 22, 1558, 620, 10),
    };

    private const byte Fnc1 = 232;

    private DataMatrix(string text, bool[,] modules) : base(text, modules, 1) { }

    /// <summary>Encodes text (UTF-8 for non-Latin-1 characters is applied automatically via Base 256 mode).</summary>
    public static DataMatrix Create(string text, DataMatrixShape shape = DataMatrixShape.Square)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool latin1 = text.All(c => c <= 0xFF);
        byte[] bytes = latin1 ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text);
        return Create(text, bytes, shape);
    }

    /// <summary>Encodes binary data.</summary>
    public static DataMatrix Create(byte[] data, DataMatrixShape shape = DataMatrixShape.Square) =>
        Create(Convert.ToHexString(data), data, shape);

    /// <summary>
    /// Creates a GS1 Data Matrix from element strings in the form "(01)09501101530003(17)250101(10)ABC".
    /// FNC1 separators after variable-length fields are inserted automatically.
    /// </summary>
    public static DataMatrix CreateGs1(string elementStrings, DataMatrixShape shape = DataMatrixShape.Square)
    {
        var codewords = new List<byte> { Fnc1 };
        var fields = Gs1.Parse(elementStrings);
        for (int i = 0; i < fields.Count; i++)
        {
            EncodeAscii(Encoding.ASCII.GetBytes(fields[i].Ai + fields[i].Value), codewords);
            if (i < fields.Count - 1 && !Gs1.IsFixedLength(fields[i].Ai)) codewords.Add(Fnc1);
        }
        return Build(elementStrings, codewords, shape);
    }

    private static DataMatrix Create(string text, byte[] bytes, DataMatrixShape shape)
    {
        var codewords = new List<byte>();
        int high = bytes.Count(b => b > 127);
        if (high * 3 > bytes.Length) EncodeBase256(bytes, codewords);
        else EncodeAscii(bytes, codewords);
        return Build(text, codewords, shape);
    }

    private static void EncodeAscii(byte[] bytes, List<byte> codewords)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (IsDigit(b) && i + 1 < bytes.Length && IsDigit(bytes[i + 1]))
            {
                codewords.Add((byte)(130 + (b - '0') * 10 + (bytes[i + 1] - '0')));
                i++;
            }
            else if (b < 128) codewords.Add((byte)(b + 1));
            else
            {
                codewords.Add(235); // upper shift
                codewords.Add((byte)(b - 128 + 1));
            }
        }
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static void EncodeBase256(byte[] bytes, List<byte> codewords)
    {
        codewords.Add(231); // latch to Base 256
        var field = new List<byte>();
        if (bytes.Length <= 249) field.Add((byte)bytes.Length);
        else
        {
            field.Add((byte)(bytes.Length / 250 + 249));
            field.Add((byte)(bytes.Length % 250));
        }
        field.AddRange(bytes);
        foreach (byte b in field)
        {
            int position = codewords.Count + 1;
            int r = (149 * position) % 255 + 1;
            codewords.Add((byte)((b + r) % 256));
        }
    }

    private static DataMatrix Build(string text, List<byte> codewords, DataMatrixShape shape)
    {
        var symbol = Symbols.Where(s => shape == DataMatrixShape.Any || (shape == DataMatrixShape.Square) == (s.Rows == s.Cols))
            .OrderBy(s => s.Rows * s.Cols)
            .Cast<(int Rows, int Cols, int RegionRows, int RegionCols, int Data, int Ecc, int Blocks)?>()
            .FirstOrDefault(s => s!.Value.Data >= codewords.Count)
            ?? throw new ArgumentException("The data is too long for a Data Matrix symbol.");

        // padding
        var data = new byte[symbol.Data];
        codewords.CopyTo(data);
        for (int i = codewords.Count; i < data.Length; i++)
        {
            if (i == codewords.Count) data[i] = 129;
            else
            {
                int r = (149 * (i + 1)) % 253 + 1;
                int pad = 129 + r;
                data[i] = (byte)(pad > 254 ? pad - 254 : pad);
            }
        }

        // error correction, interleaved over the blocks
        int blocks = symbol.Blocks;
        int eccPerBlock = symbol.Ecc / blocks;
        var generator = GaloisField.DataMatrix.Generator(eccPerBlock, 1);
        var all = new byte[symbol.Data + symbol.Ecc];
        data.CopyTo(all, 0);
        for (int b = 0; b < blocks; b++)
        {
            var blockData = new List<byte>();
            for (int i = b; i < data.Length; i += blocks) blockData.Add(data[i]);
            var ecc = GaloisField.DataMatrix.Remainder(blockData.ToArray(), generator);
            for (int i = 0; i < eccPerBlock; i++) all[symbol.Data + i * blocks + b] = ecc[i];
        }

        int regionsV = symbol.Rows / (symbol.RegionRows + 2), regionsH = symbol.Cols / (symbol.RegionCols + 2);
        int mappingRows = regionsV * symbol.RegionRows, mappingCols = regionsH * symbol.RegionCols;
        var mapping = Placement.Place(all, mappingRows, mappingCols);

        var modules = new bool[symbol.Rows, symbol.Cols];
        int rr = symbol.RegionRows, rc = symbol.RegionCols;
        for (int row = 0; row < symbol.Rows; row++)
        {
            int regionRow = row / (rr + 2), inRow = row % (rr + 2);
            for (int col = 0; col < symbol.Cols; col++)
            {
                int regionCol = col / (rc + 2), inCol = col % (rc + 2);
                bool dark;
                if (inRow == rr + 1) dark = true; // bottom: solid line
                else if (inCol == 0) dark = true; // left: solid line
                else if (inRow == 0) dark = col % 2 == 0; // top: alternating
                else if (inCol == rc + 1) dark = row % 2 == 1; // right: alternating
                else dark = mapping[regionRow * rr + inRow - 1, regionCol * rc + inCol - 1];
                modules[row, col] = dark;
            }
        }
        return new DataMatrix(text, modules);
    }

    /// <summary>The codeword placement algorithm of ISO/IEC 16022 Annex F.</summary>
    private sealed class Placement
    {
        private readonly byte[] _codewords;
        private readonly int _rows, _cols;
        private readonly bool[,] _bits;
        private readonly bool[,] _set;

        private Placement(byte[] codewords, int rows, int cols)
        {
            _codewords = codewords;
            _rows = rows;
            _cols = cols;
            _bits = new bool[rows, cols];
            _set = new bool[rows, cols];
        }

        public static bool[,] Place(byte[] codewords, int rows, int cols)
        {
            var p = new Placement(codewords, rows, cols);
            p.Run();
            return p._bits;
        }

        private void Run()
        {
            int pos = 0, row = 4, col = 0;
            do
            {
                if (row == _rows && col == 0) Corner1(pos++);
                if (row == _rows - 2 && col == 0 && _cols % 4 != 0) Corner2(pos++);
                if (row == _rows - 2 && col == 0 && _cols % 8 == 4) Corner3(pos++);
                if (row == _rows + 4 && col == 2 && _cols % 8 == 0) Corner4(pos++);
                do
                {
                    if (row < _rows && col >= 0 && !_set[row, col]) Utah(row, col, pos++);
                    row -= 2;
                    col += 2;
                } while (row >= 0 && col < _cols);
                row++;
                col += 3;
                do
                {
                    if (row >= 0 && col < _cols && !_set[row, col]) Utah(row, col, pos++);
                    row += 2;
                    col -= 2;
                } while (row < _rows && col >= 0);
                row += 3;
                col++;
            } while (row < _rows || col < _cols);
            if (!_set[_rows - 1, _cols - 1])
            {
                _bits[_rows - 1, _cols - 1] = _bits[_rows - 2, _cols - 2] = true;
                _set[_rows - 1, _cols - 1] = _set[_rows - 2, _cols - 2] = true;
            }
        }

        private void Module(int row, int col, int pos, int bit)
        {
            if (row < 0)
            {
                row += _rows;
                col += 4 - (_rows + 4) % 8;
            }
            if (col < 0)
            {
                col += _cols;
                row += 4 - (_cols + 4) % 8;
            }
            _set[row, col] = true;
            _bits[row, col] = pos < _codewords.Length && (_codewords[pos] & (1 << (8 - bit))) != 0;
        }

        private void Utah(int row, int col, int pos)
        {
            Module(row - 2, col - 2, pos, 1);
            Module(row - 2, col - 1, pos, 2);
            Module(row - 1, col - 2, pos, 3);
            Module(row - 1, col - 1, pos, 4);
            Module(row - 1, col, pos, 5);
            Module(row, col - 2, pos, 6);
            Module(row, col - 1, pos, 7);
            Module(row, col, pos, 8);
        }

        private void Corner1(int pos)
        {
            Module(_rows - 1, 0, pos, 1);
            Module(_rows - 1, 1, pos, 2);
            Module(_rows - 1, 2, pos, 3);
            Module(0, _cols - 2, pos, 4);
            Module(0, _cols - 1, pos, 5);
            Module(1, _cols - 1, pos, 6);
            Module(2, _cols - 1, pos, 7);
            Module(3, _cols - 1, pos, 8);
        }

        private void Corner2(int pos)
        {
            Module(_rows - 3, 0, pos, 1);
            Module(_rows - 2, 0, pos, 2);
            Module(_rows - 1, 0, pos, 3);
            Module(0, _cols - 4, pos, 4);
            Module(0, _cols - 3, pos, 5);
            Module(0, _cols - 2, pos, 6);
            Module(0, _cols - 1, pos, 7);
            Module(1, _cols - 1, pos, 8);
        }

        private void Corner3(int pos)
        {
            Module(_rows - 3, 0, pos, 1);
            Module(_rows - 2, 0, pos, 2);
            Module(_rows - 1, 0, pos, 3);
            Module(0, _cols - 2, pos, 4);
            Module(0, _cols - 1, pos, 5);
            Module(1, _cols - 1, pos, 6);
            Module(2, _cols - 1, pos, 7);
            Module(3, _cols - 1, pos, 8);
        }

        private void Corner4(int pos)
        {
            Module(_rows - 1, 0, pos, 1);
            Module(_rows - 1, _cols - 1, pos, 2);
            Module(0, _cols - 3, pos, 3);
            Module(0, _cols - 2, pos, 4);
            Module(0, _cols - 1, pos, 5);
            Module(1, _cols - 3, pos, 6);
            Module(1, _cols - 2, pos, 7);
            Module(1, _cols - 1, pos, 8);
        }
    }
}

/// <summary>GS1 element string parsing ("(01)...(10)...").</summary>
internal static class Gs1
{
    // application identifiers (first two digits) with a predefined, fixed length - no FNC1 separator needed
    private static readonly HashSet<string> FixedPrefixes = new()
    {
        "00", "01", "02", "03", "04", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "31", "32", "33", "34", "35", "36", "41",
    };

    public static bool IsFixedLength(string ai) => FixedPrefixes.Contains(ai[..2]);

    public static List<(string Ai, string Value)> Parse(string elementStrings)
    {
        var result = new List<(string, string)>();
        int i = 0;
        while (i < elementStrings.Length)
        {
            if (elementStrings[i] != '(') throw new FormatException("GS1 element strings must have the form (AI)value(AI)value...");
            int close = elementStrings.IndexOf(')', i);
            if (close < 0) throw new FormatException("Missing ')' in GS1 element string.");
            string ai = elementStrings[(i + 1)..close];
            if (ai.Length is < 2 or > 4 || !ai.All(char.IsAsciiDigit)) throw new FormatException($"Invalid application identifier '{ai}'.");
            int next = elementStrings.IndexOf('(', close);
            if (next < 0) next = elementStrings.Length;
            result.Add((ai, elementStrings[(close + 1)..next]));
            i = next;
        }
        if (result.Count == 0) throw new FormatException("No GS1 element strings found.");
        return result;
    }
}
