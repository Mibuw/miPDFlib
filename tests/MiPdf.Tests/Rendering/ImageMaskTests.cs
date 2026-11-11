using System.Buffers.Binary;
using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.Objects;
using MiPdf.Rendering;

namespace MiPdf.Tests.Rendering;

/// <summary>Image masks: color key masking (/Mask arrays) and masks with a different resolution than their image.</summary>
public class ImageMaskTests
{
    private static readonly (byte, byte, byte) Red = (255, 0, 0), Green = (0, 255, 0), Blue = (0, 0, 255);

    private static PdfArray A(params double[] values) => new(values.Select(v => (PdfObject)new PdfNumber(v)));

    private static PdfName N(string name) => PdfName.Get(name);

    private static PdfStream Image(int width, int height, int bpc, PdfObject colorSpace, byte[] data)
    {
        var s = new PdfStream(data, compress: true);
        s[N("Type")] = N("XObject");
        s[N("Subtype")] = N("Image");
        s[N("Width")] = new PdfNumber(width);
        s[N("Height")] = new PdfNumber(height);
        s[N("BitsPerComponent")] = new PdfNumber(bpc);
        s[N("ColorSpace")] = colorSpace;
        return s;
    }

    private static PdfStream Stencil(int width, int height, byte[] data)
    {
        var s = Image(width, height, 1, PdfNull.Instance, data);
        s.Remove(N("ColorSpace"));
        s[N("ImageMask")] = PdfBoolean.True;
        return s;
    }

    /// <summary>Paints the image over a green 100x50 page and renders it at 72 dpi.</summary>
    private static (RenderedPage Page, IReadOnlyList<PdfExtractedImage> Images) Render(Func<PdfDocument, PdfStream> image)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 100, 50));
        page.Resources[PdfName.XObject] = new PdfDictionary { [N("Im1")] = doc.AddObject(image(doc)) };
        using (var canvas = page.GetCanvas()) canvas.WriteRaw("0 1 0 rg 0 0 100 50 re f q 100 0 0 50 0 0 cm /Im1 Do Q");
        var reopened = PdfDocument.Open(doc.ToArray());
        return (PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 }), PdfImageExtractor.Extract(reopened.GetPage(1)));
    }

    private static (byte R, byte G, byte B) Pixel(RenderedPage page, int x, int y)
    {
        int i = (y * page.Width + x) * 4;
        return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
    }

    /// <summary>The colors of the four quarters of the page (left to right).</summary>
    private static (byte, byte, byte)[] Quarters(RenderedPage page) => new[] { Pixel(page, 12, 25), Pixel(page, 37, 25), Pixel(page, 62, 25), Pixel(page, 87, 25) };

    private static (int Width, int Height) PngSize(byte[] png) =>
        (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

    [Fact]
    public void ColorKeyMasksHidePixelsInTheirRanges()
    {
        // RGB, 8 bits: red, blue, dark red (in the range), blue; the red range is masked out
        var (page, images) = Render(_ =>
        {
            var image = Image(4, 1, 8, N("DeviceRGB"), new byte[] { 255, 0, 0, 0, 0, 255, 200, 0, 0, 0, 0, 255 });
            image[N("Mask")] = A(200, 255, 0, 0, 0, 0);
            return image;
        });
        Assert.Equal(new[] { Green, Blue, Green, Blue }, Quarters(page));
        Assert.True(images.Single().HasTransparency);
        var png = images.Single().ToPng()!;
        Assert.Equal(6, png[25]); // PNG color type 6: RGB with alpha
        Assert.Equal((4, 1), PngSize(png));

        // Indexed, 4 bits: the key is compared with the index, not with the color
        (page, _) = Render(_ =>
        {
            var image = Image(4, 1, 4, new PdfArray(N("Indexed"), N("DeviceRGB"), new PdfNumber(2), new PdfString(new byte[] { 255, 0, 0, 0, 0, 255, 255, 0, 0 })),
                new byte[] { 0x01, 0x21 });
            image[N("Mask")] = A(2, 2);
            return image;
        });
        Assert.Equal(new[] { Red, Blue, Green, Blue }, Quarters(page));

        // 16 bits: the full sample value counts (0x00FF is in the range, 0x01FF is not), Decode does not
        (page, _) = Render(_ =>
        {
            var image = Image(4, 1, 16, N("DeviceGray"), new byte[] { 0x00, 0xFF, 0x01, 0xFF, 0x00, 0x00, 0xFF, 0xFF });
            image[N("Mask")] = A(0, 0x00FF);
            image[N("Decode")] = A(1, 0);
            return image;
        });
        var quarters = Quarters(page);
        Assert.Equal(Green, quarters[0]);
        Assert.NotEqual(Green, quarters[1]);
        Assert.Equal(Green, quarters[2]);
        Assert.Equal((0, 0, 0), quarters[3]); // 0xFFFF decoded with [1 0]
    }

    [Fact]
    public void MasksFinerThanTheImageKeepTheirResolution()
    {
        // a red 1x1 image with a 4x1 stencil mask: sample 1 = masked out
        var (page, images) = Render(doc =>
        {
            var image = Image(1, 1, 8, N("DeviceRGB"), new byte[] { 255, 0, 0 });
            image[N("Mask")] = doc.AddObject(Stencil(4, 1, new byte[] { 0x50 }));
            return image;
        });
        Assert.Equal(new[] { Red, Green, Red, Green }, Quarters(page));
        Assert.Equal((4, 1), PngSize(images.Single().ToPng()!));

        // a 2x1 image (red, blue) with a 4x1 soft mask (opaque, transparent, transparent, opaque)
        (page, _) = Render(doc =>
        {
            var image = Image(2, 1, 8, N("DeviceRGB"), new byte[] { 255, 0, 0, 0, 0, 255 });
            var smask = Image(4, 1, 8, N("DeviceGray"), new byte[] { 255, 0, 0, 255 });
            image[N("SMask")] = doc.AddObject(smask);
            return image;
        });
        Assert.Equal(new[] { Red, Green, Green, Blue }, Quarters(page));

        // a mask coarser than the image is enlarged: 4x1 image, 2x1 stencil mask (masked, painted)
        (page, _) = Render(doc =>
        {
            var image = Image(4, 1, 8, N("DeviceRGB"), new byte[] { 255, 0, 0, 0, 0, 255, 255, 0, 0, 0, 0, 255 });
            image[N("Mask")] = doc.AddObject(Stencil(2, 1, new byte[] { 0x80 }));
            return image;
        });
        Assert.Equal(new[] { Green, Green, Red, Blue }, Quarters(page));
    }
}
