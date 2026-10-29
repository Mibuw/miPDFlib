using System.Text;
using MiPdf.Fonts;
using MiPdf.Fonts.TrueType;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Tests.Content;

public class FontTests
{
    [Fact]
    public void StandardFontWidthsMatchAfm()
    {
        // H 722 + e 556 + l 222 + l 222 + o 556 = 2278 / 1000 em
        Assert.Equal(22.78, PdfStandardFont.Helvetica.GetTextWidth("Hello", 10), 6);
        Assert.Equal(60, PdfStandardFont.Courier.GetTextWidth("abcdefghij", 10), 6);
        Assert.Equal(556, PdfStandardFont.Helvetica.GetCharWidth('€'));
        Assert.True(PdfStandardFont.Helvetica.CanDisplay('ü'));
        Assert.False(PdfStandardFont.Helvetica.CanDisplay('ł'));
    }

    [Fact]
    public void StandardFontTextIsWinAnsiEncoded()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        using (var c = page.GetCanvas()) c.DrawText("Grüße (100 €)", PdfStandardFont.Helvetica, 12, 72, 700);
        var content = Encoding.Latin1.GetString(page.GetContentBytes());
        Assert.Contains(@"(Gr\374\337e \(100 \200\)) Tj", content);

        var reopened = PdfDocument.Open(doc.ToArray());
        var font = reopened.GetPage(1).Resources.GetDictionary(PdfName.Font)!.GetDictionary("F1")!;
        Assert.Equal("Helvetica", font.GetName(PdfName.BaseFont)!.Value);
        Assert.Equal(PdfName.WinAnsiEncoding, font.GetName(PdfName.Encoding));
        Assert.Equal(224, font.GetArray(PdfName.Widths)!.Count);
    }

    [Fact]
    public void EmbedsSubsetTrueTypeFontWithToUnicode()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null) return; // no system font available on this platform
        var font = PdfTrueTypeFont.FromFile(path);
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("Zażółć ĄĘ", font, 12, 72, 700);
        var bytes = doc.ToArray();
        var reopened = PdfDocument.Open(bytes);
        var type0 = reopened.GetPage(1).Resources.GetDictionary(PdfName.Font)!.GetDictionary("F1")!;
        Assert.Equal(PdfName.Type0, type0.GetName(PdfName.Subtype));
        Assert.Equal(PdfName.IdentityH, type0.GetName(PdfName.Encoding));
        var cid = type0.GetArray(PdfName.DescendantFonts)!.GetDictionary(0)!;
        Assert.Equal(PdfName.CIDFontType2, cid.GetName(PdfName.Subtype));
        Assert.Matches("^[A-Z]{6}\\+", cid.GetName(PdfName.BaseFont)!.Value);

        var toUnicode = Encoding.ASCII.GetString(type0.GetStream(PdfName.ToUnicode)!.GetBytes());
        Assert.Contains("<017C>", toUnicode); // ż
        Assert.Contains("<0104>", toUnicode); // Ą

        var fontFile = cid.GetDictionary(PdfName.FontDescriptor)!.GetStream(PdfName.FontFile2)!.GetBytes();
        var original = new FileInfo(path).Length;
        Assert.True(fontFile.Length < original / 2, $"subset {fontFile.Length} not smaller than original {original}");
        var subset = new TrueTypeFont(fontFile);
        var full = new TrueTypeFont(File.ReadAllBytes(path));
        Assert.Equal(full.NumGlyphs, subset.NumGlyphs); // glyph ids are preserved
        Assert.Equal(full.GetAdvanceWidth(full.GetGlyphId('Z')), subset.GetAdvanceWidth(full.GetGlyphId('Z')));
    }

    [Fact]
    public void TrueTypeMetrics()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null) return;
        var font = PdfTrueTypeFont.FromFile(path);
        Assert.True(font.Ascent > 600 && font.Ascent < 1100);
        Assert.True(font.Descent < 0);
        Assert.True(font.GetTextWidth("WWW", 10) > font.GetTextWidth("iii", 10));
        Assert.True(font.CanDisplay('A'));
    }

    [Fact]
    public void FontIsEmbeddedOncePerDocument()
    {
        var doc = PdfDocument.Create();
        for (int i = 0; i < 3; i++)
            using (var c = doc.AddPage().GetCanvas())
                c.DrawText("x", PdfStandardFont.TimesBold, 10, 10, 10);
        var refs = doc.Pages.Select(p => p.Resources.GetDictionary(PdfName.Font)!["F1"]).Distinct().ToList();
        Assert.Single(refs);
    }
}
