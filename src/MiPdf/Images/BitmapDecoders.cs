using System.Buffers.Binary;

namespace MiPdf.Images;

/// <summary>Decoded image: 8-bit RGB pixels and an optional 8-bit alpha channel.</summary>
internal sealed record DecodedBitmap(int Width, int Height, byte[] Rgb, byte[]? Alpha);

/// <summary>Windows bitmap (BMP/DIB) decoder: 1/4/8-bit palette, 16/24/32-bit, BI_RGB and BI_BITFIELDS, RLE8/RLE4.</summary>
internal static class BmpDecoder
{
    public static DecodedBitmap Decode(byte[] d)
    {
        try
        {
            return DecodeCore(d);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            throw new PdfException("Invalid or truncated BMP image.", ex);
        }
    }

    private static int I32(byte[] d, int p) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p, 4));

    private static uint U32(byte[] d, int p) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));

    private static int U16(byte[] d, int p) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));

    private static DecodedBitmap DecodeCore(byte[] d)
    {
        int dataOffset = I32(d, 10);
        int headerSize = I32(d, 14);
        int width, height, bpp, compression = 0, colorsUsed = 0;
        bool core = headerSize == 12;
        if (core)
        {
            width = U16(d, 18);
            height = (short)U16(d, 20);
            bpp = U16(d, 24);
        }
        else
        {
            width = I32(d, 18);
            height = I32(d, 22);
            bpp = U16(d, 28);
            compression = I32(d, 30);
            colorsUsed = I32(d, 46);
        }
        bool topDown = height < 0;
        height = Math.Abs(height);
        PdfLimits.Current.CheckImage(width, height);

        // palette
        int paletteStart = 14 + headerSize + (compression is 3 or 6 && headerSize == 40 ? 12 : 0) + (compression == 6 && headerSize == 40 ? 4 : 0);
        int entrySize = core ? 3 : 4;
        int paletteCount = bpp <= 8 ? (colorsUsed > 0 ? colorsUsed : 1 << bpp) : 0;
        var palette = new byte[paletteCount * 3];
        for (int i = 0; i < paletteCount && paletteStart + i * entrySize + 2 < d.Length; i++)
        {
            int p = paletteStart + i * entrySize;
            palette[i * 3] = d[p + 2];
            palette[i * 3 + 1] = d[p + 1];
            palette[i * 3 + 2] = d[p];
        }

        // bit field masks
        uint rMask = 0, gMask = 0, bMask = 0, aMask = 0;
        if (compression is 3 or 6)
        {
            rMask = U32(d, 54);
            gMask = U32(d, 58);
            bMask = U32(d, 62);
            if (headerSize >= 56 || compression == 6) aMask = U32(d, 66);
        }
        else if (bpp == 16)
        {
            (rMask, gMask, bMask) = (0x7C00u, 0x03E0u, 0x001Fu);
        }
        else if (bpp == 32 && headerSize >= 108)
        {
            aMask = U32(d, 66);
        }

        var rgb = new byte[width * height * 3];
        byte[]? alpha = null;
        if (compression is 1 or 2)
        {
            var indices = DecodeRle(d, dataOffset, width, height, compression == 2);
            for (int i = 0; i < indices.Length; i++)
            {
                int y = i / width, x = i % width, row = topDown ? y : height - 1 - y;
                int idx = Math.Min(indices[i], Math.Max(0, paletteCount - 1)) * 3;
                if (paletteCount == 0) continue;
                Array.Copy(palette, idx, rgb, (row * width + x) * 3, 3);
            }
            return new DecodedBitmap(width, height, rgb, null);
        }
        if (compression is not (0 or 3 or 6)) throw new PdfException($"Unsupported BMP compression {compression}.");

        int stride = (width * bpp + 31) / 32 * 4;
        if (bpp == 32 && aMask != 0) alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int src = dataOffset + (topDown ? y : height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                int dst = (y * width + x) * 3;
                switch (bpp)
                {
                    case 1:
                    case 4:
                    case 8:
                    {
                        int bitPos = x * bpp;
                        int value = (d[src + bitPos / 8] >> (8 - bpp - bitPos % 8)) & ((1 << bpp) - 1);
                        if (value < paletteCount) Array.Copy(palette, value * 3, rgb, dst, 3);
                        break;
                    }
                    case 16:
                    {
                        uint v = (uint)U16(d, src + x * 2);
                        rgb[dst] = Channel(v, rMask);
                        rgb[dst + 1] = Channel(v, gMask);
                        rgb[dst + 2] = Channel(v, bMask);
                        break;
                    }
                    case 24:
                        rgb[dst] = d[src + x * 3 + 2];
                        rgb[dst + 1] = d[src + x * 3 + 1];
                        rgb[dst + 2] = d[src + x * 3];
                        break;
                    case 32:
                    {
                        uint v = U32(d, src + x * 4);
                        if (rMask == 0)
                        {
                            rgb[dst] = (byte)(v >> 16);
                            rgb[dst + 1] = (byte)(v >> 8);
                            rgb[dst + 2] = (byte)v;
                        }
                        else
                        {
                            rgb[dst] = Channel(v, rMask);
                            rgb[dst + 1] = Channel(v, gMask);
                            rgb[dst + 2] = Channel(v, bMask);
                        }
                        if (alpha != null) alpha[y * width + x] = Channel(v, aMask);
                        break;
                    }
                    default:
                        throw new PdfException($"Unsupported BMP bit depth {bpp}.");
                }
            }
        }
        // a fully transparent alpha channel usually means "unused"
        if (alpha != null && alpha.All(a => a == 0)) alpha = null;
        return new DecodedBitmap(width, height, rgb, alpha);
    }

    private static byte Channel(uint value, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        uint v = (value & mask) >> shift;
        return (byte)(v * 255 / max);
    }

    private static byte[] DecodeRle(byte[] d, int pos, int width, int height, bool rle4)
    {
        var indices = new byte[width * height];
        int x = 0, y = 0;
        void Put(int value)
        {
            if (x < width && y < height) indices[y * width + x] = (byte)value;
            x++;
        }
        while (pos + 1 < d.Length && y < height)
        {
            int count = d[pos++], value = d[pos++];
            if (count > 0)
            {
                for (int i = 0; i < count; i++) Put(rle4 ? (i % 2 == 0 ? value >> 4 : value & 0x0F) : value);
                continue;
            }
            switch (value)
            {
                case 0: x = 0; y++; break;           // end of line
                case 1: return indices;              // end of bitmap
                case 2: x += d[pos++]; y += d[pos++]; break; // delta
                default:                             // absolute run
                    int bytes = rle4 ? (value + 1) / 2 : value;
                    for (int i = 0; i < value; i++)
                    {
                        int b = d[pos + (rle4 ? i / 2 : i)];
                        Put(rle4 ? (i % 2 == 0 ? b >> 4 : b & 0x0F) : b);
                    }
                    pos += bytes + (bytes % 2); // word aligned
                    break;
            }
        }
        return indices;
    }
}

/// <summary>GIF decoder (first frame, global/local palette, transparency, interlacing).</summary>
internal static class GifDecoder
{
    public static DecodedBitmap Decode(byte[] d)
    {
        try
        {
            return DecodeCore(d);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new PdfException("Invalid or truncated GIF image.", ex);
        }
    }

    private static int U16(byte[] d, int p) => d[p] | (d[p + 1] << 8);

    private static DecodedBitmap DecodeCore(byte[] d)
    {
        int width = U16(d, 6), height = U16(d, 8), flags = d[10];
        PdfLimits.Current.CheckImage(width, height);
        int pos = 13;
        byte[]? globalPalette = null;
        if ((flags & 0x80) != 0)
        {
            int size = 3 << ((flags & 7) + 1);
            globalPalette = d.AsSpan(pos, size).ToArray();
            pos += size;
        }
        int transparent = -1;
        while (pos < d.Length)
        {
            int block = d[pos++];
            if (block == 0x3B) break; // trailer
            if (block == 0x21)
            {
                int label = d[pos++];
                if (label == 0xF9 && d[pos] >= 4 && (d[pos + 1] & 1) != 0) transparent = d[pos + 4];
                pos = SkipSubBlocks(d, pos);
                continue;
            }
            if (block != 0x2C) throw new PdfException("Invalid GIF block.");
            int left = U16(d, pos), top = U16(d, pos + 2), w = U16(d, pos + 4), h = U16(d, pos + 6), imageFlags = d[pos + 8];
            pos += 9;
            byte[]? palette = globalPalette;
            if ((imageFlags & 0x80) != 0)
            {
                int size = 3 << ((imageFlags & 7) + 1);
                palette = d.AsSpan(pos, size).ToArray();
                pos += size;
            }
            if (palette == null) throw new PdfException("GIF without color table.");
            int minCodeSize = d[pos++];
            var data = new MemoryStream();
            while (pos < d.Length && d[pos] != 0)
            {
                int length = d[pos++];
                data.Write(d, pos, Math.Min(length, d.Length - pos));
                pos += length;
            }
            var indices = Lzw(data.ToArray(), minCodeSize, w * h);
            if ((imageFlags & 0x40) != 0) indices = Deinterlace(indices, w, h);

            var rgb = new byte[width * height * 3];
            var alpha = new byte[width * height];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int px = left + x, py = top + y;
                    if (px >= width || py >= height) continue;
                    int index = indices[y * w + x];
                    if (index == transparent) continue;
                    int target = py * width + px;
                    if (index * 3 + 2 < palette.Length) Array.Copy(palette, index * 3, rgb, target * 3, 3);
                    alpha[target] = 255;
                }
            }
            return new DecodedBitmap(width, height, rgb, alpha.All(a => a == 255) ? null : alpha);
        }
        throw new PdfException("GIF contains no image.");
    }

    private static int SkipSubBlocks(byte[] d, int pos)
    {
        while (pos < d.Length && d[pos] != 0) pos += d[pos] + 1;
        return pos + 1;
    }

    private static byte[] Lzw(byte[] data, int minCodeSize, int pixelCount)
    {
        if (minCodeSize is < 2 or > 8) throw new PdfException("Invalid GIF LZW code size.");
        var output = new byte[pixelCount];
        int outPos = 0;
        int clear = 1 << minCodeSize, end = clear + 1;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        for (int i = 0; i < clear; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
        }
        int codeSize = minCodeSize + 1, next = clear + 2, previous = -1;
        int bitBuffer = 0, bitCount = 0;
        var stack = new byte[4097];

        // writes the string of a code; returns its first byte
        byte Emit(int code)
        {
            int sp = 0;
            for (int c = code; c >= 0 && sp < stack.Length; c = c >= clear ? prefix[c] : -1) stack[sp++] = suffix[c];
            for (int i = sp - 1; i >= 0 && outPos < pixelCount; i--) output[outPos++] = stack[i];
            return stack[sp - 1];
        }

        foreach (byte b in data)
        {
            bitBuffer |= b << bitCount;
            bitCount += 8;
            while (bitCount >= codeSize)
            {
                int code = bitBuffer & ((1 << codeSize) - 1);
                bitBuffer >>= codeSize;
                bitCount -= codeSize;
                if (code == clear)
                {
                    codeSize = minCodeSize + 1;
                    next = clear + 2;
                    previous = -1;
                    continue;
                }
                if (code == end || outPos >= pixelCount) return output;
                byte first;
                if (code < next && code != end && (code < clear || code > end))
                {
                    first = Emit(code);
                }
                else if (code == next && previous >= 0)
                {
                    first = Emit(previous);
                    if (outPos < pixelCount) output[outPos++] = first; // KwKwK case: string(previous) + first char
                }
                else return output; // corrupt data
                if (previous >= 0 && next < 4096)
                {
                    prefix[next] = previous;
                    suffix[next] = first;
                    next++;
                    if (next == 1 << codeSize && codeSize < 12) codeSize++;
                }
                previous = code;
            }
        }
        return output;
    }

    private static byte[] Deinterlace(byte[] indices, int width, int height)
    {
        var result = new byte[indices.Length];
        int row = 0;
        foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
            for (int y = start; y < height; y += step, row++)
                Array.Copy(indices, row * width, result, y * width, width);
        return result;
    }
}
