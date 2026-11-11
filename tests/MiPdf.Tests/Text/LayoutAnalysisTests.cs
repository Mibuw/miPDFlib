using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.Text;

namespace MiPdf.Tests.Text;

/// <summary>
/// <see cref="PdfLayoutAnalyzer"/>: blocks and their kinds. During development the analysis was also compared with the
/// structure tree of tagged IRS instructions (headings 89 %, list items 88 %, paragraphs 83 % recognised) and with
/// randomly generated documents of the layout engine.
/// </summary>
public class LayoutAnalysisTests
{
    private const string Body = "The analysis splits the text of a page into blocks and tells what each of them is, so that a reader " +
                                "of the extracted text finds the structure again that the layout gave the page.";

    private static PdfDocument Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToArray());

    [Fact]
    public void DocumentWithHeadingsListsTableHeaderAndFooter()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A4, new Thickness(60, 70, 60, 70)))
        {
            layout.Header = ctx => ctx.Canvas.DrawText("Annual Report 2026", PdfStandardFont.HelveticaOblique, 8, ctx.ContentArea.Left, ctx.PageArea.Top - 40);
            layout.Footer = ctx => ctx.Canvas.DrawText($"Page {ctx.PageNumber} of {ctx.TotalPages}", PdfStandardFont.Helvetica, 8, 280, 35);
            for (int section = 1; section <= 3; section++)
            {
                layout.Add(new Paragraph($"{section} Results of part {section}", PdfStandardFont.HelveticaBold, 16).AsHeading(1).SetSpacing(12, 6));
                layout.Add(new Paragraph(Body + " " + Body, PdfStandardFont.TimesRoman, 11).SetSpacing(0, 8));
                layout.Add(new Paragraph($"{section}.1 Details", PdfStandardFont.HelveticaBold, 13).AsHeading(2).SetSpacing(10, 4));
                var list = new ListElement(section == 2 ? ListStyle.Decimal : ListStyle.Bullet) { Font = PdfStandardFont.TimesRoman, FontSize = 11 };
                list.AddItems("A first point of the list.", "A second point that is long enough to break onto a second line of the list item, " +
                    "because it goes on with a few more words than fit.", "A third point.");
                layout.Add(list);
                var table = new Table(1, 1, 1);
                table.AddHeaderCell("Item"); table.AddHeaderCell("Quantity"); table.AddHeaderCell("Amount");
                for (int row = 0; row < 4; row++) { table.AddCell($"Article {row}"); table.AddCell($"{row + 2}"); table.AddCell($"{(row + 1) * 12},50"); }
                layout.Add(table);
                layout.Add(new Paragraph(Body, PdfStandardFont.TimesRoman, 11).SetSpacing(8, 8));
                layout.Add(new PageBreak());
            }
        }
        using var reopened = Reopen(doc);
        var pages = PdfLayoutAnalyzer.Analyze(reopened);
        Assert.True(pages.Count >= 3);
        var blocks = pages.SelectMany(p => p.Blocks).ToList();

        Assert.Equal(new[] { "1 Results of part 1", "1.1 Details", "2 Results of part 2", "2.1 Details", "3 Results of part 3", "3.1 Details" },
            blocks.Where(b => b.Kind == LayoutBlockKind.Heading).Select(b => b.Text));
        Assert.All(blocks.Where(b => b.Kind == LayoutBlockKind.Heading), b => Assert.Equal(b.Text.Contains('.') ? 2 : 1, b.Level));
        // the numbered items of section 2 are list items, not headings; the long item keeps its second line
        var items = blocks.Where(b => b.Kind == LayoutBlockKind.ListItem).ToList();
        Assert.Equal(9, items.Count);
        Assert.Equal(2, items[1].Lines.Count);
        Assert.StartsWith("1.", items[3].Text);
        // the table with its bold head row; header and footer on every page
        var tables = blocks.Where(b => b.Kind == LayoutBlockKind.Table).ToList();
        Assert.Equal(3, tables.Count);
        Assert.All(tables, t => Assert.Equal(5, t.Lines.Count));
        Assert.StartsWith("Item Quantity Amount", tables[0].Lines[0].Text);
        Assert.All(pages, p => Assert.Equal("Annual Report 2026", Assert.Single(p.Blocks, b => b.Kind == LayoutBlockKind.Header).Text));
        Assert.All(pages, p => Assert.StartsWith("Page ", Assert.Single(p.Blocks, b => b.Kind == LayoutBlockKind.Footer).Text));
        Assert.Equal(6, blocks.Count(b => b.Kind == LayoutBlockKind.Paragraph));
        Assert.Equal(11, pages[0].BodyFontSize);
    }

    [Fact]
    public void TitlesCaptionsRunInHeadsAndStamps()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(PageSize.A4).GetCanvas())
        {
            // a centered title of two lines
            c.DrawText("A Title That Needs Two Lines", PdfStandardFont.HelveticaBold, 20, 150, 780);
            c.DrawText("To Be Set Centered", PdfStandardFont.HelveticaBold, 20, 205, 756);
            // a paragraph that begins with bold words running on into the text
            var words = Body.Split(' ');
            c.DrawText("Bold beginning of a paragraph that runs on", PdfStandardFont.TimesBold, 11, 72, 700);
            c.DrawText(string.Join(' ', words.Take(10)), PdfStandardFont.TimesRoman, 11, 72 + PdfStandardFont.TimesBold.GetTextWidth("Bold beginning of a paragraph that runs on ", 11), 700);
            c.DrawText(string.Join(' ', words.Skip(10).Take(14)), PdfStandardFont.TimesRoman, 11, 72, 687);
            c.DrawText(string.Join(' ', words.Skip(24)), PdfStandardFont.TimesRoman, 11, 72, 674);
            // a caption below a figure
            c.DrawText("Figure 3. The blocks found on a page.", PdfStandardFont.TimesRoman, 9, 72, 500);
            // a stamp running up the margin, large
            c.SaveState().Transform(0, 1, -1, 0, 40, 200);
            c.DrawText("Preprint 2611.12345 – not reviewed", PdfStandardFont.Helvetica, 20, 0, 0);
            c.RestoreState();
            c.DrawText("7", PdfStandardFont.TimesRoman, 10, 300, 40);
        }
        using var reopened = Reopen(doc);
        var page = PdfLayoutAnalyzer.Analyze(reopened.GetPage(1));
        var title = Assert.Single(page.Blocks, b => b.Kind == LayoutBlockKind.Heading);
        Assert.Equal("A Title That Needs Two Lines\nTo Be Set Centered", title.Text);
        var paragraph = page.Blocks.First(b => b.Text.StartsWith("Bold beginning"));
        Assert.Equal(LayoutBlockKind.Paragraph, paragraph.Kind);
        Assert.Equal(3, paragraph.Lines.Count);
        Assert.Equal(LayoutBlockKind.Caption, page.Blocks.Single(b => b.Text.StartsWith("Figure 3")).Kind);
        Assert.Equal(LayoutBlockKind.Paragraph, page.Blocks.Single(b => b.Text.StartsWith("Preprint")).Kind);
        Assert.Equal(LayoutBlockKind.Footer, page.Blocks.Single(b => b.Text == "7").Kind);
    }

    [Fact]
    public void BlocksFollowTheColumns()
    {
        var doc = PdfDocument.Create();
        var words = Body.Split(' ');
        using (var c = doc.AddPage(PageSize.A4).GetCanvas())
        {
            for (int col = 0; col < 2; col++)
            {
                double x = 50 + col * 260, y = 760;
                c.DrawText($"{col + 1} Column heading", PdfStandardFont.HelveticaBold, 13, x, y);
                y -= 22;
                for (int p = 0; p < 2; p++)
                {
                    for (int line = 0; line < 4; line++, y -= 13)
                        c.DrawText(string.Join(' ', words.Skip(line * 6).Take(6)), PdfStandardFont.TimesRoman, 11, x, y);
                    y -= 10;
                }
            }
        }
        using var reopened = Reopen(doc);
        var page = PdfLayoutAnalyzer.Analyze(reopened.GetPage(1));
        Assert.Equal(new[] { LayoutBlockKind.Heading, LayoutBlockKind.Paragraph, LayoutBlockKind.Paragraph, LayoutBlockKind.Heading, LayoutBlockKind.Paragraph, LayoutBlockKind.Paragraph },
            page.Blocks.Select(b => b.Kind));
        Assert.Equal("2 Column heading", page.Blocks[3].Text);
        Assert.All(page.Blocks.Where(b => b.Kind == LayoutBlockKind.Paragraph), b => Assert.Equal(4, b.Lines.Count));
    }

    [Fact]
    public void TeXFontNamesTellBoldAndItalic()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(PageSize.A4);
        PdfDictionary Font(string name) => new()
        {
            [PdfName.Type] = PdfName.Font, [PdfName.Subtype] = PdfName.Get("Type1"), [PdfName.BaseFont] = PdfName.Get(name),
            [PdfName.Get("FirstChar")] = new PdfNumber(65), [PdfName.Get("LastChar")] = new PdfNumber(66), [PdfName.Get("Widths")] = PdfArray.FromNumbers(600, 600),
        };
        page.Dictionary[PdfName.Resources] = new PdfDictionary
        {
            [PdfName.Font] = new PdfDictionary
            {
                [PdfName.Get("F1")] = Font("ABCDEF+CMBX12"), [PdfName.Get("F2")] = Font("ABCDEF+SFTI1000"),
                [PdfName.Get("F3")] = Font("NimbusRomNo9L-Medi"), [PdfName.Get("F4")] = Font("ABCDEF+CMR10"),
            },
        };
        page.SetContent(Encoding.ASCII.GetBytes("BT /F1 10 Tf 72 700 Td (A) Tj /F2 10 Tf 20 0 Td (B) Tj /F3 10 Tf 20 0 Td (A) Tj /F4 10 Tf 20 0 Td (B) Tj ET"));
        var letters = PdfTextExtractor.ExtractLetters(Reopen(doc).GetPage(1));
        Assert.Equal(new[] { (true, false), (false, true), (true, false), (false, false) }, letters.Select(l => (l.IsBold, l.IsItalic)));
    }
}
