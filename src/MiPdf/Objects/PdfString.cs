using System.Text;
using MiPdf.Text;

namespace MiPdf.Objects;

/// <summary>
/// A PDF string. Internally a byte sequence; <see cref="Text"/> interprets it as a text string
/// (PDFDocEncoding, UTF-16BE or UTF-8 with byte order mark).
/// </summary>
public sealed class PdfString : PdfObject
{
    public PdfString(byte[] bytes, bool hex = false)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        IsHex = hex;
    }

    /// <summary>Creates a text string (PDFDocEncoding if possible, otherwise UTF-16BE with BOM).</summary>
    public PdfString(string text) : this(EncodeText(text)) { }

    public override PdfObjectType ObjectType => PdfObjectType.String;

    /// <summary>The raw bytes of the string (already decrypted when read from an encrypted file).</summary>
    public byte[] Bytes { get; set; }

    /// <summary>Preferred serialization as hexadecimal string.</summary>
    public bool IsHex { get; set; }

    /// <summary>Decodes the string as a PDF text string.</summary>
    public string Text => DecodeText(Bytes);

    public static PdfString FromText(string text) => new(text);

    public static PdfString FromBytes(byte[] bytes, bool hex = false) => new(bytes, hex);

    public static byte[] EncodeText(string text)
    {
        if (PdfDocEncoding.CanEncode(text)) return PdfDocEncoding.GetBytes(text);
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var result = new byte[utf16.Length + 2];
        result[0] = 0xFE;
        result[1] = 0xFF;
        utf16.CopyTo(result, 2);
        return result;
    }

    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) // not allowed by spec, seen in the wild
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        return PdfDocEncoding.GetString(bytes);
    }

    public override PdfObject Clone() => new PdfString((byte[])Bytes.Clone(), IsHex);

    public override bool Equals(object? obj) => obj is PdfString s && s.Bytes.AsSpan().SequenceEqual(Bytes);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(Bytes);
        return h.ToHashCode();
    }
}
