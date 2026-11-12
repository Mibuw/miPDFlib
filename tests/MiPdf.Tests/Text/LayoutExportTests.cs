using System.Text.RegularExpressions;
using System.Xml;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Layout;
using MiPdf.Text;

namespace MiPdf.Tests.Text;

/// <summary>
/// <see cref="PdfLayoutExport"/>: Markdown, hOCR and ALTO. During development the ALTO output of arXiv papers, IRS forms
/// and invoices was validated against the ALTO 4.4 schema, and the hOCR word boxes were drawn over the rendered pages.
/// </summary>
public class LayoutExportTests
{
    private static PdfDocument Sample()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A4, new Thickness(60, 70, 60, 70)))
        {
            layout.Header = ctx => ctx.Canvas.DrawText("Report", PdfStandardFont.HelveticaOblique, 8, ctx.ContentArea.Left, ctx.PageArea.Top - 40);
            layout.Footer = ctx => ctx.Canvas.DrawText($"Page {ctx.PageNumber}", PdfStandardFont.Helvetica, 8, 280, 35);
            layout.Add(new Paragraph("1 Overview", PdfStandardFont.HelveticaBold, 16).AsHeading(1).SetSpacing(0, 6));
            layout.Add(new Paragraph("Some *special* text with [brackets] and a_b underscore.", PdfStandardFont.TimesRoman, 11).SetSpacing(0, 8));
            layout.Add(new Paragraph("1.1 Points", PdfStandardFont.HelveticaBold, 13).AsHeading(2).SetSpacing(6, 4));
            var bullets = new ListElement(ListStyle.Bullet) { Font = PdfStandardFont.TimesRoman, FontSize = 11 };
            bullets.AddItems("First point.", "Second point.");
            layout.Add(bullets);
            var numbers = new ListElement(ListStyle.Decimal) { Font = PdfStandardFont.TimesRoman, FontSize = 11 };
            numbers.AddItems("Step one.", "Step two.");
            layout.Add(numbers);
            var table = new Table(1, 1, 1);
            table.AddHeaderCell("Item"); table.AddHeaderCell("Quantity"); table.AddHeaderCell("Amount");
            table.AddCell("Pens"); table.AddCell("3"); table.AddCell("4,50");
            table.AddCell("Paper | A4"); table.AddCell("1"); table.AddCell("12,00");
            layout.Add(table);
            layout.Add(new Paragraph("Figure 1. A caption.", PdfStandardFont.TimesRoman, 9).SetSpacing(8, 8));
        }
        return PdfDocument.Open(doc.ToArray());
    }

    [Fact]
    public void Markdown()
    {
        using var doc = Sample();
        string md = PdfLayoutExport.ToMarkdown(doc);
        Assert.Equal(
            "# 1 Overview\n\n" +
            "Some \\*special\\* text with \\[brackets\\] and a\\_b underscore.\n\n" +
            "## 1.1 Points\n\n" +
            "- First point.\n- Second point.\n" +
            "1. Step one.\n2. Step two.\n\n" +
            "| Item | Quantity | Amount |\n| --- | --- | --- |\n| Pens | 3 | 4,50 |\n| Paper \\| A4 | 1 | 12,00 |\n\n" +
            "*Figure 1. A caption.*\n", md);
        // headers and footers on request
        string withMargins = PdfLayoutExport.ToMarkdown(PdfLayoutAnalyzer.Analyze(doc), new MarkdownExportOptions { IncludeHeadersAndFooters = true });
        Assert.StartsWith("Report\n\n# 1 Overview", withMargins);
        Assert.EndsWith("Page 1\n", withMargins);
    }

    [Fact]
    public void MarkdownJoinsHyphenatedWordsAndBrokenParagraphs()
    {
        var doc = PdfDocument.Create();
        var font = PdfStandardFont.TimesRoman;
        // page 1: a paragraph whose words are broken at the line ends, running on to page 2
        using (var c = doc.AddPage(PageSize.A4).GetCanvas())
        {
            string[] lines = { "The analysis of the docu-", "ment keeps self-", "employed and target-", "catalog as they are, and LLM-", "extracted too, and the text" };
            for (int i = 0; i < lines.Length; i++) c.DrawText(lines[i], font, 11, 72, 700 - i * 13);
        }
        using (var c = doc.AddPage(PageSize.A4).GetCanvas())
        {
            c.DrawText("goes on here on the next page.", font, 11, 72, 760);
            c.DrawText("A self-employed target-catalog is mentioned once more here.", font, 11, 72, 700);
        }
        string md = PdfLayoutExport.ToMarkdown(PdfDocument.Open(doc.ToArray()));
        Assert.StartsWith("The analysis of the document keeps self-employed and target-catalog as they are, and LLM-extracted too, " +
                          "and the text goes on here on the next page.", md);
        // and without joining
        string raw = PdfLayoutExport.ToMarkdown(PdfLayoutAnalyzer.Analyze(PdfDocument.Open(doc.ToArray())),
            new MarkdownExportOptions { JoinHyphenatedWords = false, JoinBrokenParagraphs = false, PageSeparator = "---" });
        Assert.Contains("docu- ment", raw);
        Assert.Contains("the text\n\n---\n\ngoes on", raw);
    }

    [Fact]
    public void Hocr()
    {
        using var doc = Sample();
        string hocr = PdfLayoutExport.ToHocr(doc);
        var xml = new XmlDocument { XmlResolver = null };
        xml.Load(new XmlTextReader(new StringReader(hocr)) { DtdProcessing = DtdProcessing.Ignore });
        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("h", "http://www.w3.org/1999/xhtml");
        var page = (XmlElement)xml.SelectSingleNode("//h:div[@class='ocr_page']", ns)!;
        Assert.Equal("bbox 0 0 595 842; ppageno 0; scan_res 72 72", page.GetAttribute("title"));
        Assert.NotNull(xml.SelectSingleNode("//h:p[@class='ocr_header']", ns));
        Assert.NotNull(xml.SelectSingleNode("//h:p[@class='ocr_footer']", ns));
        Assert.NotNull(xml.SelectSingleNode("//h:p[@class='ocr_caption']", ns));
        Assert.NotNull(xml.SelectSingleNode("//h:p[@data-kind='heading1']", ns));
        // the first word of the heading, measured from the top of the page (the page's margin is 70 pt)
        var word = (XmlElement)xml.SelectSingleNode("//h:p[@data-kind='heading1']//h:span[@class='ocrx_word']", ns)!;
        Assert.Equal("1", word.InnerText);
        var box = Regex.Match(word.GetAttribute("title"), @"bbox (\d+) (\d+) (\d+) (\d+)").Groups;
        Assert.InRange(int.Parse(box[1].Value), 58, 61);
        Assert.InRange(int.Parse(box[2].Value), 65, 75);
    }

    [Fact]
    public void Alto()
    {
        using var doc = Sample();
        string alto = PdfLayoutExport.ToAlto(doc);
        var xml = new XmlDocument();
        xml.LoadXml(alto);
        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("a", "http://www.loc.gov/standards/alto/ns-v4#");
        Assert.Equal("inch1200", xml.SelectSingleNode("//a:MeasurementUnit", ns)!.InnerText);
        var page = (XmlElement)xml.SelectSingleNode("//a:Page", ns)!;
        Assert.Equal("9916.7", page.GetAttribute("WIDTH"));  // 595 pt in 1/1200 inch
        // the margins come before the print space, as the schema orders them
        Assert.Equal(new[] { "TopMargin", "BottomMargin", "PrintSpace" }, page.ChildNodes.OfType<XmlElement>().Select(e => e.LocalName));
        Assert.Equal("Report", xml.SelectSingleNode("//a:TopMargin//a:String/@CONTENT", ns)!.Value);
        Assert.Contains("TAG_heading1", xml.SelectNodes("//a:LayoutTag/@ID", ns)!.OfType<XmlAttribute>().Select(a => a.Value));
        var heading = (XmlElement)xml.SelectSingleNode("//a:TextBlock[@TAGREFS='TAG_heading1']", ns)!;
        Assert.Equal("1 Overview", string.Join(' ', heading.SelectNodes(".//a:String/@CONTENT", ns)!.OfType<XmlAttribute>().Select(a => a.Value)));
        var style = (XmlElement)xml.SelectSingleNode($"//a:TextStyle[@ID='{((XmlElement)heading.SelectSingleNode(".//a:String", ns)!).GetAttribute("STYLEREFS")}']", ns)!;
        Assert.Equal("bold", style.GetAttribute("FONTSTYLE"));
        Assert.Equal("16", style.GetAttribute("FONTSIZE"));
    }
}
