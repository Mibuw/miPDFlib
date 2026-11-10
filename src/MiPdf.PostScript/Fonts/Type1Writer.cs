using System.Globalization;
using System.Text;
using MiPdf.PostScript.Interpreter;

namespace MiPdf.PostScript.Fonts;

/// <summary>
/// Writes a Type 1 font program (Adobe Type 1 Font Format) from a font dictionary: the clear-text part with name,
/// matrix and bounding box, and the eexec-encrypted part with the Private dictionary, all Subrs and the charstrings of
/// the selected glyphs. The charstrings are copied as they are (they stay charstring-encrypted).
/// </summary>
internal static class Type1Writer
{
    /// <summary>Private dictionary entries that are copied (numbers, booleans and arrays of numbers).</summary>
    private static readonly string[] PrivateKeys =
    {
        "BlueValues", "OtherBlues", "FamilyBlues", "FamilyOtherBlues", "BlueScale", "BlueShift", "BlueFuzz", "StdHW", "StdVW",
        "StemSnapH", "StemSnapV", "ForceBold", "LanguageGroup", "lenIV", "MinFeature", "RndStemUp", "ExpansionFactor",
    };

    public static (byte[] Program, int Length1, int Length2, int Length3) Write(PsFont font, IEnumerable<string> glyphs, string name, PsInterpreter ip)
    {
        var inv = CultureInfo.InvariantCulture;
        var m = font.BaseMatrix;
        var b = font.FontBBox;
        var clear = new StringBuilder();
        clear.Append("%!PS-AdobeFont-1.0: ").Append(name).Append(" 001.000\n");
        clear.Append("11 dict begin\n");
        clear.Append("/FontName /").Append(name).Append(" def\n");
        clear.Append("/Encoding StandardEncoding def\n");
        clear.Append("/PaintType ").Append(font.PaintType.ToString(inv)).Append(" def\n");
        if (font.PaintType == 2) clear.Append("/StrokeWidth ").Append(Number(font.StrokeWidth)).Append(" def\n");
        clear.Append("/FontType 1 def\n");
        clear.Append("/FontMatrix [").Append(string.Join(" ", new[] { m.A, m.B, m.C, m.D, m.E, m.F }.Select(Number))).Append("] readonly def\n");
        clear.Append("/FontBBox {").Append(string.Join(" ", b.Select(v => Number(Math.Round(v))))).Append("} readonly def\n");
        clear.Append("currentdict end\ncurrentfile eexec\n");
        var clearBytes = Encoding.ASCII.GetBytes(clear.ToString());

        var priv = new MemoryStream();
        void Text(string s) => priv.Write(Encoding.ASCII.GetBytes(s));
        var privateEntries = new List<(string Key, string Value)>();
        if (font.Private != null)
        {
            foreach (string key in PrivateKeys)
            {
                if (!font.Private.Map.TryGetValue(ip.Key(key), out var v)) continue;
                string? value = v.Type switch
                {
                    PsType.Integer or PsType.Real => Number(v.Number),
                    PsType.Boolean => v.Bool ? "true" : "false",
                    PsType.Array or PsType.PackedArray when v.AsArray.Span.ToArray().All(x => x.IsNumber) =>
                        "[" + string.Join(" ", v.AsArray.Span.ToArray().Select(x => Number(x.Number))) + "]",
                    _ => null,
                };
                if (value != null) privateEntries.Add((key, value));
            }
        }
        Text($"dup /Private {privateEntries.Count + 8} dict dup begin\n");
        Text("/RD{string currentfile exch readstring pop}executeonly def\n");
        Text("/ND{noaccess def}executeonly def\n");
        Text("/NP{noaccess put}executeonly def\n");
        Text("/password 5839 def\n");
        if (!privateEntries.Any(e => e.Key == "MinFeature")) Text("/MinFeature{16 16}def\n");
        foreach (var (key, value) in privateEntries)
        {
            if (key == "MinFeature") Text("/MinFeature{16 16}def\n");
            else Text($"/{key} {value} def\n");
        }
        int subrCount = font.SubrCount;
        Text($"/Subrs {subrCount} array\n");
        for (int i = 0; i < subrCount; i++)
        {
            var bytes = font.RawSubr(i) ?? Array.Empty<byte>();
            Text($"dup {i} {bytes.Length} RD ");
            priv.Write(bytes);
            Text(" NP\n");
        }
        Text("ND\n");
        var charstrings = glyphs.Select(g => (Name: g, Data: font.RawCharstring(g, ip))).Where(g => g.Data != null).ToList();
        Text($"2 index /CharStrings {charstrings.Count} dict dup begin\n");
        foreach (var (glyph, bytes) in charstrings)
        {
            Text($"/{glyph} {bytes!.Length} RD ");
            priv.Write(bytes);
            Text(" ND\n");
        }
        Text("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");
        var encrypted = Type1Charstrings.Encrypt(priv.ToArray(), Type1Charstrings.EexecKey, new byte[] { 0x8A, 0x3C, 0x51, 0x2E });

        var trailer = new StringBuilder();
        for (int i = 0; i < 8; i++) trailer.Append(new string('0', 64)).Append('\n');
        trailer.Append("cleartomark\n");
        var trailerBytes = Encoding.ASCII.GetBytes(trailer.ToString());

        var program = new byte[clearBytes.Length + encrypted.Length + trailerBytes.Length];
        clearBytes.CopyTo(program, 0);
        encrypted.CopyTo(program, clearBytes.Length);
        trailerBytes.CopyTo(program, clearBytes.Length + encrypted.Length);
        return (program, clearBytes.Length, encrypted.Length, trailerBytes.Length);
    }

    private static string Number(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);
}
