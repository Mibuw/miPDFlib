namespace MiPdf.Barcodes;

/// <summary>
/// Code 128 (ISO/IEC 15417). Code sets A, B and C are selected automatically (digit runs are compressed).
/// Use <see cref="CreateGs1"/> for GS1-128 (EAN-128) with application identifiers.
/// </summary>
public sealed class Code128 : LinearBarcode
{
    private static readonly string[] Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    };

    private const int CodeC = 99, CodeB = 100, CodeA = 101, Fnc1 = 102, StartA = 103, StartB = 104, StartC = 105, Stop = 106;
    private const char Fnc1Char = 'ñ';

    private Code128(string text, bool[] modules, string humanReadable) : base(text, modules, 10, humanReadable) { }

    /// <summary>The symbol values including start, check and stop symbol.</summary>
    public IReadOnlyList<int> Values { get; private init; } = Array.Empty<int>();

    /// <summary>Encodes ASCII text (characters 0-127).</summary>
    public static Code128 Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) throw new ArgumentException("Text must not be empty.", nameof(text));
        if (text.Any(c => c > 127)) throw new ArgumentException("Code 128 supports ASCII characters (0-127) only.", nameof(text));
        return Build(text, text, text);
    }

    /// <summary>
    /// Creates a GS1-128 barcode from element strings in the form "(01)09501101530003(3103)000189(10)ABC".
    /// FNC1 separators after variable-length fields are inserted automatically.
    /// </summary>
    public static Code128 CreateGs1(string elementStrings)
    {
        var fields = Gs1.Parse(elementStrings);
        var sb = new System.Text.StringBuilder().Append(Fnc1Char);
        for (int i = 0; i < fields.Count; i++)
        {
            sb.Append(fields[i].Ai).Append(fields[i].Value);
            if (i < fields.Count - 1 && !Gs1.IsFixedLength(fields[i].Ai)) sb.Append(Fnc1Char);
        }
        return Build(elementStrings, sb.ToString(), elementStrings);
    }

    private static Code128 Build(string text, string data, string humanReadable)
    {
        var values = Encode(data);
        int checksum = values[0];
        for (int i = 1; i < values.Count; i++) checksum += i * values[i];
        values.Add(checksum % 103);
        values.Add(Stop);
        var modules = new List<bool>();
        foreach (int v in values) AppendWidths(modules, Patterns[v].Select(c => c - '0'));
        return new Code128(text, modules.ToArray(), humanReadable) { Values = values };
    }

    private static int DigitRun(string s, int start)
    {
        int n = 0;
        while (start + n < s.Length && char.IsAsciiDigit(s[start + n])) n++;
        return n;
    }

    private static List<int> Encode(string s)
    {
        var values = new List<int>();
        int set; // 'A', 'B', 'C'
        int run = DigitRun(s, 0);
        if (run >= 4 && run % 2 == 0 || run == s.Length && run >= 2 && run % 2 == 0) set = 'C';
        else if (s.Length > 0 && s[0] == Fnc1Char && DigitRun(s, 1) >= 2) set = 'C';
        else set = NeedsSetA(s, 0) ? 'A' : 'B';
        values.Add(set switch { 'A' => StartA, 'B' => StartB, _ => StartC });

        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == Fnc1Char)
            {
                values.Add(Fnc1);
                i++;
                continue;
            }
            if (set == 'C')
            {
                if (i + 1 < s.Length && char.IsAsciiDigit(c) && char.IsAsciiDigit(s[i + 1]))
                {
                    values.Add((c - '0') * 10 + (s[i + 1] - '0'));
                    i += 2;
                    continue;
                }
                set = NeedsSetA(s, i) ? 'A' : 'B';
                values.Add(set == 'A' ? CodeA : CodeB);
                continue;
            }
            run = DigitRun(s, i);
            if (run >= 4)
            {
                if (run % 2 == 1)
                {
                    values.Add(EncodeChar(c, set));
                    i++;
                }
                set = 'C';
                values.Add(CodeC);
                continue;
            }
            if (set == 'B' && c < 32)
            {
                set = 'A';
                values.Add(CodeA);
            }
            else if (set == 'A' && c >= 96)
            {
                set = 'B';
                values.Add(CodeB);
            }
            values.Add(EncodeChar(c, set));
            i++;
        }
        return values;
    }

    private static bool NeedsSetA(string s, int start)
    {
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] < 32) return true;
            if (s[i] >= 96 && s[i] != Fnc1Char) return false;
        }
        return false;
    }

    private static int EncodeChar(char c, int set) => set == 'A' ? (c < 32 ? c + 64 : c - 32) : c - 32;
}

/// <summary>EAN-13, EAN-8 and UPC-A retail barcodes. The check digit is calculated if omitted and verified if given.</summary>
public sealed class Ean : LinearBarcode
{
    private static readonly string[] L = { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };
    private static readonly string[] Parity = { "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL" };

    private Ean(string text, bool[] modules, int quietZone, List<(string, int, int)> groups, HashSet<int> guards)
        : base(text, modules, quietZone, text, groups, guards) { }

    /// <summary>EAN-13 from 12 digits (check digit appended) or 13 digits (check digit verified).</summary>
    public static Ean CreateEan13(string digits)
    {
        string code = Complete(digits, 13);
        var modules = new List<bool>();
        var guards = new HashSet<int>();
        Guard(modules, guards, "101");
        string parity = Parity[code[0] - '0'];
        for (int i = 1; i <= 6; i++) Append(modules, parity[i - 1] == 'L' ? L[code[i] - '0'] : G(code[i] - '0'));
        Guard(modules, guards, "01010");
        for (int i = 7; i <= 12; i++) Append(modules, R(code[i] - '0'));
        Guard(modules, guards, "101");
        var groups = new List<(string, int, int)> { (code[..1], -8, -1), (code[1..7], 3, 45), (code[7..], 50, 92) };
        return new Ean(code, modules.ToArray(), 11, groups, guards);
    }

    /// <summary>EAN-8 from 7 digits (check digit appended) or 8 digits.</summary>
    public static Ean CreateEan8(string digits)
    {
        string code = Complete(digits, 8);
        var modules = new List<bool>();
        var guards = new HashSet<int>();
        Guard(modules, guards, "101");
        for (int i = 0; i < 4; i++) Append(modules, L[code[i] - '0']);
        Guard(modules, guards, "01010");
        for (int i = 4; i < 8; i++) Append(modules, R(code[i] - '0'));
        Guard(modules, guards, "101");
        var groups = new List<(string, int, int)> { (code[..4], 3, 31), (code[4..], 36, 64) };
        return new Ean(code, modules.ToArray(), 7, groups, guards);
    }

    /// <summary>UPC-A from 11 digits (check digit appended) or 12 digits.</summary>
    public static Ean CreateUpcA(string digits)
    {
        string code = Complete(digits, 12);
        var ean = CreateEan13("0" + code);
        var guards = new HashSet<int>();
        for (int i = 0; i < 10; i++) guards.Add(i);
        for (int i = 45; i < 50; i++) guards.Add(i);
        for (int i = 85; i < 95; i++) guards.Add(i);
        var groups = new List<(string, int, int)> { (code[..1], -8, -1), (code[1..6], 10, 45), (code[6..11], 50, 85), (code[11..], 96, 103) };
        return new Ean(code, ean.Modules.ToArray(), 11, groups, guards);
    }

    /// <summary>Calculates the GS1 check digit (weights 3/1 from the right).</summary>
    public static int CheckDigit(string digitsWithoutCheck)
    {
        int sum = 0;
        for (int i = 0; i < digitsWithoutCheck.Length; i++)
        {
            int d = digitsWithoutCheck[digitsWithoutCheck.Length - 1 - i] - '0';
            sum += i % 2 == 0 ? d * 3 : d;
        }
        return (10 - sum % 10) % 10;
    }

    private static string Complete(string digits, int length)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (!digits.All(char.IsAsciiDigit)) throw new ArgumentException("Only digits are allowed.", nameof(digits));
        if (digits.Length == length - 1) return digits + CheckDigit(digits);
        if (digits.Length != length) throw new ArgumentException($"Expected {length - 1} or {length} digits.", nameof(digits));
        if (CheckDigit(digits[..^1]) != digits[^1] - '0') throw new ArgumentException("Invalid check digit.", nameof(digits));
        return digits;
    }

    private static string R(int d) => new(L[d].Select(c => c == '0' ? '1' : '0').ToArray());

    private static string G(int d) => new(R(d).Reverse().ToArray());

    private static void Append(List<bool> modules, string pattern) => modules.AddRange(pattern.Select(c => c == '1'));

    private static void Guard(List<bool> modules, HashSet<int> guards, string pattern)
    {
        for (int i = 0; i < pattern.Length; i++) guards.Add(modules.Count + i);
        Append(modules, pattern);
    }
}

/// <summary>Code 39 (digits, upper-case letters and - . space $ / + %), optionally with a modulo 43 check character.</summary>
public sealed class Code39 : LinearBarcode
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    private static readonly int[] Encodings =
    {
        0x034, 0x121, 0x061, 0x160, 0x031, 0x130, 0x070, 0x025, 0x124, 0x064,
        0x109, 0x049, 0x148, 0x019, 0x118, 0x058, 0x00D, 0x10C, 0x04C, 0x01C,
        0x103, 0x043, 0x142, 0x013, 0x112, 0x052, 0x007, 0x106, 0x046, 0x016,
        0x181, 0x0C1, 0x1C0, 0x091, 0x190, 0x0D0, 0x085, 0x184, 0x0C4, 0x0A8,
        0x0A2, 0x08A, 0x02A,
    };

    private const int Asterisk = 0x094;

    private Code39(string text, bool[] modules, string humanReadable) : base(text, modules, 10, humanReadable) { }

    public static Code39 Create(string text, bool addCheckCharacter = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var invalid = text.FirstOrDefault(c => !Alphabet.Contains(c));
        if (invalid != default) throw new ArgumentException($"Code 39 cannot encode '{invalid}' (use upper-case letters).", nameof(text));
        string data = text;
        if (addCheckCharacter) data += Alphabet[text.Sum(c => Alphabet.IndexOf(c)) % 43];
        var modules = new List<bool>();
        AppendChar(modules, Asterisk);
        foreach (char c in data) AppendChar(modules, Encodings[Alphabet.IndexOf(c)]);
        AppendChar(modules, Asterisk, last: true);
        return new Code39(text, modules.ToArray(), data);
    }

    private static void AppendChar(List<bool> modules, int encoding, bool last = false)
    {
        var widths = new List<int>();
        for (int i = 8; i >= 0; i--) widths.Add(((encoding >> i) & 1) != 0 ? 3 : 1);
        if (!last) widths.Add(1); // inter-character gap
        AppendWidths(modules, widths);
    }
}

/// <summary>Interleaved 2 of 5 (ITF), e.g. for ITF-14 shipping container codes. The number of digits must be even.</summary>
public sealed class Interleaved2Of5 : LinearBarcode
{
    private static readonly string[] Patterns = { "nnwwn", "wnnnw", "nwnnw", "wwnnn", "nnwnw", "wnwnn", "nwwnn", "nnnww", "wnnwn", "nwnwn" };

    private Interleaved2Of5(string text, bool[] modules) : base(text, modules, 10) { }

    /// <param name="digits">Digits; a leading zero is added if the count (including an optional check digit) is odd.</param>
    /// <param name="addCheckDigit">Appends a GS1 check digit (as for ITF-14).</param>
    public static Interleaved2Of5 Create(string digits, bool addCheckDigit = false)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) throw new ArgumentException("Only digits are allowed.", nameof(digits));
        string code = addCheckDigit ? digits + Ean.CheckDigit(digits) : digits;
        if (code.Length % 2 == 1) code = "0" + code;
        var widths = new List<int> { 1, 1, 1, 1 };
        for (int i = 0; i < code.Length; i += 2)
        {
            string bars = Patterns[code[i] - '0'], spaces = Patterns[code[i + 1] - '0'];
            for (int k = 0; k < 5; k++)
            {
                widths.Add(bars[k] == 'w' ? 3 : 1);
                widths.Add(spaces[k] == 'w' ? 3 : 1);
            }
        }
        widths.AddRange(new[] { 3, 1, 1 });
        var modules = new List<bool>();
        AppendWidths(modules, widths);
        return new Interleaved2Of5(code, modules.ToArray());
    }
}
