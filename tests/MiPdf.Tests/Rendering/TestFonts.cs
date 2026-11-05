using System.Text;

namespace MiPdf.Tests.Rendering;

/// <summary>
/// Minimal synthetic font programs for the font parser and text rendering tests (no font files needed). Both fonts
/// have a square (code 65, 500x500 units) and a triangle drawn partly by a subroutine (code 66); the Type 1 font also
/// has an accented composite made with seac (code 67).
/// </summary>
internal static class TestFonts
{
    // charstring numbers (the same encoding in Type 1 and Type 2 for -1131..1131)
    private static IEnumerable<byte> Num(int v) => v switch
    {
        >= -107 and <= 107 => new[] { (byte)(v + 139) },
        >= 108 and <= 1131 => new[] { (byte)((v - 108) / 256 + 247), (byte)((v - 108) % 256) },
        >= -1131 and <= -108 => new[] { (byte)((-v - 108) / 256 + 251), (byte)((-v - 108) % 256) },
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>A charstring from numbers and operators ("op:N" for operator N, "esc:N" for 12 N).</summary>
    private static byte[] CharString(params object[] items)
    {
        var bytes = new List<byte>();
        foreach (var item in items)
        {
            switch (item)
            {
                case int n:
                    bytes.AddRange(Num(n));
                    break;
                case string s when s.StartsWith("esc:", StringComparison.Ordinal):
                    bytes.Add(12);
                    bytes.Add(byte.Parse(s[4..]));
                    break;
                case string s:
                    bytes.Add(byte.Parse(s[3..]));
                    break;
            }
        }
        return bytes.ToArray();
    }

    private const string Hsbw = "op:13", RMoveTo = "op:21", RLineTo = "op:5", ClosePath = "op:9", EndChar = "op:14",
        CallSubr = "op:10", Return = "op:11", Seac = "esc:6", HLineTo = "op:6", VLineTo = "op:7", CallGSubr = "op:29";

    private static byte[] Encrypt(byte[] plain, int key)
    {
        var result = new byte[plain.Length + 4];
        int r = key;
        for (int i = 0; i < result.Length; i++)
        {
            int p = i < 4 ? 0 : plain[i - 4];
            int c = p ^ (r >> 8);
            r = ((c + r) * 52845 + 22719) & 0xFFFF;
            result[i] = (byte)c;
        }
        return result;
    }

    /// <summary>A Type 1 font (PFA layout: clear text, then binary eexec section); the clear text length is returned.</summary>
    public static byte[] Type1(out int clearLength)
    {
        var clear = Encoding.ASCII.GetBytes(
            "%!PS-AdobeFont-1.0: MiTest 001\n/FontName /MiTest def\n/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n" +
            "/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\ndup 65 /A put\ndup 66 /B put\ndup 67 /C put\n" +
            "readonly def\ncurrentfile eexec\n");
        clearLength = clear.Length;

        var priv = new MemoryStream();
        void Text(string s) => priv.Write(Encoding.ASCII.GetBytes(s));
        void Binary(string prefix, byte[] charString, string suffix)
        {
            var encrypted = Encrypt(charString, 4330);
            Text($"{prefix} {encrypted.Length} RD ");
            priv.Write(encrypted);
            Text($" {suffix}\n");
        }
        Text("dup /Private 8 dict dup begin\n/RD {string currentfile exch readstring pop} executeonly def\n" +
             "/ND {noaccess def} executeonly def\n/NP {noaccess put} executeonly def\n/lenIV 4 def\n/Subrs 1 array\n");
        Binary("dup 0", CharString(500, 0, RLineTo, Return), "NP");
        Text("ND\n2 index /CharStrings 4 dict dup begin\n");
        Binary("/.notdef", CharString(0, 600, Hsbw, EndChar), "ND");
        Binary("/A", CharString(50, 600, Hsbw, 0, 0, RMoveTo, 500, 0, RLineTo, 0, 500, RLineTo, -500, 0, RLineTo, ClosePath, EndChar), "ND");
        Binary("/B", CharString(0, 600, Hsbw, 100, 0, RMoveTo, 0, CallSubr, -250, 400, RLineTo, ClosePath, EndChar), "ND");
        Binary("/C", CharString(0, 600, Hsbw, 0, 0, 600, 65, 66, Seac), "ND");
        Text("end\nend\nreadonly put\nput\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");
        var encryptedPrivate = Encrypt(priv.ToArray(), 55665);
        return clear.Concat(encryptedPrivate).ToArray();
    }

    // ------------------------------------------------------------------ CFF

    private static byte[] Index(params byte[][] items)
    {
        if (items.Length == 0) return new byte[] { 0, 0 };
        var result = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 2 };
        int offset = 1;
        result.AddRange(new[] { (byte)(offset >> 8), (byte)offset });
        foreach (var item in items)
        {
            offset += item.Length;
            result.AddRange(new[] { (byte)(offset >> 8), (byte)offset });
        }
        foreach (var item in items) result.AddRange(item);
        return result.ToArray();
    }

    private static IEnumerable<byte> DictInt(int v) => new[] { (byte)29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>A bare CFF font: square "square" (code 65) and triangle "tri" (code 66, drawn partly by a global subroutine).</summary>
    public static byte[] Cff()
    {
        var header = new byte[] { 1, 0, 4, 1 };
        var names = Index(Encoding.ASCII.GetBytes("MiTest"));
        var strings = Index(Encoding.ASCII.GetBytes("square"), Encoding.ASCII.GetBytes("tri"));
        var globalSubrs = Index(CharString(500, 0, RLineTo, Return));
        var charStrings = Index(
            CharString(EndChar),
            CharString(0, 0, RMoveTo, 500, HLineTo, 500, VLineTo, -500, HLineTo, EndChar),
            CharString(100, 0, RMoveTo, 0 - 107, CallGSubr, -250, 400, RLineTo, EndChar)); // bias 107 for < 1240 subrs
        var charset = new byte[] { 0, 391 >> 8, 391 & 0xFF, 392 >> 8, 392 & 0xFF }; // format 0: SIDs of glyphs 1..
        var encoding = new byte[] { 0, 2, 65, 66 };                                // format 0: codes of glyphs 1..
        var privateDict = Array.Empty<byte>();

        byte[] TopDict(int charsetAt, int encodingAt, int charStringsAt, int privateAt) =>
            DictInt(charsetAt).Append((byte)15).Concat(DictInt(encodingAt)).Append((byte)16)
                .Concat(DictInt(charStringsAt)).Append((byte)17).Concat(DictInt(privateDict.Length)).Concat(DictInt(privateAt)).Append((byte)18).ToArray();

        int prefix = header.Length + names.Length + Index(TopDict(0, 0, 0, 0)).Length + strings.Length + globalSubrs.Length;
        int charsetAt = prefix, encodingAt = charsetAt + charset.Length, charStringsAt = encodingAt + encoding.Length;
        int privateAt = charStringsAt + charStrings.Length;
        var top = Index(TopDict(charsetAt, encodingAt, charStringsAt, privateAt));
        return header.Concat(names).Concat(top).Concat(strings).Concat(globalSubrs).Concat(charset).Concat(encoding)
            .Concat(charStrings).Concat(privateDict).ToArray();
    }
}
