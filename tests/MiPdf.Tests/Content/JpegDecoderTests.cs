using MiPdf.Filters;
using MiPdf.Images;
using MiPdf.Objects;

namespace MiPdf.Tests.Content;

/// <summary>JPEG decoding compared with the pixels libjpeg-turbo decodes (Resources/jpeg-fixtures.txt, made with Pillow).</summary>
public class JpegDecoderTests
{
    private static readonly Dictionary<string, string[]> Fixtures =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Resources", "jpeg-fixtures.txt"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).ToDictionary(p => p[0]);

    public static TheoryData<string> Names()
    {
        var names = new TheoryData<string>();
        foreach (var name in Fixtures.Keys) names.Add(name);
        return names;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodesLikeLibjpeg(string name)
    {
        var fixture = Fixtures[name];
        int width = int.Parse(fixture[1]), height = int.Parse(fixture[2]), components = int.Parse(fixture[3]);
        var jpeg = Convert.FromBase64String(fixture[4]);
        var expected = Convert.FromBase64String(fixture[5]);
        var decoded = JpegDecoder.Decode(jpeg, null, out int w, out int h, out int c);
        Assert.Equal((width, height, components), (w, h, c));
        // Adobe CMYK JPEGs store inverted values; Pillow inverts them, the PDF filter does not (/Decode handles it)
        if (name.StartsWith("cmyk", StringComparison.Ordinal)) decoded = decoded.Select(b => (byte)(255 - b)).ToArray();
        Assert.Equal(expected.Length, decoded.Length);
        int differences = 0, maxDifference = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int d = Math.Abs(expected[i] - decoded[i]);
            if (d > 0) differences++;
            maxDifference = Math.Max(maxDifference, d);
        }
        Assert.True(differences == 0, $"{name}: {differences} of {expected.Length} samples differ, at most by {maxDifference}");
    }

    private static PdfStream JpegImage(string name, out byte[] expected)
    {
        var fixture = Fixtures[name];
        expected = Convert.FromBase64String(fixture[5]);
        var image = new PdfStream(Convert.FromBase64String(fixture[4]), compress: false);
        image[PdfName.Type] = PdfName.XObject;
        image[PdfName.Subtype] = PdfName.Get("Image");
        image.Set(PdfName.Get("Width"), int.Parse(fixture[1]));
        image.Set(PdfName.Get("Height"), int.Parse(fixture[2]));
        image[PdfName.ColorSpace] = PdfName.Get(fixture[3] == "1" ? "DeviceGray" : fixture[3] == "3" ? "DeviceRGB" : "DeviceCMYK");
        image.Set(PdfName.BitsPerComponent, 8);
        image[PdfName.Filter] = PdfName.Get("DCTDecode");
        return image;
    }

    [Fact]
    public void JpegImagesAreDecodedToPixels()
    {
        var image = JpegImage("rgb-420", out var expected);
        var decoded = ImageDecoder.Decode(image)!.Value;
        Assert.Equal(expected, decoded.Rgb);
    }

    [Fact]
    public void DctEncodedSoftMasksAreDecoded()
    {
        // soft masks are often JPEG-compressed like the image itself
        var image = JpegImage("rgb-420", out _);
        image[PdfName.SMask] = JpegImage("gray-baseline", out var expectedAlpha);
        var decoded = ImageDecoder.Decode(image)!.Value;
        Assert.Equal(expectedAlpha, decoded.Alpha);
    }

    [Fact]
    public void JpegImagesArePartlyRedacted()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 45, 31));
        page.Resources[PdfName.XObject] = new PdfDictionary { [PdfName.Get("Im1")] = doc.AddObject(JpegImage("rgb-444", out var expected)) };
        using (var canvas = page.GetCanvas()) canvas.WriteRaw("q 45 0 0 31 0 0 cm /Im1 Do Q");
        var result = page.Redact(new[] { new PdfRectangle(0, 0, 10, 10) });
        Assert.Equal((1, 0), (result.ImagesModified, result.ImagesRemoved));
        var redacted = ImageDecoder.Decode(PdfImageExtractor.Extract(PdfDocument.Open(doc.ToArray()).GetPage(1)).Single().Stream)!.Value;
        Assert.Equal(new byte[] { 0, 0, 0 }, redacted.Rgb.AsSpan(((31 - 2) * 45 + 2) * 3, 3).ToArray()); // inside: black
        int topRight = (2 * 45 + 40) * 3;
        Assert.Equal(expected.AsSpan(topRight, 3).ToArray(), redacted.Rgb.AsSpan(topRight, 3).ToArray()); // outside: unchanged
    }

    [Fact]
    public void ColorTransformCanBeSwitchedOff()
    {
        var jpeg = Convert.FromBase64String(Fixtures["rgb-444"][4]);
        var parms = new PdfDictionary { [PdfName.Get("ColorTransform")] = new PdfNumber(0) };
        var raw = JpegDecoder.Decode(jpeg, parms, out _, out _, out _);
        var rgb = JpegDecoder.Decode(jpeg, null, out _, out _, out _);
        Assert.NotEqual(rgb, raw); // the YCbCr samples themselves
    }

    [Fact]
    public void DamagedDataFailsCleanly()
    {
        var jpeg = Convert.FromBase64String(Fixtures["rgb-420-progressive"][4]);
        var random = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            var damaged = jpeg.AsSpan(0, random.Next(jpeg.Length)).ToArray();
            for (int j = 0; j < 4 && damaged.Length > 0; j++) damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            try
            {
                JpegDecoder.Decode(damaged, null, out _, out _, out _);
            }
            catch (PdfException)
            {
                // expected for broken headers
            }
        }
    }
}
