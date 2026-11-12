using MiPdf.Content;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Layout;
using MiPdf.Text;

namespace MiPdf.Tests.Text;

/// <summary>
/// Tables of the layout analysis: the rules of a page (<see cref="PdfGraphicsExtractor"/>), the cells of tables drawn
/// with rules and of tables without. During development the detection was checked on the line items of the ZUGFeRD
/// and Factur-X sample invoices, on the tax and credit tables of the IRS instructions (form fields and page frames are
/// no tables) and on the tables of arXiv papers.
/// </summary>
public class TableDetectionTests
{
    private static PdfDocument Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToArray());

    private static PdfFont Font => PdfStandardFont.Helvetica;

    [Fact]
    public void RuledTableWithSpanningAndWrappedCells()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A4, new Thickness(60)))
        {
            layout.Add(new Paragraph("The items of the order with their quantities and amounts."));
            var table = new Table(3, 1, 1, 1) { BorderWidth = 0.5 };
            table.AddHeaderCell("Description"); table.AddHeaderCell("Qty"); table.AddHeaderCell("Price"); table.AddHeaderCell("Amount");
            table.AddCell("A long description that needs more than one line in its cell to be shown"); table.AddCell("2"); table.AddCell("5,00"); table.AddCell("10,00");
            table.AddCell(new Cell("Spanning two rows", rowSpan: 2)); table.AddCell("1"); table.AddCell("5,00"); table.AddCell("5,00");
            table.AddCell("3"); table.AddCell("1,00"); table.AddCell("3,00");
            table.AddCell(new Cell("Total", colSpan: 3)); table.AddCell("18,00");
            layout.Add(table);
            layout.Add(new Paragraph("A paragraph after the table."));
        }
        using var reopened = Reopen(doc);
        var page = PdfLayoutAnalyzer.Analyze(reopened.GetPage(1));
        Assert.Equal(new[] { LayoutBlockKind.Paragraph, LayoutBlockKind.Table, LayoutBlockKind.Paragraph }, page.Blocks.Select(b => b.Kind));

        var t = page.Blocks[1].Table!;
        Assert.True(t.HasRules);
        Assert.Equal((5, 4), (t.RowCount, t.ColumnCount));
        Assert.Equal("Description", t[0, 0]!.Text);
        // the wrapped description is one cell with a line break
        Assert.Equal("A long description that needs more than one line in its cell to be shown", t[1, 0]!.Text.Replace('\n', ' '));
        Assert.Contains('\n', t[1, 0]!.Text);
        Assert.Equal("10,00", t[1, 3]!.Text);
        var spanning = t[3, 0]!;
        Assert.Equal(("Spanning two rows", 2, 0, 2), (spanning.Text, spanning.Row, spanning.Column, spanning.RowSpan));
        Assert.Same(spanning, t[2, 0]);
        Assert.Equal(("Total", 3), (t[4, 0]!.Text, t[4, 0]!.ColumnSpan));
        Assert.Equal("18,00", t[4, 3]!.Text);
        Assert.Equal(17, t.Cells.Count);

        var markdown = PdfLayoutExport.ToMarkdown(new[] { page });
        Assert.Contains("| Description | Qty | Price | Amount |\n| --- | --- | --- | --- |\n", markdown);
        Assert.Contains("| Spanning two rows | 1 | 5,00 | 5,00 |\n|  | 3 | 1,00 | 3,00 |\n| Total |  |  | 18,00 |", markdown);
    }

    [Fact]
    public void TableWithRowBackgroundsOnlyIsReadFromItsRows()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A4, new Thickness(60)))
        {
            var table = new Table(2, 1, 1) { BorderWidth = 0, HeaderBackground = PdfColor.Gray(0.8), AlternateRowBackground = PdfColor.Gray(0.93) };
            table.AddHeaderCell("City"); table.AddHeaderCell("Country"); table.AddHeaderCell("Population");
            foreach (var (city, country, population) in new[] { ("Vienna", "Austria", "2,000,000"), ("Graz", "Austria", "300,000"), ("Munich", "Germany", "1,500,000") })
            {
                table.AddCell(city); table.AddCell(country); table.AddCell(population);
            }
            layout.Add(table);
        }
        using var reopened = Reopen(doc);
        var block = Assert.Single(PdfLayoutAnalyzer.Analyze(reopened.GetPage(1)).Blocks);
        Assert.Equal(LayoutBlockKind.Table, block.Kind);
        var t = block.Table!;
        // the backgrounds span the whole width: no grid, so the columns come from the gaps of the rows
        Assert.False(t.HasRules);
        Assert.Equal((4, 3), (t.RowCount, t.ColumnCount));
        Assert.Equal(new[] { "Graz", "Austria", "300,000" }, t.Cells.Where(c => c.Row == 2).OrderBy(c => c.Column).Select(c => c.Text));
    }

    [Fact]
    public void RulesFromStrokesThinFillsAndBackgrounds()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            c.DrawLine(100, 700, 300, 700, PdfColor.Black, 1);         // a stroked line
            c.DrawLine(300, 700, 400, 700, PdfColor.Black, 1);         // touching it: joined
            c.DrawRectangle(99.75, 500, 0.5, 150, fill: PdfColor.Black); // a thin filled rectangle: a vertical rule
            c.DrawRectangle(100, 300, 200, 100, fill: PdfColor.Gray(0.9)); // a background: its four edges
            c.DrawLine(100, 100, 300, 200, PdfColor.Black, 1);         // slanted: no rule
            c.MoveTo(400, 100).CurveTo(450, 150, 500, 150, 550, 100).Stroke(); // a curve: no rule
        }
        using var reopened = Reopen(doc);
        var rules = PdfGraphicsExtractor.ExtractRules(reopened.GetPage(1));
        Assert.Equal(6, rules.Count);

        var joined = Assert.Single(rules, r => r.IsHorizontal && r.Start.Y == 700);
        Assert.Equal((100, 400, 1, false), (joined.Start.X, joined.End.X, joined.Thickness, joined.IsEdgeOfFill));
        var thin = Assert.Single(rules, r => !r.IsHorizontal && !r.IsEdgeOfFill);
        Assert.Equal((100, 500, 650, 150), (thin.Start.X, thin.Start.Y, thin.End.Y, thin.Length));
        Assert.Equal(0.5, thin.Thickness, 3);
        var edges = rules.Where(r => r.IsEdgeOfFill).ToList();
        Assert.Equal(4, edges.Count);
        Assert.Equal(new[] { 300.0, 400 }, edges.Where(r => r.IsHorizontal).Select(r => r.Start.Y).Order());
        Assert.Equal(new[] { 100.0, 300 }, edges.Where(r => !r.IsHorizontal).Select(r => r.Start.X).Order());
    }

    [Fact]
    public void DataRowsWithoutRulesBetweenThemAreSplitAtTheirLines()
    {
        // vertical rules, a rule under the head and around the table, none between the data rows
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            foreach (var x in new[] { 50.0, 200, 300, 400 }) c.DrawLine(x, 600, x, 700, PdfColor.Black, 0.5);
            foreach (var y in new[] { 600.0, 680, 700 }) c.DrawLine(50, y, 400, y, PdfColor.Black, 0.5);
            void Row(double y, string a, string b, string d) { c.DrawText(a, Font, 10, 55, y); c.DrawText(b, Font, 10, 205, y); c.DrawText(d, Font, 10, 305, y); }
            Row(686, "Name", "Count", "Share");
            Row(665, "Alpha", "12", "40%");
            Row(650, "Beta", "9", "30%");
            Row(635, "Gamma", "9", "30%");
            // a wrapped name: its second line has text in one cell only and stays with its row
            Row(620, "Delta with a", "0", "0%");
            c.DrawText("long name", Font, 10, 55, 607);
        }
        using var reopened = Reopen(doc);
        var block = Assert.Single(PdfLayoutAnalyzer.Analyze(reopened.GetPage(1)).Blocks);
        var t = block.Table!;
        Assert.True(t.HasRules);
        Assert.Equal((5, 3), (t.RowCount, t.ColumnCount));
        Assert.Equal(new[] { "Beta", "9", "30%" }, t.Cells.Where(c => c.Row == 2).Select(c => c.Text));
        Assert.Equal("Delta with a\nlong name", t[4, 0]!.Text);
        Assert.All(t.Cells, c => Assert.Equal((1, 1), (c.RowSpan, c.ColumnSpan)));
    }

    [Fact]
    public void CellsJoinedAcrossColumnsAreSplitAtClearGaps()
    {
        // the column rules divide only the head; under it the columns are separated by space
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            foreach (var y in new[] { 600.0, 660, 680, 700 }) c.DrawLine(50, y, 410, y, PdfColor.Black, 0.5);
            foreach (var x in new[] { 50.0, 410 }) c.DrawLine(x, 600, x, 700, PdfColor.Black, 0.5);
            foreach (var x in new[] { 170.0, 290 }) c.DrawLine(x, 660, x, 700, PdfColor.Black, 0.5);
            c.DrawText("At least", Font, 10, 55, 686); c.DrawText("But less than", Font, 10, 175, 686); c.DrawText("Your credit is", Font, 10, 295, 686);
            c.DrawText("Amount", Font, 10, 55, 666); c.DrawText("Amount", Font, 10, 175, 666); c.DrawText("Single", Font, 10, 295, 666);
            c.DrawText("5,600", Font, 10, 55, 640); c.DrawText("5,650", Font, 10, 175, 640); c.DrawText("430", Font, 10, 295, 640);
        }
        using var reopened = Reopen(doc);
        var t = Assert.Single(PdfLayoutAnalyzer.Analyze(reopened.GetPage(1)).Blocks).Table!;
        Assert.Equal((3, 3), (t.RowCount, t.ColumnCount));
        Assert.Equal(new[] { "5,600", "5,650", "430" }, t.Cells.Where(c => c.Row == 2).Select(c => c.Text));
        Assert.All(t.Cells, c => Assert.Equal(1, c.ColumnSpan));
    }

    [Fact]
    public void FramesOfAPageLayoutAreNoTable()
    {
        // an address frame over two rows and two columns of the grid that the other frames make
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            c.DrawRectangle(50, 400, 500, 300, PdfColor.Black, lineWidth: 0.5);
            c.DrawLine(350, 400, 350, 700, PdfColor.Black, 0.5);
            c.DrawLine(200, 400, 200, 500, PdfColor.Black, 0.5);
            c.DrawLine(50, 500, 550, 500, PdfColor.Black, 0.5);
            c.DrawLine(350, 600, 550, 600, PdfColor.Black, 0.5);
            string[] address = { "Supplier Ltd", "Supplier Street 20", "12345 Sample Town" };
            for (int i = 0; i < address.Length; i++) c.DrawText(address[i], Font, 10, 60, 680 - 14 * i);
            c.DrawText("INVOICE", Font, 14, 360, 680);
            c.DrawText("Number 4711", Font, 10, 360, 580);
            c.DrawText("Bank: Sample Bank", Font, 8, 60, 480);
            c.DrawText("Tax number 123", Font, 8, 210, 480);
            c.DrawText("Managing director: A. Sample", Font, 8, 360, 480);
        }
        using var reopened = Reopen(doc);
        var blocks = PdfLayoutAnalyzer.Analyze(reopened.GetPage(1)).Blocks;
        Assert.DoesNotContain(blocks, b => b.Table?.HasRules == true);
        Assert.Contains(blocks, b => b.Text.Contains("Supplier Street 20"));
    }

    [Fact]
    public void LinesRunningThroughTablesSideBySideAreSplitBetweenThem()
    {
        // three ruled tables next to each other, their rows on the same baselines
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(new PdfRectangle(0, 0, 600, 800)).GetCanvas())
        {
            for (int table = 0; table < 3; table++)
            {
                double left = 40 + 180 * table;
                foreach (var x in new[] { left, left + 80, left + 160 }) c.DrawLine(x, 620, x, 700, PdfColor.Black, 0.5);
                foreach (var y in new[] { 620.0, 640, 660, 680, 700 }) c.DrawLine(left, y, left + 160, y, PdfColor.Black, 0.5);
                for (int row = 0; row < 4; row++)
                {
                    c.DrawText($"T{table} R{row}", Font, 9, left + 4, 686 - 20 * row);
                    c.DrawText($"{table * 100 + row}", Font, 9, left + 84, 686 - 20 * row);
                }
            }
        }
        using var reopened = Reopen(doc);
        var tables = PdfLayoutAnalyzer.Analyze(reopened.GetPage(1)).Blocks.Where(b => b.Kind == LayoutBlockKind.Table).ToList();
        Assert.Equal(3, tables.Count);
        for (int table = 0; table < 3; table++)
        {
            var t = tables.Single(b => b.Text.StartsWith($"T{table} ")).Table!;
            Assert.Equal((4, 2), (t.RowCount, t.ColumnCount));
            Assert.Equal(new[] { $"T{table} R3", $"{table * 100 + 3}" }, t.Cells.Where(c => c.Row == 3).Select(c => c.Text));
        }
    }
}
