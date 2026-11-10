using System.Globalization;
using System.Text;
using MiPdf.Objects;
using MiPdf.PostScript;
using MiPdf.PostScript.Fonts;
using MiPdf.PostScript.Interpreter;
using MiPdf.Rendering;

namespace MiPdf.Tests.PostScript;

/// <summary>
/// Text and fonts (stage 3). A small Type 1 test font (a square for A, a triangle for B) is built here from
/// charstrings; the PDF fonts written for it are checked by rendering them with miPDFlib.Rendering, which reads Type 1
/// programs with its own parser. During development, text pages with the URW fonts of Ghostscript, Type 3 fonts, a Type
/// 42 font and Ghostscript's ps2write output were compared with Ghostscript rendering the PostScript.
/// </summary>
public class FontTests
{
    private static PdfDocument Convert(string program, out PsJobResult result) =>
        PsConverter.Convert(Encoding.Latin1.GetBytes(program), out result);

    private static (PdfDocument Doc, RenderedPage Page) RenderDoc(string program)
    {
        var doc = Convert(program, out var result);
        Assert.Null(result.ErrorName);
        var reopened = PdfDocument.Open(doc.ToArray());
        return (reopened, PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 }));
    }

    private static (int R, int G, int B) At(RenderedPage page, double x, double y)
    {
        int i = ((page.Height - 1 - (int)y) * page.Width + (int)x) * 4;
        return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
    }

    // ------------------------------------------------------------------ a Type 1 test font

    private static byte[] Charstring(params object[] items)
    {
        var bytes = new List<byte>();
        foreach (var item in items)
        {
            if (item is string command)
            {
                bytes.Add(command switch { "hsbw" => 13, "rmoveto" => 21, "rlineto" => 5, "closepath" => 9, "endchar" => 14, _ => throw new ArgumentException(command) });
                continue;
            }
            int v = (int)item;
            if (v is >= -107 and <= 107) bytes.Add((byte)(v + 139));
            else if (v is >= 108 and <= 1131)
            {
                bytes.Add((byte)((v - 108) / 256 + 247));
                bytes.Add((byte)((v - 108) % 256));
            }
            else if (v is >= -1131 and <= -108)
            {
                bytes.Add((byte)((-v - 108) / 256 + 251));
                bytes.Add((byte)((-v - 108) % 256));
            }
            else
            {
                bytes.Add(255);
                bytes.AddRange(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
            }
        }
        return Type1Charstrings.Encrypt(bytes.ToArray(), Type1Charstrings.CharstringKey, new byte[] { 1, 2, 3, 4 });
    }

    private static readonly byte[] Notdef = Charstring(0, 500, "hsbw", "endchar");
    private static readonly byte[] Square = Charstring(50, 600, "hsbw", 0, 0, "rmoveto", 500, 0, "rlineto", 0, 500, "rlineto", -500, 0, "rlineto", "closepath", "endchar");
    private static readonly byte[] Triangle = Charstring(50, 700, "hsbw", 0, 0, "rmoveto", 600, 0, "rlineto", -300, 600, "rlineto", "closepath", "endchar");

    private static string Hex(byte[] bytes) => "<" + System.Convert.ToHexString(bytes) + ">";

    /// <summary>The test font as font dictionary in PostScript.</summary>
    private static string TestFont(string name = "TestFont") =>
        $"/{name} 10 dict begin /FontType 1 def /FontName /{name} def /FontMatrix [0.001 0 0 0.001 0 0] def " +
        "/FontBBox [0 0 1000 1000] def /PaintType 0 def /Encoding StandardEncoding def " +
        "/Private 4 dict dup begin /lenIV 4 def /Subrs 0 array def /BlueValues [-10 0 500 510] def end def " +
        $"/CharStrings 3 dict dup begin /.notdef {Hex(Notdef)} def /A {Hex(Square)} def /B {Hex(Triangle)} def end def " +
        $"currentdict end definefont pop\n";

    /// <summary>The test font in the usual format of a font program: clear text, then the private part with eexec.</summary>
    private static string TestFontEexec()
    {
        var priv = new StringBuilder();
        priv.Append("dup /Private 8 dict dup begin /RD {string currentfile exch readstring pop} executeonly def ");
        priv.Append("/ND {noaccess def} executeonly def /NP {noaccess put} executeonly def /lenIV 4 def /Subrs 0 array ND ");
        priv.Append("2 index /CharStrings 3 dict dup begin\n");
        var plain = new MemoryStream();
        plain.Write(Encoding.Latin1.GetBytes(priv.ToString()));
        foreach (var (name, cs) in new[] { (".notdef", Notdef), ("A", Square), ("B", Triangle) })
        {
            plain.Write(Encoding.Latin1.GetBytes($"/{name} {cs.Length} RD "));
            plain.Write(cs);
            plain.Write(Encoding.Latin1.GetBytes(" ND\n"));
        }
        plain.Write(Encoding.Latin1.GetBytes("end end readonly put noaccess put dup /FontName get exch definefont pop mark currentfile closefile\n"));
        var encrypted = Type1Charstrings.Encrypt(plain.ToArray(), Type1Charstrings.EexecKey, new byte[] { 5, 6, 7, 8 });
        string hex = System.Convert.ToHexString(encrypted);
        var lines = Enumerable.Range(0, (hex.Length + 63) / 64).Select(i => hex.Substring(i * 64, Math.Min(64, hex.Length - i * 64)));
        return "%!PS-AdobeFont-1.0: EexecFont\n11 dict begin /FontName /EexecFont def /FontType 1 def /PaintType 0 def " +
               "/FontMatrix [0.001 0 0 0.001 0 0] readonly def /Encoding StandardEncoding def /FontBBox {0 0 1000 1000} readonly def " +
               "currentdict end\ncurrentfile eexec\n" + string.Join("\n", lines) + "\n" +
               string.Concat(Enumerable.Repeat(new string('0', 64) + "\n", 8)) + "cleartomark\n";
    }

    private const string Page = "<< /PageSize [300 200] >> setpagedevice\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Type1FontsAreEmbeddedAsSubsets(bool eexec)
    {
        string font = eexec ? TestFontEexec() : TestFont();
        string name = eexec ? "EexecFont" : "TestFont";
        var (doc, page) = RenderDoc(Page + font + $"/{name} findfont 100 scalefont setfont 10 50 moveto (AB) show currentpoint = = showpage");
        using (doc)
        {
            // a square of 50 units from x = 15, a triangle after the advance of 60 units
            Assert.Equal((0, 0, 0), At(page, 40, 75));
            Assert.Equal((255, 255, 255), At(page, 68, 75));
            Assert.Equal((0, 0, 0), At(page, 105, 60));
            var fontDict = doc.GetPage(1).Resources.GetDictionary(PdfName.Font)!.Select(kv => kv.Value.Resolve()).OfType<PdfDictionary>().Single();
            Assert.Equal("Type1", fontDict.GetName(PdfName.Subtype)!.Value);
            Assert.Matches("^[A-Z]{6}\\+" + name + "$", fontDict.GetName(PdfName.Get("BaseFont"))!.Value);
            var widths = fontDict.GetArray(PdfName.Get("Widths"))!;
            Assert.Equal(new[] { 600.0, 700.0 }, new[] { widths.GetDouble(0), widths.GetDouble(1) });
            var file = (PdfStream)fontDict.GetDictionary(PdfName.Get("FontDescriptor"))!.Get(PdfName.Get("FontFile"))!.Resolve()!;
            Assert.True(file.GetInt(PdfName.Get("Length2")) > 0);
            Assert.Equal("AB", doc.GetPage(1).ExtractText().Trim());
        }
    }

    [Fact]
    public void ShowOperatorsPositionTheGlyphs()
    {
        using var doc = Convert(Page + TestFont() + "/TestFont findfont 10 scalefont setfont " +
                                "0 0 moveto (AB) show currentpoint = = " +
                                "0 0 moveto 1 2 (AB) ashow currentpoint = = " +
                                "0 0 moveto 5 0 66 (ABAB) widthshow currentpoint = = " +
                                "0 0 moveto (AB) [20 30] xshow currentpoint = = " +
                                "0 0 moveto { pop pop 100 0 rmoveto } (AB) kshow currentpoint = = " +
                                "(AB) stringwidth = = " +
                                "[ { 3 array astore } (AB) cshow ] length =", out var result);
        Assert.Null(result.ErrorName);
        Assert.Equal("0.0\n13.0\n4.0\n15.0\n0.0\n36.0\n0.0\n50.0\n0.0\n113.0\n0.0\n13.0\n2\n", result.Output);
    }

    [Fact]
    public void CharpathGivesTheOutlines()
    {
        var (doc, page) = RenderDoc(Page + TestFont() + "/TestFont findfont 100 scalefont setfont " +
                                    "newpath 10 50 moveto (A) false charpath 0 0 1 setrgbcolor fill " +
                                    "newpath 150 50 moveto (B) true charpath clip 1 0 0 setrgbcolor 0 0 300 200 rectfill showpage");
        using (doc)
        {
            Assert.Equal((0, 0, 255), At(page, 40, 75));
            Assert.Equal((255, 0, 0), At(page, 190, 60));
            Assert.Equal((255, 255, 255), At(page, 160, 100)); // outside the triangle
        }
    }

    [Fact]
    public void StandardFontsAreReferencedWithTheirMetrics()
    {
        using var doc = Convert(Page + "/Helvetica findfont 10 scalefont setfont 10 10 moveto (Hello) show (Hello) stringwidth pop = " +
                                "/Palatino-Bold findfont 10 scalefont setfont (x) show " +
                                "/Helvetica findfont dup length dict begin { 1 index /FID ne { def } { pop pop } ifelse } forall " +
                                "/Encoding ISOLatin1Encoding def currentdict end /Helvetica-L1 exch definefont 10 scalefont setfont (\\374) show showpage", out var result);
        Assert.Null(result.ErrorName);
        // H e l l o in Helvetica: 722 + 556 + 222 + 222 + 556 = 2278 / 1000 * 10
        Assert.Equal("22.78\n", result.Output);
        using var reopened = PdfDocument.Open(doc.ToArray());
        var fonts = reopened.GetPage(1).Resources.GetDictionary(PdfName.Font)!.Select(kv => kv.Value.Resolve()).OfType<PdfDictionary>().ToList();
        var baseFonts = fonts.Select(f => f.GetName(PdfName.Get("BaseFont"))!.Value).ToList();
        Assert.Contains("Helvetica", baseFonts);
        Assert.Contains("Times-Bold", baseFonts); // the substitute of Palatino-Bold
        Assert.Contains("Hello", reopened.GetPage(1).ExtractText());
        Assert.Contains("ü", reopened.GetPage(1).ExtractText()); // re-encoded to ISOLatin1Encoding
    }

    [Fact]
    public void Type3FontsBecomePdfType3Fonts()
    {
        const string type3 =
            "/Shapes 8 dict begin /FontType 3 def /FontMatrix [0.001 0 0 0.001 0 0] def /FontBBox [0 0 1000 1000] def " +
            "/Encoding 256 array def 0 1 255 { Encoding exch /.notdef put } for Encoding 65 /square put " +
            "/BuildGlyph { 1000 0 0 0 1000 1000 setcachedevice pop pop 100 100 800 800 rectfill } def " +
            "currentdict end /Shapes exch definefont pop " +
            "/Colored 8 dict begin /FontType 3 def /FontMatrix [0.01 0 0 0.01 0 0] def /FontBBox [0 0 100 100] def " +
            "/Encoding 256 array def 0 1 255 { Encoding exch /.notdef put } for " +
            "/BuildChar { pop pop 100 0 setcharwidth 0 0.6 0 setrgbcolor 10 10 80 80 rectfill } def " +
            "currentdict end /Colored exch definefont pop ";
        var (doc, page) = RenderDoc(Page + type3 + "/Shapes findfont 50 scalefont setfont 0 0 1 setrgbcolor 10 50 moveto (AA) show " +
                                    "/Colored findfont 50 scalefont setfont 150 50 moveto (12) show showpage");
        using (doc)
        {
            Assert.Equal((0, 0, 255), At(page, 30, 70)); // uncolored glyph in the text color
            Assert.Equal((0, 0, 255), At(page, 80, 70));
            Assert.Equal((0, 153, 0), At(page, 175, 75)); // colored glyph
            var fonts = doc.GetPage(1).Resources.GetDictionary(PdfName.Font)!.Select(kv => kv.Value.Resolve()).OfType<PdfDictionary>().ToList();
            Assert.All(fonts, f => Assert.Equal("Type3", f.GetName(PdfName.Subtype)!.Value));
            Assert.Contains(fonts, f => f.GetDictionary(PdfName.Get("CharProcs"))!.Count == 1);
        }
    }

    [Fact]
    public void CompositeFontsSelectTheirDescendants()
    {
        // FMapType 2: a font number byte and a code byte
        var (doc, page) = RenderDoc(Page + TestFont() +
                                    "/Comp << /FontType 0 /FMapType 2 /FontMatrix [1 0 0 1 0 0] /Encoding [0 1] " +
                                    "/FDepVector [/TestFont findfont /Helvetica findfont] >> definefont pop " +
                                    "/Comp findfont 50 scalefont setfont 10 50 moveto <0041 0142 0042> show currentpoint pop = showpage");
        using (doc)
        {
            Assert.Equal((0, 0, 0), At(page, 25, 60)); // A of the test font: a square
            var fonts = doc.GetPage(1).Resources.GetDictionary(PdfName.Font)!.Select(kv => kv.Value.Resolve()).OfType<PdfDictionary>().ToList();
            Assert.Equal(2, fonts.Count);
        }
    }

    /// <summary>A CIDFontType 0 font with the charstrings of the test font in a GlyphDirectory (CID 1: square, 2: triangle).</summary>
    private static string CidFont0() =>
        "/CIDInit /ProcSet findresource begin 20 dict begin /CIDFontName /TestCID def /CIDFontType 0 def /FontType 9 def " +
        "/FontMatrix [1 0 0 1 0 0] def /FontBBox [0 0 1000 1000] def /CIDCount 3 def /FDBytes 1 def /GDBytes 1 def " +
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def " +
        "/FDArray [ << /FontType 1 /FontMatrix [0.001 0 0 0.001 0 0] /Private << /lenIV 4 >> >> ] def " +
        $"/GlyphDirectory 3 dict dup begin 0 <00{System.Convert.ToHexString(Notdef)}> def 1 <00{System.Convert.ToHexString(Square)}> def " +
        $"2 <00{System.Convert.ToHexString(Triangle)}> def end def CIDFontName currentdict /CIDFont defineresource pop end end\n";

    [Fact]
    public void CidKeyedFontsWorkWithCMaps()
    {
        // Identity-H: two-byte CIDs; a CMap defined in PostScript: one-byte codes mapped to CIDs
        const string cmap =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /TestMap def /CMapType 1 def " +
            "1 begincodespacerange <00> <FF> endcodespacerange 1 begincidrange <61> <62> 1 endcidrange endcmap " +
            "CMapName currentdict /CMap defineresource pop end end\n";
        var (doc, page) = RenderDoc(Page + CidFont0() + cmap +
                                    "/TestH /Identity-H [/TestCID] composefont 50 scalefont setfont 10 100 moveto <00010002> show currentpoint pop = " +
                                    "/TestM /TestMap [/TestCID] composefont 50 scalefont setfont 10 30 moveto (ba) show " +
                                    "newpath 150 30 moveto (a) false charpath 1 0 0 setrgbcolor fill showpage");
        using (doc)
        {
            Assert.Equal((0, 0, 0), At(page, 25, 110));  // CID 1: square
            Assert.Equal((0, 0, 0), At(page, 55, 110));  // CID 2: triangle after the advance of 30
            Assert.Equal((0, 0, 0), At(page, 25, 35));   // b = CID 2: triangle
            Assert.Equal((255, 0, 0), At(page, 165, 40)); // charpath of a = CID 1
            // the glyphs become a Type 1 font of the FDArray entry
            var fonts = doc.GetPage(1).Resources.GetDictionary(PdfName.Font)!.Select(kv => kv.Value.Resolve()).OfType<PdfDictionary>().ToList();
            Assert.All(fonts, f => Assert.Equal("Type1", f.GetName(PdfName.Subtype)!.Value));
        }
    }

    [Fact]
    public void CidFontType1UsesGlyphProcedures()
    {
        var (doc, page) = RenderDoc(Page +
            "/CIDInit /ProcSet findresource begin 20 dict begin /CIDFontName /Procs def /CIDFontType 1 def /FontType 10 def " +
            "/FontMatrix [0.001 0 0 0.001 0 0] def /FontBBox [0 0 1000 1000] def /CIDCount 10 def " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def " +
            "/BuildGlyph { exch pop 800 0 0 0 800 800 setcachedevice 100 mul 100 add 0 0 700 4 -1 roll rectfill } def " +
            "CIDFontName currentdict /CIDFont defineresource pop end end " +
            "/P /Identity-H [/Procs] composefont 100 scalefont setfont 10 50 moveto <00010005> show showpage");
        using (doc)
        {
            Assert.Equal((0, 0, 0), At(page, 20, 60));    // CID 1: a bar of 200 units height
            Assert.Equal((255, 255, 255), At(page, 20, 80));
            Assert.Equal((0, 0, 0), At(page, 100, 100));  // CID 5: 600 units
        }
    }

    [Fact]
    public void Type42FontsBecomeType0Fonts()
    {
        string? path = TestHelpers.FindSystemFont();
        if (path == null) return;
        var data = File.ReadAllBytes(path);
        var font = new MiPdf.Fonts.TrueType.TrueTypeFont(data);
        // sfnts in strings of at most 60000 bytes (a TrueType font split anywhere works for glyf access here)
        var strings = Enumerable.Range(0, (data.Length + 59999) / 60000)
            .Select(i => "<" + System.Convert.ToHexString(data, i * 60000, Math.Min(60000, data.Length - i * 60000)) + ">");
        var charStrings = new StringBuilder();
        foreach (char c in "Typ42")
            charStrings.Append(CultureInfo.InvariantCulture, $"/{(c is >= '0' and <= '9' ? new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" }[c - '0'] : c.ToString())} {font.GetGlyphId(c)} def ");
        string program = Page + "/TT 12 dict begin /FontType 42 def /FontMatrix [1 0 0 1 0 0] def /FontBBox [0 0 1 1] def " +
                         "/Encoding StandardEncoding def /sfnts [" + string.Join("\n", strings) + "] def " +
                         $"/CharStrings 8 dict dup begin /.notdef 0 def {charStrings}end def currentdict end /TT exch definefont pop " +
                         "/TT findfont 40 scalefont setfont 10 50 moveto (Typ42) show showpage";
        var (doc, page) = RenderDoc(program);
        using (doc)
        {
            var fontDict = doc.GetPage(1).Resources.GetDictionary(PdfName.Font)!.Select(kv => kv.Value.Resolve()).OfType<PdfDictionary>().Single();
            Assert.Equal("Type0", fontDict.GetName(PdfName.Subtype)!.Value);
            Assert.Equal("Typ42", doc.GetPage(1).ExtractText().Trim());
            // the T is drawn
            bool ink = Enumerable.Range(10, 20).Any(x => At(page, x, 70) != (255, 255, 255));
            Assert.True(ink);
        }
    }
}
