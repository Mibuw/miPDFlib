using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MiPdf.PostScript;

/// <summary>
/// Reads what an importing application needs from an EPS file (EPSF specification 3.0): the PostScript section of a
/// DOS EPS binary file and the bounding box from the DSC comments.
/// </summary>
internal static class EpsFile
{
    /// <summary>
    /// The PostScript part of the file: a DOS EPS binary file (with a TIFF or WMF preview) holds it at an offset given
    /// in its header, other files are PostScript as they are.
    /// </summary>
    public static byte[] PostScript(byte[] data)
    {
        if (data.Length < 30 || data[0] != 0xC5 || data[1] != 0xD0 || data[2] != 0xD3 || data[3] != 0xC6) return data;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        if (offset < 30 || offset > data.Length) throw new PdfException("The DOS EPS header points outside the file.");
        // a length beyond the end is cut (some writers count the preview in)
        return data.AsSpan((int)offset, (int)Math.Min(length, (uint)(data.Length - (int)offset))).ToArray();
    }

    /// <summary>
    /// The bounding box of the graphic in PostScript default user space: %%HiResBoundingBox if it is valid, otherwise
    /// %%BoundingBox (as Ghostscript prefers them). The header comment counts; one deferred with <c>(atend)</c> is
    /// taken from the last comment in the file (the trailer), and one missing or invalid in the header from the first
    /// valid one in the file. Null if there is no valid bounding box.
    /// </summary>
    public static PdfRectangle? BoundingBox(byte[] program) =>
        Find(program, "%%HiResBoundingBox:") ?? Find(program, "%%BoundingBox:");

    private static PdfRectangle? Find(byte[] program, string key)
    {
        var lines = Lines(program).ToList();
        int header = HeaderEnd(lines);
        bool atEnd = false;
        for (int i = 0; i < header; i++)
        {
            if (!lines[i].StartsWith(key, StringComparison.Ordinal)) continue;
            string value = lines[i][key.Length..].Trim();
            if (value.StartsWith("(atend)", StringComparison.Ordinal))
            {
                atEnd = true;
                break;
            }
            if (Parse(value) is { } box) return box;
            break; // an invalid one is ignored, as Ghostscript does
        }
        var found = lines.Where(l => l.StartsWith(key, StringComparison.Ordinal)).Select(l => Parse(l[key.Length..])).Where(b => b != null);
        return atEnd ? found.LastOrDefault() : found.FirstOrDefault();
    }

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

    private static PdfRectangle? Parse(string value)
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

    /// <summary>The lines of the program (ended by CR, LF or CR LF), as Latin-1 text; only lines starting with % matter.</summary>
    private static IEnumerable<string> Lines(byte[] data)
    {
        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i < data.Length && data[i] != '\r' && data[i] != '\n') continue;
            // only comment lines are decoded (binary data in between is skipped cheaply)
            int length = Math.Min(i - start, 256);
            yield return length > 0 && (data[start] == '%' || data[start] == 0x04) ? Encoding.Latin1.GetString(data, start, length) : "";
            if (i < data.Length - 1 && data[i] == '\r' && data[i + 1] == '\n') i++;
            start = i + 1;
        }
    }
}
