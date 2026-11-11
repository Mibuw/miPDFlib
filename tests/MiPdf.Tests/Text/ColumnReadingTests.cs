using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Text;

namespace MiPdf.Tests.Text;

/// <summary>
/// <see cref="TextExtractionOptions.DetectColumns"/>: pages set in columns are read column by column, while tables,
/// invoices, lists and line-by-line layouts keep their rows. During development the reading order was also measured on
/// pages of recent arXiv papers against the content order of their LaTeX output.
/// </summary>
public class ColumnReadingTests
{
    private static readonly PdfFont Font = PdfStandardFont.Helvetica;
    private static readonly TextExtractionOptions Columns = new() { DetectColumns = true };

    /// <summary>Numbered words "c1w1 c1w2 ..." in lines of <paramref name="perLine"/> words.</summary>
    private static List<string> Lines(string prefix, int lines, int perLine) =>
        Enumerable.Range(0, lines).Select(l => string.Join(' ', Enumerable.Range(1, perLine).Select(w => $"{prefix}w{l * perLine + w}"))).ToList();

    private static PdfPage Page(Action<PdfCanvas> draw)
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage(PageSize.A4).GetCanvas()) draw(c);
        return PdfDocument.Open(doc.ToArray()).GetPage(1);
    }

    private static void DrawColumn(PdfCanvas c, List<string> lines, double x, double top, double size = 10, double leading = 12)
    {
        for (int i = 0; i < lines.Count; i++) c.DrawText(lines[i], Font, size, x, top - i * leading);
    }

    private static string[] TextLines(PdfPage page, TextExtractionOptions? options = null) => page.ExtractText(options ?? Columns).Split('\n');

    [Fact]
    public void TwoAndThreeColumnsAreReadOneAfterTheOther()
    {
        var left = Lines("a", 20, 5);
        var right = Lines("b", 20, 5);
        var page = Page(c =>
        {
            DrawColumn(c, left, 50, 780);
            DrawColumn(c, right, 310, 780);
        });
        Assert.Equal(left.Concat(right), TextLines(page));
        // without the option: line by line across the page, as before
        Assert.Equal(left[0] + " " + right[0], TextLines(page, new TextExtractionOptions())[0]);

        var cols = new[] { Lines("a", 15, 3), Lines("b", 15, 3), Lines("c", 15, 3) };
        page = Page(c =>
        {
            c.DrawText("Headline over three columns", PdfStandardFont.HelveticaBold, 18, 50, 800);
            for (int i = 0; i < 3; i++) DrawColumn(c, cols[i], 50 + i * 175, 760);
            c.DrawText("Page 7", Font, 8, 280, 40);
        });
        Assert.Equal(new[] { "Headline over three columns" }.Concat(cols[0]).Concat(cols[1]).Concat(cols[2]).Append("Page 7"), TextLines(page));
    }

    [Fact]
    public void LinesDrawnInAnyOrderAreReadByPosition()
    {
        var left = Lines("a", 18, 5);
        var right = Lines("b", 18, 5);
        var draws = left.Select((t, i) => (t, 50.0, 780.0 - i * 12)).Concat(right.Select((t, i) => (t, 310.0, 780.0 - i * 12)))
            .OrderBy(d => d.Item1.GetHashCode()).ToList();
        var page = Page(c =>
        {
            foreach (var (text, x, y) in draws) c.DrawText(text, Font, 10, x, y);
        });
        Assert.Equal(left.Concat(right), TextLines(page));
    }

    [Fact]
    public void TitleBlockAndHeadingsInAColumn()
    {
        var intro = Lines("a", 10, 5);
        var method = Lines("m", 12, 5);
        var right = Lines("b", 24, 5);
        var page = Page(c =>
        {
            c.DrawText("A Title Centered Across Both Columns", PdfStandardFont.HelveticaBold, 18, 140, 800);
            c.DrawText("First Author, Second Author", Font, 11, 220, 780);
            DrawColumn(c, intro, 50, 740);
            c.DrawText("2 Method", PdfStandardFont.HelveticaBold, 11, 50, 600);
            DrawColumn(c, method, 50, 580);
            DrawColumn(c, right, 310, 740);
        });
        var expected = new[] { "A Title Centered Across Both Columns", "First Author, Second Author" }
            .Concat(intro).Append("2 Method").Concat(method).Concat(right);
        Assert.Equal(expected, TextLines(page));
    }

    [Fact]
    public void TablesInvoicesAndListsKeepTheirRows()
    {
        // a table of short cells
        var page = Page(c =>
        {
            for (int row = 0; row < 12; row++)
                for (int col = 0; col < 5; col++)
                    c.DrawText($"r{row}c{col}", Font, 10, 50 + col * 100, 760 - row * 20);
        });
        Assert.Equal(Enumerable.Range(0, 12).Select(r => string.Join(' ', Enumerable.Range(0, 5).Select(col => $"r{r}c{col}"))), TextLines(page));

        // an invoice: a description of several words next to quantities and amounts
        page = Page(c =>
        {
            for (int row = 0; row < 10; row++)
            {
                c.DrawText($"Consulting services for project phase {row} with workshop", Font, 10, 50, 760 - row * 16);
                c.DrawText($"{row + 1}", Font, 10, 380, 760 - row * 16);
                c.DrawText($"{(row + 1) * 120},00", Font, 10, 480, 760 - row * 16);
            }
        });
        Assert.Equal($"Consulting services for project phase 3 with workshop 4 480,00", TextLines(page)[3]);

        // a bulleted list
        var items = Lines("i", 8, 6);
        page = Page(c =>
        {
            for (int i = 0; i < items.Count; i++)
            {
                c.DrawText("-", Font, 10, 50, 760 - i * 14);
                c.DrawText(items[i], Font, 10, 70, 760 - i * 14);
            }
        });
        Assert.Equal(items.Select(i => "- " + i), TextLines(page));
    }

    [Fact]
    public void SideNotesNextToTheText()
    {
        var main = Lines("a", 16, 7);
        var page = Page(c =>
        {
            DrawColumn(c, main, 50, 760);
            // side notes on their own baselines, beside the text
            c.DrawText("Note one", Font, 7, 470, 754);
            c.DrawText("Note two", Font, 7, 470, 700);
            c.DrawText("Note three", Font, 7, 470, 660);
        });
        Assert.Equal(main.Concat(new[] { "Note one", "Note two", "Note three" }), TextLines(page));
    }

    [Fact]
    public void WordsAndLettersFollowTheColumns()
    {
        var left = Lines("a", 10, 4);
        var right = Lines("b", 10, 4);
        var page = Page(c =>
        {
            DrawColumn(c, left, 50, 780);
            DrawColumn(c, right, 310, 780);
        });
        var words = PdfTextExtractor.ExtractWords(page, Columns).Select(w => w.Text);
        Assert.Equal(left.Concat(right).SelectMany(l => l.Split(' ')), words);
        var letters = string.Concat(PdfTextExtractor.ExtractLetters(page, Columns).Select(l => l.Text));
        Assert.Equal(string.Concat(left.Concat(right).Select(l => l.Replace(" ", ""))), letters.Replace(" ", ""));
    }
}
