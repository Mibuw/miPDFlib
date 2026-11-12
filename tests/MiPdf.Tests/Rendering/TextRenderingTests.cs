using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.Rendering;
using MiPdf.Fonts.Programs;
using MiPdf.Rendering.Fonts;
using MiPdf.Text;

namespace MiPdf.Tests.Rendering;

public class TextRenderingTests
{
    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(PathGeometry path)
    {
        var points = path.Segments.Where(s => s.Kind != PathGeometry.Kind.Close).SelectMany(s => s.Kind == PathGeometry.Kind.Curve
            ? new[] { (s.X1, s.Y1), (s.X2, s.Y2), (s.X3, s.Y3) }
            : new[] { (s.X1, s.Y1) }).ToList();
        Assert.NotEmpty(points);
        return (points.Min(p => p.Item1), points.Min(p => p.Item2), points.Max(p => p.Item1), points.Max(p => p.Item2));
    }

    [Fact]
    public void NameTablesAreComplete()
    {
        Assert.Equal(".notdef", MacGlyphNames.Get(0));
        Assert.Equal("dcroat", MacGlyphNames.Get(257));
        Assert.Null(MacGlyphNames.Get(258));
        Assert.Equal(391, CffStandardStrings.Count);
        Assert.Equal("Semibold", CffStandardStrings.Get(390));
        Assert.Null(CffStandardStrings.Get(391));
        Assert.Equal("A", CffStandardStrings.StandardEncoding(65));
        Assert.Equal("germandbls", CffStandardStrings.StandardEncoding(0xFB));
    }

    [Fact]
    public void Type1FontProgramsAreParsed()
    {
        var font = new Type1Font(TestFonts.Type1(out int clearLength), clearLength);
        Assert.Equal("A", font.NameForCode(65));
        Assert.True(font.HasGlyph("B"));
        Assert.Equal((50, 0, 550, 500), Bounds(font.GetOutline("A")!));
        Assert.Equal((100, 0, 600, 400), Bounds(font.GetOutline("B")!)); // partly drawn by Subrs 0
        // seac: base glyph A and accent B raised by 600 units
        Assert.Equal((50, 0, 600, 1000), Bounds(font.GetOutline("C")!));
        Assert.Null(font.GetOutline("missing"));
    }

    [Fact]
    public void CffFontProgramsAreParsed()
    {
        var font = new CffFont(TestFonts.Cff());
        Assert.False(font.IsCidKeyed);
        Assert.Equal(3, font.GlyphCount);
        Assert.Equal(1, font.GlyphByName("square"));
        Assert.Equal(2, font.GlyphForCode(66));
        Assert.Equal((0, 0, 500, 500), Bounds(font.GetOutline(1)!));
        Assert.Equal((100, 0, 600, 400), Bounds(font.GetOutline(2)!)); // partly drawn by a global subroutine
    }

    private static (byte R, byte G, byte B) Pixel(RenderedPage page, int x, int y)
    {
        int i = (y * page.Width + x) * 4;
        return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
    }

    /// <summary>Renders a 200x100 pt page at 72 dpi whose resources have the font /F1 (and /F3, a Type 3 font).</summary>
    private static RenderedPage RenderText(PdfDictionary? f1, string content)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 200, 100));
        var fonts = new PdfDictionary();
        if (f1 != null) fonts[PdfName.Get("F1")] = doc.AddObject(f1);
        fonts[PdfName.Get("F3")] = doc.AddObject(Type3Font(doc));
        page.Resources[PdfName.Font] = fonts;
        using (var canvas = page.GetCanvas()) canvas.WriteRaw(content);
        using var reopened = PdfDocument.Open(doc.ToArray());
        return PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 });
    }

    private static PdfDictionary EmbeddedFont(string fileKey, PdfStream program)
    {
        var descriptor = new PdfDictionary(PdfName.Get("FontDescriptor"));
        descriptor[PdfName.Get("FontName")] = PdfName.Get("MiTest");
        descriptor.Set(PdfName.Get("Flags"), 4);
        descriptor[PdfName.Get(fileKey)] = program;
        var font = new PdfDictionary(PdfName.Get("Font"));
        font[PdfName.Subtype] = PdfName.Get("Type1");
        font[PdfName.BaseFont] = PdfName.Get("MiTest");
        font.Set(PdfName.FirstChar, 65);
        font.Set(PdfName.Get("LastChar"), 67);
        font[PdfName.Widths] = new PdfArray(new PdfNumber(600), new PdfNumber(600), new PdfNumber(600));
        font[PdfName.FontDescriptor] = descriptor;
        return font;
    }

    private static PdfDictionary Type1Font()
    {
        var data = TestFonts.Type1(out int clearLength);
        var program = new PdfStream(data, compress: false);
        program.Set(PdfName.Get("Length1"), clearLength);
        program.Set(PdfName.Get("Length2"), data.Length - clearLength);
        program.Set(PdfName.Get("Length3"), 0);
        return EmbeddedFont("FontFile", program);
    }

    private static PdfDictionary CffFontDict()
    {
        var program = new PdfStream(TestFonts.Cff(), compress: false);
        program[PdfName.Subtype] = PdfName.Get("Type1C");
        return EmbeddedFont("FontFile3", program);
    }

    private static PdfDictionary Type3Font(PdfDocument doc)
    {
        // a colored glyph (d0, its own red) and an uncolored one (d1, painted in the fill color)
        var procs = new PdfDictionary
        {
            [PdfName.Get("box")] = doc.AddObject(new PdfStream(Encoding.ASCII.GetBytes("1000 0 0 0 800 800 d1 0 0 800 800 re f"), compress: false)),
            [PdfName.Get("red")] = doc.AddObject(new PdfStream(Encoding.ASCII.GetBytes("1000 0 d0 1 0 0 rg 0 0 800 800 re f"), compress: false)),
        };
        var font = new PdfDictionary(PdfName.Get("Font"));
        font[PdfName.Subtype] = PdfName.Get("Type3");
        font[PdfName.Get("FontBBox")] = new PdfArray(new PdfNumber(0), new PdfNumber(0), new PdfNumber(800), new PdfNumber(800));
        font[PdfName.Get("FontMatrix")] = new PdfArray(new PdfNumber(0.001), new PdfNumber(0), new PdfNumber(0), new PdfNumber(0.001), new PdfNumber(0), new PdfNumber(0));
        font[PdfName.Get("CharProcs")] = procs;
        font[PdfName.Encoding] = new PdfDictionary { [PdfName.Differences] = new PdfArray(new PdfNumber(65), PdfName.Get("box"), PdfName.Get("red")) };
        font.Set(PdfName.FirstChar, 65);
        font.Set(PdfName.Get("LastChar"), 66);
        font[PdfName.Widths] = new PdfArray(new PdfNumber(1000), new PdfNumber(1000));
        return font;
    }

    private static readonly (byte, byte, byte) White = (255, 255, 255), Black = (0, 0, 0);

    [Theory]
    [InlineData("Type1")]
    [InlineData("CFF")]
    public void EmbeddedFontsAreDrawnAndAdvanced(string kind)
    {
        // 100 pt font: the square glyph spans 50 pt; the triangle starts 60 pt (its width) further right
        var page = RenderText(kind == "Type1" ? Type1Font() : CffFontDict(), "BT /F1 100 Tf 10 10 Td (AB) Tj ET");
        int squareLeft = kind == "Type1" ? 15 : 10;
        Assert.Equal(Black, Pixel(page, squareLeft + 25, 100 - 35));  // inside the square
        Assert.Equal(White, Pixel(page, squareLeft - 3, 100 - 35));   // left of it
        Assert.Equal(Black, Pixel(page, 105, 100 - 15));              // triangle near its base (x 80..130)
        Assert.Equal(White, Pixel(page, 75, 100 - 15));               // gap between the glyphs
        Assert.Equal(White, Pixel(page, 85, 100 - 45));               // beside the triangle's tip
    }

    [Fact]
    public void TextStateAndRenderingModes()
    {
        var font = Type1Font();
        // invisible text (mode 3) draws nothing
        Assert.DoesNotContain(RenderText(font, "BT 3 Tr /F1 100 Tf 10 10 Td (A) Tj ET").Rgba, b => b < 250);
        // mode 7 adds the glyph to the clip: the following rectangle is only painted inside the square (x 15..65)
        var clipped = RenderText(font, "q BT 7 Tr /F1 100 Tf 10 10 Td (A) Tj ET 1 0 0 rg 0 0 200 100 re f Q");
        Assert.Equal(((byte)255, (byte)0, (byte)0), Pixel(clipped, 40, 100 - 35));
        Assert.Equal(White, Pixel(clipped, 100, 100 - 35));
        // character spacing and horizontal scaling move the second glyph: (600/1000*100 + 20) * 0.5 = 40 pt
        var spaced = RenderText(font, "BT /F1 100 Tf 50 Tz 20 Tc 10 10 Td (AA) Tj ET");
        Assert.Equal(Black, Pixel(spaced, 10 + 40 + 15, 100 - 35));  // second square: x 52.5..77.5
        Assert.Equal(White, Pixel(spaced, 10 + 32, 100 - 35));       // the first one ends at 37.5
        // TJ adjustments move by thousandths of the font size
        var kerned = RenderText(font, "BT /F1 100 Tf 10 10 Td [(A) -400 (A)] TJ ET");
        Assert.Equal(White, Pixel(kerned, 10 + 60 + 5, 100 - 35));   // second square starts at 10 + 100 + 5
        Assert.Equal(Black, Pixel(kerned, 10 + 100 + 15, 100 - 35));
    }

    [Fact]
    public void Type3GlyphsUseTheirProcedures()
    {
        var page = RenderText(null, "BT 0 0 1 rg /F3 50 Tf 10 10 Td (AB) Tj ET");
        Assert.Equal(((byte)0, (byte)0, (byte)255), Pixel(page, 25, 100 - 25)); // d1 glyph: fill color
        Assert.Equal(((byte)255, (byte)0, (byte)0), Pixel(page, 75, 100 - 25)); // d0 glyph: own color, 50 pt further
        Assert.Equal(White, Pixel(page, 55, 100 - 25));                          // gap (glyphs are 40 pt wide)
    }

    [Fact]
    public void StandardFontsAreSubstitutedWhenAvailable()
    {
        // non-embedded standard 14 fonts are drawn with an installed substitute (if the system has none, nothing)
        if (SystemFonts.Find("Helvetica", null) == null) return;
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 200, 100));
        using (var canvas = page.GetCanvas())
            canvas.BeginText().SetFont(PdfStandardFont.Helvetica, 40).SetTextPosition(10, 30).ShowText("Hello").EndText();
        using var reopened = PdfDocument.Open(doc.ToArray());
        var rendered = PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 });
        Assert.Contains(rendered.Rgba, b => b < 64);
    }

    [Fact]
    public void ZapfDingbatsCodesMapToUnicode()
    {
        Assert.Equal(0x2714, FontDecoder.ZapfDingbatsUnicode(0x34)); // heavy check mark
        Assert.Equal(0x260E, FontDecoder.ZapfDingbatsUnicode(0x25)); // telephone
        Assert.Equal(0x25CF, FontDecoder.ZapfDingbatsUnicode(0x6C)); // black circle
        Assert.Equal(0x2665, FontDecoder.ZapfDingbatsUnicode(0xAA)); // heart
        Assert.Equal(0x2460, FontDecoder.ZapfDingbatsUnicode(0xAC)); // circled digit one
        Assert.Equal(0x2794, FontDecoder.ZapfDingbatsUnicode(0xD4));
        Assert.Equal(0x2192, FontDecoder.ZapfDingbatsUnicode(0xD5));
        Assert.Equal(0x27BE, FontDecoder.ZapfDingbatsUnicode(0xFE));
        Assert.Equal(0, FontDecoder.ZapfDingbatsUnicode(0xF0));

        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 200, 100));
        using (var canvas = page.GetCanvas())
            canvas.BeginText().SetFont(PdfStandardFont.ZapfDingbats, 12).SetTextPosition(10, 50).ShowText("3%(").EndText();
        using var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Contains("✓☎✈", reopened.GetPage(1).ExtractText());
    }

    [Fact]
    public void CropBoxOffsetAndPixelSize()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 200, 100));
        page.Dictionary[PdfName.CropBox] = new PdfArray(new PdfNumber(50), new PdfNumber(20), new PdfNumber(150), new PdfNumber(100));
        using (var canvas = page.GetCanvas()) canvas.SetFillColor(PdfColor.Black).Rectangle(60, 30, 10, 10).Fill();
        using var reopened = PdfDocument.Open(doc.ToArray());
        var rendered = PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 });
        Assert.Equal((100, 80), (rendered.Width, rendered.Height));
        Assert.Equal(Black, Pixel(rendered, 15, 80 - 15)); // (65, 35) in page space
        Assert.Equal(White, Pixel(rendered, 5, 80 - 15));

        // A4 at 100 dpi: 826.8 x 1169.3 pixels are rounded like Ghostscript and pdftoppm do
        var a4 = PdfDocument.Create();
        a4.AddPage(new PdfRectangle(0, 0, 595.28, 841.89));
        using var a4Reopened = PdfDocument.Open(a4.ToArray());
        var a4Page = PdfRenderer.Render(a4Reopened.GetPage(1), new PdfRenderOptions { Dpi = 100 });
        Assert.Equal((827, 1169), (a4Page.Width, a4Page.Height));
    }
}
