using System.Text;
using MiPdf.Objects;

namespace MiPdf.Fonts;

/// <summary>
/// A font that can be used to draw text. Font instances are independent of documents and can be shared;
/// each document creates its own font dictionary (and subset) on first use.
/// </summary>
public abstract class PdfFont
{
    /// <summary>The PostScript name of the font.</summary>
    public abstract string Name { get; }

    /// <summary>Ascent in 1/1000 em (positive).</summary>
    public abstract double Ascent { get; }

    /// <summary>Descent in 1/1000 em (negative).</summary>
    public abstract double Descent { get; }

    /// <summary>Cap height in 1/1000 em.</summary>
    public abstract double CapHeight { get; }

    /// <summary>Additional line gap in 1/1000 em.</summary>
    public virtual double LineGap => 0;

    public virtual double UnderlinePosition => -100;

    public virtual double UnderlineThickness => 50;

    /// <summary>Advance width of a character in 1/1000 em.</summary>
    public abstract double GetCharWidth(int codePoint);

    /// <summary>True if the font can display the given character.</summary>
    public abstract bool CanDisplay(int codePoint);

    /// <summary>Width of <paramref name="text"/> in points at the given font size.</summary>
    public double GetTextWidth(string text, double fontSize) => GetTextWidth(text, fontSize, kerning: false);

    /// <summary>Width of <paramref name="text"/> in points, optionally including pair kerning.</summary>
    public double GetTextWidth(string text, double fontSize, bool kerning)
    {
        double total = 0;
        int previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            total += GetCharWidth(rune.Value);
            if (kerning && previous >= 0) total += GetKerning(previous, rune.Value);
            previous = rune.Value;
        }
        return total * fontSize / 1000.0;
    }

    /// <summary>True if the font has pair kerning information.</summary>
    public virtual bool HasKerning => false;

    /// <summary>Kerning between two characters in 1/1000 em (negative = closer), 0 if none.</summary>
    public virtual double GetKerning(int left, int right) => 0;

    /// <summary>Recommended distance between baselines in points.</summary>
    public double GetLineHeight(double fontSize) => (Ascent - Descent + LineGap) * fontSize / 1000.0;

    /// <summary>Ascent in points for the given size.</summary>
    public double GetAscent(double fontSize) => Ascent * fontSize / 1000.0;

    /// <summary>Descent in points for the given size (negative).</summary>
    public double GetDescent(double fontSize) => Descent * fontSize / 1000.0;

    internal abstract FontResource CreateResource(PdfDocument document);
}

/// <summary>The per-document representation of a font: its dictionary and used characters.</summary>
internal abstract class FontResource
{
    protected FontResource(PdfDocument document, PdfFont font)
    {
        Document = document;
        Font = font;
        Dictionary = new PdfDictionary(PdfName.Font);
        Reference = document.AddObject(Dictionary);
    }

    public PdfDocument Document { get; }

    public PdfFont Font { get; }

    public PdfDictionary Dictionary { get; }

    public PdfReference Reference { get; }

    /// <summary>True for fonts with 2-byte codes (written as hex strings).</summary>
    public abstract bool IsMultiByte { get; }

    /// <summary>Converts text into character codes and records the used glyphs.</summary>
    public abstract byte[] Encode(string text);

    /// <summary>Completes the font dictionary (widths, subset, ToUnicode) before saving.</summary>
    public abstract void Finish();

    /// <summary>Writes the entries needed to read text drawn so far (widths, ToUnicode) without subsetting.</summary>
    public virtual void PrepareForReading() => Finish();
}

/// <summary>Builds ToUnicode CMaps (ISO 32000-2, 9.10.3).</summary>
internal static class ToUnicodeCMap
{
    public static byte[] Build(IEnumerable<KeyValuePair<int, string>> mapping, int codeBytes)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n");
        sb.Append(codeBytes == 1 ? "<00> <FF>\n" : "<0000> <FFFF>\n");
        sb.Append("endcodespacerange\n");
        var entries = mapping.Where(kv => !string.IsNullOrEmpty(kv.Value)).OrderBy(kv => kv.Key).ToList();
        for (int i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (code, text) in chunk)
            {
                sb.Append('<').Append(code.ToString(codeBytes == 1 ? "X2" : "X4")).Append("> <");
                foreach (byte b in Encoding.BigEndianUnicode.GetBytes(text)) sb.Append(b.ToString("X2"));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
