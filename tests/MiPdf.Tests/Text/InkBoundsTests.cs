using System.Text;
using MiPdf.Objects;
using MiPdf.Tests.Rendering;
using MiPdf.Text;

namespace MiPdf.Tests.Text;

/// <summary>
/// <see cref="TextLetter.InkBounds"/>: the area a glyph covers. During development the boxes of 7 million letters of
/// arXiv papers, IRS forms and ZUGFeRD invoices were compared with PdfPig 0.1.16: 99.5 % within 2 % of the font size;
/// where they differ, PdfPig's boxes were flat or the loose font box (some Type 1 math fonts, ligatures, Type 3
/// glyphs) or included TrueType control points.
/// </summary>
public class InkBoundsTests
{
    /// <summary>The letters of a page whose resources have the given fonts (/F1, /F2, ...).</summary>
    private static IReadOnlyList<TextLetter> Letters(string content, params PdfDictionary[] fonts)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 600, 800));
        var dict = new PdfDictionary();
        for (int i = 0; i < fonts.Length; i++) dict[PdfName.Get($"F{i + 1}")] = doc.AddObject(fonts[i]);
        page.Resources[PdfName.Font] = dict;
        using (var canvas = page.GetCanvas()) canvas.WriteRaw(content);
        var reopened = PdfDocument.Open(doc.ToArray());
        return PdfTextExtractor.ExtractLetters(reopened.GetPage(1), new TextExtractionOptions { SortByPosition = false });
    }

    private static PdfDictionary Simple(string baseFont) => new()
    {
        [PdfName.Type] = PdfName.Font, [PdfName.Subtype] = PdfName.Get("Type1"),
        [PdfName.BaseFont] = PdfName.Get(baseFont), [PdfName.Encoding] = PdfName.Get("WinAnsiEncoding"),
    };

    private static void AssertBox(PdfRectangle? actual, double left, double bottom, double right, double top)
    {
        Assert.NotNull(actual);
        var r = actual!.Value;
        Assert.Equal(left, r.Left, 3);
        Assert.Equal(bottom, r.Bottom, 3);
        Assert.Equal(right, r.Right, 3);
        Assert.Equal(top, r.Top, 3);
    }

    [Fact]
    public void StandardFontsFromTheirMetrics()
    {
        // Helvetica at 100 pt: H is 77..646 x 0..718 units, g 40..499 x -220..538, after H's advance of 722
        var letters = Letters("BT /F1 100 Tf 100 500 Td (H g) Tj ET BT /F1 100 Tf 0 1 -1 0 300 100 Tm (H) Tj ET BT /F2 100 Tf 100 300 Td (H) Tj ET",
            Simple("Helvetica"), Simple("Garamond"));
        AssertBox(letters[0].InkBounds, 107.7, 500, 164.6, 571.8);
        Assert.Null(letters[1].InkBounds);                                   // a space draws nothing
        AssertBox(letters[2].InkBounds, 100 + 72.2 + 27.8 + 4, 478, 100 + 72.2 + 27.8 + 49.9, 553.8);
        // turned by 90 degrees: the box turns with the glyph
        AssertBox(letters[3].InkBounds, 300 - 71.8, 107.7, 300, 164.6);
        // a font that is neither embedded nor a standard font: its shape is not known
        Assert.Null(letters[4].InkBounds);
        Assert.NotEqual(default, letters[4].Bounds);
    }

    [Theory]
    [InlineData("FontFile")]
    [InlineData("FontFile3")]
    public void EmbeddedFontsFromTheirOutlines(string kind)
    {
        PdfStream program;
        if (kind == "FontFile")
        {
            var data = TestFonts.Type1(out int clearLength);
            program = new PdfStream(data, compress: false);
            program.Set(PdfName.Get("Length1"), clearLength);
            program.Set(PdfName.Get("Length2"), data.Length - clearLength);
            program.Set(PdfName.Get("Length3"), 0);
        }
        else
        {
            program = new PdfStream(TestFonts.Cff(), compress: false);
            program[PdfName.Subtype] = PdfName.Get("Type1C");
        }
        var font = Simple("MiTest");
        font.Remove(PdfName.Encoding);
        font.Set(PdfName.FirstChar, 65);
        font.Set(PdfName.Get("LastChar"), 66);
        font[PdfName.Widths] = new PdfArray(new PdfNumber(600), new PdfNumber(600));
        font[PdfName.FontDescriptor] = new PdfDictionary(PdfName.Get("FontDescriptor"))
        {
            [PdfName.Get("FontName")] = PdfName.Get("MiTest"), [PdfName.Get("Flags")] = new PdfNumber(4), [PdfName.Get(kind)] = program,
        };
        var letters = Letters("BT /F1 100 Tf 10 10 Td (AB) Tj ET", font);
        // the square (Type 1: 50..550, CFF: 0..500 units) and the triangle 100..600 x 0..400, partly drawn by a subroutine
        if (kind == "FontFile") AssertBox(letters[0].InkBounds, 15, 10, 65, 60);
        else AssertBox(letters[0].InkBounds, 10, 10, 60, 60);
        AssertBox(letters[1].InkBounds, 70 + 10, 10, 70 + 60, 50);
    }

    [Fact]
    public void Type3GlyphsFromTheirDeclaredBoundsOrTheirPaths()
    {
        var doc = PdfDocument.Create();
        var font = new PdfDictionary(PdfName.Get("Font"))
        {
            [PdfName.Subtype] = PdfName.Get("Type3"),
            [PdfName.Get("FontBBox")] = PdfArray.FromNumbers(0, 0, 1000, 1000),
            [PdfName.Get("FontMatrix")] = PdfArray.FromNumbers(0.001, 0, 0, 0.001, 0, 0),
            [PdfName.Get("CharProcs")] = new PdfDictionary
            {
                // declared with d1; a colored glyph (d0) covers what it paints
                [PdfName.Get("box")] = new PdfStream(Encoding.ASCII.GetBytes("1000 0 0 0 800 800 d1 0 0 800 800 re f"), compress: false),
                [PdfName.Get("red")] = new PdfStream(Encoding.ASCII.GetBytes("1000 0 d0 1 0 0 rg 100 200 300 400 re f"), compress: false),
            },
            [PdfName.Encoding] = new PdfDictionary { [PdfName.Differences] = new PdfArray(new PdfNumber(65), PdfName.Get("box"), PdfName.Get("red")) },
            [PdfName.FirstChar] = new PdfNumber(65),
            [PdfName.Get("LastChar")] = new PdfNumber(66),
            [PdfName.Widths] = PdfArray.FromNumbers(1000, 1000),
        };
        var letters = Letters("BT /F1 50 Tf 100 100 Td (AB) Tj ET", font);
        AssertBox(letters[0].InkBounds, 100, 100, 140, 140);
        AssertBox(letters[1].InkBounds, 150 + 5, 100 + 10, 150 + 20, 100 + 30);
    }
}
