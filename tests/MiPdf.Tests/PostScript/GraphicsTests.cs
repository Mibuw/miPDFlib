using System.Text;
using MiPdf.PostScript;
using MiPdf.PostScript.Interpreter;
using MiPdf.Rendering;

namespace MiPdf.Tests.PostScript;

/// <summary>
/// PostScript graphics written as PDF (stage 2). The pages are rendered with miPDFlib.Rendering and checked at known
/// points. During development the same programs and about 100 random graphics programs were compared with
/// Ghostscript rendering the PostScript directly (identical up to antialiasing), also through Ghostscript's ps2write
/// and eps2write output, which carries a PDF interpreter written in PostScript.
/// </summary>
public class GraphicsTests
{
    private static PdfDocument Convert(string program, out PsJobResult result) =>
        PsConverter.Convert(Encoding.Latin1.GetBytes(program), out result);

    /// <summary>Converts, writes and reopens the document, renders page 1 at 72 dpi.</summary>
    private static RenderedPage Render(string program, int page = 1)
    {
        using var doc = Convert(program, out var result);
        Assert.Null(result.ErrorName);
        using var reopened = PdfDocument.Open(doc.ToArray());
        return PdfRenderer.Render(reopened.GetPage(page), new PdfRenderOptions { Dpi = 72 });
    }

    /// <summary>The color at (x, y) in PostScript coordinates (origin bottom left).</summary>
    private static (int R, int G, int B) At(RenderedPage page, double x, double y)
    {
        int px = (int)x, py = page.Height - 1 - (int)y;
        int i = (py * page.Width + px) * 4;
        return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
    }

    private static void Near((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance = 3) =>
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected}, got {actual}");

    private const string Page = "<< /PageSize [200 200] >> setpagedevice\n";

    [Fact]
    public void PathsAreFilledAndStroked()
    {
        var page = Render(Page + "1 0 0 setrgbcolor 10 10 50 50 rectfill " +
                          "0 0 1 setrgbcolor newpath 150 50 30 0 360 arc fill " +
                          "0 setgray 10 setlinewidth newpath 10 150 moveto 190 150 lineto stroke showpage");
        Assert.Equal(200, page.Width);
        Near((255, 0, 0), At(page, 30, 30));
        Near((0, 0, 255), At(page, 150, 50));
        Near((255, 255, 255), At(page, 150, 85));
        Near((0, 0, 0), At(page, 100, 152));
        Near((255, 255, 255), At(page, 100, 160));
    }

    [Fact]
    public void TransformationsApplyToPathsAndLineWidths()
    {
        // a rotated square and a stroke in a non-uniformly scaled space (the pen is an ellipse)
        var page = Render(Page + "gsave 100 100 translate 45 rotate 0 0 1 setrgbcolor -20 -20 40 40 rectfill grestore " +
                          "gsave 40 170 translate 4 1 scale 0 setgray 4 setlinewidth newpath -5 0 moveto 5 0 lineto stroke grestore showpage");
        Near((0, 0, 255), At(page, 100, 125)); // the corner of the rotated square points up
        Near((255, 255, 255), At(page, 122, 122));
        Near((0, 0, 0), At(page, 40, 171)); // 4 units of line width scaled by 1 vertically
        Near((255, 255, 255), At(page, 40, 175));
    }

    [Fact]
    public void ClippingFollowsTheGraphicsState()
    {
        var page = Render(Page + "gsave newpath 100 100 50 0 360 arc clip 0 0 1 setrgbcolor 0 0 200 200 rectfill " +
                          "gsave 100 100 100 100 rectclip 1 0 0 setrgbcolor 0 0 200 200 rectfill grestore " +
                          "0 1 0 setrgbcolor 60 60 10 10 rectfill grestore 0 setgray 0 0 20 20 rectfill showpage");
        Near((0, 0, 255), At(page, 80, 100));
        Near((255, 0, 0), At(page, 120, 120));
        Near((0, 255, 0), At(page, 65, 65));
        Near((255, 255, 255), At(page, 160, 160)); // outside the circle
        Near((0, 0, 0), At(page, 10, 10)); // after grestore, no clip
    }

    [Theory]
    [InlineData("0.5 setgray", 128, 128, 128)]
    [InlineData("0 0 1 0 setcmykcolor", 255, 235, 61)] // the renderer approximates a SWOP press
    [InlineData("0 1 1 sethsbcolor", 255, 0, 0)]
    [InlineData("[/Indexed /DeviceRGB 1 <000000 00FF00>] setcolorspace 1 setcolor", 0, 255, 0)]
    [InlineData("[/Separation /Spot /DeviceRGB { dup 0 exch }] setcolorspace 1 setcolor", 255, 0, 255)]
    [InlineData("[/DeviceN [/A /B] /DeviceGray { add 2 div }] setcolorspace 0.2 0.8 setcolor", 128, 128, 128)]
    [InlineData("[/CIEBasedABC << /WhitePoint [0.9505 1 1.089] /MatrixLMN [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505] >>] setcolorspace 1 1 1 setcolor", 255, 255, 255)]
    public void ColorSpacesAreReducedToDeviceColors(string setColor, int r, int g, int b)
    {
        var page = Render(Page + setColor + " 0 0 200 200 rectfill showpage");
        Near((r, g, b), At(page, 100, 100), 4);
    }

    [Fact]
    public void SeparationNoneIsNotPainted()
    {
        var page = Render(Page + "[/Separation /None /DeviceGray { pop 0 }] setcolorspace 1 setcolor 0 0 200 200 rectfill showpage");
        Near((255, 255, 255), At(page, 100, 100));
    }

    [Fact]
    public void PagesHaveTheirSizes()
    {
        using var doc = Convert("<< /PageSize [200 100] >> setpagedevice 10 10 50 50 rectfill showpage " +
                                "<< /PageSize [100 300] >> setpagedevice showpage " +
                                "0.5 setgray 0 0 100 100 rectfill erasepage showpage " +
                                "0 0 10 10 rectfill", out var result);
        Assert.Null(result.ErrorName);
        Assert.Equal(3, doc.PageCount); // the last page is not shown
        Assert.Equal((200, 100), ((int)doc.GetPage(1).MediaBox.Width, (int)doc.GetPage(1).MediaBox.Height));
        Assert.Equal((100, 300), ((int)doc.GetPage(2).MediaBox.Width, (int)doc.GetPage(2).MediaBox.Height));
        // an EPS or a program without showpage: the painted page is kept
        using var single = Convert("0 0 10 10 rectfill", out _);
        Assert.Equal(1, single.PageCount);
    }

    [Fact]
    public void SaveAndGrestoreRestoreTheGraphicsState()
    {
        var page = Render(Page + "1 0 0 setrgbcolor save 0 0 1 setrgbcolor 50 50 translate restore 0 0 20 20 rectfill " +
                          "gsave 0 1 0 setrgbcolor grestore 30 0 20 20 rectfill " +
                          "gsave nulldevice 0 0 200 200 rectfill grestore 0 0 1 setrgbcolor 60 0 20 20 rectfill showpage");
        Near((255, 0, 0), At(page, 10, 10));
        Near((255, 0, 0), At(page, 40, 10));
        Near((0, 0, 255), At(page, 70, 10));
        Near((255, 255, 255), At(page, 100, 100)); // nothing from the null device
    }

    [Fact]
    public void StrokePathAndUserPathsMakeAreas()
    {
        var page = Render(Page + "20 setlinewidth newpath 20 100 moveto 180 100 lineto strokepath 0 0 1 setrgbcolor fill " +
                          "/up { 0 0 200 200 setbbox 10 10 moveto 60 10 lineto 35 50 lineto closepath } cvlit def 1 0 0 setrgbcolor up ufill showpage");
        Near((0, 0, 255), At(page, 100, 108)); // inside the outline of the 20-unit stroke
        Near((255, 255, 255), At(page, 100, 115));
        Near((255, 0, 0), At(page, 35, 20));
    }

    [Fact]
    public void PathQueriesWorkInUserSpace()
    {
        using var doc = Convert("2 2 scale newpath 10 20 moveto 30 40 lineto currentpoint = = pathbbox = = = = " +
                                "[ { (m) } { (l) } { (c) } { (h) } pathforall ] length = 1 0 dtransform = = matrix currentmatrix ==", out var result);
        Assert.Null(result.ErrorName);
        // device space is 1/720 inch: the default matrix scales by 10
        Assert.Equal("40.0\n30.0\n40.0\n30.0\n20.0\n10.0\n6\n0.0\n20.0\n[20.0 0.0 0.0 20.0 0.0 0.0]\n", result.Output);
    }

    [Fact]
    public void ArcsAndCurvesGiveTheExpectedGeometry()
    {
        // arcto returns the tangent points; arc from the current point adds a line
        using var doc = Convert("newpath 0 0 moveto 100 0 100 100 10 arcto 4 { =only ( ) print } repeat " +
                                "newpath 0 0 moveto 50 50 10 0 90 arc currentpoint exch round = round =", out var result);
        Assert.Null(result.ErrorName);
        Assert.Equal("10.0 100.0 0.0 90.0 50.0\n60.0\n", result.Output);
    }
}
