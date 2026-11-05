using System.Text;
using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.Objects;
using MiPdf.Rendering;

namespace MiPdf.Tests.Rendering;

/// <summary>Rendering stage 4: Separation and DeviceN with tint transforms, CMYK, ICC-based and Lab colors, also in images.</summary>
public class ColorTests
{
    private static PdfArray A(params double[] values) => new(values.Select(v => (PdfObject)new PdfNumber(v)));

    private static PdfName N(string name) => PdfName.Get(name);

    private static PdfDictionary Exponential(double[] c0, double[] c1) => new()
    {
        [N("FunctionType")] = new PdfNumber(2), [N("Domain")] = A(0, 1), [N("C0")] = A(c0), [N("C1")] = A(c1), [N("N")] = new PdfNumber(1),
    };

    /// <summary>[/Separation /Orange /DeviceCMYK {0 t*0.6 t 0}]</summary>
    private static PdfArray OrangeSeparation() =>
        new(N("Separation"), N("Orange"), N("DeviceCMYK"), Exponential(new double[] { 0, 0, 0, 0 }, new double[] { 0, 0.6, 1, 0 }));

    private static (byte R, byte G, byte B) Pixel(RenderedPage page, int x, int y)
    {
        int i = (y * page.Width + x) * 4;
        return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
    }

    private static (byte, byte, byte) Bytes(Rgb c) => (c.R, c.G, c.B);

    private static RenderedPage Render(string content, PdfDictionary colorSpaces)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 100, 50));
        page.Resources[PdfName.ColorSpace] = colorSpaces;
        using (var canvas = page.GetCanvas()) canvas.WriteRaw(content);
        using var reopened = PdfDocument.Open(doc.ToArray());
        return PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 });
    }

    [Fact]
    public void CmykApproximatesAPressProfile()
    {
        // pure cyan is not (0, 255, 255) on paper, black ink is not pure black
        var cyan = ColorSpace.CmykToRgb(1, 0, 0, 0);
        Assert.InRange(cyan.R, 0, 10);
        Assert.InRange(cyan.G, 170, 195);
        Assert.InRange(cyan.B, 230, 250);
        var black = ColorSpace.CmykToRgb(0, 0, 0, 1);
        Assert.InRange(black.R, 25, 60);
        Assert.Equal(new Rgb(255, 255, 255), ColorSpace.CmykToRgb(0, 0, 0, 0));
        // device CMYK, ICC-based CMYK and the image decoder agree
        var page = Render("1 0 0 0 k 0 0 50 50 re f /Icc cs 1 0 0 0 sc 50 0 50 50 re f",
            new PdfDictionary { [N("Icc")] = new PdfArray(N("ICCBased"), new PdfStream(new byte[] { 0 }) { [PdfName.N] = new PdfNumber(4) }) });
        Assert.Equal(Bytes(cyan), Pixel(page, 25, 25));
        Assert.Equal(Bytes(cyan), Pixel(page, 75, 25));
        Assert.Equal((cyan.R, cyan.G, cyan.B), ((byte, byte, byte))ToBytes(ImageDecoder.ToRgb(PdfColor.Cmyk(1, 0, 0, 0))));
    }

    private static (byte, byte, byte) ToBytes((int R, int G, int B) c) => ((byte)c.R, (byte)c.G, (byte)c.B);

    [Fact]
    public void SeparationAndDeviceNUseTheirTintTransforms()
    {
        var page = Render("/Sep cs 1 sc 0 0 30 50 re f /Sep cs 0.5 sc 30 0 30 50 re f /Two cs 1 0.5 sc 60 0 40 50 re f",
            new PdfDictionary
            {
                [N("Sep")] = OrangeSeparation(),
                // two colorants -> RGB (t1, t2, 0): the program leaves t1 t2 0 on the stack
                [N("Two")] = new PdfArray(N("DeviceN"), new PdfArray(N("Red"), N("Green")), N("DeviceRGB"), Calculator("{ 0 }")),
            });
        Assert.Equal(Bytes(ColorSpace.CmykToRgb(0, 0.6, 1, 0)), Pixel(page, 15, 25));   // full tint
        Assert.Equal(Bytes(ColorSpace.CmykToRgb(0, 0.3, 0.5, 0)), Pixel(page, 45, 25));  // half tint
        Assert.Equal(((byte)255, (byte)128, (byte)0), Pixel(page, 80, 25));
    }

    /// <summary>A PostScript calculator function with two inputs and three outputs.</summary>
    private static PdfStream Calculator(string program) => new(Encoding.ASCII.GetBytes(program), compress: false)
    {
        [N("FunctionType")] = new PdfNumber(4), [N("Domain")] = A(0, 1, 0, 1), [N("Range")] = A(0, 1, 0, 1, 0, 1),
    };

    [Fact]
    public void TheColorantNoneLeavesNoMarks()
    {
        var none = new PdfArray(N("Separation"), N("None"), N("DeviceGray"), Exponential(new double[] { 1 }, new double[] { 0 }));
        var page = Render("/No cs 1 sc 0 0 100 50 re f", new PdfDictionary { [N("No")] = none });
        Assert.DoesNotContain(page.Rgba, b => b != 255);
    }

    [Fact]
    public void ImagesInSpotColorsAndLab()
    {
        // a 3x1 Separation image (8 bit): tints 0, 0.5, 1
        var separation = new PdfStream(new byte[] { 0, 128, 255 }, compress: false)
        {
            [N("Width")] = new PdfNumber(3), [N("Height")] = new PdfNumber(1), [PdfName.BitsPerComponent] = new PdfNumber(8),
            [PdfName.ColorSpace] = OrangeSeparation(),
        };
        var rgb = ImageDecoder.Decode(separation)!.Value.Rgb;
        Assert.Equal(new byte[] { 255, 255, 255 }, rgb[..3]);
        var full = ColorSpace.CmykToRgb(0, 0.6, 1, 0);
        Assert.Equal(new[] { full.R, full.G, full.B }, rgb[6..9]);

        // Indexed with a Separation base: index 1 -> tint 255
        var indexed = new PdfStream(new byte[] { 0x40 }, compress: false) // 2 bits per index: 01 00 00 00
        {
            [N("Width")] = new PdfNumber(1), [N("Height")] = new PdfNumber(1), [PdfName.BitsPerComponent] = new PdfNumber(2),
            [PdfName.ColorSpace] = new PdfArray(N("Indexed"), OrangeSeparation(), new PdfNumber(1), new PdfString(new byte[] { 0, 255 })),
        };
        Assert.Equal(new[] { full.R, full.G, full.B }, ImageDecoder.Decode(indexed)!.Value.Rgb);

        // Lab: L 100, a 0, b 0 is white (default decode: L 0..100, a/b over the range)
        var lab = new PdfStream(new byte[] { 255, 128, 128 }, compress: false)
        {
            [N("Width")] = new PdfNumber(1), [N("Height")] = new PdfNumber(1), [PdfName.BitsPerComponent] = new PdfNumber(8),
            [PdfName.ColorSpace] = new PdfArray(N("Lab"), new PdfDictionary { [N("WhitePoint")] = A(0.9642, 1, 0.8249), [N("Range")] = A(-128, 127, -128, 127) }),
        };
        var white = ImageDecoder.Decode(lab)!.Value.Rgb;
        Assert.All(white, v => Assert.InRange(v, 250, 255));

        // a Decode array [1 0] inverts gray samples
        var inverted = new PdfStream(new byte[] { 0 }, compress: false)
        {
            [N("Width")] = new PdfNumber(1), [N("Height")] = new PdfNumber(1), [PdfName.BitsPerComponent] = new PdfNumber(8),
            [PdfName.ColorSpace] = N("DeviceGray"), [PdfName.Decode] = A(1, 0),
        };
        Assert.Equal(new byte[] { 255, 255, 255 }, ImageDecoder.Decode(inverted)!.Value.Rgb);
    }
}
