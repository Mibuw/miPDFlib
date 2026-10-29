using System.Text;
using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.Objects;

namespace MiPdf.Tests.Content;

public class ImageAndCanvasTests
{
    [Fact]
    public void PngWithAlphaGetsSoftMask()
    {
        // 2x1 RGBA: red opaque, blue half transparent
        var png = TestHelpers.CreatePng(2, 1, new byte[] { 255, 0, 0, 255, 0, 0, 255, 128 }, alpha: true);
        var image = PdfImage.FromBytes(png);
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);

        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawImage(image, 10, 10, 100, 50);
        var reopened = PdfDocument.Open(doc.ToArray());
        var xobject = reopened.GetPage(1).Resources.GetDictionary(PdfName.XObject)!.GetStream("Im1")!;
        Assert.Equal(PdfName.DeviceRGB, xobject.GetName(PdfName.ColorSpace));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255 }, xobject.GetBytes());
        var mask = xobject.GetStream(PdfName.SMask)!;
        Assert.Equal(new byte[] { 255, 128 }, mask.GetBytes());
    }

    [Fact]
    public void OpaquePngHasNoSoftMask()
    {
        var png = TestHelpers.CreatePng(1, 2, new byte[] { 1, 2, 3, 4, 5, 6 }, alpha: false);
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawImage(PdfImage.FromBytes(png), 0, 0, 10, 10);
        var xobject = doc.GetPage(1).Resources.GetDictionary(PdfName.XObject)!.GetStream("Im1")!;
        Assert.Null(xobject[PdfName.SMask]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, xobject.GetBytes());
    }

    [Fact]
    public void JpegIsEmbeddedUnchanged()
    {
        var jpeg = TestHelpers.CreateJpegHeader(640, 480, 3);
        var image = PdfImage.FromBytes(jpeg);
        Assert.Equal(640, image.Width);
        Assert.Equal(480, image.Height);
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawImage(image, new PdfRectangle(0, 0, 100, 100));
        var xobject = doc.GetPage(1).Resources.GetDictionary(PdfName.XObject)!.GetStream("Im1")!;
        Assert.Equal(PdfName.DCTDecode, xobject.GetName(PdfName.Filter));
        Assert.Equal(jpeg, xobject.RawBytes);
    }

    [Fact]
    public void UnsupportedImageFormatThrows() =>
        Assert.Throws<PdfException>(() => PdfImage.FromBytes(Encoding.ASCII.GetBytes("GIF89a....")));

    [Fact]
    public void CanvasWritesOperators()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        using (var c = page.GetCanvas())
        {
            c.SaveState().SetStrokeColor(PdfColor.Rgb(1, 0, 0)).SetLineWidth(2).MoveTo(0, 0).LineTo(100, 50.5).Stroke().RestoreState();
            c.SetFillColor(PdfColor.Cmyk(0, 0, 0, 1)).Rectangle(10, 10, 20, 30).Fill();
            c.SetOpacity(0.5);
        }
        var content = Encoding.ASCII.GetString(page.GetContentBytes());
        Assert.Contains("1 0 0 RG", content);
        Assert.Contains("100 50.5 l", content);
        Assert.Contains("0 0 0 1 k", content);
        Assert.Contains("10 10 20 30 re", content);
        Assert.Contains("/GS1 gs", content);
        var gs = page.Resources.GetDictionary(PdfName.ExtGState)!.GetDictionary("GS1")!;
        Assert.Equal(0.5, gs.GetDouble("ca"));
    }

    [Fact]
    public void BackgroundCanvasIsPrepended()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        page.SetContent(Encoding.ASCII.GetBytes("% foreground"));
        using (var c = page.GetCanvas(background: true)) c.WriteRaw("% background");
        using (var c = page.GetCanvas()) c.WriteRaw("% overlay");
        var content = Encoding.ASCII.GetString(page.GetContentBytes());
        int bg = content.IndexOf("% background", StringComparison.Ordinal);
        int fg = content.IndexOf("% foreground", StringComparison.Ordinal);
        int ov = content.IndexOf("% overlay", StringComparison.Ordinal);
        Assert.True(bg < fg && fg < ov, content);
    }

    [Fact]
    public void FormXObjectCanBeReused()
    {
        var doc = PdfDocument.Create();
        var stamp = new PdfFormXObject(doc, new PdfRectangle(0, 0, 100, 20));
        using (var c = stamp.GetCanvas()) c.DrawRectangle(0, 0, 100, 20, PdfColor.Red);
        for (int i = 0; i < 2; i++)
            using (var c = doc.AddPage().GetCanvas())
                c.DrawXObject(stamp, 50, 50, 200, 40);
        var reopened = PdfDocument.Open(doc.ToArray());
        var x1 = reopened.GetPage(1).Resources.GetDictionary(PdfName.XObject)!["Fm1"];
        var x2 = reopened.GetPage(2).Resources.GetDictionary(PdfName.XObject)!["Fm1"];
        Assert.Equal(x1, x2);
        Assert.Contains("2 0 0 2 50 50 cm", Encoding.ASCII.GetString(reopened.GetPage(1).GetContentBytes()));
    }

    [Fact]
    public void ColorFromHex()
    {
        var c = PdfColor.FromHex("#FF8000");
        Assert.Equal(PdfColorSpace.Rgb, c.ColorSpace);
        Assert.Equal(1, c.C1);
        Assert.Equal(128 / 255.0, c.C2, 6);
        Assert.Equal(PdfColor.FromHex("ff8000"), c);
        Assert.Equal(PdfColor.FromHex("#ff8800"), PdfColor.FromHex("f80"));
    }
}
