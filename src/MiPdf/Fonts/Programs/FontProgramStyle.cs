using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MiPdf.Objects;

namespace MiPdf.Fonts.Programs;

/// <summary>
/// Bold and italic as an embedded font program records them, for fonts whose name and descriptor do not tell: the OS/2
/// weight class and selection flags or the head table's macStyle of TrueType and OpenType programs, the Weight and
/// ItalicAngle of Type 1 and CFF programs. Read once per program.
/// </summary>
internal static class FontProgramStyle
{
    private static readonly ConditionalWeakTable<PdfStream, StrongBox<(bool Bold, bool Italic)?>> Cache = new();

    /// <summary>The style of the font program of a descriptor, or null if it has none or does not record one.</summary>
    public static (bool Bold, bool Italic)? Read(PdfDictionary? descriptor)
    {
        if (descriptor == null) return null;
        if (descriptor.GetStream(PdfName.FontFile2) is { } trueType) return Cached(trueType, s => Sfnt(s.GetBytes()));
        if (descriptor.GetStream(PdfName.Get("FontFile3")) is { } fontFile3)
            return Cached(fontFile3, s =>
            {
                var data = s.GetBytes();
                if (s.GetName(PdfName.Subtype)?.Value == "OpenType" || data.Length > 4 && (data[0] == 'O' && data[1] == 'T' || data[0] == 0 && data[1] == 1))
                    return Sfnt(data);
                var cff = new CffFont(data);
                return (IsBoldWeight(cff.Weight), Math.Abs(cff.ItalicAngle) > 0.5);
            });
        if (descriptor.GetStream(PdfName.Get("FontFile")) is { } type1)
            return Cached(type1, s =>
            {
                var data = s.GetBytes();
                int clear = s.GetInt(PdfName.Get("Length1"));
                string text = Encoding.Latin1.GetString(data, 0, clear > 0 && clear <= data.Length ? clear : Math.Min(data.Length, 4096));
                var weight = Regex.Match(text, @"/Weight\s*\(([^)]*)\)");
                var angle = Regex.Match(text, @"/ItalicAngle\s+(-?[\d.]+)");
                bool italic = angle.Success && double.TryParse(angle.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) && Math.Abs(a) > 0.5;
                return weight.Success || angle.Success ? (IsBoldWeight(weight.Groups[1].Value), italic) : null;
            });
        return null;
    }

    private static (bool Bold, bool Italic)? Cached(PdfStream stream, Func<PdfStream, (bool Bold, bool Italic)?> read) =>
        Cache.GetValue(stream, s =>
        {
            try
            {
                return new(read(s));
            }
            catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex) || ex is PdfException)
            {
                return new(null);   // a damaged program tells nothing
            }
        }).Value;

    private static bool IsBoldWeight(string? weight) =>
        weight != null && Regex.IsMatch(weight, "Bold|Black|Heavy|Semibold|SemiBold|Demi|Extra ?Bold|Ultra ?Bold", RegexOptions.IgnoreCase);

    /// <summary>The style of a TrueType or OpenType program: OS/2 (weight class 600 or more, selection flags), else head.macStyle.</summary>
    private static (bool Bold, bool Italic)? Sfnt(byte[] data)
    {
        if (data.Length < 12) return null;
        int tables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        int os2 = -1, os2Length = 0, head = -1, headLength = 0;
        for (int i = 0; i < tables && 12 + 16 * (i + 1) <= data.Length; i++)
        {
            int record = 12 + 16 * i;
            string tag = Encoding.ASCII.GetString(data, record, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12));
            if (offset < 0 || length < 0 || (long)offset + length > data.Length) continue;
            if (tag == "OS/2") (os2, os2Length) = (offset, length);
            else if (tag == "head") (head, headLength) = (offset, length);
        }
        if (os2 >= 0 && os2Length >= 64)
        {
            int weightClass = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2 + 4));
            int selection = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2 + 62));
            return (weightClass >= 600 || (selection & 0x20) != 0, (selection & 0x201) != 0);
        }
        if (head >= 0 && headLength >= 46)
        {
            int macStyle = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(head + 44));
            return ((macStyle & 1) != 0, (macStyle & 2) != 0);
        }
        return null;
    }
}
