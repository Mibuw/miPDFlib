using System.Text;
using MiPdf.Text;

namespace MiPdf.Objects;

/// <summary>
/// A PDF string. Internally a byte sequence; <see cref="Text"/> interprets it as a text string
/// (PDFDocEncoding, UTF-16BE or UTF-8 with byte order mark).
/// </summary>
public sealed class PdfString : PdfObject
{
    /// <summary>Creates a string from raw bytes.</summary>
    /// <param name="bytes">The raw bytes; the array is used directly, not copied.</param>
    /// <param name="hex">True to prefer serialization as hexadecimal string (<see cref="IsHex"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    public PdfString(byte[] bytes, bool hex = false)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        IsHex = hex;
    }

    /// <summary>Creates a text string (PDFDocEncoding if possible, otherwise UTF-16BE with BOM).</summary>
    public PdfString(string text) : this(EncodeText(text)) { }

    /// <inheritdoc/>
    public override PdfObjectType ObjectType => PdfObjectType.String;

    /// <summary>The raw bytes of the string (already decrypted when read from an encrypted file).</summary>
    public byte[] Bytes { get; set; }

    /// <summary>Preferred serialization as hexadecimal string.</summary>
    public bool IsHex { get; set; }

    /// <summary>Decodes the string as a PDF text string.</summary>
    public string Text => DecodeText(Bytes);

    /// <summary>Creates a text string (PDFDocEncoding if possible, otherwise UTF-16BE with BOM).</summary>
    public static PdfString FromText(string text) => new(text);

    /// <summary>Creates a string from raw bytes; see <see cref="PdfString(byte[], bool)"/>.</summary>
    public static PdfString FromBytes(byte[] bytes, bool hex = false) => new(bytes, hex);

    /// <summary>Encodes text as a PDF text string: PDFDocEncoding if all characters can be encoded, otherwise UTF-16BE with byte order mark.</summary>
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

    /// <summary>
    /// Decodes a PDF text string: UTF-16BE or UTF-8 when the corresponding byte order mark is present
    /// (UTF-16LE with BOM is tolerated as well), otherwise PDFDocEncoding.
    /// </summary>
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

    /// <summary>Creates a copy with a copy of the byte array.</summary>
    public override PdfObject Clone() => new PdfString((byte[])Bytes.Clone(), IsHex);

    /// <summary>True if <paramref name="obj"/> is a <see cref="PdfString"/> with the same bytes (<see cref="IsHex"/> is ignored).</summary>
    public override bool Equals(object? obj) => obj is PdfString s && s.Bytes.AsSpan().SequenceEqual(Bytes);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(Bytes);
        return h.ToHashCode();
    }
}
