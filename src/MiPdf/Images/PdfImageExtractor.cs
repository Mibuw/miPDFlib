using System.Buffers.Binary;
using System.IO.Compression;
using MiPdf.Content;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Images;

/// <summary>An image found on a page or in an annotation appearance.</summary>
public sealed class PdfExtractedImage
{
    internal PdfExtractedImage(PdfStream stream, Matrix transform, string? name)
    {
        Stream = stream;
        Transform = transform;
        Name = name;
        var corners = new[] { transform.Transform(0, 0), transform.Transform(1, 0), transform.Transform(0, 1), transform.Transform(1, 1) };
        Bounds = new PdfRectangle(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    /// <summary>The image XObject (or the expanded inline image).</summary>
    public PdfStream Stream { get; }

    /// <summary>Resource name (null for inline images).</summary>
    public string? Name { get; }

    /// <summary>Transformation from the unit square to page coordinates.</summary>
    public Matrix Transform { get; }

    /// <summary>Position on the page (default user space).</summary>
    public PdfRectangle Bounds { get; }

    public int Width => Stream.GetInt(PdfName.Get("Width"));

    public int Height => Stream.GetInt(PdfName.Get("Height"));

    /// <summary>True for JPEG images (DCTDecode); <see cref="GetEncodedData"/> returns a complete JPEG file.</summary>
    public bool IsJpeg => Filters.StreamFilters.GetFilterNames(Stream).LastOrDefault()?.Value is "DCTDecode" or "DCT";

    /// <summary>True for JPEG 2000 images; <see cref="GetEncodedData"/> returns a JPEG 2000 file.</summary>
    public bool IsJpeg2000 => Filters.StreamFilters.GetFilterNames(Stream).LastOrDefault()?.Value is "JPXDecode";

    /// <summary>True if the image has a soft mask or stencil mask (transparency).</summary>
    public bool HasTransparency => Stream.Get(PdfName.SMask) is PdfStream || Stream.Get(PdfName.Get("Mask")) is PdfStream || Stream.GetBool(PdfName.Get("ImageMask"));

    /// <summary>The data with all non-image filters removed (for JPEG/JPEG 2000 the original file data).</summary>
    public byte[] GetEncodedData() => Filters.StreamFilters.Decode(Stream.RawBytes, Stream, out _);

    /// <summary>
    /// Decodes the image to PNG (with alpha channel from /SMask, /Mask or stencil masks). Returns null if the image
    /// cannot be decoded (JPEG, JPEG 2000, JBIG2, CCITT, Lab color) – use <see cref="GetEncodedData"/> for JPEG.
    /// </summary>
    /// <param name="background">If set, transparency is composited onto this color (the PNG has no alpha channel).</param>
    public byte[]? ToPng(PdfColor? background = null)
    {
        var decoded = ImageDecoder.Decode(Stream);
        if (decoded == null) return null;
        var (width, height, rgb, alpha) = decoded.Value;
        if (alpha != null && background is { } bg)
        {
            var (br, bgc, bb) = ImageDecoder.ToRgb(bg);
            for (int i = 0; i < width * height; i++)
            {
                int a = alpha[i];
                rgb[i * 3] = (byte)((rgb[i * 3] * a + br * (255 - a)) / 255);
                rgb[i * 3 + 1] = (byte)((rgb[i * 3 + 1] * a + bgc * (255 - a)) / 255);
                rgb[i * 3 + 2] = (byte)((rgb[i * 3 + 2] * a + bb * (255 - a)) / 255);
            }
            alpha = null;
        }
        return PngEncoder.Encode(width, height, rgb, alpha);
    }

    /// <summary>Returns the image as a file: JPEG data for JPEG images without transparency, otherwise PNG (null if not decodable).</summary>
    public byte[]? ToFile(out string extension)
    {
        if ((IsJpeg || IsJpeg2000) && !HasTransparency)
        {
            var data = GetEncodedData();
            // JPEG 2000 in PDF is either a JP2 file or a raw codestream (starts with SOC FF 4F)
            extension = IsJpeg ? ".jpg" : data.Length > 1 && data[0] == 0xFF && data[1] == 0x4F ? ".j2k" : ".jp2";
            return data;
        }
        extension = ".png";
        return ToPng();
    }

    public override string ToString() => $"{Name ?? "inline"} {Width}x{Height} at {Bounds}";
}

/// <summary>Extracts images from page content and annotation appearances.</summary>
public static class PdfImageExtractor
{
    /// <summary>Images drawn on the page (including images inside form XObjects and inline images).</summary>
    public static IReadOnlyList<PdfExtractedImage> Extract(PdfPage page)
    {
        var result = new List<PdfExtractedImage>();
        new GraphicsWalker { Image = (s, m, n) => result.Add(new PdfExtractedImage(s, m, n)) }
            .Walk(page.GetContentBytes(), page.GetInheritable(PdfName.Resources) as PdfDictionary, Matrix.Identity);
        return result;
    }

    /// <summary>
    /// Images in the normal appearance of an annotation (e.g. the image of a visible signature or a push button);
    /// bounds are in page coordinates.
    /// </summary>
    public static IReadOnlyList<PdfExtractedImage> Extract(PdfDictionary annotation)
    {
        var result = new List<PdfExtractedImage>();
        var normal = annotation.GetDictionary(PdfName.AP)?.Get(PdfName.N);
        if (normal is PdfDictionary states and not PdfStream)
            normal = annotation.GetName(PdfName.AS) is { } state ? states.Get(state) : null;
        if (normal is not PdfStream ap || annotation.GetRectangle(PdfName.Rect) is not { } rect) return result;
        var bbox = ap.GetRectangle(PdfName.BBox) ?? rect;
        var matrix = ap.GetArray(PdfName.Get("Matrix")) is { Count: 6 } m
            ? new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5))
            : Matrix.Identity;
        // map the transformed bbox onto the annotation rectangle (ISO 32000-1, 12.5.5)
        var corners = new[] { matrix.Transform(bbox.Left, bbox.Bottom), matrix.Transform(bbox.Right, bbox.Bottom), matrix.Transform(bbox.Left, bbox.Top), matrix.Transform(bbox.Right, bbox.Top) };
        double minX = corners.Min(c => c.X), minY = corners.Min(c => c.Y), maxX = corners.Max(c => c.X), maxY = corners.Max(c => c.Y);
        double sx = maxX - minX == 0 ? 1 : rect.Width / (maxX - minX), sy = maxY - minY == 0 ? 1 : rect.Height / (maxY - minY);
        var toPage = matrix.Multiply(new Matrix(sx, 0, 0, sy, rect.Left - minX * sx, rect.Bottom - minY * sy));
        new GraphicsWalker { Image = (s, mm, n) => result.Add(new PdfExtractedImage(s, mm, n)) }
            .Walk(ap.GetBytes(), ap.GetDictionary(PdfName.Resources), toPage);
        return result;
    }
}

/// <summary>Decodes image XObjects into 8-bit RGB with optional alpha.</summary>
internal static class ImageDecoder
{
    public static (int Width, int Height, byte[] Rgb, byte[]? Alpha)? Decode(PdfStream image)
    {
        int width = image.GetInt(PdfName.Get("Width")), height = image.GetInt(PdfName.Get("Height"));
        if (width <= 0 || height <= 0) return null;
        PdfLimits.Current.CheckImage(width, height);
        byte[] data;
        try
        {
            data = Filters.StreamFilters.Decode(image.RawBytes, image, out var remaining);
            if (remaining != null) return null;
        }
        catch (PdfException)
        {
            return null;
        }

        var rgb = new byte[width * height * 3];
        byte[]? alpha = null;
        if (image.GetBool(PdfName.Get("ImageMask")))
        {
            // stencil mask: painted samples black, others transparent
            var samples = Samples(data, width, height, 1, 1);
            if (samples == null) return null;
            bool inverted = image.GetArray(PdfName.Decode) is { Count: >= 2 } d && d.GetDouble(0) == 1;
            alpha = new byte[width * height];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)((samples[i] == 0) != inverted ? 255 : 0);
            return (width, height, rgb, alpha);
        }

        int bpc = image.GetInt(PdfName.BitsPerComponent, 8);
        var colorSpace = image.Get(PdfName.ColorSpace);
        if (!Convert(data, width, height, bpc, colorSpace, image.GetArray(PdfName.Decode), rgb)) return null;

        if (image.Get(PdfName.SMask) is PdfStream smask) alpha = DecodeAlpha(smask, width, height, soft: true);
        else if (image.Get(PdfName.Get("Mask")) is PdfStream mask) alpha = DecodeAlpha(mask, width, height, soft: false);
        return (width, height, rgb, alpha);
    }

    private static byte[]? DecodeAlpha(PdfStream mask, int width, int height, bool soft)
    {
        int mw = mask.GetInt(PdfName.Get("Width")), mh = mask.GetInt(PdfName.Get("Height"));
        if (mw <= 0 || mh <= 0) return null;
        byte[] data;
        try
        {
            data = Filters.StreamFilters.Decode(mask.RawBytes, mask, out var remaining);
            if (remaining != null) return null;
        }
        catch (PdfException)
        {
            return null;
        }
        int bpc = soft ? mask.GetInt(PdfName.BitsPerComponent, 8) : 1;
        var samples = Samples(data, mw, mh, 1, bpc);
        if (samples == null) return null;
        bool inverted = mask.GetArray(PdfName.Decode) is { Count: >= 2 } d && d.GetDouble(0) == 1;
        var alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int v = samples[(y * mh / height) * mw + x * mw / width];
                if (soft && bpc == 1) v *= 255;
                // soft mask: sample = opacity; stencil /Mask: sample 1 (default decode) = masked out
                alpha[y * width + x] = soft ? (byte)(inverted ? 255 - v : v) : (byte)((v != 0) != inverted ? 0 : 255);
            }
        }
        return alpha;
    }

    /// <summary>Expands packed samples (1-16 bits) to one byte per sample (0-255; for 1 bit: 0 or 1 when raw).</summary>
    private static byte[]? Samples(byte[] data, int width, int height, int components, int bpc, bool raw = false)
    {
        if (bpc is not (1 or 2 or 4 or 8 or 16)) return null;
        long rowBytes = ((long)width * components * bpc + 7) / 8;
        if (rowBytes * height > data.Length) return null;
        var result = new byte[width * height * components];
        int max = (1 << Math.Min(bpc, 8)) - 1;
        for (int y = 0; y < height; y++)
        {
            long row = y * rowBytes;
            for (int i = 0; i < width * components; i++)
            {
                int value = bpc switch
                {
                    8 => data[row + i],
                    16 => data[row + i * 2],
                    _ => (data[row + (long)i * bpc / 8] >> (8 - bpc - i * bpc % 8)) & max,
                };
                result[y * width * components + i] = bpc is 8 or 16 || raw ? (byte)value : bpc == 1 ? (byte)value : (byte)(value * 255 / max);
            }
        }
        return result;
    }

    private static bool Convert(byte[] data, int width, int height, int bpc, PdfObject? colorSpace, PdfArray? decode, byte[] rgb)
    {
        var cs = colorSpace?.Resolve();
        string family = cs switch
        {
            PdfName n => n.Value,
            PdfArray a when a.Count > 0 && a[0].Resolve() is PdfName f => f.Value,
            _ => "DeviceGray",
        };
        if (family is "ICCBased" && cs is PdfArray icc && icc.Count > 1 && icc[1].Resolve() is PdfStream profile)
        {
            family = profile.GetInt(PdfName.N, 3) switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" };
        }
        family = family switch { "G" or "CalGray" => "DeviceGray", "RGB" or "CalRGB" => "DeviceRGB", "CMYK" => "DeviceCMYK", "I" => "Indexed", _ => family };

        if (family == "Indexed" && cs is PdfArray indexed && indexed.Count >= 4)
        {
            var baseSpace = indexed[1].Resolve();
            int hival = (indexed[2].Resolve() as PdfNumber)?.IntValue ?? 255;
            byte[] lookup = indexed[3].Resolve() switch { PdfString s => s.Bytes, PdfStream s => s.GetBytes(), _ => Array.Empty<byte>() };
            int baseComponents = baseSpace switch
            {
                PdfName { Value: "DeviceGray" or "G" } => 1,
                PdfName { Value: "DeviceCMYK" or "CMYK" } => 4,
                PdfArray b when b.Count > 1 && b[0].Resolve() is PdfName { Value: "ICCBased" } && b[1].Resolve() is PdfStream p => p.GetInt(PdfName.N, 3),
                _ => 3,
            };
            var indices = Samples(data, width, height, 1, bpc, raw: true);
            if (indices == null) return false;
            for (int i = 0; i < width * height; i++)
            {
                int index = Math.Min(indices[i], hival) * baseComponents;
                if (index + baseComponents > lookup.Length) continue;
                SetPixel(rgb, i, lookup.AsSpan(index, baseComponents));
            }
            return true;
        }

        int components = family switch
        {
            "DeviceGray" or "Separation" => 1,
            "DeviceRGB" => 3,
            "DeviceCMYK" => 4,
            "DeviceN" when cs is PdfArray dn && dn.Count > 1 && dn[1].Resolve() is PdfArray names => names.Count,
            _ => -1,
        };
        if (components <= 0) return false;
        var samples = Samples(data, width, height, components, bpc);
        if (samples == null) return false;
        if (bpc == 1)
            for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(samples[i] * 255);
        if (decode is { Count: >= 2 } && decode.GetDouble(0) > decode.GetDouble(1))
            for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(255 - samples[i]);
        for (int i = 0; i < width * height; i++)
        {
            var pixel = samples.AsSpan(i * components, components);
            if (family is "Separation" or "DeviceN")
            {
                // tint 1 = full colorant: show as dark
                int tint = 0;
                foreach (var t in pixel) tint = Math.Max(tint, t);
                rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = (byte)(255 - tint);
            }
            else SetPixel(rgb, i, pixel);
        }
        return true;
    }

    private static void SetPixel(byte[] rgb, int i, ReadOnlySpan<byte> c)
    {
        switch (c.Length)
        {
            case 1:
                rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = c[0];
                break;
            case 4:
                int k = c[3];
                rgb[i * 3] = (byte)((255 - c[0]) * (255 - k) / 255);
                rgb[i * 3 + 1] = (byte)((255 - c[1]) * (255 - k) / 255);
                rgb[i * 3 + 2] = (byte)((255 - c[2]) * (255 - k) / 255);
                break;
            default:
                rgb[i * 3] = c[0];
                rgb[i * 3 + 1] = c[1];
                rgb[i * 3 + 2] = c[2];
                break;
        }
    }

    internal static (int R, int G, int B) ToRgb(PdfColor color)
    {
        var c = color.Components;
        return c.Length switch
        {
            1 => ((int)(c[0] * 255), (int)(c[0] * 255), (int)(c[0] * 255)),
            4 => ((int)((1 - c[0]) * (1 - c[3]) * 255), (int)((1 - c[1]) * (1 - c[3]) * 255), (int)((1 - c[2]) * (1 - c[3]) * 255)),
            _ => ((int)(c[0] * 255), (int)(c[1] * 255), (int)(c[2] * 255)),
        };
    }
}

/// <summary>Minimal PNG encoder (8-bit RGB or RGBA, filter "Sub").</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>Encodes 8-bit RGB pixels (row by row) with an optional 8-bit alpha channel as PNG.</summary>
    public static byte[] Encode(int width, int height, byte[] rgb, byte[]? alpha = null)
    {
        int channels = alpha != null ? 4 : 3;
        var raw = new byte[(width * channels + 1) * height];
        for (int y = 0, p = 0; y < height; y++)
        {
            raw[p++] = 1; // Sub filter
            int rowStart = p;
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                raw[p++] = rgb[i * 3];
                raw[p++] = rgb[i * 3 + 1];
                raw[p++] = rgb[i * 3 + 2];
                if (alpha != null) raw[p++] = alpha[i];
            }
            for (int i = p - 1; i >= rowStart + channels; i--) raw[i] = (byte)(raw[i] - raw[i - channels]);
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)(alpha != null ? 6 : 2);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeBytes.Concat(data)) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xFFFFFFFF);
        output.Write(length);
    }
}
