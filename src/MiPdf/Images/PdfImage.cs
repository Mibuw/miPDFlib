using MiPdf.Filters;
using MiPdf.Objects;

namespace MiPdf.Images;

/// <summary>
/// A raster image that can be drawn on pages. Supported formats: JPEG (embedded unchanged) and PNG
/// (all color types and bit depths, transparency becomes a soft mask). Images are independent of documents;
/// each document embeds an image only once, however often it is drawn.
/// </summary>
public sealed class PdfImage
{
    private readonly Func<PdfDocument, PdfStream> _factory;

    private PdfImage(int width, int height, Func<PdfDocument, PdfStream> factory)
    {
        Width = width;
        Height = height;
        _factory = factory;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Width / height.</summary>
    public double AspectRatio => Height == 0 ? 1 : (double)Width / Height;

    public static PdfImage FromFile(string path) => FromBytes(File.ReadAllBytes(path));

    public static PdfImage FromStream(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return FromBytes(ms.ToArray());
    }

    /// <summary>Creates an image from JPEG, PNG, BMP or GIF data (detected by signature). JPEG is embedded as is; the others are decoded.</summary>
    public static PdfImage FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) return FromJpeg(data);
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return FromPng(data);
        if (data.Length > 26 && data[0] == 'B' && data[1] == 'M') return FromDecoded(BmpDecoder.Decode(data));
        if (data.Length > 13 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8') return FromDecoded(GifDecoder.Decode(data));
        throw new PdfException("Unsupported image format (supported: JPEG, PNG, BMP, GIF).");
    }

    private static PdfImage FromDecoded(DecodedBitmap bitmap) => FromPixels(bitmap.Width, bitmap.Height, bitmap.Rgb, 3, bitmap.Alpha);

    /// <summary>
    /// Creates an image from raw 8-bit pixel data (RGB, gray or CMYK, row by row without padding),
    /// optionally with an 8-bit alpha channel.
    /// </summary>
    public static PdfImage FromPixels(int width, int height, byte[] pixels, int components, byte[]? alpha = null)
    {
        if (components is not (1 or 3 or 4)) throw new ArgumentException("components must be 1, 3 or 4", nameof(components));
        if (width <= 0 || height <= 0) throw new ArgumentException("width and height must be positive");
        if (pixels.Length < (long)width * height * components) throw new ArgumentException("pixel data too short", nameof(pixels));
        return new PdfImage(width, height, doc => CreateRawImage(doc, width, height, pixels, components, 8, alpha));
    }

    internal PdfStream CreateStream(PdfDocument document) => _factory(document);

    private static PdfImage FromJpeg(byte[] data)
    {
        var info = JpegInfo.Parse(data);
        return new PdfImage(info.Width, info.Height, _ =>
        {
            var stream = NewImageStream(info.Width, info.Height, 8);
            stream[PdfName.ColorSpace] = info.Components switch
            {
                1 => PdfName.DeviceGray,
                4 => PdfName.DeviceCMYK,
                _ => PdfName.DeviceRGB,
            };
            if (info.Components == 4 && info.AdobeInverted)
                stream[PdfName.Decode] = PdfArray.FromNumbers(1, 0, 1, 0, 1, 0, 1, 0);
            stream.SetRawBytes(data);
            stream[PdfName.Filter] = PdfName.DCTDecode;
            return stream;
        });
    }

    private static PdfImage FromPng(byte[] data)
    {
        var png = PngDecoder.Decode(data);
        return new PdfImage(png.Width, png.Height, doc =>
        {
            if (png.Palette != null)
            {
                var stream = NewImageStream(png.Width, png.Height, png.BitDepth);
                var lookup = new PdfString(png.Palette, hex: true);
                stream[PdfName.ColorSpace] = new PdfArray(PdfName.Get("Indexed"), PdfName.DeviceRGB, new PdfNumber(png.Palette.Length / 3 - 1), lookup);
                stream.SetBytes(png.Pixels);
                if (png.Alpha != null) stream[PdfName.SMask] = doc.AddObject(CreateSoftMask(png.Width, png.Height, png.Alpha));
                return stream;
            }
            return CreateRawImage(doc, png.Width, png.Height, png.Pixels, png.Components, png.BitDepth, png.Alpha);
        });
    }

    private static PdfStream CreateRawImage(PdfDocument doc, int width, int height, byte[] pixels, int components, int bitDepth, byte[]? alpha)
    {
        var stream = NewImageStream(width, height, bitDepth);
        stream[PdfName.ColorSpace] = components switch
        {
            1 => PdfName.DeviceGray,
            4 => PdfName.DeviceCMYK,
            _ => PdfName.DeviceRGB,
        };
        stream.SetBytes(pixels);
        if (alpha != null && alpha.Any(a => a != 255))
            stream[PdfName.SMask] = doc.AddObject(CreateSoftMask(width, height, alpha));
        return stream;
    }

    private static PdfStream CreateSoftMask(int width, int height, byte[] alpha)
    {
        var mask = NewImageStream(width, height, 8);
        mask[PdfName.ColorSpace] = PdfName.DeviceGray;
        mask.SetBytes(alpha);
        return mask;
    }

    private static PdfStream NewImageStream(int width, int height, int bitsPerComponent)
    {
        var stream = new PdfStream();
        stream[PdfName.Type] = PdfName.XObject;
        stream[PdfName.Subtype] = PdfName.Image;
        stream.Set(PdfName.Get("Width"), width);
        stream.Set(PdfName.Get("Height"), height);
        stream.Set(PdfName.BitsPerComponent, bitsPerComponent);
        return stream;
    }
}

/// <summary>Reads the frame header of a JPEG file.</summary>
internal sealed record JpegInfo(int Width, int Height, int Components, int BitsPerComponent, bool AdobeInverted)
{
    public static JpegInfo Parse(byte[] d)
    {
        int pos = 2;
        bool adobe = false;
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF)
            {
                pos++;
                continue;
            }
            byte marker = d[pos + 1];
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                pos += 2;
                continue;
            }
            int length = (d[pos + 2] << 8) | d[pos + 3];
            if (marker == 0xEE && length >= 12 && pos + 9 <= d.Length &&
                d[pos + 4] == 'A' && d[pos + 5] == 'd' && d[pos + 6] == 'o' && d[pos + 7] == 'b' && d[pos + 8] == 'e')
                adobe = true;
            bool isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof && pos + 9 < d.Length)
            {
                int bits = d[pos + 4];
                int height = (d[pos + 5] << 8) | d[pos + 6];
                int width = (d[pos + 7] << 8) | d[pos + 8];
                int components = d[pos + 9];
                return new JpegInfo(width, height, components, bits, adobe);
            }
            if (marker == 0xDA) break; // start of scan without frame header
            pos += 2 + length;
        }
        throw new PdfException("Invalid JPEG: no frame header found.");
    }
}

/// <summary>Decoded PNG image (8-bit or 16-bit samples, alpha separated).</summary>
internal sealed class PngImage
{
    public int Width;
    public int Height;
    public int BitDepth;       // of Pixels (1, 2, 4, 8 for palette; 8 or 16 otherwise)
    public int Components;     // 1 or 3 (palette: 1 index)
    public byte[] Pixels = Array.Empty<byte>();
    public byte[]? Alpha;      // 8-bit
    public byte[]? Palette;    // RGB triplets
}

internal static class PngDecoder
{
    public static PngImage Decode(byte[] data)
    {
        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int length = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int start = pos + 8;
            if (length < 0 || start + length > data.Length) break;
            switch (type)
            {
                case "IHDR":
                    width = ReadInt(data, start);
                    height = ReadInt(data, start + 4);
                    bitDepth = data[start + 8];
                    colorType = data[start + 9];
                    interlace = data[start + 12];
                    break;
                case "PLTE":
                    palette = data.AsSpan(start, length).ToArray();
                    break;
                case "tRNS":
                    trns = data.AsSpan(start, length).ToArray();
                    break;
                case "IDAT":
                    idat.Write(data, start, length);
                    break;
            }
            if (type == "IEND") break;
            pos = start + length + 4;
        }
        if (width <= 0 || height <= 0) throw new PdfException("Invalid PNG: missing header.");
        PdfLimits.Current.CheckImage(width, height);
        if (bitDepth is not (1 or 2 or 4 or 8 or 16)) throw new PdfException($"Invalid PNG bit depth {bitDepth}.");

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new PdfException($"Unsupported PNG color type {colorType}.") };
        var raw = FlateFilter.Decode(idat.ToArray());
        byte[] samples = interlace == 1
            ? Deinterlace(raw, width, height, channels, bitDepth)
            : Unfilter(raw, width, height, channels, bitDepth, 0);

        var result = new PngImage { Width = width, Height = height };
        if (colorType == 3)
        {
            if (palette == null) throw new PdfException("Invalid PNG: missing palette.");
            result.Palette = palette;
            result.Components = 1;
            result.BitDepth = bitDepth;
            result.Pixels = samples;
            if (trns != null)
            {
                result.Alpha = new byte[width * height];
                int rowBytes = (width * bitDepth + 7) / 8;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int index = GetPackedSample(samples, y * rowBytes, x, bitDepth);
                        result.Alpha[y * width + x] = index < trns.Length ? trns[index] : (byte)255;
                    }
            }
            return result;
        }

        // expand to 8 bit (or keep 16 bit) and split alpha
        int colorChannels = colorType is 0 or 4 ? 1 : 3;
        bool hasAlpha = colorType is 4 or 6;
        int outDepth = bitDepth == 16 ? 16 : 8;
        int bytesPerSample = outDepth / 8;
        var pixels = new byte[width * height * colorChannels * bytesPerSample];
        byte[]? alpha = hasAlpha || trns != null ? new byte[width * height] : null;
        int srcRowBytes = (width * channels * bitDepth + 7) / 8;
        int[]? transparentKey = null;
        if (trns != null && !hasAlpha)
        {
            transparentKey = colorType == 0
                ? new[] { (trns[0] << 8) | trns[1] }
                : new[] { (trns[0] << 8) | trns[1], (trns[2] << 8) | trns[3], (trns[4] << 8) | trns[5] };
        }
        int dst = 0;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * srcRowBytes;
            for (int x = 0; x < width; x++)
            {
                bool keyMatch = transparentKey != null;
                for (int c = 0; c < channels; c++)
                {
                    int sampleIndex = x * channels + c;
                    int value = bitDepth switch
                    {
                        16 => (samples[rowStart + sampleIndex * 2] << 8) | samples[rowStart + sampleIndex * 2 + 1],
                        8 => samples[rowStart + sampleIndex],
                        _ => GetPackedSample(samples, rowStart, sampleIndex, bitDepth),
                    };
                    if (transparentKey != null && c < transparentKey.Length && value != transparentKey[c]) keyMatch = false;
                    if (c < colorChannels)
                    {
                        if (outDepth == 16)
                        {
                            pixels[dst++] = (byte)(value >> 8);
                            pixels[dst++] = (byte)value;
                        }
                        else pixels[dst++] = bitDepth == 8 ? (byte)value : (byte)(value * 255 / ((1 << bitDepth) - 1));
                    }
                    else alpha![y * width + x] = bitDepth == 16 ? (byte)(value >> 8) : (byte)value;
                }
                if (transparentKey != null) alpha![y * width + x] = keyMatch ? (byte)0 : (byte)255;
            }
        }
        result.Pixels = pixels;
        result.Alpha = alpha;
        result.Components = colorChannels;
        result.BitDepth = outDepth;
        return result;
    }

    private static int ReadInt(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];

    private static int GetPackedSample(byte[] data, int rowStart, int index, int bitDepth)
    {
        int bitPos = index * bitDepth;
        int b = data[rowStart + bitPos / 8];
        int shift = 8 - bitDepth - bitPos % 8;
        return (b >> shift) & ((1 << bitDepth) - 1);
    }

    /// <summary>Reverses PNG row filters for an image (or interlace pass) starting at <paramref name="offset"/>.</summary>
    private static byte[] Unfilter(byte[] raw, int width, int height, int channels, int bitDepth, int offset)
    {
        int rowBytes = (width * channels * bitDepth + 7) / 8;
        int bpp = Math.Max(1, channels * bitDepth / 8);
        var output = new byte[rowBytes * height];
        var prior = new byte[rowBytes];
        int pos = offset;
        for (int y = 0; y < height && pos < raw.Length; y++)
        {
            int filter = raw[pos++];
            var row = output.AsSpan(y * rowBytes, rowBytes);
            int available = Math.Min(rowBytes, raw.Length - pos);
            raw.AsSpan(pos, available).CopyTo(row);
            pos += rowBytes;
            for (int i = 0; i < rowBytes; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
                row[i] += filter switch
                {
                    1 => (byte)a,
                    2 => (byte)b,
                    3 => (byte)((a + b) / 2),
                    4 => (byte)Predictor.Paeth(a, b, c),
                    _ => 0,
                };
            }
            row.CopyTo(prior);
        }
        return output;
    }

    private static byte[] Deinterlace(byte[] raw, int width, int height, int channels, int bitDepth)
    {
        int[] startX = { 0, 4, 0, 2, 0, 1, 0 }, startY = { 0, 0, 4, 0, 2, 0, 1 };
        int[] stepX = { 8, 8, 4, 4, 2, 2, 1 }, stepY = { 8, 8, 8, 4, 4, 2, 2 };
        int bitsPerPixel = channels * bitDepth;
        int rowBytes = (width * bitsPerPixel + 7) / 8;
        var output = new byte[rowBytes * height];
        int offset = 0;
        for (int pass = 0; pass < 7; pass++)
        {
            int pw = (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            int ph = (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
            if (pw <= 0 || ph <= 0) continue;
            var passData = Unfilter(raw, pw, ph, channels, bitDepth, offset);
            int passRowBytes = (pw * bitsPerPixel + 7) / 8;
            offset += (passRowBytes + 1) * ph;
            for (int py = 0; py < ph; py++)
            {
                int y = startY[pass] + py * stepY[pass];
                for (int px = 0; px < pw; px++)
                {
                    int x = startX[pass] + px * stepX[pass];
                    for (int bit = 0; bit < bitsPerPixel; bit++)
                    {
                        int srcBit = px * bitsPerPixel + bit;
                        int dstBit = x * bitsPerPixel + bit;
                        int v = (passData[py * passRowBytes + srcBit / 8] >> (7 - srcBit % 8)) & 1;
                        if (v != 0) output[y * rowBytes + dstBit / 8] |= (byte)(0x80 >> (dstBit % 8));
                    }
                }
            }
        }
        return output;
    }
}
