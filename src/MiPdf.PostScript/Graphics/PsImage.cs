using System.Security.Cryptography;
using System.Text;
using MiPdf.Objects;

namespace MiPdf.PostScript.Graphics;

/// <summary>
/// An image as it is written to PDF: samples in a device color space (or Indexed with a device base), or a stencil mask
/// painted with the current color. JPEG data from a DCTDecode data source is passed through unchanged.
/// </summary>
internal sealed class PsImageOutput
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPerComponent { get; init; }

    /// <summary>The PDF color space (a name or an Indexed array); null for a stencil mask.</summary>
    public PdfObject? ColorSpace { get; init; }

    /// <summary>The decode array (null: the default).</summary>
    public double[]? Decode { get; init; }

    public bool Interpolate { get; init; }

    public bool IsStencil => ColorSpace == null;

    /// <summary>The samples (rows padded to whole bytes), or JPEG data if <see cref="IsJpeg"/>.</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();

    public bool IsJpeg { get; init; }

    /// <summary>The DecodeParms of JPEG data (ColorTransform).</summary>
    public int? ColorTransform { get; init; }

    /// <summary>
    /// An explicit mask: a stencil, sample 1 masks out. Color key masking (ImageType 4) is written this way too, as
    /// not all readers apply color key masks.
    /// </summary>
    public PsImageOutput? Mask { get; init; }
}

/// <summary>Writes images as XObjects; identical images are written once per document.</summary>
internal sealed class PsPdfImages
{
    /// <summary>Images up to this many bytes of data are written inline in the content.</summary>
    public const int InlineLimit = 2048;

    private readonly PdfDocument _document;
    private readonly Dictionary<string, PdfReference> _written = new();

    public PsPdfImages(PdfDocument document) => _document = document;

    /// <summary>True if the image is written inline (small, without masks: not all readers apply masks to inline images).</summary>
    public static bool IsInline(PsImageOutput image) => image.Data.Length <= InlineLimit && image.Mask == null && !image.IsJpeg;

    public PdfReference Get(PsImageOutput image)
    {
        string key = Key(image);
        if (_written.TryGetValue(key, out var existing)) return existing;
        var stream = new PdfStream();
        stream[PdfName.Type] = PdfName.XObject;
        stream[PdfName.Subtype] = PdfName.Image;
        stream[PdfName.Get("Width")] = new PdfNumber(image.Width);
        stream[PdfName.Get("Height")] = new PdfNumber(image.Height);
        if (image.IsStencil) stream[PdfName.Get("ImageMask")] = PdfBoolean.True;
        else
        {
            stream[PdfName.ColorSpace] = image.ColorSpace!;
            stream[PdfName.BitsPerComponent] = new PdfNumber(image.BitsPerComponent);
        }
        if (image.Decode != null) stream[PdfName.Decode] = Numbers(image.Decode);
        if (image.Interpolate) stream[PdfName.Get("Interpolate")] = PdfBoolean.True;
        if (image.Mask != null) stream[PdfName.Get("Mask")] = Get(image.Mask);
        if (image.IsJpeg)
        {
            stream.SetRawBytes(image.Data);
            stream[PdfName.Filter] = PdfName.Get("DCTDecode");
            if (image.ColorTransform is int ct) stream[PdfName.DecodeParms] = new PdfDictionary { [PdfName.Get("ColorTransform")] = new PdfNumber(ct) };
        }
        else stream.SetBytes(image.Data);
        var reference = _document.AddObject(stream);
        if (_written.Count < 10_000) _written[key] = reference;
        return reference;
    }

    private static PdfArray Numbers(double[] values) => new(values.Select(v => (PdfObject)new PdfNumber(v)));

    /// <summary>Identifies an image by its parameters and a hash of its data.</summary>
    private static string Key(PsImageOutput image)
    {
        var sb = new StringBuilder();
        for (var i = image; i != null; i = i.Mask)
        {
            sb.Append(i.Width).Append('x').Append(i.Height).Append('/').Append(i.BitsPerComponent).Append('/');
            sb.Append(i.ColorSpace == null ? "mask" : Describe(i.ColorSpace)).Append('/');
            if (i.Decode != null) sb.Append(string.Join(",", i.Decode)).Append('/');
            sb.Append(i.Interpolate).Append(i.IsJpeg).Append(i.ColorTransform).Append('/');
            sb.Append(Convert.ToHexString(SHA256.HashData(i.Data))).Append(';');
        }
        return sb.ToString();
    }

    private static string Describe(PdfObject o) => o switch
    {
        PdfName n => "/" + n.Value,
        PdfArray a => "[" + string.Join(" ", a.Select(Describe)) + "]",
        PdfString s => "<" + Convert.ToHexString(s.Bytes) + ">",
        PdfNumber n => n.ToString() ?? "",
        _ => o.ToString() ?? "",
    };
}
