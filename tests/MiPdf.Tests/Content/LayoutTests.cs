using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Layout;

namespace MiPdf.Tests.Content;

public class LayoutTests
{
    [Fact]
    public void BreaksLinesAtWordBoundaries()
    {
        var lines = TextLayout.BreakLines("aaa bbb ccc ddd", PdfStandardFont.Courier, 10, 6 * 7.5);
        // Courier: 6pt per char at 10pt -> 7.5 chars per line -> "aaa bbb" fits (7 chars)
        Assert.Equal(new[] { "aaa bbb", "ccc ddd" }, lines);
    }

    [Fact]
    public void BreaksLongWordsByCharacters()
    {
        var lines = TextLayout.BreakLines("abcdefghij", PdfStandardFont.Courier, 10, 24);
        Assert.Equal(new[] { "abcd", "efgh", "ij" }, lines);
    }

    [Fact]
    public void HonorsHardLineBreaks()
    {
        var lines = TextLayout.BreakLines("a\nb\r\n\nc", PdfStandardFont.Helvetica, 10, 500);
        Assert.Equal(new[] { "a", "b", "", "c" }, lines);
    }

    [Fact]
    public void ParagraphsFlowOntoNewPages()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A5))
        {
            for (int i = 0; i < 100; i++) layout.Add(new Paragraph($"Line {i}"));
        }
        Assert.True(doc.PageCount > 1);
        var lastText = Encoding.Latin1.GetString(doc.GetPage(doc.PageCount).GetContentBytes());
        Assert.Contains("(Line 99)", lastText);
    }

    [Fact]
    public void TableRepeatsHeaderOnEveryPage()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A5))
        {
            var table = Table.WithColumns(2);
            table.AddHeaderCell("HeaderA");
            table.AddHeaderCell("HeaderB");
            for (int i = 0; i < 80; i++)
            {
                table.AddCell($"R{i}");
                table.AddCell(new Cell($"V{i}") { TextAlignment = TextAlignment.Right });
            }
            layout.Add(table);
        }
        Assert.True(doc.PageCount >= 3);
        foreach (var page in doc.Pages)
            Assert.Contains("(HeaderA)", Encoding.Latin1.GetString(page.GetContentBytes()));
        var all = string.Concat(doc.Pages.Select(p => Encoding.Latin1.GetString(p.GetContentBytes())));
        for (int i = 0; i < 80; i++) Assert.Contains($"(R{i})", all);
    }

    [Fact]
    public void FooterKnowsTotalPageCount()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc))
        {
            layout.Footer = ctx => ctx.Canvas.DrawText($"Page {ctx.PageNumber} of {ctx.TotalPages}", PdfStandardFont.Helvetica, 8, 300, 20);
            layout.Add(new Paragraph("first"));
            layout.Add(new PageBreak());
            layout.Add(new Paragraph("second"));
        }
        Assert.Equal(2, doc.PageCount);
        Assert.Contains("(Page 2 of 2)", Encoding.Latin1.GetString(doc.GetPage(2).GetContentBytes()));
    }

    [Fact]
    public void JustifiedTextUsesTjAdjustments()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc))
        {
            layout.Add(new Paragraph(string.Join(' ', Enumerable.Repeat("justify", 40))) { Alignment = TextAlignment.Justify });
        }
        Assert.Contains("] TJ", Encoding.Latin1.GetString(doc.GetPage(1).GetContentBytes()));
    }
}
