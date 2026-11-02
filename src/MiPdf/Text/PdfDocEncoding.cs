namespace MiPdf.Text;

/// <summary>PDFDocEncoding (ISO 32000-2, Annex D.3) used for text strings outside content streams.</summary>
public static class PdfDocEncoding
{
    private static readonly char[] ToUnicode = BuildTable();
    private static readonly Dictionary<char, byte> FromUnicode = BuildReverse();

    private static char[] BuildTable()
    {
        var t = new char[256];
        for (int i = 0; i < 256; i++) t[i] = (char)i;
        "˘ˇˆ˙˝˛˚˜".CopyTo(0, t, 0x18, 8);
        ("•†‡…—–ƒ⁄‹›−‰„“”‘" +
         "’‚™ﬁﬂŁŒŠŸŽıłœšž")
            .CopyTo(0, t, 0x80, 31);
        t[0x9F] = '�';
        t[0xA0] = '€';
        t[0xAD] = '�';
        return t;
    }

    private static Dictionary<char, byte> BuildReverse()
    {
        var d = new Dictionary<char, byte>();
        for (int i = 255; i >= 0; i--)
        {
            char c = ToUnicode[i];
            if (c == '�') continue;
            d[c] = (byte)i;
        }
        return d;
    }

    /// <summary>Decodes PDFDocEncoding bytes; the undefined codes 0x9F and 0xAD become U+FFFD.</summary>
    public static string GetString(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) chars[i] = ToUnicode[bytes[i]];
        return new string(chars);
    }

    /// <summary>Returns true if every character of <paramref name="text"/> is representable.</summary>
    public static bool CanEncode(string text)
    {
        foreach (char c in text)
            if (!FromUnicode.ContainsKey(c)) return false;
        return true;
    }

    /// <summary>
    /// Encodes <paramref name="text"/> in PDFDocEncoding (one byte per UTF-16 char); characters that cannot be
    /// represented become '?'. Use <see cref="CanEncode"/> to check beforehand.
    /// </summary>
    public static byte[] GetBytes(string text)
    {
        var result = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
            result[i] = FromUnicode.TryGetValue(text[i], out var b) ? b : (byte)'?';
        return result;
    }
}
