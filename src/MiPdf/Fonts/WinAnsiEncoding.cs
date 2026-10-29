namespace MiPdf.Fonts;

/// <summary>WinAnsiEncoding (Windows code page 1252) as used by simple PDF fonts (ISO 32000-2, Annex D).</summary>
public static class WinAnsiEncoding
{
    private static readonly char[] Table = BuildTable();
    private static readonly Dictionary<char, byte> Reverse = BuildReverse();

    private static char[] BuildTable()
    {
        var t = new char[256];
        for (int i = 0; i < 256; i++) t[i] = i is >= 0x20 and < 0x7F or >= 0xA0 ? (char)i : '\0';
        const string high = "€\0‚ƒ„…†‡ˆ‰Š‹Œ\0Ž\0" +
                            "\0‘’“”•–—˜™š›œ\0žŸ";
        for (int i = 0; i < 32; i++) t[0x80 + i] = high[i];
        return t;
    }

    private static Dictionary<char, byte> BuildReverse()
    {
        var d = new Dictionary<char, byte>();
        for (int i = 255; i >= 0x20; i--)
            if (Table[i] != '\0') d[Table[i]] = (byte)i;
        d[' '] = 0x20; // no-break space -> space
        return d;
    }

    /// <summary>Unicode character for a code, or '\0' if undefined.</summary>
    public static char ToUnicode(int code) => code is >= 0 and < 256 ? Table[code] : '\0';

    /// <summary>Returns the code for a character, or -1 if it is not representable.</summary>
    public static int ToCode(char c) => Reverse.TryGetValue(c, out var b) ? b : -1;
}
