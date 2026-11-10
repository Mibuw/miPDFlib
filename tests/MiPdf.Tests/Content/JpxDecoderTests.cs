using MiPdf.Filters;
using MiPdf.Images;
using MiPdf.Objects;

namespace MiPdf.Tests.Content;

/// <summary>
/// JPEG 2000 decoding (Resources/jpx-fixtures.txt). Every fixture encodes <see cref="Pattern"/> with other coding
/// parameters (OpenJPEG); lossless ones must reproduce it exactly. In addition the decoder was compared with the
/// reference images of the ISO/IEC 15444-4 conformance suite (all files exact or within the tolerances of the standard)
/// and sample by sample with OpenJPEG.
/// </summary>
public class JpxDecoderTests
{
    private const int Width = 61, Height = 47;

    private static readonly Dictionary<string, byte[]> Fixtures =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Resources", "jpx-fixtures.txt"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t'))
            .ToDictionary(p => p[0], p => Convert.FromBase64String(p[1]));

    public static IEnumerable<object[]> Names() => Fixtures.Keys.Select(n => new object[] { n });

    /// <summary>The test image: a texture with a checkerboard in the second channel.</summary>
    private static int Pattern(int x, int y, int c) =>
        (x * 4 + y * 3 + c * 85 + ((x * y) >> 4) + (c == 1 ? 37 : 0) * ((x / 8 + y / 8) % 2)) % 256;

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodesTheTestImage(string name)
    {
        var image = JpxDecoder.Decode(Fixtures[name]);
        Assert.Equal((Width, Height), (image.Width, image.Height));
        int channels = name.StartsWith("rgba", StringComparison.Ordinal) ? 4 : name.StartsWith("rgb", StringComparison.Ordinal) ? 3 : 1;
        Assert.Equal(channels, image.Channels.Length);
        bool wide = name.StartsWith("gray16", StringComparison.Ordinal);
        int tolerance = name.Contains("irreversible", StringComparison.Ordinal) ? 2 : 0;
        for (int c = 0; c < channels; c++)
        {
            Assert.Equal(wide ? 16 : 8, image.Precision[c]);
            int worst = 0;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int expected = Pattern(x, y, c) * (wide ? 257 : 1);
                    worst = Math.Max(worst, Math.Abs(image.Channels[c][y * Width + x] - expected));
                }
            Assert.True(worst <= tolerance, $"channel {c}: error {worst}");
        }
    }

    [Fact]
    public void ReadsTheJp2Header()
    {
        Assert.Equal(17, JpxDecoder.Decode(Fixtures["gray-lossless"]).ColorSpace);
        Assert.Equal(0, JpxDecoder.Decode(Fixtures["gray-raw-codestream"]).ColorSpace);
        var rgba = JpxDecoder.Decode(Fixtures["rgba-lossless"]);
        Assert.Equal(16, rgba.ColorSpace);
        Assert.Equal(3, rgba.AlphaChannel);
        Assert.Equal(3, rgba.ColorChannels);
    }

    private static PdfStream Image(byte[] data, string? colorSpace, int smaskInData = 0)
    {
        var image = new PdfStream(data, compress: false);
        image[PdfName.Type] = PdfName.XObject;
        image[PdfName.Subtype] = PdfName.Get("Image");
        image.Set(PdfName.Get("Width"), Width);
        image.Set(PdfName.Get("Height"), Height);
        if (colorSpace != null) image[PdfName.ColorSpace] = PdfName.Get(colorSpace);
        if (smaskInData != 0) image.Set(PdfName.Get("SMaskInData"), smaskInData);
        image[PdfName.Filter] = PdfName.JPXDecode;
        return image;
    }

    [Fact]
    public void JpxImagesInPdfAreDecoded()
    {
        // color space from the image dictionary
        var (w, h, rgb, alpha) = ImageDecoder.Decode(Image(Fixtures["rgb-lossless"], "DeviceRGB"))!.Value;
        Assert.Equal((Width, Height), (w, h));
        Assert.Null(alpha);
        for (int i = 0; i < Width * Height; i++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(Pattern(i % Width, i / Width, c), rgb[i * 3 + c]);

        // no /ColorSpace: from the image (raw codestream with one component: gray)
        var gray = ImageDecoder.Decode(Image(Fixtures["gray-raw-codestream"], null))!.Value.Rgb;
        Assert.Equal(Pattern(10, 20, 0), gray[(20 * Width + 10) * 3 + 1]);

        // /SMaskInData: the opacity channel of the image
        var withAlpha = ImageDecoder.Decode(Image(Fixtures["rgba-lossless"], null, smaskInData: 1))!.Value;
        Assert.NotNull(withAlpha.Alpha);
        Assert.Equal(Pattern(30, 5, 3), withAlpha.Alpha![5 * Width + 30]);
        Assert.Equal(Pattern(30, 5, 2), withAlpha.Rgb[(5 * Width + 30) * 3 + 2]);
        // without it, the opacity channel is ignored
        Assert.Null(ImageDecoder.Decode(Image(Fixtures["rgba-lossless"], "DeviceRGB"))!.Value.Alpha);
    }

    [Fact]
    public void JpxImagesAreRenderedAndExported()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, Width, Height));
        var resources = new PdfDictionary
        {
            [PdfName.XObject] = new PdfDictionary { [PdfName.Get("Im0")] = doc.AddObject(Image(Fixtures["rgb-pcrl-precincts"], "DeviceRGB")) },
        };
        page.Dictionary[PdfName.Resources] = resources;
        using (var canvas = page.GetCanvas()) canvas.WriteRaw($"q {Width} 0 0 {Height} 0 0 cm /Im0 Do Q");

        using var reopened = PdfDocument.Open(doc.ToArray());
        var extracted = Assert.Single(reopened.GetPage(1).ExtractImages());
        Assert.True(extracted.IsJpeg2000);
        Assert.NotNull(extracted.ToPng());
        Assert.Empty(reopened.Diagnostics);
    }

    [Fact]
    public void JpxImagesArePartlyRedacted()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, Width, Height));
        // no /ColorSpace: the redacted copy gets the one of the JPEG 2000 data
        page.Resources[PdfName.XObject] = new PdfDictionary { [PdfName.Get("Im1")] = doc.AddObject(Image(Fixtures["rgb-lossless"], null)) };
        using (var canvas = page.GetCanvas()) canvas.WriteRaw($"q {Width} 0 0 {Height} 0 0 cm /Im1 Do Q");
        var result = page.Redact(new[] { new PdfRectangle(0, 0, 10, 10) });
        Assert.Equal((1, 0), (result.ImagesModified, result.ImagesRemoved));

        var stream = PdfImageExtractor.Extract(PdfDocument.Open(doc.ToArray()).GetPage(1)).Single().Stream;
        Assert.False(stream.Get(PdfName.Filter) is PdfName { Value: "JPXDecode" });
        var redacted = ImageDecoder.Decode(stream)!.Value;
        Assert.Equal(new byte[] { 0, 0, 0 }, redacted.Rgb.AsSpan(((Height - 2) * Width + 2) * 3, 3).ToArray()); // inside: black
        for (int c = 0; c < 3; c++) Assert.Equal(Pattern(50, 3, c), redacted.Rgb[(3 * Width + 50) * 3 + c]); // outside: unchanged
    }

    [Fact]
    public void DamagedDataRaisesPdfException()
    {
        Assert.ThrowsAny<PdfException>(() => JpxDecoder.Decode(new byte[] { 0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20 }));
        Assert.ThrowsAny<PdfException>(() => JpxDecoder.Decode(new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0, 2 }));
        foreach (var name in new[] { "rgb-lossless", "gray-tiles-offset", "rgb-rpcl-precincts" })
        {
            var broken = (byte[])Fixtures[name].Clone();
            for (int i = 90; i < broken.Length; i += 11) broken[i] ^= 0x5A;
            try
            {
                JpxDecoder.Decode(broken); // garbage samples are fine, crashes are not
            }
            catch (PdfException)
            {
            }
            try
            {
                JpxDecoder.Decode(broken.AsSpan(0, broken.Length / 2).ToArray()); // truncated
            }
            catch (PdfException)
            {
            }
        }
        // in a PDF: not shown, with a diagnostic
        Assert.Null(ImageDecoder.Decode(Image(new byte[] { 1, 2, 3 }, "DeviceRGB")));
    }
}
