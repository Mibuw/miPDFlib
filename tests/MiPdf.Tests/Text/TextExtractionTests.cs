using System.Text;
using MiPdf.Content;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.Text;

namespace MiPdf.Tests.Text;

public class TextExtractionTests
{
    private static PdfPage Reopen(PdfDocument doc, int page = 1) => PdfDocument.Open(doc.ToArray()).GetPage(page);

    [Fact]
    public void ExtractsStandardFontTextInReadingOrder()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas())
        {
            // drawn bottom-up and right-to-left on purpose
            c.DrawText("third line", PdfStandardFont.Helvetica, 12, 72, 600);
            c.DrawText("world", PdfStandardFont.Helvetica, 12, 150, 700);
            c.DrawText("Hello", PdfStandardFont.Helvetica, 12, 72, 700);
            c.DrawText("Grüße 100 €", PdfStandardFont.TimesRoman, 12, 72, 650);
        }
        Assert.Equal("Hello world\nGrüße 100 €\nthird line", Reopen(doc).ExtractText());
    }

    [Fact]
    public void ContentOrderOption()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas())
        {
            c.DrawText("second", PdfStandardFont.Helvetica, 12, 72, 600);
            c.DrawText("first", PdfStandardFont.Helvetica, 12, 72, 700);
        }
        Assert.Equal("second\nfirst", Reopen(doc).ExtractText(new TextExtractionOptions { SortByPosition = false }));
    }

    [Fact]
    public void ExtractsEmbeddedUnicodeFont()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null) return;
        var font = PdfTrueTypeFont.FromFile(path);
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("Zażółć gęślą jaźń", font, 14, 72, 700);
        Assert.Equal("Zażółć gęślą jaźń", Reopen(doc).ExtractText());
    }

    [Fact]
    public void JustifiedTextKeepsSingleSpaces()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc))
            layout.Add(new Paragraph("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.")
            { Alignment = TextAlignment.Justify, FontSize = 12 });
        var text = Reopen(doc).ExtractText().Replace('\n', ' ');
        Assert.Equal("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.", text);
    }

    [Fact]
    public void TjAdjustmentsCreateSpaces()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var font = doc.GetFontResource(PdfStandardFont.Helvetica);
        page.Resources[PdfName.Font] = new PdfDictionary { ["F1"] = font.Reference };
        page.SetContent(Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td [(Version) -250 (4.38) 30 (x)] TJ ET"));
        Assert.Equal("Version 4.38x", Reopen(doc).ExtractText());
    }

    [Fact]
    public void RotatedTextIsExtracted()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas())
        {
            c.SaveState().Rotate(90, 300, 400);
            c.DrawText("vertical text", PdfStandardFont.Helvetica, 12, 300, 400);
            c.RestoreState();
        }
        Assert.Equal("vertical text", Reopen(doc).ExtractText());
    }

    [Fact]
    public void TextInFormXObjectsIsIncluded()
    {
        var doc = PdfDocument.Create();
        var stamp = new PdfFormXObject(doc, new PdfRectangle(0, 0, 100, 20));
        using (var c = stamp.GetCanvas()) c.DrawText("STAMP", PdfStandardFont.HelveticaBold, 10, 5, 5);
        using (var c = doc.AddPage().GetCanvas()) c.DrawXObject(stamp, 300, 300);
        Assert.Equal("STAMP", Reopen(doc).ExtractText());
        Assert.Equal("", Reopen(doc).ExtractText(new TextExtractionOptions { IncludeXObjects = false }));
    }

    [Fact]
    public void FindTextReturnsPosition()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas())
            c.DrawText("Unterschrift: {{SIGNATURE}}", PdfStandardFont.Helvetica, 10, 100, 200);
        var hits = Reopen(doc).FindText("{{SIGNATURE}}");
        var hit = Assert.Single(hits);
        double expectedLeft = 100 + PdfStandardFont.Helvetica.GetTextWidth("Unterschrift: ", 10);
        Assert.Equal(expectedLeft, hit.Left, 1);
        Assert.Equal(100 + PdfStandardFont.Helvetica.GetTextWidth("Unterschrift: {{SIGNATURE}}", 10), hit.Right, 1);
        Assert.True(hit.Bottom < 200 && hit.Top > 200);
    }

    [Fact]
    public void WordsHaveBounds()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("alpha beta", PdfStandardFont.Courier, 10, 50, 50);
        var words = PdfTextExtractor.ExtractWords(Reopen(doc));
        Assert.Equal(new[] { "alpha", "beta" }, words.Select(w => w.Text));
        Assert.Equal(50, words[0].Bounds.Left, 3);
        Assert.Equal(80, words[0].Bounds.Right, 3);   // 5 chars * 6pt
        Assert.Equal(86, words[1].Bounds.Left, 3);
        Assert.Equal("Courier", words[0].FontName);
    }

    [Fact]
    public void DifferencesEncodingIsDecoded()
    {
        const string pdf = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
            "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n" +
            "4 0 obj<</Length 30>>stream\nBT /F1 10 Tf 10 10 Td (\\001\\002\\003) Tj ET\nendstream endobj\n" +
            "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Custom/Encoding<</Type/Encoding/Differences[1/germandbls/uni20AC/f_f]>>>>endobj\n" +
            "trailer<</Root 1 0 R>>\n%%EOF";
        var doc = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf));
        Assert.Equal("ß€ff", doc.GetPage(1).ExtractText());
    }

    [Fact]
    public void ParsesToUnicodeCMapRanges()
    {
        var cmap = CMap.Parse(Encoding.ASCII.GetBytes(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "2 beginbfchar <0001> <0041> <0002> <00660066> endbfchar\n" +
            "2 beginbfrange <0010> <0012> <0061> <0020> <0021> [<00DF> <20AC>] endbfrange"));
        Assert.Equal("A", cmap.GetUnicode(1));
        Assert.Equal("ff", cmap.GetUnicode(2));
        Assert.Equal("c", cmap.GetUnicode(0x12));
        Assert.Equal("€", cmap.GetUnicode(0x21));
        int pos = 0;
        Assert.Equal(0x0102, cmap.ReadCode(new byte[] { 1, 2 }, ref pos, out int len));
        Assert.Equal(2, len);
    }

    [Theory]
    [InlineData("Adieresis", "Ä")]
    [InlineData("zcaron", "ž")]
    [InlineData("longs", "ſ")]
    [InlineData("uni0041", "A")]
    [InlineData("u1F600", "😀")]
    [InlineData("a.sc", "a")]
    [InlineData("quotedblright", "”")]
    public void GlyphNamesToUnicode(string name, string expected) => Assert.Equal(expected, GlyphNames.ToUnicode(name));

    [Fact]
    public void ParsesInlineImages()
    {
        var content = Encoding.Latin1.GetBytes("q BI /W 2 /H 1 /CS /G /BPC 8 ID \u0001EI\u0002 EI Q (after) Tj");
        var ops = ContentParser.Parse(content).ToList();
        var bi = ops.Single(o => o.Operator == "BI");
        Assert.Equal(new byte[] { 1, (byte)'E', (byte)'I', 2 }, bi.InlineImage!.RawBytes);
        Assert.Equal("Tj", ops[^1].Operator);
    }
}
