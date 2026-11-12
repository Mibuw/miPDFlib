namespace MiPdf.Fonts;

internal sealed record StandardFontInfo(
    string Name, short[]? Widths, int FixedWidth, double Ascent, double Descent, double CapHeight, double XHeight,
    double ItalicAngle, int StemV, double[] BBox, bool Symbolic);

internal static partial class StandardFontMetrics
{
    // Built lazily: field initializers of partial class parts run in an unspecified order.
    private static readonly Lazy<Dictionary<string, StandardFontInfo>> LazyFonts = new(Build);

    private static Dictionary<string, StandardFontInfo> Fonts => LazyFonts.Value;

    // Ascent/descent/cap height/bbox values follow the Adobe Font Metrics of the standard 14 fonts.
    private static Dictionary<string, StandardFontInfo> Build() => new()
    {
        ["Helvetica"] = new("Helvetica", Helvetica, 0, 718, -207, 718, 523, 0, 88, new double[] { -166, -225, 1000, 931 }, false),
        ["Helvetica-Bold"] = new("Helvetica-Bold", HelveticaBold, 0, 718, -207, 718, 532, 0, 140, new double[] { -170, -228, 1003, 962 }, false),
        ["Helvetica-Oblique"] = new("Helvetica-Oblique", HelveticaOblique, 0, 718, -207, 718, 523, -12, 88, new double[] { -170, -225, 1116, 931 }, false),
        ["Helvetica-BoldOblique"] = new("Helvetica-BoldOblique", HelveticaBoldOblique, 0, 718, -207, 718, 532, -12, 140, new double[] { -174, -228, 1114, 962 }, false),
        ["Times-Roman"] = new("Times-Roman", TimesRoman, 0, 683, -217, 662, 450, 0, 84, new double[] { -168, -218, 1000, 898 }, false),
        ["Times-Bold"] = new("Times-Bold", TimesBold, 0, 683, -217, 676, 461, 0, 139, new double[] { -168, -218, 1000, 935 }, false),
        ["Times-Italic"] = new("Times-Italic", TimesItalic, 0, 683, -217, 653, 441, -15.5, 76, new double[] { -169, -217, 1010, 883 }, false),
        ["Times-BoldItalic"] = new("Times-BoldItalic", TimesBoldItalic, 0, 683, -217, 669, 462, -15, 121, new double[] { -200, -218, 996, 921 }, false),
        ["Courier"] = new("Courier", null, 600, 629, -157, 562, 426, 0, 51, new double[] { -23, -250, 715, 805 }, false),
        ["Courier-Bold"] = new("Courier-Bold", null, 600, 629, -157, 562, 439, 0, 106, new double[] { -113, -250, 749, 801 }, false),
        ["Courier-Oblique"] = new("Courier-Oblique", null, 600, 629, -157, 562, 426, -12, 51, new double[] { -27, -250, 849, 805 }, false),
        ["Courier-BoldOblique"] = new("Courier-BoldOblique", null, 600, 629, -157, 562, 439, -12, 106, new double[] { -57, -250, 869, 801 }, false),
        ["Symbol"] = new("Symbol", Symbol, 0, 1010, -293, 1010, 0, 0, 85, new double[] { -180, -293, 1090, 1010 }, true),
        ["ZapfDingbats"] = new("ZapfDingbats", null, 788, 820, -143, 820, 0, 0, 28, new double[] { -1, -143, 981, 820 }, true),
    };

    public static StandardFontInfo Get(string name) => Fonts[name];

    public static bool IsStandardFont(string name) => Fonts.ContainsKey(name);

    /// <summary>The standard font a font name stands for (the names of the metric-compatible Windows fonts too).</summary>
    public static string CanonicalName(string name) => name switch
    {
        "Arial" or "ArialMT" => "Helvetica",
        "Arial,Bold" or "Arial-BoldMT" => "Helvetica-Bold",
        "Arial,Italic" or "Arial-ItalicMT" => "Helvetica-Oblique",
        "Arial,BoldItalic" or "Arial-BoldItalicMT" => "Helvetica-BoldOblique",
        "TimesNewRoman" or "TimesNewRomanPSMT" => "Times-Roman",
        "TimesNewRoman,Bold" or "TimesNewRomanPS-BoldMT" => "Times-Bold",
        "TimesNewRoman,Italic" or "TimesNewRomanPS-ItalicMT" => "Times-Italic",
        "TimesNewRoman,BoldItalic" or "TimesNewRomanPS-BoldItalicMT" => "Times-BoldItalic",
        "CourierNew" or "CourierNewPSMT" => "Courier",
        "CourierNew,Bold" or "CourierNewPS-BoldMT" => "Courier-Bold",
        "CourierNew,Italic" or "CourierNewPS-ItalicMT" => "Courier-Oblique",
        "CourierNew,BoldItalic" or "CourierNewPS-BoldItalicMT" => "Courier-BoldOblique",
        // the URW clones of the standard fonts (Ghostscript, TeX distributions), current and older names
        "NimbusSans-Regular" or "NimbusSanL-Regu" => "Helvetica",
        "NimbusSans-Bold" or "NimbusSanL-Bold" => "Helvetica-Bold",
        "NimbusSans-Italic" or "NimbusSans-Oblique" or "NimbusSanL-ReguItal" => "Helvetica-Oblique",
        "NimbusSans-BoldItalic" or "NimbusSans-BoldOblique" or "NimbusSanL-BoldItal" => "Helvetica-BoldOblique",
        "NimbusRoman-Regular" or "NimbusRomNo9L-Regu" => "Times-Roman",
        "NimbusRoman-Bold" or "NimbusRomNo9L-Medi" => "Times-Bold",
        "NimbusRoman-Italic" or "NimbusRomNo9L-ReguItal" => "Times-Italic",
        "NimbusRoman-BoldItalic" or "NimbusRomNo9L-MediItal" => "Times-BoldItalic",
        "NimbusMonoPS-Regular" or "NimbusMonL-Regu" => "Courier",
        "NimbusMonoPS-Bold" or "NimbusMonL-Bold" => "Courier-Bold",
        "NimbusMonoPS-Italic" or "NimbusMonL-ReguObli" => "Courier-Oblique",
        "NimbusMonoPS-BoldItalic" or "NimbusMonL-BoldObli" => "Courier-BoldOblique",
        "StandardSymbolsPS" or "StandardSymL" => "Symbol",
        "D050000L" or "Dingbats" => "ZapfDingbats",
        _ => name,
    };

    /// <summary>The glyph boxes of a standard font by glyph name (1/1000 em) and its built-in encoding.</summary>
    internal sealed record GlyphBoxes(Dictionary<string, (short Left, short Bottom, short Right, short Top)> ByName, string?[] BuiltIn);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GlyphBoxes> ParsedBoxes = new();

    /// <summary>The glyph boxes of a standard font (by any of its names), or null for other fonts.</summary>
    public static GlyphBoxes? GetGlyphBoxes(string name)
    {
        name = CanonicalName(name);
        if (!GlyphBoundsData.TryGetValue(name, out var data)) return null;
        return ParsedBoxes.GetOrAdd(name, _ =>
        {
            var byName = new Dictionary<string, (short, short, short, short)>(StringComparer.Ordinal);
            var builtIn = new string?[256];
            foreach (var item in data.Split('|'))
            {
                var f = item.Split(' ');
                byName[f[0]] = (short.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), short.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture),
                    short.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture), short.Parse(f[5], System.Globalization.CultureInfo.InvariantCulture));
                int code = int.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture);
                if (code is >= 0 and < 256) builtIn[code] = f[0];
            }
            return new GlyphBoxes(byName, builtIn);
        });
    }

    /// <summary>Width of a character code (32..255) in 1/1000 em.</summary>
    public static int GetWidth(StandardFontInfo info, int code)
    {
        if (info.Widths == null)
        {
            if (info.Name != "ZapfDingbats") return info.FixedWidth;
            return code switch
            {
                0x34 => 760, // a20 check mark
                0x6C => 791, // a71 circle
                0x6E => 761, // a73 square
                0x75 => 759, // a77 diamond
                0x48 => 816, // a28 star
                0x38 => 846, // a24 cross
                0x20 => 278,
                _ => info.FixedWidth,
            };
        }
        return code is >= 32 and <= 255 ? info.Widths[code - 32] : 0;
    }
}
