using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.Objects;
using MiPdf.Rendering;

namespace MiPdf.Tests.Rendering;

public class RendererTests
{
    private static (byte R, byte G, byte B, byte A) Pixel(RenderedPage page, int x, int y)
    {
        int i = (y * page.Width + x) * 4;
        return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2], page.Rgba[i + 3]);
    }

    private static RenderedPage Render(Action<PdfCanvas> draw, PdfRectangle? size = null, PdfRenderOptions? options = null, Action<PdfDocument, PdfPage>? setup = null)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(size ?? new PdfRectangle(0, 0, 100, 50));
        setup?.Invoke(doc, page);
        using (var canvas = page.GetCanvas()) draw(canvas);
        var reopened = PdfDocument.Open(doc.ToArray());
        return PdfRenderer.Render(reopened.GetPage(1), options ?? new PdfRenderOptions { Dpi = 72 });
    }

    [Fact]
    public void FillsAtTheRightPositionAndSize()
    {
        var page = Render(c => c.SetFillColor(PdfColor.Red).Rectangle(10, 10, 30, 20).Fill());
        Assert.Equal((100, 50), (page.Width, page.Height));
        Assert.Equal((255, 0, 0, 255), Pixel(page, 20, 30));        // inside (y down: 50 - 20)
        Assert.Equal((255, 255, 255, 255), Pixel(page, 5, 5));      // outside
        Assert.Equal((255, 255, 255, 255), Pixel(page, 45, 30));
        // anti-aliased edge: a rectangle edge at half a pixel gives a mixed color
        var half = Render(c => c.SetFillColor(PdfColor.Black).Rectangle(10.5, 10, 30, 20).Fill());
        Assert.InRange(Pixel(half, 10, 30).R, 100, 160);
    }

    [Fact]
    public void ResolutionAndRotation()
    {
        var page = Render(c => c.SetFillColor(PdfColor.Blue).Rectangle(0, 0, 10, 10).Fill(),
            options: new PdfRenderOptions { Dpi = 144 }, setup: (_, p) => p.Rotation = 90);
        Assert.Equal((100, 200), (page.Width, page.Height)); // 50x100 pt rotated, 2 px per pt
        // the bottom left corner of the page is at the top left after a clockwise rotation
        Assert.Equal((0, 0, 255, 255), Pixel(page, 5, 5));
        Assert.Equal((255, 255, 255, 255), Pixel(page, 95, 195));
    }

    [Fact]
    public void FillRules()
    {
        void Star(PdfCanvas c) => c.MoveTo(50, 48).LineTo(65, 2).LineTo(25, 32).LineTo(75, 32).LineTo(35, 2).ClosePath();
        var nonzero = Render(c => { c.SetFillColor(PdfColor.Black); Star(c); c.Fill(); });
        var evenOdd = Render(c => { c.SetFillColor(PdfColor.Black); Star(c); c.Fill(evenOdd: true); });
        Assert.Equal(0, Pixel(nonzero, 50, 26).R);   // center filled
        Assert.Equal(255, Pixel(evenOdd, 50, 26).R); // center empty
    }

    [Fact]
    public void ClippingAndOpacity()
    {
        var page = Render(c =>
        {
            c.SaveState().Rectangle(0, 0, 50, 50).Clip();
            c.SetFillColor(PdfColor.Black).Rectangle(0, 0, 100, 50).Fill();
            c.RestoreState();
            c.SaveState().SetOpacity(0.5).SetFillColor(PdfColor.Red).Rectangle(60, 10, 30, 30).Fill().RestoreState();
        });
        Assert.Equal(0, Pixel(page, 25, 25).R);
        Assert.Equal((255, 255, 255, 255), Pixel(page, 55, 5));      // clipped
        var blended = Pixel(page, 75, 25);
        Assert.Equal(255, blended.R);
        Assert.InRange(blended.G, 126, 129);                       // 50 % red over white
    }

    [Fact]
    public void StrokesWithDashes()
    {
        var page = Render(c => c.SetStrokeColor(PdfColor.Black).SetLineWidth(4).SetDashPattern(10, 10).MoveTo(0, 25).LineTo(100, 25).Stroke());
        Assert.Equal(0, Pixel(page, 5, 25).R);      // first dash
        Assert.Equal(255, Pixel(page, 15, 25).R);   // gap
        Assert.Equal(0, Pixel(page, 25, 25).R);
        Assert.Equal(255, Pixel(page, 5, 20).R);    // outside the line width
    }

    [Fact]
    public void ImagesAreDrawnInTheirUnitSquare()
    {
        var pixels = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 }; // 2x2: red green / blue yellow
        var page = Render(c => c.DrawImage(PdfImage.FromPixels(2, 2, pixels, 3), 0, 0, 100, 50));
        Assert.Equal((255, 0, 0, 255), Pixel(page, 25, 10));   // top left
        Assert.Equal((0, 255, 0, 255), Pixel(page, 75, 10));
        Assert.Equal((0, 0, 255, 255), Pixel(page, 25, 40));
        Assert.Equal((255, 255, 0, 255), Pixel(page, 75, 40));
    }

    [Fact]
    public void HiddenLayersAndAnnotations()
    {
        PdfLayer? hidden = null;
        var page = Render(c => c.Layer(hidden!, x => x.SetFillColor(PdfColor.Black).Rectangle(0, 0, 50, 50).Fill()),
            setup: (doc, p) =>
            {
                hidden = doc.AddLayer("Hidden", visible: false);
                p.AddRectangleAnnotation(new PdfRectangle(60, 10, 90, 40), fill: PdfColor.Green);
            });
        Assert.Equal((255, 255, 255, 255), Pixel(page, 25, 25));   // hidden layer
        Assert.Equal(0, Pixel(page, 75, 25).R);                   // annotation appearance (green)
        Assert.True(Pixel(page, 75, 25).G > 100);
    }

    [Fact]
    public void TransparentBackgroundAndPng()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 20, 20)).GetCanvas()) c.SetFillColor(PdfColor.Black).Rectangle(0, 0, 10, 10).Fill();
        var page = PdfRenderer.Render(doc.GetPage(1), new PdfRenderOptions { Dpi = 72, Background = null });
        Assert.Equal(0, Pixel(page, 15, 5).A);
        Assert.Equal(255, Pixel(page, 5, 15).A);
        var png = page.ToPng();
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
    }

    [Fact]
    public void BrokenContentDoesNotFail()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 50, 50));
        page.SetContent("q 1 0 0 rg 0 0 m 50 50 l f Q Q Q cm re W n 10 10 20 20 re f /Unknown Do (x) Tj 1e400 0 m 5 5 l S"u8.ToArray());
        var rendered = PdfRenderer.Render(page, new PdfRenderOptions { Dpi = 72 });
        Assert.Equal(50, rendered.Width);
    }
}
