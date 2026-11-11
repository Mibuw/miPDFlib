using System.Text;
using MiPdf.Objects;
using MiPdf.PostScript;
using MiPdf.PostScript.Interpreter;
using MiPdf.Rendering;

namespace MiPdf.Tests.PostScript;

/// <summary>
/// PostScript LanguageLevel 2 and 3 graphics written as PDF (stage 5): tiling and shading patterns, shfill with all
/// shading types, forms, and the BeginPage, EndPage and Install procedures of setpagedevice. Expected colors, errors and
/// output were taken from Ghostscript; during development the test pages were also compared with Ghostscript through
/// its ps2write and eps2write output, which uses its own pattern, shading and form procedures.
/// </summary>
public class PatternTests
{
    private const string Page = "<< /PageSize [200 200] >> setpagedevice\n";

    private static PdfDocument Convert(string program, out PsJobResult result) =>
        PsConverter.Convert(Encoding.Latin1.GetBytes(program), out result);

    private static RenderedPage Render(string program, int pageNumber = 1)
    {
        using var doc = Convert(program, out var result);
        Assert.Null(result.ErrorName);
        using var reopened = PdfDocument.Open(doc.ToArray());
        return PdfRenderer.Render(reopened.GetPage(pageNumber), new PdfRenderOptions { Dpi = 72 });
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

    private static readonly (int, int, int) White = (255, 255, 255), Red = (255, 0, 0), Blue = (0, 0, 255);

    /// <summary>The resources of page 1 in a category.</summary>
    private static List<PdfDictionary> Resources(PdfDocument doc, string category) =>
        doc.GetPage(1).Resources.GetDictionary(PdfName.Get(category))?.Select(kv => kv.Value.Resolve()).OfType<PdfDictionary>().ToList()
        ?? new List<PdfDictionary>();

    private const string Checker =
        "/checker << /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 " +
        "/PaintProc { pop 1 0 0 setrgbcolor 0 0 10 10 rectfill 0 0 1 setrgbcolor 10 10 10 10 rectfill } >> def\n";

    [Fact]
    public void TilingPatternsPaintTheirCells()
    {
        var page = Render(Page + Checker +
            // a colored pattern: the cell repeats from the origin of the page
            "checker matrix makepattern setpattern 0 100 100 100 rectfill\n" +
            // scaled when made: the cells are twice as large
            "checker 2 2 matrix scale makepattern setpattern 100 100 100 100 rectfill\n" +
            // an uncolored pattern painted in green, with the components before the pattern
            "/dot << /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 " +
            "/PaintProc { pop 1 0 0 setrgbcolor 0 0 5 5 rectfill } >> matrix makepattern def\n" +
            "[/Pattern /DeviceRGB] setcolorspace 0 1 0 dot setcolor 0 0 100 100 rectfill\n" +
            // text and stroke with a pattern
            "/DeviceRGB setcolorspace 0 0 1 dot setpattern 20 setlinewidth 100 50 moveto 200 50 lineto stroke showpage");
        Near(Red, At(page, 5, 105));
        Near(White, At(page, 15, 105));
        Near(Blue, At(page, 15, 115));
        Near(Red, At(page, 25, 125));
        Near(Blue, At(page, 105, 105));     // 2 x 2 scaled: the cells are 40 units
        Near(Blue, At(page, 115, 115));
        Near(Red, At(page, 125, 125));
        Near((0, 255, 0), At(page, 2, 2));  // the color, not the red of the PaintProc
        Near(White, At(page, 7, 2));
        Near(Blue, At(page, 102, 42));
        Near(White, At(page, 107, 42));
        Near(White, At(page, 102, 62));     // outside the stroke
    }

    [Fact]
    public void PatternsAreWrittenAsPdfPatterns()
    {
        using var doc = Convert(Page + Checker + "checker matrix makepattern setpattern 0 0 50 50 rectfill 100 100 50 50 rectfill showpage", out var result);
        Assert.Null(result.ErrorName);
        var pattern = Assert.Single(Resources(doc, "Pattern"));
        Assert.Equal(1, pattern.GetInt(PdfName.Get("PatternType")));
        Assert.Equal(1, pattern.GetInt(PdfName.Get("PaintType")));
        // the cell is in pattern space; the pattern matrix maps it to the default space of the page
        Assert.Equal(new[] { 1.0, 0, 0, 1, 0, 0 }, pattern.GetArray(PdfName.Get("Matrix"))!.Select(v => ((PdfNumber)v).Value).Select(v => Math.Round(v, 6)));
        string content = Encoding.ASCII.GetString(doc.GetPage(1).GetContentBytes());
        Assert.Equal(1, content.Split("/Pattern cs").Length - 1); // set once for both rectangles
    }

    [Theory]
    [InlineData("<< >> matrix makepattern", "undefined")]
    [InlineData("<< /PatternType 3 >> matrix makepattern", "rangecheck")]
    [InlineData("<< /PatternType 1.0 >> matrix makepattern", "undefined")]
    [InlineData("checker dup /PaintType 3 put matrix makepattern", "rangecheck")]
    [InlineData("checker dup /TilingType undef matrix makepattern", "undefined")]
    [InlineData("checker dup /BBox [0 0 1] put matrix makepattern", "rangecheck")]
    [InlineData("checker dup /BBox [0 0 0 0] put matrix makepattern", "rangecheck")]
    [InlineData("checker dup /XStep 0 put matrix makepattern", "rangecheck")]
    [InlineData("checker dup /PaintProc (x) put matrix makepattern", "typecheck")]
    [InlineData("checker [1 2] makepattern", "rangecheck")]
    [InlineData("checker matrix makepattern /X 1 put", "invalidaccess")]
    [InlineData("<< /PatternType 2 >> matrix makepattern", "undefined")]
    [InlineData("<< /PatternType 2 /Shading 5 >> matrix makepattern", "typecheck")]
    [InlineData("checker setpattern", "undefined")]
    [InlineData("<< >> execform", "undefined")]
    [InlineData("<< /FormType 2 /BBox [0 0 1 1] /Matrix [1 0 0 1 0 0] /PaintProc {pop} >> execform", "undefined")]
    [InlineData("<< /FormType 1 /BBox [0 0 1 1] /PaintProc {pop} >> execform", "undefined")]
    public void InvalidPatternsAndFormsAreErrors(string program, string error)
    {
        using var doc = Convert(Checker + program, out var result);
        Assert.Equal(error, result.ErrorName);
    }

    [Fact]
    public void PatternColorOperators()
    {
        using var doc = Convert(Checker +
            "/p checker matrix makepattern def p /Implementation get type =only ( ) print\n" +
            // setpattern puts the current color space under the Pattern space
            "1 0 0 setrgbcolor p setpattern currentcolorspace == currentcolor count =only ( ) print clear\n" +
            "currentgray =only ( ) print\n" +
            // an uncolored pattern takes the components of the underlying space
            "/u checker dup /PaintType 2 put matrix makepattern def /DeviceRGB setcolorspace 0.5 0 1 u setpattern currentcolor count =only\n",
            out var result);
        Assert.Null(result.ErrorName);
        Assert.Equal("arraytype [/Pattern [/DeviceRGB]]\n1 0.0 4", result.Output);
    }

    // ------------------------------------------------------------------ shadings

    [Fact]
    public void AxialAndRadialShadings()
    {
        var page = Render(Page +
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> gsave 0 100 200 100 rectclip shfill grestore\n" +
            "<< /ShadingType 3 /ColorSpace /DeviceGray /Coords [100 50 0 100 50 40] /Extend [false true] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >> gsave 0 0 200 100 rectclip shfill grestore showpage");
        Near(Red, At(page, 1, 150), 6);
        Near((128, 0, 128), At(page, 100, 150), 6);
        Near(Blue, At(page, 198, 150), 6);
        Near((0, 0, 0), At(page, 100, 50), 6);
        Near((128, 128, 128), At(page, 120, 50), 8);
        Near(White, At(page, 190, 90), 3); // extended beyond the outer circle
    }

    [Fact]
    public void ShadingsAreWrittenAsPdfShadings()
    {
        using var doc = Convert(Page +
            // a device space: the function is written as it is
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Extend [true true] " +
            "/Function << /FunctionType 3 /Domain [0 1] /Bounds [0.5] /Encode [0 1 0 1] /Functions [" +
            "<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >> << /FunctionType 0 /Domain [0 1] /Range [0 1 0 1 0 1] " +
            "/Size [2] /BitsPerSample 8 /DataSource <00FF00 0000FF> >> ] >> >> shfill\n" +
            // a Separation: converted to its alternate space through a sampled function
            "<< /ShadingType 2 /ColorSpace [/Separation /Orange /DeviceCMYK { dup 0.6 mul exch 0 exch 0 4 1 roll 0 exch }] " +
            "/Coords [0 0 0 200] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >> shfill showpage", out var result);
        Assert.Null(result.ErrorName);
        var shadings = Resources(doc, "Shading");
        Assert.Equal(2, shadings.Count);
        var device = shadings.Single(s => s.GetName(PdfName.ColorSpace)?.Value == "DeviceRGB");
        var function = device.GetDictionary(PdfName.Get("Function"))!;
        Assert.Equal(3, function.GetInt(PdfName.Get("FunctionType")));
        Assert.Equal(2, device.GetArray(PdfName.Get("Extend"))!.Count);
        var separation = shadings.Single(s => s.GetName(PdfName.ColorSpace)?.Value == "DeviceCMYK");
        var sampled = (PdfStream)separation.Get(PdfName.Get("Function"))!.Resolve()!;
        Assert.Equal(0, sampled.GetInt(PdfName.Get("FunctionType")));
        Assert.Equal(8, sampled.GetArray(PdfName.Get("Range"))!.Count);
    }

    [Fact]
    public void MeshShadings()
    {
        var page = Render(Page +
            // free-form triangles from an array: one red triangle
            "<< /ShadingType 4 /ColorSpace /DeviceRGB /DataSource [0 0 100 1 0 0  0 100 100 1 0 0  0 0 200 1 0 0] >> shfill\n" +
            // packed: 8-bit flags and coordinates, an Indexed space
            "<< /ShadingType 4 /ColorSpace [/Indexed /DeviceRGB 1 <FF0000 0000FF>] /BitsPerCoordinate 8 /BitsPerComponent 8 " +
            "/BitsPerFlag 8 /Decode [0 255 0 255 0 255] /DataSource <00 64 00 00  00 C8 00 00  00 C8 64 01> >> shfill\n" +
            // a lattice, gray from black to white
            "<< /ShadingType 5 /ColorSpace /DeviceGray /VerticesPerRow 2 /DataSource [0 0 0  100 0 1  0 90 0  100 90 1] >> " +
            "gsave 0 0 100 90 rectclip shfill grestore\n" +
            // a Coons patch with a function on t (all corners 1: green)
            "<< /ShadingType 6 /ColorSpace /DeviceRGB /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >> " +
            "/DataSource [0  0 150 10 150 20 150 30 150  30 160 30 170 30 180  20 180 10 180 0 180  0 170 0 160  1 1 1 1] >> shfill showpage");
        Near(Red, At(page, 20, 120), 3);
        Near((11, 0, 243), At(page, 198, 95), 4);  // near the third vertex, index 1: the colors are interpolated
        Near((151, 0, 103), At(page, 170, 40), 4);
        Near((0, 0, 0), At(page, 1, 40), 6);
        Near((128, 128, 128), At(page, 50, 40), 8);
        Near((0, 255, 0), At(page, 15, 165), 3);
        Near(White, At(page, 45, 165), 3);
    }

    [Fact]
    public void ShadingPatternsFillPaths()
    {
        var page = Render(Page +
            "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Background [0 1 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> >> matrix makepattern setpattern\n" +
            "20 20 160 60 rectfill showpage");
        Near((227, 0, 26), At(page, 21, 50), 4);
        Near((26, 0, 227), At(page, 178, 50), 4);
        Near(White, At(page, 100, 100));
    }

    [Theory]
    [InlineData("<< >> shfill", "undefined")]
    [InlineData("<< /ShadingType 8 /ColorSpace /DeviceGray >> shfill", "rangecheck")]
    [InlineData("ax dup /ColorSpace undef shfill", "undefined")]
    [InlineData("ax dup /ColorSpace /Pattern put shfill", "typecheck")]
    [InlineData("ax dup /Function undef shfill", "undefined")]
    [InlineData("ax dup /Coords undef shfill", "rangecheck")]
    [InlineData("ax dup /Coords (abcd) put shfill", "typecheck")]
    [InlineData("ax dup /ColorSpace /DeviceRGB put shfill", "rangecheck")]
    [InlineData("ax dup /Function << /FunctionType 4 /Domain [0 1] /Range [0 1] >> put shfill", "rangecheck")]
    [InlineData("ax dup /Function << /FunctionType 2 /Domain [0 1] >> put shfill", "rangecheck")]
    [InlineData("ax dup /Function << /FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2] /BitsPerSample 7 /DataSource <00FF> >> put shfill", "rangecheck")]
    [InlineData("ax dup /Function << /FunctionType 3 /Domain [0 1] /Functions [f2 f2] /Bounds [] /Encode [0 1 0 1] >> put shfill", "rangecheck")]
    [InlineData("<< /ShadingType 3 /ColorSpace /DeviceGray /Coords [0 0 -1 0 0 10] /Function f2 >> shfill", "rangecheck")]
    [InlineData("<< /ShadingType 1 /ColorSpace /DeviceGray /Function f2 >> shfill", "rangecheck")]
    [InlineData("<< /ShadingType 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 255 0 255 0 1] /DataSource <00> >> shfill", "undefined")]
    [InlineData("<< /ShadingType 4 /ColorSpace /DeviceGray /BitsPerCoordinate 7 /BitsPerComponent 8 /BitsPerFlag 8 /DataSource <00> >> shfill", "rangecheck")]
    [InlineData("<< /ShadingType 4 /ColorSpace /DeviceGray /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 3 /DataSource <00> >> shfill", "rangecheck")]
    [InlineData("<< /ShadingType 5 /ColorSpace /DeviceGray /DataSource [0 0 0] >> shfill", "undefined")]
    [InlineData("ax dup /ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>] put shfill", "typecheck")]
    [InlineData("ax dup /Background [1 2] put shfill", "rangecheck")]
    [InlineData("ax dup /Extend [true] put shfill", "rangecheck")]
    public void InvalidShadingsAreErrors(string program, string error)
    {
        const string setup = "/f2 << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> def " +
                             "/ax { << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 100 0] /Function f2 >> } def\n";
        using var doc = Convert(setup + program, out var result);
        Assert.Equal(error, result.ErrorName);
    }

    // ------------------------------------------------------------------ forms

    private const string Logo =
        "/logo << /FormType 1 /BBox [0 0 50 50] /Matrix [1 0 0 1 0 0] " +
        "/PaintProc { pop -10 -10 100 100 rectfill } >> def\n";

    [Fact]
    public void FormsBecomeFormXObjects()
    {
        string program = Page + Logo +
            "1 0 0 setrgbcolor 10 10 translate logo execform 60 0 translate logo execform\n" +
            // painted again with another color: a second form XObject
            "0 0 1 setrgbcolor 60 0 translate logo execform\n" +
            // the form's own matrix and its BBox clip
            "initmatrix 0 1 0 setrgbcolor << /FormType 1 /BBox [0 0 50 50] /Matrix [2 0 0 1 0 0] /PaintProc { pop -10 -10 100 100 rectfill } >> " +
            "0 100 translate execform showpage";
        using var doc = Convert(program, out var result);
        Assert.Null(result.ErrorName);
        var forms = Resources(doc, "XObject");
        Assert.Equal(3, forms.Count);
        Assert.All(forms, f => Assert.Equal("Form", f.GetName(PdfName.Subtype)?.Value));
        string content = Encoding.ASCII.GetString(doc.GetPage(1).GetContentBytes());
        Assert.Equal(4, content.Split(" Do Q").Length - 1);

        var page = Render(program);
        Near(Red, At(page, 15, 15));
        Near(White, At(page, 5, 5));     // outside the BBox
        Near(Red, At(page, 75, 55));
        Near(Blue, At(page, 135, 15));
        Near((0, 255, 0), At(page, 95, 145));
        Near(White, At(page, 105, 145)); // the BBox scaled by the form matrix
    }

    [Fact]
    public void FormsRunInsideFormsAndWithPatterns()
    {
        var page = Render(Page + Logo + Checker +
            "/outer << /FormType 1 /BBox [0 0 200 200] /Matrix [1 0 0 1 0 0] /PaintProc { pop " +
            "0 0 1 setrgbcolor logo execform 100 0 translate checker matrix makepattern setpattern 0 0 60 60 rectfill } >> def\n" +
            "outer execform 0 100 translate outer execform showpage");
        Near(Blue, At(page, 25, 25));
        Near(Red, At(page, 105, 5));     // the pattern is anchored to the page, as in Ghostscript
        Near(Blue, At(page, 115, 15));
        Near(Blue, At(page, 25, 125));
        Near(Red, At(page, 105, 105));
    }

    // ------------------------------------------------------------------ page device

    [Fact]
    public void BeginPageAndEndPageProcedures()
    {
        using var doc = Convert("<< /BeginPage { (B) print =only ( ) print } /EndPage { (E) print exch =only (/) print dup =only ( ) print 2 ne } >> " +
                                "setpagedevice showpage copypage << /PageSize [300 300] >> setpagedevice (|) print", out var result);
        Assert.Null(result.ErrorName);
        Assert.Equal("B0 E0/0 B1 E1/0 B2 E2/2 B2 |", result.Output);
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public void EndPageCanCollectPagesOnOneSheet()
    {
        // two-up: the first page of each pair is not shown and not erased, the second one is painted over it
        string program = "<< /PageSize [200 200] /EndPage { exch pop 0 eq { pagecount 2 mod 1 eq } { false } ifelse } " +
                         "/BeginPage { /pagecount exch def pagecount 2 mod 100 mul 0 translate } >> setpagedevice\n" +
                         "1 0 0 setrgbcolor 10 10 50 50 rectfill showpage 0 0 1 setrgbcolor 10 10 50 50 rectfill showpage\n" +
                         "0 1 0 setrgbcolor 10 10 50 50 rectfill showpage";
        using var doc = Convert(program, out var result);
        Assert.Null(result.ErrorName);
        Assert.Equal(1, doc.PageCount);
        var page = Render(program);
        Near(Red, At(page, 30, 30));
        Near(Blue, At(page, 130, 30));
    }

    [Fact]
    public void BindMakesNestedProceduresReadOnly()
    {
        // a procedure bound before M was an operator keeps the name, even when bound again inside a later procedure
        using var doc = Convert("/outer { 1 { M } repeat } bind def /M /setmiterlimit load def /later { //outer exec } bind def\n" +
                                "/outer load 1 get dup 0 get type =only ( ) print wcheck =only ( ) print /outer load wcheck =only", out var result);
        Assert.Null(result.ErrorName);
        Assert.Equal("nametype false true", result.Output);
    }
}
