using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MiPdf.PostScript;

/// <summary>
/// What the converter and the EPS import take from the Document Structuring Conventions (DSC 3.0) comments of a
/// PostScript file: the bounding box, whether it is an EPS file, the document information (%%Title, %%Creator, %%For)
/// and the orientation of the pages. The program itself does not depend on them.
/// </summary>
internal sealed class DscComments
{
    private const int MaxLine = 512;
    private static readonly byte[] Uel = "\x1B%-12345X"u8.ToArray();

    private readonly List<string> _lines;
    private readonly int _headerEnd;

    public DscComments(byte[] program)
    {
        _lines = Lines(program).ToList();
        _headerEnd = HeaderEnd(_lines);
        IsEps = _lines.Count > 0 && _lines[0].TrimStart('\x04').StartsWith("%!PS-Adobe-", StringComparison.Ordinal)
            && _lines[0].Contains(" EPSF-", StringComparison.Ordinal);
        Title = Text("%%Title:");
        Creator = Text("%%Creator:");
        For = Text("%%For:");
        ReadOrientations();
    }

    /// <summary>The first line declares an EPS file (<c>%!PS-Adobe-3.0 EPSF-3.0</c>).</summary>
    public bool IsEps { get; }

    public string? Title { get; }

    public string? Creator { get; }

    /// <summary>The person the document is for (Ghostscript makes it the author).</summary>
    public string? For { get; }

    /// <summary>The orientation of all pages from %%Orientation or a default %%PageOrientation: 0 or 90 (Landscape).</summary>
    public int? DocumentOrientation { get; private set; }

    /// <summary>The orientations from %%PageOrientation, by the order of the %%Page comments (null where none is given).</summary>
    public List<int?> PageOrientations { get; } = new();

    /// <summary>
    /// The PostScript part of a file: a DOS EPS binary file (with a TIFF or WMF preview) holds it at an offset given
    /// in its header; a print file may start with a PJL job header after a universal exit language sequence and end
    /// with another one. Other files are PostScript as they are.
    /// </summary>
    public static byte[] PostScript(byte[] data)
    {
        if (data.Length >= 30 && data[0] == 0xC5 && data[1] == 0xD0 && data[2] == 0xD3 && data[3] == 0xC6)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            if (offset < 30 || offset > data.Length) throw new PdfException("The DOS EPS header points outside the file.");
            // a length beyond the end is cut (some writers count the preview in)
            return data.AsSpan((int)offset, (int)Math.Min(length, (uint)(data.Length - (int)offset))).ToArray();
        }
        if (!data.AsSpan().StartsWith(Uel)) return data;
        // PJL commands up to the line that starts the PostScript program, which ends at the next exit sequence
        int start = Uel.Length;
        while (start < data.Length && data[start] != '%')
        {
            int end = data.AsSpan(start).IndexOfAny((byte)'\n', (byte)'\r');
            if (end < 0) return Array.Empty<byte>();
            start += end + 1;
        }
        int stop = data.AsSpan(start).IndexOf(Uel);
        return data.AsSpan(start, stop < 0 ? data.Length - start : stop).ToArray();
    }

    /// <summary>
    /// The bounding box of the graphic in PostScript default user space: %%HiResBoundingBox if it is valid, otherwise
    /// %%BoundingBox (as Ghostscript prefers them). The header comment counts; one deferred with <c>(atend)</c> is
    /// taken from the last comment in the file (the trailer), and one missing or invalid in the header from the first
    /// valid one in the file. Null if there is no valid bounding box.
    /// </summary>
    public PdfRectangle? BoundingBox => Box("%%HiResBoundingBox:") ?? Box("%%BoundingBox:");

    /// <summary>The value of a header comment (null if it is missing); <c>(atend)</c> refers to the last one in the file.</summary>
    private string? Header(string key)
    {
        for (int i = 0; i < _headerEnd; i++)
        {
            if (!_lines[i].StartsWith(key, StringComparison.Ordinal)) continue;
            string value = _lines[i][key.Length..].Trim();
            if (!value.StartsWith("(atend)", StringComparison.Ordinal)) return value;
            for (int j = _lines.Count - 1; j > i; j--)
                if (_lines[j].StartsWith(key, StringComparison.Ordinal)) return _lines[j][key.Length..].Trim();
            return null;
        }
        return null;
    }

    private PdfRectangle? Box(string key)
    {
        bool atEnd = false;
        for (int i = 0; i < _headerEnd; i++)
        {
            if (!_lines[i].StartsWith(key, StringComparison.Ordinal)) continue;
            string value = _lines[i][key.Length..].Trim();
            if (value.StartsWith("(atend)", StringComparison.Ordinal))
            {
                atEnd = true;
                break;
            }
            if (ParseBox(value) is { } box) return box;
            break; // an invalid one is ignored, as Ghostscript does
        }
        var found = _lines.Where(l => l.StartsWith(key, StringComparison.Ordinal)).Select(l => ParseBox(l[key.Length..])).Where(b => b != null);
        return atEnd ? found.LastOrDefault() : found.FirstOrDefault();
    }

    private static PdfRectangle? ParseBox(string value)
    {
        var parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return null;
        var n = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !double.IsFinite(n[i])
                || Math.Abs(n[i]) > 1e6) return null;
        }
        if (n[2] <= n[0] || n[3] <= n[1]) return null;
        return new PdfRectangle(n[0], n[1], n[2], n[3]);
    }

    /// <summary>
    /// A text comment: a PostScript string in parentheses or angle brackets is decoded, other text taken as it is.
    /// Bytes starting with a UTF-16 byte order mark are UTF-16, valid UTF-8 is UTF-8, anything else Latin-1.
    /// </summary>
    private string? Text(string key)
    {
        string? value = Header(key);
        if (string.IsNullOrEmpty(value)) return null;
        byte[] bytes = Encoding.Latin1.GetBytes(value);
        if (value[0] == '(') bytes = LiteralString(bytes);
        else if (value[0] == '<' && value[^1] == '>' && value.Length % 2 == 0 && value[1..^1].All(Uri.IsHexDigit))
            bytes = Convert.FromHexString(value[1..^1]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2 & ~1);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }
        text = new string(text.Where(c => c >= ' ' || c == '\t').ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The bytes of a string in parentheses with its escapes (PLRM 3.2.2); an unbalanced one ends at the line end.</summary>
    private static byte[] LiteralString(byte[] s)
    {
        var result = new List<byte>(s.Length);
        int depth = 0;
        for (int i = 1; i < s.Length; i++)
        {
            byte c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                c = s[++i];
                switch (c)
                {
                    case (byte)'n': result.Add(10); break;
                    case (byte)'r': result.Add(13); break;
                    case (byte)'t': result.Add(9); break;
                    case (byte)'b': result.Add(8); break;
                    case (byte)'f': result.Add(12); break;
                    case >= (byte)'0' and <= (byte)'7':
                    {
                        int v = c - '0';
                        for (int k = 0; k < 2 && i + 1 < s.Length && s[i + 1] is >= (byte)'0' and <= (byte)'7'; k++) v = v * 8 + s[++i] - '0';
                        result.Add((byte)v);
                        break;
                    }
                    default: result.Add(c); break;
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && depth-- == 0) break;
            result.Add(c);
        }
        return result.ToArray();
    }

    /// <summary>
    /// %%Orientation in the header and %%PageOrientation in the defaults or after each %%Page comment, outside of
    /// embedded documents. Only Portrait and Landscape count, as with Ghostscript.
    /// </summary>
    private void ReadOrientations()
    {
        DocumentOrientation = Orientation(Header("%%Orientation:"));
        int nested = 0;
        for (int i = _headerEnd; i < _lines.Count; i++)
        {
            string line = _lines[i];
            if (!line.StartsWith("%%", StringComparison.Ordinal)) continue;
            if (line.StartsWith("%%BeginDocument", StringComparison.Ordinal)) nested++;
            else if (line.StartsWith("%%EndDocument", StringComparison.Ordinal)) nested = Math.Max(0, nested - 1);
            else if (nested > 0) continue;
            else if (line.StartsWith("%%Page:", StringComparison.Ordinal)) PageOrientations.Add(null);
            else if (line.StartsWith("%%PageOrientation:", StringComparison.Ordinal) && Orientation(line["%%PageOrientation:".Length..]) is { } o)
            {
                if (PageOrientations.Count == 0) DocumentOrientation = o;
                else PageOrientations[^1] = o;
            }
        }
    }

    private static int? Orientation(string? value) => value?.Trim() switch
    {
        "Portrait" => 0,
        "Landscape" => 90,
        _ => null,
    };

    /// <summary>The number of header lines: up to %%EndComments or the first line that is not a comment.</summary>
    private static int HeaderEnd(List<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("%%EndComments", StringComparison.Ordinal)) return i;
            if (!lines[i].StartsWith('%') && !(i == 0 && lines[i].StartsWith('\x04'))) return i;
        }
        return lines.Count;
    }

    /// <summary>The lines of the program (ended by CR, LF or CR LF), as Latin-1 text; only lines starting with % matter.</summary>
    private static IEnumerable<string> Lines(byte[] data)
    {
        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i < data.Length && data[i] != '\r' && data[i] != '\n') continue;
            // only comment lines are decoded (binary data in between is skipped cheaply)
            int length = Math.Min(i - start, MaxLine);
            yield return length > 0 && (data[start] == '%' || data[start] == 0x04) ? Encoding.Latin1.GetString(data, start, length) : "";
            if (i < data.Length - 1 && data[i] == '\r' && data[i + 1] == '\n') i++;
            start = i + 1;
        }
    }
}
