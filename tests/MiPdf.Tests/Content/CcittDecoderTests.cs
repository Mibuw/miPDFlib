using MiPdf.Filters;
using MiPdf.Images;
using MiPdf.Objects;

namespace MiPdf.Tests.Content;

/// <summary>CCITT fax decoding compared with reference pixels produced by Ghostscript (Resources/ccitt-fixtures.txt).</summary>
public class CcittDecoderTests
{
    private static readonly Dictionary<string, string[]> Fixtures =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Resources", "ccitt-fixtures.txt"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).ToDictionary(p => p[0]);

    private static readonly int Width = int.Parse(Fixtures["reference"][1]);
    private static readonly int Height = int.Parse(Fixtures["reference"][2]);
    private static readonly byte[] Reference = Convert.FromBase64String(Fixtures["reference"][3]); // 1 = black

    private static PdfDictionary Parms(int k, bool rows = true, bool blackIs1 = false)
    {
        var parms = new PdfDictionary
        {
            [PdfName.Get("K")] = new PdfNumber(k),
            [PdfName.Get("Columns")] = new PdfNumber(Width),
        };
        if (rows) parms.Set(PdfName.Get("Rows"), Height);
        if (blackIs1) parms.Set(PdfName.Get("BlackIs1"), true);
        return parms;
    }

    [Theory]
    [InlineData("g3-1d", true)]
    [InlineData("g3-2d", true)]
    [InlineData("g4", true)]
    [InlineData("pdfwrite", false)] // as Ghostscript writes it: without /Rows, ended by EOFB
    public void DecodesLikeTheReference(string name, bool rows)
    {
        var data = Convert.FromBase64String(Fixtures[name][2]);
        var decoded = CcittFaxDecoder.Decode(data, Parms(int.Parse(Fixtures[name][1]), rows));
        Assert.Equal(Reference.Length, decoded.Length);
        // default polarity: black pixels are 0 bits
        Assert.Equal(Reference, decoded.Select(b => (byte)~b).ToArray());
    }

    [Fact]
    public void BlackIs1InvertsTheBits()
    {
        var decoded = CcittFaxDecoder.Decode(Convert.FromBase64String(Fixtures["g4"][2]), Parms(-1, blackIs1: true));
        Assert.Equal(Reference, decoded);
    }

    private static PdfDocument CreatePage(out PdfPage page)
    {
        var doc = PdfDocument.Create();
        var image = new PdfStream(Convert.FromBase64String(Fixtures["g4"][2]), compress: false);
        image[PdfName.Type] = PdfName.XObject;
        image[PdfName.Subtype] = PdfName.Get("Image");
        image.Set(PdfName.Get("Width"), Width);
        image.Set(PdfName.Get("Height"), Height);
        image[PdfName.ColorSpace] = PdfName.Get("DeviceGray");
        image.Set(PdfName.BitsPerComponent, 1);
        image[PdfName.Filter] = PdfName.Get("CCITTFaxDecode");
        image[PdfName.DecodeParms] = Parms(-1);
        page = doc.AddPage(new PdfRectangle(0, 0, Width, Height));
        page.Resources[PdfName.XObject] = new PdfDictionary { [PdfName.Get("Im1")] = doc.AddObject(image) };
        using (var canvas = page.GetCanvas()) canvas.WriteRaw($"q {Width} 0 0 {Height} 0 0 cm /Im1 Do Q");
        return doc;
    }

    [Fact]
    public void FaxImagesAreExtractedAsPng()
    {
        using var doc = CreatePage(out var page);
        var image = Assert.Single(PdfImageExtractor.Extract(page));
        var decoded = ImageDecoder.Decode(image.Stream)!.Value;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                bool black = ((Reference[y * ((Width + 7) / 8) + x / 8] >> (7 - x % 8)) & 1) == 1;
                Assert.Equal(black ? 0 : 255, decoded.Rgb[(y * Width + x) * 3]);
            }
        Assert.NotNull(image.ToPng());
        Assert.Equal(".png", image.ToFile(out var extension) != null ? extension : null);
    }

    [Fact]
    public void FaxImagesArePartlyRedacted()
    {
        using var doc = CreatePage(out var page);
        var result = page.Redact(new[] { new PdfRectangle(0, 0, 200, 100) });
        Assert.Equal(1, result.ImagesModified);
        Assert.Equal(0, result.ImagesRemoved);
        var reopened = PdfDocument.Open(doc.ToArray());
        var decoded = ImageDecoder.Decode(PdfImageExtractor.Extract(reopened.GetPage(1)).Single().Stream)!.Value;
        Assert.Equal(0, decoded.Rgb[((Height - 10) * Width + 10) * 3]);       // inside the area (bottom left): black
        Assert.Equal(255, decoded.Rgb[(5 * Width + 590) * 3]);               // outside (top right): unchanged white
    }

    [Fact]
    public void DamagedDataDoesNotFail()
    {
        var data = Convert.FromBase64String(Fixtures["g3-2d"][2]);
        var random = new Random(42);
        for (int i = 0; i < 200; i++)
        {
            var damaged = data.AsSpan(0, random.Next(data.Length)).ToArray();
            for (int j = 0; j < 5 && damaged.Length > 0; j++) damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
            foreach (int k in new[] { -1, 0, 1 })
            {
                var decoded = CcittFaxDecoder.Decode(damaged, Parms(k));
                Assert.Equal(Reference.Length, decoded.Length);
            }
        }
        Assert.Throws<PdfException>(() => CcittFaxDecoder.Decode(data, new PdfDictionary { [PdfName.Get("Columns")] = new PdfNumber(0) }));
    }
}
