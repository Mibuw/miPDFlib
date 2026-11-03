using MiPdf.Objects;

namespace MiPdf.Tests.Core;

public class ViewerAndPageLabelTests
{
    private static PdfDocument Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToArray());

    private static PdfDocument WithPages(int count)
    {
        var doc = PdfDocument.Create();
        for (int i = 0; i < count; i++) doc.AddPage();
        return doc;
    }

    [Fact]
    public void PageSizesMatchTheStandards()
    {
        Assert.Equal(new PdfRectangle(0, 0, 2384, 3370), PageSize.A0);
        Assert.Equal(new PdfRectangle(0, 0, 298, 420), PageSize.A6);
        Assert.Equal(new PdfRectangle(0, 0, 709, 1001), PageSize.B4);
        Assert.Equal(new PdfRectangle(0, 0, 792, 1224), PageSize.Tabloid);
        Assert.Equal(PageSize.Ledger, PageSize.Landscape(PageSize.Tabloid));
        Assert.Equal(new PdfRectangle(0, 0, 522, 756), PageSize.Executive);
        // each ISO size is the next larger one halved
        Assert.InRange(PageSize.A0.Height / 2 - PageSize.A1.Width, 0, 1);
        Assert.Equal(PageSize.A2.Height, PageSize.A1.Width);
        Assert.Equal(PageSize.A4.Height, PageSize.A3.Width);
        foreach (var size in new[] { PageSize.A0, PageSize.A1, PageSize.A2, PageSize.A6, PageSize.B4, PageSize.B5 })
            Assert.InRange(size.Height / size.Width, 1.40, 1.43);
    }

    [Fact]
    public void LandscapeKeepsTheLowerLeftCorner()
    {
        Assert.Equal(new PdfRectangle(0, 0, 842, 595), PageSize.Landscape(PageSize.A4));
        Assert.Equal(new PdfRectangle(10, 20, 110, 70), PageSize.Landscape(new PdfRectangle(10, 20, 60, 120)));
    }

    [Fact]
    public void ViewerSettingsDefaultToThePdfDefaults()
    {
        var doc = WithPages(1);
        Assert.Equal(PdfPageLayout.SinglePage, doc.PageLayout);
        Assert.Equal(PdfPageMode.UseNone, doc.PageMode);
        Assert.False(doc.ViewerPreferences.HideToolbar);
        Assert.Null(doc.ViewerPreferences.Dictionary);
        doc.ViewerPreferences.FitWindow = false;
        Assert.Null(doc.ViewerPreferences.Dictionary);
    }

    [Fact]
    public void ViewerSettingsAreSavedAndRead()
    {
        var doc = WithPages(2);
        doc.PageLayout = PdfPageLayout.TwoColumnLeft;
        doc.PageMode = PdfPageMode.UseThumbs;
        var prefs = doc.ViewerPreferences;
        prefs.HideToolbar = prefs.HideMenubar = prefs.HideWindowUI = prefs.FitWindow = prefs.CenterWindow = prefs.DisplayDocTitle = true;

        var saved = Reopen(doc);
        Assert.Equal(PdfName.Get("TwoColumnLeft"), saved.Catalog.GetName(PdfName.Get("PageLayout")));
        Assert.Equal(PdfPageLayout.TwoColumnLeft, saved.PageLayout);
        Assert.Equal(PdfPageMode.UseThumbs, saved.PageMode);
        var p = saved.ViewerPreferences;
        Assert.True(p.HideToolbar && p.HideMenubar && p.HideWindowUI && p.FitWindow && p.CenterWindow && p.DisplayDocTitle);

        p.HideToolbar = false;
        Assert.False(Reopen(saved).ViewerPreferences.HideToolbar);
        Assert.False(saved.ViewerPreferences.Dictionary!.ContainsKey(PdfName.Get("HideToolbar")));
    }

    [Fact]
    public void UnknownViewerValuesReadAsDefaults()
    {
        var doc = WithPages(1);
        doc.Catalog[PdfName.Get("PageLayout")] = PdfName.Get("Spiral");
        doc.Catalog[PdfName.Get("PageMode")] = PdfName.Get("usethumbs");
        Assert.Equal(PdfPageLayout.SinglePage, doc.PageLayout);
        Assert.Equal(PdfPageMode.UseNone, doc.PageMode);
    }

    [Fact]
    public void CreatingBookmarksShowsTheBookmarksPanel()
    {
        var doc = WithPages(1);
        doc.Outlines.AddChild("Start", doc.GetPage(1));
        Assert.Equal(PdfPageMode.UseOutlines, doc.PageMode);
    }

    [Fact]
    public void PageLabelsAreWrittenAndRead()
    {
        var doc = WithPages(8);
        doc.PageLabels.Add(startPage: 1, PdfPageLabelStyle.LowerRoman);
        doc.PageLabels.Add(startPage: 5, PdfPageLabelStyle.Decimal);
        doc.PageLabels.Add(startPage: 8, PdfPageLabelStyle.UpperLetters, prefix: "Appendix ", startNumber: 2);

        var saved = Reopen(doc);
        Assert.Equal(new[] { "i", "ii", "iii", "iv", "1", "2", "3", "Appendix B" },
            Enumerable.Range(1, 8).Select(saved.GetPageLabel));
        Assert.Equal(3, saved.PageLabels.Count);
        Assert.Equal(new PdfPageLabelRange(8, PdfPageLabelStyle.UpperLetters, "Appendix ", 2), saved.PageLabels[2]);

        // flat number tree with 0-based page indexes
        var nums = saved.Catalog.GetDictionary(PdfName.Get("PageLabels"))!.GetArray(PdfName.Get("Nums"))!;
        Assert.Equal(new[] { 0, 4, 7 }, new[] { nums[0], nums[2], nums[4] }.Select(n => ((PdfNumber)n).IntValue));
        Assert.Equal(PdfName.Get("r"), ((PdfDictionary)nums[1]).GetName(PdfName.S));
    }

    [Fact]
    public void PageLabelsCanBeReplacedAndRemoved()
    {
        var doc = WithPages(3);
        Assert.Equal("2", doc.GetPageLabel(2)); // no labels: the page number
        doc.PageLabels.Add(1, PdfPageLabelStyle.Decimal, "A-");
        doc.PageLabels.Add(1, PdfPageLabelStyle.None, "Cover");
        Assert.Equal("Cover", doc.GetPageLabel(3));
        Assert.Single(doc.PageLabels);
        Assert.False(doc.PageLabels.Remove(2));
        Assert.True(doc.PageLabels.Remove(1));
        Assert.False(doc.Catalog.ContainsKey(PdfName.Get("PageLabels")));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.PageLabels.Add(0, PdfPageLabelStyle.Decimal));
    }

    [Fact]
    public void PageLabelNumberTreesWithKidsAreRead()
    {
        var doc = WithPages(6);
        PdfDictionary Label(string style, int start = 1)
        {
            var d = new PdfDictionary { [PdfName.S] = PdfName.Get(style) };
            if (start != 1) d.Set(PdfName.Get("St"), start);
            return d;
        }
        var left = new PdfDictionary { [PdfName.Get("Nums")] = new PdfArray(new PdfNumber(0), Label("R")) };
        var right = new PdfDictionary { [PdfName.Get("Nums")] = new PdfArray(new PdfNumber(2), Label("D", 10), new PdfNumber(4), Label("a", 27)) };
        doc.Catalog[PdfName.Get("PageLabels")] = new PdfDictionary
        {
            [PdfName.Kids] = new PdfArray(doc.AddObject(left), doc.AddObject(right)),
        };
        var saved = Reopen(doc);
        Assert.Equal(new[] { "I", "II", "10", "11", "aa", "bb" }, Enumerable.Range(1, 6).Select(saved.GetPageLabel));
    }
}
