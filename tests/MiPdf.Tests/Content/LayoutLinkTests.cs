using MiPdf.Fonts;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.Tagging;
using MiPdf.Text;

namespace MiPdf.Tests.Content;

public class LayoutLinkTests
{
    private const string LongText = "documentation with guides, examples and the complete API reference";

    private static Paragraph LinkParagraph(PdfFont font) =>
        new Paragraph("See ", font, 10).AddLink("GitHub", "https://github.com/Mibuw/miPDFlib")
            .Add(" and the ").AddLink(LongText, "https://mibuw.github.io/miPDFlib/").Add(".");

    [Fact]
    public void LinksInRunningTextGetAnAnnotationPerLine()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A6))
            layout.Add(LinkParagraph(PdfStandardFont.Helvetica));
        var page = PdfDocument.Open(doc.ToArray()).GetPage(1);

        var links = page.Annotations.Where(a => a.Subtype == "Link").ToList();
        Assert.Equal("https://github.com/Mibuw/miPDFlib", links[0].Uri);
        Assert.Equal("GitHub", links[0].Contents);
        var wrapped = links.Skip(1).ToList();
        Assert.True(wrapped.Count >= 2); // the long link wraps: one annotation per line
        Assert.All(wrapped, a => Assert.Equal(LongText, a.Contents));
        Assert.All(wrapped, a => Assert.Equal("https://mibuw.github.io/miPDFlib/", a.Uri));
        Assert.True(wrapped[1].Rect.Top <= wrapped[0].Rect.Bottom + 0.01);

        // the annotations cover the linked words
        var lines = PdfTextExtractor.ExtractLines(page);
        Assert.StartsWith("See GitHub and the", lines[0].Text);
        Assert.InRange(links[0].Rect.Left, lines[0].Bounds.Left + 10, lines[0].Bounds.Right);
        Assert.InRange(links[0].Rect.Width, 25, 45);
        Assert.False(doc.IsTagged);
    }

    [Fact]
    public void TaggedLinksArePartOfTheParagraph()
    {
        var fontPath = TestHelpers.FindSystemFont();
        if (fontPath == null) return;
        var doc = PdfDocument.Create();
        doc.Info.Title = "Links";
        doc.Language = "en";
        doc.UAConformance = PdfUAConformance.PdfUA1;
        using (var layout = new LayoutDocument(doc, PageSize.A6))
            layout.Add(LinkParagraph(PdfTrueTypeFont.FromFile(fontPath)));
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Empty(reopened.CheckPdfUA());

        var document = (PdfDictionary)reopened.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.K)!.Resolve()!;
        var paragraph = document.GetArray(PdfName.K)!.Select(k => k.Resolve()).OfType<PdfDictionary>().Single();
        Assert.Equal(StructureType.Paragraph, paragraph.GetName(PdfName.S)!.Value);
        var kids = paragraph.GetArray(PdfName.K)!.Select(k => k.Resolve()).ToList();
        var linkElements = kids.OfType<PdfDictionary>().Where(k => k.GetName(PdfName.S)?.Value == StructureType.Link).ToList();
        Assert.Equal(2, linkElements.Count); // one Link element per linked run, also across lines
        Assert.IsType<PdfNumber>(kids[0]); // "See " before the first link: reading order kept
        foreach (var link in linkElements)
        {
            var linkKids = link.GetArray(PdfName.K)!.Select(k => k.Resolve()).ToList();
            Assert.Contains(linkKids, k => k is PdfNumber); // the linked text
            Assert.Contains(linkKids, k => k is PdfDictionary d && d.GetName(PdfName.Type)?.Value == "OBJR");
        }
        Assert.All(reopened.GetPage(1).Annotations, a => Assert.True(a.Dictionary.ContainsKey(PdfName.Get("StructParent"))));
        Assert.Contains("See GitHub and the", PdfTextExtractor.ExtractText(reopened));
    }

    [Fact]
    public void AddLinkRequiresAnAddress()
    {
        Assert.Throws<ArgumentException>(() => new Paragraph().AddLink("text", ""));
        var run = new Paragraph().AddLink("text", "mailto:info@example.com").Runs[0];
        Assert.Equal("mailto:info@example.com", run.Uri);
        Assert.True(run.Underline);
    }
}
