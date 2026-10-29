using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.Text;

namespace MiPdf.Tests.Content;

public class LayoutExtensionTests
{
    private static PdfDocument Render(Action<LayoutDocument> build, PdfRectangle? size = null)
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, size ?? PageSize.A4, new Thickness(50))) build(layout);
        return PdfDocument.Open(doc.ToArray());
    }

    [Fact]
    public void ListsRenderLabelsAndNestedItems()
    {
        var doc = Render(layout =>
        {
            var list = new ListElement(ListStyle.Decimal);
            list.AddItem("Erster");
            list.AddItem("Zweiter").Add(new ListElement(ListStyle.LowerAlpha).AddItems("innen a", "innen b"));
            list.AddItem("Dritter");
            layout.Add(list);
            layout.Add(new ListElement(ListStyle.UpperRoman) { StartNumber = 4 }.AddItems("vier", "fünf"));
        });
        var lines = doc.GetPage(1).ExtractText().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Equal(new[] { "1. Erster", "2. Zweiter", "a) innen a", "b) innen b", "3. Dritter", "IV. vier", "V. fünf" }, lines);
        var words = PdfTextExtractor.ExtractWords(doc.GetPage(1));
        double outer = words.First(w => w.Text == "Erster").Bounds.Left, inner = words.First(w => w.Text == "innen").Bounds.Left;
        Assert.True(inner > outer + 10); // nested list is indented
        Assert.Equal(words.First(w => w.Text == "1.").Bounds.Bottom, words.First(w => w.Text == "Erster").Bounds.Bottom, 1); // label on the baseline
    }

    [Fact]
    public void LabelsAreFormatted()
    {
        var list = new ListElement(ListStyle.UpperRoman);
        Assert.Equal("XIV.", list.GetLabel(13));
        list.Style = ListStyle.LowerAlpha;
        Assert.Equal("aa)", list.GetLabel(26));
        list.LabelFormatter = n => $"§ {n}";
        Assert.Equal("§ 3", list.GetLabel(2));
    }

    [Fact]
    public void LongListsContinueOnTheNextPage()
    {
        var doc = Render(layout =>
        {
            var list = new ListElement(ListStyle.Decimal);
            for (int i = 1; i <= 120; i++) list.AddItem($"Eintrag {i}");
            layout.Add(list);
        });
        Assert.True(doc.PageCount >= 2);
        var all = doc.ExtractText();
        for (int i = 1; i <= 120; i++) Assert.Contains($"{i}. Eintrag {i}", all);
    }

    [Fact]
    public void RowSpanCellsSpanSeveralRows()
    {
        var doc = Render(layout =>
        {
            var table = new Table(1, 1, 1);
            table.AddHeaderCell("A");
            table.AddHeaderCell("B");
            table.AddHeaderCell("C");
            table.AddCell(new Cell("span", rowSpan: 2));
            table.AddCell("r1b");
            table.AddCell("r1c");
            table.AddCell("r2b");
            table.AddCell("r2c");
            table.AddCell(new Cell("wide", colSpan: 2));
            table.AddCell("r3c");
            layout.Add(table);
        });
        var words = PdfTextExtractor.ExtractWords(doc.GetPage(1)).ToDictionary(w => w.Text, w => w.Bounds);
        Assert.True(words["r2b"].Left > words["span"].Right);          // second row continues right of the spanning cell
        Assert.Equal(words["r1b"].Left, words["r2b"].Left, 1);
        Assert.True(words["r2b"].Top < words["r1b"].Bottom);
        Assert.Equal(words["span"].Left, words["wide"].Left, 1);
        Assert.True(words["wide"].Top < words["r2b"].Bottom);
    }

    [Fact]
    public void RowsTallerThanAPageAreSplit()
    {
        string longText = string.Join(" ", Enumerable.Range(1, 800).Select(i => "w" + i));
        var doc = Render(layout =>
        {
            var table = new Table(1, 3);
            table.AddHeaderCell("Kopf");
            table.AddHeaderCell("Inhalt");
            table.AddCell("x");
            table.AddCell(longText);
            table.AddCell("Ende");
            table.AddCell("fertig");
            layout.Add(table);
        }, PageSize.A5);
        Assert.True(doc.PageCount >= 2);
        var all = doc.ExtractText();
        Assert.Contains("w1 ", all);
        Assert.Contains("w800", all);
        Assert.Contains("fertig", all);
        Assert.Contains("Kopf", doc.GetPage(2).ExtractText()); // header repeated

        var noSplit = Render(layout =>
        {
            var table = new Table(1, 3) { SplitRowsAcrossPages = false };
            table.AddCell("x");
            table.AddCell(longText);
            layout.Add(table);
        }, PageSize.A5);
        Assert.Equal(1, noSplit.PageCount); // drawn anyway (overflowing) to guarantee progress
    }

    [Fact]
    public void KerningTightensTextForTrueTypeFonts()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null) return;
        var font = PdfTrueTypeFont.FromFile(path);
        if (!font.HasKerning) return;
        Assert.True(font.GetKerning('A', 'V') < 0);
        Assert.True(font.GetTextWidth("AVATAR", 12, kerning: true) < font.GetTextWidth("AVATAR", 12));

        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("AVATAR", font, 20, 72, 700, kerning: true);
        var content = System.Text.Encoding.Latin1.GetString(doc.GetPage(1).GetContentBytes());
        Assert.Contains("] TJ", content);
        var word = PdfTextExtractor.ExtractWords(PdfDocument.Open(doc.ToArray()).GetPage(1)).Single();
        Assert.Equal("AVATAR", word.Text);
        Assert.Equal(font.GetTextWidth("AVATAR", 20, true), word.Bounds.Width, 1);
    }

    [Fact]
    public void SoftHyphensAndPatternHyphenation()
    {
        var hyphenator = new PatternHyphenator(new[] { "hy3ph", "he2n", "hena4", "hen5at", "1na", "n2at", "1tio", "2io", "o2n" });
        Assert.Equal(new[] { 2, 6 }, hyphenator.GetBreakPoints("hyphenation"));
        var tex = PatternHyphenator.FromTex("% comment\n\\patterns{\nhy3ph he2n hena4 hen5at 1na n2at 1tio 2io o2n\n}\n\\hyphenation{ta-ble}");
        Assert.Equal(new[] { 2 }, tex.GetBreakPoints("table"));

        var doc = Render(layout =>
        {
            layout.Add(new Paragraph("aaaa aaaa aaaa aaaa Silben\u00ADtrennung", PdfStandardFont.Helvetica, 12) { RightIndent = 495 - 170 });
            layout.Add(new Paragraph("bbbb bbbb bbbb bbbb hyphenation", PdfStandardFont.Helvetica, 12) { RightIndent = 495 - 165, Hyphenator = hyphenator });
        });
        var text = doc.GetPage(1).ExtractText();
        Assert.Contains("Silben-", text);
        Assert.False(text.Contains((char)0xAD)); // soft hyphens are not output
        Assert.Contains("hyphen-", text);
        Assert.Contains("ation", text);
    }

    [Fact]
    public void TaggedListsAndRowSpans()
    {
        var doc = PdfDocument.Create();
        doc.EnableTagging();
        using (var layout = new LayoutDocument(doc))
        {
            layout.Add(new ListElement().AddItems("eins", "zwei"));
            var table = new Table(1, 1);
            table.AddCell(new Cell("span", rowSpan: 2));
            table.AddCell("a");
            table.AddCell("b");
            layout.Add(table);
        }
        var reopened = PdfDocument.Open(doc.ToArray());
        var types = new List<string>();
        PdfDictionary? spanCell = null;
        void Walk(PdfObject? node)
        {
            switch (node?.Resolve())
            {
                case PdfArray a:
                    foreach (var i in a) Walk(i);
                    break;
                case PdfDictionary d when d.GetName(PdfName.S) is { } s:
                    types.Add(s.Value);
                    if (d.GetDictionary(PdfName.Get("A"))?.ContainsKey(PdfName.Get("RowSpan")) == true) spanCell = d;
                    Walk(d.Get(PdfName.K));
                    break;
            }
        }
        Walk(reopened.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.K));
        Assert.Equal(new[] { "Document", "L", "LI", "Lbl", "LBody", "P", "LI", "Lbl", "LBody", "P", "Table", "TR", "TD", "TD", "TR", "TD" }, types);
        Assert.NotNull(spanCell);
    }
}
