using System.Text;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.PostScript;
using MiPdf.Rendering;

namespace MiPdf.Tests.PostScript;

/// <summary>
/// EPS import (stage 7): the graphic becomes a form XObject of its bounding box that can be drawn anywhere. During
/// development the imported graphics were compared with Ghostscript's -dEPSCrop rendering of the EPS files, also for
/// the EPS files Ghostscript's eps2write made of the test pages of the earlier stages.
/// </summary>
public class EpsImportTests
{
    private static byte[] Eps(string bbox, string body, string extraHeader = "") =>
        Encoding.Latin1.GetBytes($"%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: {bbox}\n{extraHeader}%%EndComments\n{body}\n%%EOF\n");

    /// <summary>Draws the forms on a page of the given size, saves, reopens and renders it at 72 dpi.</summary>
    private static RenderedPage Render(PdfDocument doc, double width, double height, Action<PdfCanvas> draw)
    {
        var page = doc.AddPage(new PdfRectangle(0, 0, width, height));
        using (var canvas = page.GetCanvas()) draw(canvas);
        using var reopened = PdfDocument.Open(doc.ToArray());
        return PdfRenderer.Render(reopened.GetPage(doc.PageCount), new PdfRenderOptions { Dpi = 72 });
    }

    private static (int R, int G, int B) At(RenderedPage page, double x, double y)
    {
        int px = (int)x, py = page.Height - 1 - (int)y;
        int i = (py * page.Width + px) * 4;
        return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
    }

    private static void Near((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance = 3) =>
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected}, got {actual}");

    private static readonly (int, int, int) White = (255, 255, 255), Black = (0, 0, 0), Red = (255, 0, 0), Blue = (0, 0, 255);

    [Fact]
    public void TheGraphicBecomesAFormOfItsBoundingBox()
    {
        using var doc = PdfDocument.Create();
        var form = doc.ImportEps(Eps("100 200 300 350",
            "0.5 setgray 120 220 60 40 rectfill 1 0 0 setrgbcolor 280 330 100 100 rectfill 0 0 1 setrgbcolor 0 0 110 210 rectfill"));
        Assert.Equal(new PdfRectangle(0, 0, 200, 150), form.BBox);
        var page = Render(doc, 400, 300, c => c.DrawXObject(form, 50, 50));
        Near((128, 128, 128), At(page, 75, 75));   // 120 220 in the EPS is 20 20 in the form
        Near(Red, At(page, 245, 195));             // up to the top right corner of the bounding box
        Near(White, At(page, 255, 205));           // and clipped there
        Near(Blue, At(page, 55, 55));              // the bounding box starts at 100 200
        Near(White, At(page, 45, 45));
        // placed again, scaled to a width
        page = Render(doc, 400, 300, c => c.DrawXObject(form, 0, 0, width: 100, height: 75));
        Near((128, 128, 128), At(page, 15, 15));
        Near(Red, At(page, 98, 73));
        Near(White, At(page, 102, 77));
    }

    [Fact]
    public void BoundingBoxComments()
    {
        static PdfRectangle? Box(string text) => new DscComments(Encoding.Latin1.GetBytes(text)).BoundingBox;
        // the high resolution box is preferred
        Assert.Equal(new PdfRectangle(0.5, 0.25, 99.5, 49.75), Box("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 100 50\n%%HiResBoundingBox: 0.5 0.25 99.5 49.75\n%%EndComments\n"));
        // (atend): the last one in the file, after an embedded EPS with its own
        Assert.Equal(new PdfRectangle(1, 2, 3, 4), Box("%!PS\r%%BoundingBox: (atend)\r%%EndComments\r%%BeginDocument: x.eps\r%%BoundingBox: 0 0 9 9\r%%EndDocument\r%%Trailer\r%%BoundingBox: 1 2 3 4\r"));
        // the header counts; one after it only if the header has none, or an invalid one
        Assert.Equal(new PdfRectangle(0, 0, 10, 10), Box("%!PS\n%%BoundingBox: 0 0 10 10\n%%EndComments\n%%BoundingBox: 0 0 20 20\n"));
        Assert.Equal(new PdfRectangle(0, 0, 20, 20), Box("%!PS\n%%EndComments\n%%BoundingBox: 0 0 20 20\n"));
        Assert.Equal(new PdfRectangle(0, 0, 30, 30), Box("%!PS\r\n%%BoundingBox: 0 0 0 0\r\n%%BoundingBox: 0 0 30 30\r\n%%EndComments\r\n%%BoundingBox: 0 0 20 20\r\n"));
        // an invalid high resolution box falls back to the other
        Assert.Equal(new PdfRectangle(-10, -10, 10, 10), Box("%!PS\n%%HiResBoundingBox: 5 5 1 1\n%%BoundingBox: -10 -10 10 10 extra\n"));
        Assert.Null(Box("%!PS\n0 0 10 10 rectfill\n"));
        Assert.Null(Box("%!PS\n%%BoundingBox: 0 0 10\n"));
        Assert.Null(Box("%!PS\n%%BoundingBox: (atend)\n"));
        Assert.Null(Box(""));
    }

    [Fact]
    public void FilesWithoutAValidBoundingBoxAreRejected()
    {
        using var doc = PdfDocument.Create();
        var ex = Assert.Throws<PdfException>(() => doc.ImportEps(Encoding.Latin1.GetBytes("%!PS\n0 0 10 10 rectfill\n")));
        Assert.Contains("BoundingBox", ex.Message);
        Assert.Throws<PdfException>(() => doc.ImportEps(Eps("0 0 0 0", "")));
    }

    [Fact]
    public void DosEpsBinaryFilesAreReadWithoutTheirPreview()
    {
        var ps = Eps("0 0 100 100", "0 0 1 setrgbcolor 10 10 80 80 rectfill");
        var tiff = new byte[64];
        var data = new byte[30 + ps.Length + tiff.Length];
        new byte[] { 0xC5, 0xD0, 0xD3, 0xC6 }.CopyTo(data, 0);
        BitConverter.GetBytes(30u).CopyTo(data, 4);
        BitConverter.GetBytes((uint)ps.Length).CopyTo(data, 8);
        BitConverter.GetBytes((uint)(30 + ps.Length)).CopyTo(data, 20);
        BitConverter.GetBytes((uint)tiff.Length).CopyTo(data, 24);
        ps.CopyTo(data, 30);
        using var doc = PdfDocument.Create();
        var form = doc.ImportEps(new MemoryStream(data));
        var page = Render(doc, 100, 100, c => c.DrawXObject(form, 0, 0));
        Near(Blue, At(page, 50, 50));
        Near(White, At(page, 5, 5));

        BitConverter.GetBytes(5000u).CopyTo(data, 4);
        Assert.Throws<PdfException>(() => doc.ImportEps(data));
    }

    [Fact]
    public void PageOperatorsDoNotEraseOrResizeTheGraphic()
    {
        using var doc = PdfDocument.Create();
        var form = doc.ImportEps(Eps("0 0 250 70",
            "1 0 0 setrgbcolor 10 10 50 50 rectfill showpage 70 10 50 50 rectfill erasepage copypage\n" +
            "<< /PageSize [612 792] >> setpagedevice 130 10 50 50 rectfill letter 190 10 50 50 rectfill\n" +
            "currentpagedevice /PageSize get aload pop 70 eq exch 250 eq and { 0 0 5 5 rectfill } if"));
        Assert.Equal(250, form.Width);
        var page = Render(doc, 250, 70, c => c.DrawXObject(form, 0, 0));
        foreach (var x in new[] { 35, 95, 155, 215 }) Near(Red, At(page, x, 35));
        Near(Red, At(page, 2, 2));
    }

    [Fact]
    public void TheFormDoesNotDependOnTheGraphicsStateItIsDrawnWith()
    {
        using var doc = PdfDocument.Create();
        var form = doc.ImportEps(Eps("0 0 200 100",
            "10 10 30 30 rectfill 50.5 10.5 30 30 rectstroke\n" +
            "/T3 << /FontType 3 /FontMatrix [0.001 0 0 0.001 0 0] /FontBBox [0 0 1000 1000] /Encoding 256 array dup 65 /a put " +
            "/BuildChar { pop pop 1000 0 0 0 1000 1000 setcachedevice 0 0 1000 1000 rectfill } >> definefont pop\n" +
            "/T3 findfont 20 scalefont setfont 100 10 moveto (AA) show"));
        // red fill and stroke, thick dashed lines, invisible text, character spacing, scaling and rise
        var page = Render(doc, 200, 100, c =>
        {
            c.WriteRaw("1 0 0 rg 1 0 0 RG 7 w 1 J 1 j [3 5] 0 d 3 Tr 3 Tc 50 Tz 5 Ts");
            c.DrawXObject(form, 0, 0);
        });
        Near(Black, At(page, 25, 25));
        foreach (var y in new[] { 15, 18, 21, 24, 27, 30 }) Near(Black, At(page, 50, y));     // the stroke is solid
        Near(White, At(page, 52, 25));     // and one unit wide
        Near(Black, At(page, 110, 20));
        Near(Black, At(page, 130, 20));    // the second glyph follows without spacing and at full width
        Near(White, At(page, 145, 20));
    }

    [Fact]
    public void InitMatrixAndClipPathAreRelativeToTheGraphic()
    {
        using var doc = PdfDocument.Create();
        var form = doc.ImportEps(Eps("10 10 110 60",
            "5 5 translate 2 2 scale initmatrix 0 0 1 setrgbcolor clippath fill " +
            "1 0 0 setrgbcolor 20 20 10 10 rectclip initclip 30 30 20 20 rectfill initgraphics 0 setgray 60 30 20 20 rectfill"));
        var page = Render(doc, 100, 50, c => c.DrawXObject(form, 0, 0));
        Near(Blue, At(page, 1, 1));
        Near(Blue, At(page, 98, 48));
        Near(Red, At(page, 30, 30));       // 30 30 in the EPS is 20 20 in the form
        Near(Black, At(page, 60, 30));
    }

    [Fact]
    public void PostScriptErrorsThrowUnlessIgnored()
    {
        var eps = Eps("0 0 100 100", "(before) print 0 0 50 50 rectfill nosuchoperator 50 50 50 50 rectfill");
        using var doc = PdfDocument.Create();
        var ex = Assert.Throws<PostScriptException>(() => doc.ImportEps(eps));
        Assert.Equal("undefined", ex.ErrorName);
        Assert.Equal("nosuchoperator", ex.OffendingCommand);
        Assert.StartsWith("before", ex.Output);
        Assert.IsAssignableFrom<PdfException>(ex);

        var form = doc.ImportEps(eps, new PostScriptOptions { IgnoreErrors = true });
        var page = Render(doc, 100, 100, c => c.DrawXObject(form, 0, 0));
        Near(Black, At(page, 25, 25));
        Near(White, At(page, 75, 75));
    }

    [Fact]
    public void LimitsAndCancellationEndTheProgram()
    {
        var loop = Eps("0 0 10 10", "{ } loop");
        using var doc = PdfDocument.Create();
        var ex = Assert.Throws<PdfException>(() => doc.ImportEps(loop, new PostScriptOptions { MaxOperations = 1_000_000 }));
        Assert.IsNotType<PostScriptException>(ex);
        Assert.Throws<PdfException>(() => doc.ImportEps(loop, new PostScriptOptions { Timeout = TimeSpan.FromMilliseconds(100) }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ImportEps(loop, cancellationToken: cancelled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.ImportEps(loop, new PostScriptOptions { MaxOperations = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.ImportEps(loop, new PostScriptOptions { Timeout = TimeSpan.Zero }));
    }

    [Fact]
    public void DocumentPdfMarksAreIgnoredButPicturesWork()
    {
        using var doc = PdfDocument.Create();
        var form = doc.ImportEps(Eps("0 0 100 100",
            "[ /Title (ignored) /DOCINFO pdfmark [ /Rect [0 0 10 10] /Contents (x) /ANN pdfmark [ /Title (x) /OUT pdfmark\n" +
            "[ /BBox [0 0 20 20] /_objdef {pic} /BP pdfmark 1 0 0 setrgbcolor 0 0 20 20 rectfill [ /EP pdfmark\n" +
            "gsave 50 50 translate [ {pic} /SP pdfmark grestore"));
        var page = Render(doc, 100, 100, c => c.DrawXObject(form, 0, 0));
        Assert.Null(doc.Info.Title);
        Assert.Null(doc.Catalog.Get(PdfName.Get("Outlines")));
        Assert.Null(doc.GetPage(1).Dictionary.Get(PdfName.Annots));
        Near(Red, At(page, 60, 60));
        Near(White, At(page, 10, 10));
    }

    [Fact]
    public void SeveralGraphicsInOneDocument()
    {
        using var doc = PdfDocument.Create();
        var a = doc.ImportEps(Eps("0 0 50 50", "/Helvetica findfont 12 scalefont setfont 1 0 0 setrgbcolor 0 0 50 50 rectfill 0 setgray 5 20 moveto (A) show"));
        var b = doc.ImportEps(Eps("0 0 50 50", "/Helvetica findfont 12 scalefont setfont 0 0 1 setrgbcolor 0 0 50 50 rectfill 0 setgray 5 20 moveto (B) show"));
        Assert.NotEqual(a.Reference, b.Reference);
        var page = Render(doc, 200, 100, c =>
        {
            c.DrawXObject(a, 0, 0);
            c.DrawXObject(b, 100, 0);
            c.DrawXObject(a, 100, 50, 100, 50);
        });
        Near(Red, At(page, 40, 40));
        Near(Blue, At(page, 140, 40));
        Near(Red, At(page, 190, 90));
        Assert.Single(a.Resources.GetDictionary(PdfName.Font)!);
    }
}
