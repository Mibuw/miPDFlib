using MiPdf.Fonts;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.Tagging;

namespace MiPdf.Tests.Content;

public class LayoutBookmarkTests
{
    private static void AddChapters(LayoutDocument layout)
    {
        layout.Add(new Paragraph("Introduction", fontSize: 16).AsHeading(1));
        layout.Add(new Paragraph("Some text."));
        layout.Add(new Paragraph("Background", fontSize: 13).AsHeading(2));
        layout.Add(new Paragraph("Scope  of\nthe work", fontSize: 13).AsHeading(2));
        layout.Add(new Paragraph("Details", fontSize: 11).AsHeading(3));
        layout.Add(new PageBreak());
        layout.Add(new Paragraph("Appendix", fontSize: 16).AsHeading(1));
    }

    [Fact]
    public void HeadingsGetNestedBookmarks()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A6))
            AddChapters(layout);
        var reopened = PdfDocument.Open(doc.ToArray());

        var top = reopened.Outlines.Children;
        Assert.Equal(new[] { "Introduction", "Appendix" }, top.Select(b => b.Title));
        Assert.Equal(new[] { "Background", "Scope of the work" }, top[0].Children.Select(b => b.Title));
        Assert.Equal("Details", Assert.Single(top[0].Children[1].Children).Title);
        Assert.Equal(1, top[0].PageNumber);
        Assert.Equal(2, top[1].PageNumber);
        Assert.Empty(top[1].Children);
    }

    [Fact]
    public void HeadingBookmarksCanBeTurnedOff()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A6) { HeadingBookmarks = false })
            AddChapters(layout);
        Assert.False(PdfDocument.Open(doc.ToArray()).HasOutlines);
    }

    [Fact]
    public void HeadingMovedToTheNextPageIsBookmarkedThere()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A6))
        {
            layout.Add("Text");
            layout.AddSpace(layout.RemainingHeight - 5);
            layout.Add(new Paragraph("Late heading", fontSize: 16) { SpacingBefore = 3 }.AsHeading(1));
        }
        var bookmark = Assert.Single(PdfDocument.Open(doc.ToArray()).Outlines.Children);
        Assert.Equal(2, bookmark.PageNumber);
    }

    [Fact]
    public void TaggedHeadingBookmarksHaveStructureDestinations()
    {
        var fontPath = TestHelpers.FindSystemFont();
        if (fontPath == null) return;
        var font = PdfTrueTypeFont.FromFile(fontPath);
        var doc = PdfDocument.Create();
        doc.Info.Title = "Bookmarks";
        doc.Language = "en";
        doc.UAConformance = PdfUAConformance.PdfUA2;
        using (var layout = new LayoutDocument(doc, PageSize.A6))
        {
            layout.Add(new Paragraph("Chapter", font, 16).AsHeading(1));
            layout.Add(new Paragraph("Text", font));
        }
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Empty(reopened.CheckPdfUA());

        var bookmark = Assert.Single(reopened.Outlines.Children);
        Assert.Equal(1, bookmark.PageNumber); // the page destination stays for older viewers
        var action = bookmark.Dictionary.GetDictionary(PdfName.Get("A"))!;
        var target = (PdfDictionary)action.GetArray(PdfName.Get("SD"))![0].Resolve()!;
        Assert.Equal("H1", target.GetName(PdfName.S)!.Value);

        // a bookmark with a plain page destination is reported for PDF/UA-2
        reopened.Outlines.AddChild("Plain", reopened.GetPage(1));
        Assert.Contains(reopened.CheckPdfUA(), issue => issue.Contains("structure destination"));
    }
}
