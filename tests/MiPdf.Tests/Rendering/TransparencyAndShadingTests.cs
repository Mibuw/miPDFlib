using System.Text;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.Rendering;

namespace MiPdf.Tests.Rendering;

/// <summary>Rendering stage 3: PDF functions, shadings, patterns, blend modes, soft masks and transparency groups.</summary>
public class TransparencyAndShadingTests
{
    private static PdfArray A(params double[] values) => new(values.Select(v => (PdfObject)new PdfNumber(v)));

    private static PdfName N(string name) => PdfName.Get(name);

    private static PdfDictionary D(params (string Key, PdfObject Value)[] entries)
    {
        var d = new PdfDictionary();
        foreach (var (k, v) in entries) d[N(k)] = v;
        return d;
    }

    private static PdfStream S(string content, params (string Key, PdfObject Value)[] entries)
    {
        var s = new PdfStream(Encoding.ASCII.GetBytes(content), compress: false);
        foreach (var (k, v) in entries) s[N(k)] = v;
        return s;
    }

    private static PdfStream Bin(byte[] data, params (string Key, PdfObject Value)[] entries)
    {
        var s = new PdfStream(data, compress: false);
        foreach (var (k, v) in entries) s[N(k)] = v;
        return s;
    }

    private static double[] Eval(PdfObject function, params double[] input)
    {
        var f = PdfFunction.Load(function);
        Assert.NotNull(f);
        var output = new double[f!.Outputs];
        f.Evaluate(input, output);
        return output;
    }

    // ------------------------------------------------------------------ functions

    [Fact]
    public void ExponentialAndStitchingFunctions()
    {
        var exponential = D(("FunctionType", new PdfNumber(2)), ("Domain", A(0, 1)), ("C0", A(0, 1)), ("C1", A(1, 0)), ("N", new PdfNumber(2)));
        Assert.Equal(new[] { 0.25, 0.75 }, Eval(exponential, 0.5));
        Assert.Equal(new[] { 1.0, 0.0 }, Eval(exponential, 7)); // input clipped to the domain

        var stitching = D(("FunctionType", new PdfNumber(3)), ("Domain", A(0, 1)), ("Bounds", A(0.5)), ("Encode", A(0, 1, 1, 0)),
            ("Functions", new PdfArray(
                D(("FunctionType", new PdfNumber(2)), ("Domain", A(0, 1)), ("C0", A(0)), ("C1", A(1)), ("N", new PdfNumber(1))),
                D(("FunctionType", new PdfNumber(2)), ("Domain", A(0, 1)), ("C0", A(0)), ("C1", A(1)), ("N", new PdfNumber(1))))));
        Assert.Equal(0.5, Eval(stitching, 0.25)[0], 6);  // first function, encoded 0..1
        Assert.Equal(0.5, Eval(stitching, 0.75)[0], 6);  // second function, encoded 1..0
        Assert.Equal(1.0, Eval(stitching, 0.5)[0], 6);   // the bound belongs to the second function
    }

    [Fact]
    public void SampledFunctionsInterpolate()
    {
        // one input, two samples (8 bit): 0 and 255 -> linear
        var linear = Bin(new byte[] { 0, 255 }, ("FunctionType", new PdfNumber(0)), ("Domain", A(0, 1)), ("Range", A(0, 1)),
            ("Size", A(2)), ("BitsPerSample", new PdfNumber(8)));
        Assert.Equal(0.25, Eval(linear, 0.25)[0], 6);
        // two inputs, 2x2 samples: bilinear in the middle
        var bilinear = Bin(new byte[] { 0, 255, 255, 0 }, ("FunctionType", new PdfNumber(0)), ("Domain", A(0, 1, 0, 1)), ("Range", A(0, 1)),
            ("Size", A(2, 2)), ("BitsPerSample", new PdfNumber(8)));
        Assert.Equal(0.5, Eval(bilinear, 0.5, 0.5)[0], 6);
        Assert.Equal(1.0, Eval(bilinear, 1, 0)[0], 6);
        // Decode maps the samples, 4-bit samples
        var decoded = Bin(new byte[] { 0x0F }, ("FunctionType", new PdfNumber(0)), ("Domain", A(0, 1)), ("Range", A(0, 10)),
            ("Size", A(2)), ("BitsPerSample", new PdfNumber(4)), ("Decode", A(2, 4)));
        Assert.Equal(3.0, Eval(decoded, 0.5)[0], 6);
    }

    [Theory]
    [InlineData("{ dup mul }", 0.5, 0.25)]
    [InlineData("{ 0.5 gt { 1 } { 0 } ifelse }", 0.7, 1)]
    [InlineData("{ 0.5 gt { 1 } { 0 } ifelse }", 0.3, 0)]
    [InlineData("{ 2 mul 1 exch sub abs }", 0.75, 0.5)]
    [InlineData("{ 360 mul sin }", 0.25, 1)]
    [InlineData("{ 1 2 3 3 -1 roll pop pop pop }", 0.4, 0.4)]
    [InlineData("{ 0.1 0.2 1 index add exch pop exch pop }", 0.9, 0.3)]
    [InlineData("{ 10 mul cvi 3 mod 10 div }", 0.75, 0.1)]
    [InlineData("{ 2 copy pop pop }", 0.6, 0.6)]
    [InlineData("{ 1 add }", 0.8, 1)] // clipped to the range
    public void CalculatorFunctions(string program, double input, double expected)
    {
        var function = S(program, ("FunctionType", new PdfNumber(4)), ("Domain", A(0, 1)), ("Range", A(0, 1)));
        Assert.Equal(expected, Eval(function, input)[0], 6);
    }

    [Fact]
    public void InvalidFunctionsAreRejected()
    {
        Assert.Null(PdfFunction.Load(D(("FunctionType", new PdfNumber(9)))));
        Assert.Null(PdfFunction.Load(S("{ 1 add", ("FunctionType", new PdfNumber(4)), ("Domain", A(0, 1)), ("Range", A(0, 1)))));
        // an unknown operator does not throw: the results are what is on the stack
        Assert.Equal(0.5, Eval(S("{ pop 0.5 frobnicate }", ("FunctionType", new PdfNumber(4)), ("Domain", A(0, 1)), ("Range", A(0, 1))), 0.2)[0], 6);
    }

    // ------------------------------------------------------------------ rendering helpers

    private static (byte R, byte G, byte B) Pixel(RenderedPage page, int x, int y)
    {
        int i = (y * page.Width + x) * 4;
        return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
    }

    private static void Near((byte R, byte G, byte B) expected, (byte R, byte G, byte B) actual, int tolerance = 3)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    /// <summary>A 200x100 pt page at 72 dpi (pixel y = 100 - page y) with the given resources and content.</summary>
    private static RenderedPage Render(string content, Action<PdfDocument, PdfDictionary> resources)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 200, 100));
        resources(doc, page.Resources);
        using (var canvas = page.GetCanvas()) canvas.WriteRaw(content);
        using var reopened = PdfDocument.Open(doc.ToArray());
        return PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 });
    }

    private static PdfDictionary RedToBlue() =>
        D(("FunctionType", new PdfNumber(2)), ("Domain", A(0, 1)), ("C0", A(1, 0, 0)), ("C1", A(0, 0, 1)), ("N", new PdfNumber(1)));

    private static readonly (byte, byte, byte) White = (255, 255, 255);

    // ------------------------------------------------------------------ shadings

    [Fact]
    public void AxialAndRadialShadings()
    {
        var page = Render("/Ax sh /Ra sh", (doc, res) => res[N("Shading")] = D(
            ("Ax", doc.AddObject(D(("ShadingType", new PdfNumber(2)), ("ColorSpace", N("DeviceRGB")), ("Coords", A(0, 0, 100, 0)),
                ("Function", RedToBlue()), ("Extend", new PdfArray(PdfBoolean.False, PdfBoolean.False))))),
            ("Ra", doc.AddObject(D(("ShadingType", new PdfNumber(3)), ("ColorSpace", N("DeviceRGB")), ("Coords", A(150, 50, 0, 150, 50, 40)),
                ("Function", RedToBlue()))))));
        Near((255, 0, 0), Pixel(page, 0, 50), 4);       // start of the axis
        Near((0, 0, 255), Pixel(page, 99, 50), 4);      // end of the axis
        Near((128, 0, 127), Pixel(page, 50, 50), 4);    // middle
        Near((255, 0, 0), Pixel(page, 150, 50), 8);     // center of the radial shading
        Near((128, 0, 127), Pixel(page, 170, 50), 8);   // half the radius
        Assert.Equal(White, Pixel(page, 150, 2));        // outside the circle (no extend)
        // the radial shading does not paint over the axial one beyond x 110: white between them
        Assert.Equal(White, Pixel(page, 105, 50));
    }

    [Fact]
    public void ExtendedAxialShadingFillsTheClip()
    {
        var page = Render("q 20 20 160 60 re W n /Ax sh Q", (doc, res) => res[N("Shading")] = D(
            ("Ax", doc.AddObject(D(("ShadingType", new PdfNumber(2)), ("ColorSpace", N("DeviceRGB")), ("Coords", A(80, 0, 120, 0)),
                ("Function", RedToBlue()), ("Extend", new PdfArray(PdfBoolean.True, PdfBoolean.True)))))));
        Near((255, 0, 0), Pixel(page, 30, 50));
        Near((0, 0, 255), Pixel(page, 170, 50));
        Assert.Equal(White, Pixel(page, 10, 50)); // outside the clip
    }

    [Fact]
    public void MeshShadings()
    {
        // type 4: one triangle with red, green and blue corners (8-bit flag, 16-bit coordinates, 8-bit components)
        var tri = new byte[]
        {
            0, 0x00, 0x00, 0x00, 0x00, 255, 0, 0,
            0, 0xFF, 0xFF, 0x00, 0x00, 0, 255, 0,
            0, 0x80, 0x00, 0xFF, 0xFF, 0, 0, 255,
        };
        // type 6: a square Coons patch with straight edges, colors by a function of t (0 left .. 1 right)
        var patch = new List<byte> { 0 };
        void P(int x, int y) => patch.AddRange(new[] { (byte)x, (byte)y });
        // p00 p01 p02 p03 p13 p23 p33 p32 p31 p30 p20 p10 on the square (100..200) x (0..100), 8-bit coordinates
        int[] xs = { 0, 0, 0, 0, 85, 170, 255, 255, 255, 255, 170, 85 }, ys = { 0, 85, 170, 255, 255, 255, 255, 170, 85, 0, 0, 0 };
        for (int k = 0; k < 12; k++) P(xs[k], ys[k]);
        patch.AddRange(new byte[] { 0, 0, 255, 255 }); // t at c00, c03, c33, c30
        var page = Render("/M4 sh /M6 sh", (doc, res) => res[N("Shading")] = D(
            ("M4", doc.AddObject(Bin(tri, ("ShadingType", new PdfNumber(4)), ("ColorSpace", N("DeviceRGB")), ("BitsPerCoordinate", new PdfNumber(16)),
                ("BitsPerComponent", new PdfNumber(8)), ("BitsPerFlag", new PdfNumber(8)), ("Decode", A(0, 100, 0, 100, 0, 1, 0, 1, 0, 1))))),
            ("M6", doc.AddObject(Bin(patch.ToArray(), ("ShadingType", new PdfNumber(6)), ("ColorSpace", N("DeviceRGB")), ("BitsPerCoordinate", new PdfNumber(8)),
                ("BitsPerComponent", new PdfNumber(8)), ("BitsPerFlag", new PdfNumber(8)), ("Decode", A(100, 200, 0, 100, 0, 1)), ("Function", RedToBlue()))))));
        var bottomLeft = Pixel(page, 3, 98);
        Assert.True(bottomLeft.R > 200 && bottomLeft.G < 40, $"red corner, got {bottomLeft}");
        var top = Pixel(page, 50, 6);
        Assert.True(top.B > 180, $"blue corner, got {top}");
        Assert.Equal(White, Pixel(page, 5, 10)); // outside the triangle
        // the patch: t grows from left (u = 0, red) to right (u = 1, blue)
        Near((255, 0, 0), Pixel(page, 101, 50), 10);
        Near((0, 0, 255), Pixel(page, 198, 50), 10);
        Near((128, 0, 127), Pixel(page, 150, 50), 10);
    }

    // ------------------------------------------------------------------ patterns

    [Fact]
    public void TilingPatterns()
    {
        var page = Render("/Pattern cs /Ch scn 0 0 100 100 re f /PatRGB cs 0 0.5 0 /Un scn 100 0 100 100 re f", (doc, res) =>
        {
            res[N("Pattern")] = D(
                ("Ch", doc.AddObject(S("1 0 0 rg 0 0 10 10 re f 0 0 1 rg 10 10 10 10 re f", ("PatternType", new PdfNumber(1)), ("PaintType", new PdfNumber(1)),
                    ("TilingType", new PdfNumber(1)), ("BBox", A(0, 0, 20, 20)), ("XStep", new PdfNumber(20)), ("YStep", new PdfNumber(20)), ("Resources", new PdfDictionary())))),
                ("Un", doc.AddObject(S("0 0 5 20 re f", ("PatternType", new PdfNumber(1)), ("PaintType", new PdfNumber(2)),
                    ("TilingType", new PdfNumber(1)), ("BBox", A(0, 0, 10, 20)), ("XStep", new PdfNumber(10)), ("YStep", new PdfNumber(20)), ("Resources", new PdfDictionary())))));
            res[N("ColorSpace")] = D(("PatRGB", new PdfArray(N("Pattern"), N("DeviceRGB"))));
        });
        // checker cells of 10 pt: red at (0..10, 0..10) and repeated every 20 pt, blue at (10..20, 10..20)
        Near((255, 0, 0), Pixel(page, 5, 100 - 5));
        Near((255, 0, 0), Pixel(page, 45, 100 - 65));
        Near((0, 0, 255), Pixel(page, 15, 100 - 15));
        Assert.Equal(White, Pixel(page, 15, 100 - 5));
        // uncolored pattern: stripes in the color given with scn (dark green), 5 pt wide every 10 pt
        Near((0, 128, 0), Pixel(page, 102, 50));
        Assert.Equal(White, Pixel(page, 107, 50));
        Near((0, 128, 0), Pixel(page, 192, 50));
    }

    [Fact]
    public void ShadingPatternsFillPaths()
    {
        var page = Render("/Pattern cs /SP scn 50 20 100 60 re f", (doc, res) => res[N("Pattern")] = D(
            ("SP", doc.AddObject(D(("PatternType", new PdfNumber(2)), ("Matrix", A(1, 0, 0, 1, 50, 0)),
                ("Shading", D(("ShadingType", new PdfNumber(2)), ("ColorSpace", N("DeviceRGB")), ("Coords", A(0, 0, 100, 0)),
                    ("Function", RedToBlue()), ("Extend", new PdfArray(PdfBoolean.True, PdfBoolean.True)))))))));
        Near((255, 0, 0), Pixel(page, 50, 50), 4);   // pattern space starts at x 50
        Near((0, 0, 255), Pixel(page, 149, 50), 4);
        Assert.Equal(White, Pixel(page, 40, 50));     // only the path is painted
    }

    // ------------------------------------------------------------------ transparency

    private static void GraphicsStates(PdfDocument doc, PdfDictionary res, params (string Name, PdfDictionary State)[] states)
    {
        var dict = new PdfDictionary();
        foreach (var (name, state) in states) dict[N(name)] = doc.AddObject(state);
        res[N("ExtGState")] = dict;
    }

    [Fact]
    public void BlendModes()
    {
        var page = Render(
            "1 1 0 rg 0 0 100 100 re f " +
            "q /Mul gs 0 1 1 rg 10 10 30 80 re f Q q /Scr gs 0 0 1 rg 50 10 30 80 re f Q " +
            "q /Diff gs 1 0 0 rg 120 10 30 80 re f Q",
            (doc, res) => GraphicsStates(doc, res, ("Mul", D(("BM", N("Multiply")))), ("Scr", D(("BM", N("Screen")))), ("Diff", D(("BM", new PdfArray(N("Unknown"), N("Difference")))))));
        Near((0, 255, 0), Pixel(page, 20, 50));    // cyan x yellow = green
        Near((255, 255, 255), Pixel(page, 60, 50)); // screen(yellow, blue) = white
        // on the empty page the backdrop is transparent: Difference leaves the source color (as in Acrobat)
        Near((255, 0, 0), Pixel(page, 130, 50));
    }

    [Fact]
    public void LuminositySoftMask()
    {
        var page = Render("q /Mask gs 1 0 0 rg 0 0 200 100 re f Q", (doc, res) =>
        {
            var group = S("/G sh", ("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", A(0, 0, 200, 100)),
                ("Group", D(("S", N("Transparency")), ("CS", N("DeviceGray")))),
                ("Resources", D(("Shading", D(("G", D(("ShadingType", new PdfNumber(2)), ("ColorSpace", N("DeviceGray")), ("Coords", A(0, 0, 200, 0)),
                    ("Function", D(("FunctionType", new PdfNumber(2)), ("Domain", A(0, 1)), ("C0", A(0)), ("C1", A(1)), ("N", new PdfNumber(1)))),
                    ("Extend", new PdfArray(PdfBoolean.True, PdfBoolean.True)))))))));
            GraphicsStates(doc, res, ("Mask", D(("SMask", D(("S", N("Luminosity")), ("G", doc.AddObject(group)))))));
        });
        Near((255, 254, 254), Pixel(page, 1, 50), 4);  // black in the mask: transparent
        Near((255, 128, 128), Pixel(page, 100, 50), 4); // gray: half
        Near((255, 1, 1), Pixel(page, 198, 50), 4);    // white: opaque
    }

    [Fact]
    public void GroupOpacityAndKnockout()
    {
        // overlapping squares at 50 %: individually the overlap gets darker, as a group it does not, and in a knockout
        // group the second square replaces the first one
        const string squares = "1 0 0 rg 0 0 40 40 re f 0 0 1 rg 20 20 40 40 re f";
        var page = Render("q /Half gs " + squares + " Q q /Half gs 1 0 0 1 70 0 cm /G Do Q q /Half gs 1 0 0 1 140 0 cm /K Do Q", (doc, res) =>
        {
            GraphicsStates(doc, res, ("Half", D(("ca", new PdfNumber(0.5)))));
            PdfStream Form(bool knockout) => S(squares, ("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", A(0, 0, 60, 60)),
                ("Group", D(("S", N("Transparency")), ("K", knockout ? PdfBoolean.True : PdfBoolean.False))), ("Resources", new PdfDictionary()));
            res[N("XObject")] = D(("G", doc.AddObject(Form(false))), ("K", doc.AddObject(Form(true))));
        });
        var single = Pixel(page, 30, 100 - 30);   // overlap, painted individually
        var grouped = Pixel(page, 100, 100 - 30); // overlap inside the group
        Near((128, 128, 255), Pixel(page, 70 + 50, 100 - 50)); // the blue square alone at 50 %
        Assert.True(single.R < 200 && single.R > 100, $"individual overlap mixes red and blue, got {single}");
        Near((128, 128, 255), grouped);             // the group's overlap is just blue at 50 %
        Near((128, 128, 255), Pixel(page, 170, 100 - 30));      // knockout: blue replaces red
        Near((255, 128, 128), Pixel(page, 140 + 10, 100 - 10)); // red where only red is
    }

    [Fact]
    public void KnockoutGroupElementsUseTheInitialBackdrop()
    {
        // in an isolated knockout group a nested group multiplies against the transparent initial backdrop, not
        // against the blue painted before it in the knockout group
        var page = Render("/Outer Do", (doc, res) =>
        {
            var inner = S("/M gs 1 0 0 rg 20 20 60 60 re f", ("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", A(0, 0, 200, 100)),
                ("Group", D(("S", N("Transparency")))), ("Resources", D(("ExtGState", D(("M", D(("BM", N("Multiply")))))))));
            var outer = S("0 0 1 rg 0 0 200 100 re f /Inner Do", ("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", A(0, 0, 200, 100)),
                ("Group", D(("S", N("Transparency")), ("K", PdfBoolean.True), ("I", PdfBoolean.True))), ("Resources", D(("XObject", D(("Inner", doc.AddObject(inner)))))));
            res[N("XObject")] = D(("Outer", doc.AddObject(outer)));
        });
        Near((255, 0, 0), Pixel(page, 50, 50));  // red, not red x blue = black
        Near((0, 0, 255), Pixel(page, 150, 50));
    }
}
