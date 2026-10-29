using System.Globalization;

namespace MiPdf.Fonts;

/// <summary>
/// Maps PostScript glyph names to Unicode (subset of the Adobe Glyph List covering Latin, Latin-1,
/// Latin Extended-A, Greek and common punctuation) and provides the standard simple-font encodings.
/// </summary>
internal static class GlyphNames
{
    private const string Ascii =
        "space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft parenright asterisk plus comma hyphen period slash " +
        "zero one two three four five six seven eight nine colon semicolon less equal greater question at " +
        "A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright asciicircum underscore grave " +
        "a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft bar braceright asciitilde";

    private const string Latin1 =
        "nbspace exclamdown cent sterling currency yen brokenbar section dieresis copyright ordfeminine guillemotleft logicalnot sfthyphen registered macron " +
        "degree plusminus twosuperior threesuperior acute mu paragraph periodcentered cedilla onesuperior ordmasculine guillemotright onequarter onehalf threequarters questiondown " +
        "Agrave Aacute Acircumflex Atilde Adieresis Aring AE Ccedilla Egrave Eacute Ecircumflex Edieresis Igrave Iacute Icircumflex Idieresis " +
        "Eth Ntilde Ograve Oacute Ocircumflex Otilde Odieresis multiply Oslash Ugrave Uacute Ucircumflex Udieresis Yacute Thorn germandbls " +
        "agrave aacute acircumflex atilde adieresis aring ae ccedilla egrave eacute ecircumflex edieresis igrave iacute icircumflex idieresis " +
        "eth ntilde ograve oacute ocircumflex otilde odieresis divide oslash ugrave uacute ucircumflex udieresis yacute thorn ydieresis";

    private const string LatinExtendedA =
        "Amacron amacron Abreve abreve Aogonek aogonek Cacute cacute Ccircumflex ccircumflex Cdotaccent cdotaccent Ccaron ccaron Dcaron dcaron " +
        "Dcroat dcroat Emacron emacron Ebreve ebreve Edotaccent edotaccent Eogonek eogonek Ecaron ecaron Gcircumflex gcircumflex Gbreve gbreve " +
        "Gdotaccent gdotaccent Gcommaaccent gcommaaccent Hcircumflex hcircumflex Hbar hbar Itilde itilde Imacron imacron Ibreve ibreve Iogonek iogonek " +
        "Idotaccent dotlessi IJ ij Jcircumflex jcircumflex Kcommaaccent kcommaaccent kgreenlandic Lacute lacute Lcommaaccent lcommaaccent Lcaron lcaron Ldot " +
        "ldot Lslash lslash Nacute nacute Ncommaaccent ncommaaccent Ncaron ncaron napostrophe Eng eng Omacron omacron Obreve obreve " +
        "Ohungarumlaut ohungarumlaut OE oe Racute racute Rcommaaccent rcommaaccent Rcaron rcaron Sacute sacute Scircumflex scircumflex Scedilla scedilla " +
        "Scaron scaron Tcommaaccent tcommaaccent Tcaron tcaron Tbar tbar Utilde utilde Umacron umacron Ubreve ubreve Uring uring " +
        "Uhungarumlaut uhungarumlaut Uogonek uogonek Wcircumflex wcircumflex Ycircumflex ycircumflex Ydieresis Zacute zacute Zdotaccent zdotaccent Zcaron zcaron longs";

    private const string GreekUpper = "Alpha Beta Gamma Delta Epsilon Zeta Eta Theta Iota Kappa Lambda Mu Nu Xi Omicron Pi Rho - Sigma Tau Upsilon Phi Chi Psi Omega";
    private const string GreekLower = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma1 sigma tau upsilon phi chi psi omega";

    private static readonly (string Name, int Code)[] Others =
    {
        ("quoteleft", 0x2018), ("quoteright", 0x2019), ("quotesinglbase", 0x201A), ("quotedblleft", 0x201C), ("quotedblright", 0x201D),
        ("quotedblbase", 0x201E), ("guilsinglleft", 0x2039), ("guilsinglright", 0x203A), ("endash", 0x2013), ("emdash", 0x2014),
        ("bullet", 0x2022), ("dagger", 0x2020), ("daggerdbl", 0x2021), ("ellipsis", 0x2026), ("perthousand", 0x2030), ("fi", 0xFB01),
        ("fl", 0xFB02), ("ff", 0xFB00), ("ffi", 0xFB03), ("ffl", 0xFB04), ("florin", 0x0192), ("fraction", 0x2044), ("circumflex", 0x02C6),
        ("tilde", 0x02DC), ("breve", 0x02D8), ("dotaccent", 0x02D9), ("ring", 0x02DA), ("hungarumlaut", 0x02DD), ("ogonek", 0x02DB),
        ("caron", 0x02C7), ("Euro", 0x20AC), ("trademark", 0x2122), ("minus", 0x2212), ("Omega", 0x2126), ("Delta", 0x2206),
        ("infinity", 0x221E), ("notequal", 0x2260), ("lessequal", 0x2264), ("greaterequal", 0x2265), ("partialdiff", 0x2202),
        ("summation", 0x2211), ("product", 0x220F), ("integral", 0x222B), ("radical", 0x221A), ("approxequal", 0x2248),
        ("lozenge", 0x25CA), ("apple", 0xF8FF), ("mu1", 0x00B5), ("hyphen", 0x002D), ("periodcentered", 0x00B7), ("middot", 0x00B7),
        ("arrowleft", 0x2190), ("arrowup", 0x2191), ("arrowright", 0x2192), ("arrowdown", 0x2193), ("degree", 0x00B0),
        ("checkmark", 0x2713), ("onesuperior", 0x00B9), ("dotlessj", 0x0237), ("commaaccent", 0x0326), ("Ohm", 0x2126),
    };

    private static readonly Dictionary<string, string> NameToUnicode = Build();

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        void AddRange(string names, int start)
        {
            int code = start;
            foreach (var n in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (n != "-") d.TryAdd(n, char.ConvertFromUtf32(code));
                code++;
            }
        }
        AddRange(Ascii, 0x20);
        AddRange(Latin1, 0xA0);
        AddRange(LatinExtendedA, 0x100);
        AddRange(GreekUpper, 0x391);
        AddRange(GreekLower, 0x3B1);
        foreach (var (name, code) in Others) d.TryAdd(name, char.ConvertFromUtf32(code));
        return d;
    }

    /// <summary>Converts a glyph name to Unicode text, or null if unknown.</summary>
    public static string? ToUnicode(string name)
    {
        if (NameToUnicode.TryGetValue(name, out var s)) return s;
        int dot = name.IndexOf('.');
        if (dot > 0) return ToUnicode(name[..dot]); // e.g. "a.sc", "one.oldstyle"
        if (name.Contains('_')) // ligatures like "f_f_i"
        {
            var parts = name.Split('_').Select(ToUnicode).ToList();
            return parts.All(p => p != null) ? string.Concat(parts) : null;
        }
        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7 && (name.Length - 3) % 4 == 0)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 3; i < name.Length; i += 4)
            {
                if (!int.TryParse(name.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)) return null;
                sb.Append((char)cp);
            }
            return sb.ToString();
        }
        if (name.Length is >= 5 and <= 7 && name[0] == 'u' &&
            int.TryParse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u) && u <= 0x10FFFF &&
            u is < 0xD800 or > 0xDFFF)
            return char.ConvertFromUtf32(u);
        return null;
    }

    // ------------------------------------------------------------------ base encodings (code -> Unicode)

    private static readonly char[] Standard = BuildStandard();
    private static readonly char[] MacRoman = BuildMacRoman();

    /// <summary>Returns the Unicode character for <paramref name="code"/> in a named base encoding.</summary>
    public static char Decode(string? encoding, int code)
    {
        if (code is < 0 or > 255) return '\0';
        return encoding switch
        {
            "WinAnsiEncoding" => WinAnsiEncoding.ToUnicode(code),
            "MacRomanEncoding" => MacRoman[code],
            "PDFDocEncoding" => code >= 32 ? Text.PdfDocEncoding.GetString(new[] { (byte)code })[0] : '\0',
            _ => Standard[code],
        };
    }

    private static char[] BuildStandard()
    {
        var t = new char[256];
        for (int i = 0x20; i < 0x7F; i++) t[i] = (char)i;
        t[0x27] = '’';
        t[0x60] = '‘';
        var high = new (int, int)[]
        {
            (0xA1, 0xA1), (0xA2, 0xA2), (0xA3, 0xA3), (0xA4, 0x2044), (0xA5, 0xA5), (0xA6, 0x192), (0xA7, 0xA7), (0xA8, 0xA4),
            (0xA9, 0x27), (0xAA, 0x201C), (0xAB, 0xAB), (0xAC, 0x2039), (0xAD, 0x203A), (0xAE, 0xFB01), (0xAF, 0xFB02), (0xB1, 0x2013),
            (0xB2, 0x2020), (0xB3, 0x2021), (0xB4, 0xB7), (0xB6, 0xB6), (0xB7, 0x2022), (0xB8, 0x201A), (0xB9, 0x201E), (0xBA, 0x201D),
            (0xBB, 0xBB), (0xBC, 0x2026), (0xBD, 0x2030), (0xBF, 0xBF), (0xC1, 0x60), (0xC2, 0xB4), (0xC3, 0x2C6), (0xC4, 0x2DC),
            (0xC5, 0xAF), (0xC6, 0x2D8), (0xC7, 0x2D9), (0xC8, 0xA8), (0xCA, 0x2DA), (0xCB, 0xB8), (0xCD, 0x2DD), (0xCE, 0x2DB),
            (0xCF, 0x2C7), (0xD0, 0x2014), (0xE1, 0xC6), (0xE3, 0xAA), (0xE8, 0x141), (0xE9, 0xD8), (0xEA, 0x152), (0xEB, 0xBA),
            (0xF1, 0xE6), (0xF5, 0x131), (0xF8, 0x142), (0xF9, 0xF8), (0xFA, 0x153), (0xFB, 0xDF),
        };
        foreach (var (code, u) in high) t[code] = (char)u;
        return t;
    }

    private static char[] BuildMacRoman()
    {
        var t = new char[256];
        for (int i = 0x20; i < 0x7F; i++) t[i] = (char)i;
        const string high =
            "ÄÅÇÉÑÖÜáàâäãåçéè" +
            "êëíìîïñóòôöõúùûü" +
            "†°¢£§•¶ß®©™´¨≠ÆØ" +
            "∞±≤≥¥µ∂∑∏π∫ªºΩæø" +
            "¿¡¬√ƒ≈∆«»… ÀÃÕŒœ" +
            "–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ" +
            "‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔ" +
            "ÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ";
        for (int i = 0; i < 128; i++) t[0x80 + i] = high[i];
        return t;
    }
}
