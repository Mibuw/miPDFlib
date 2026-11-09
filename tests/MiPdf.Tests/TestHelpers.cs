using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MiPdf.Tests;

internal static class TestHelpers
{
    /// <summary>A TrueType font available on the current platform (Windows or typical Linux CI images).</summary>
    public static string? FindSystemFont()
    {
        // MIPDF_TEST_FONT overrides the system font (e.g. to run the tests with the font of the Linux CI image)
        if (Environment.GetEnvironmentVariable("MIPDF_TEST_FONT") is { Length: > 0 } overridden && File.Exists(overridden)) return overridden;
        string[] candidates =
        {
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// A system font with glyphs for the Indic scripts (Nirmala UI on Windows, Noto Sans Devanagari on Linux), or null;
    /// MIPDF_INDIC_TEST_FONT overrides it ("path" or "path#collection index").
    /// </summary>
    public static (string Path, int Index)? FindIndicFont()
    {
        if (Environment.GetEnvironmentVariable("MIPDF_INDIC_TEST_FONT") is { Length: > 0 } overridden)
        {
            int hash = overridden.LastIndexOf('#');
            var file = hash > 0 ? overridden[..hash] : overridden;
            if (File.Exists(file)) return (file, hash > 0 ? int.Parse(overridden[(hash + 1)..]) : 0);
        }
        string[] candidates =
        {
            @"C:\Windows\Fonts\Nirmala.ttc",
            "/usr/share/fonts/truetype/noto/NotoSansDevanagari-Regular.ttf",
            "/usr/share/fonts/opentype/noto/NotoSansDevanagari-Regular.ttf",
        };
        return candidates.FirstOrDefault(File.Exists) is { } path ? (path, 0) : null;
    }

    /// <summary>Encodes a minimal non-interlaced PNG (color type 6 = RGBA or 2 = RGB, 8 bit).</summary>
    public static byte[] CreatePng(int width, int height, byte[] pixels, bool alpha)
    {
        int channels = alpha ? 4 : 3;
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            raw.Write(pixels, y * width * channels, width * channels);
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) raw.WriteTo(zs);

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(alpha ? 6 : 2);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", z.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. typeBytes, .. data]));
        s.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>Minimal JPEG header (SOI + SOF0) sufficient for dimension parsing.</summary>
    public static byte[] CreateJpegHeader(int width, int height, int components) => new byte[]
    {
        0xFF, 0xD8,
        0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components,
        1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1,
        0xFF, 0xD9,
    };
}
