using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Tests.Manipulation;

public class ManipulationTests
{
    private static PdfDocument CreateDoc(string label, int pages)
    {
        var doc = PdfDocument.Create();
        for (int i = 1; i <= pages; i++)
            using (var c = doc.AddPage().GetCanvas())
                c.DrawText($"{label}-{i}", PdfStandardFont.Helvetica, 12, 72, 700);
        return PdfDocument.Open(doc.ToArray());
    }

    private static string Text(PdfPage page) => Encoding.Latin1.GetString(page.GetContentBytes());

    [Fact]
    public void MergeKeepsPageOrderAndContent()
    {
        var merged = PdfDocument.Merge(new[] { CreateDoc("A", 2), CreateDoc("B", 3) });
        var reopened = PdfDocument.Open(merged.ToArray());
        Assert.Equal(5, reopened.PageCount);
        Assert.Contains("(A-1)", Text(reopened.GetPage(1)));
        Assert.Contains("(B-1)", Text(reopened.GetPage(3)));
        Assert.Contains("(B-3)", Text(reopened.GetPage(5)));
        // fonts of the copied pages are resolvable in the new document
        Assert.Equal("Helvetica", reopened.GetPage(4).Resources.GetDictionary(PdfName.Font)!.GetDictionary("F1")!.GetName(PdfName.BaseFont)!.Value);
    }

    [Fact]
    public void SharedResourcesAreCopiedOnce()
    {
        var source = CreateDoc("S", 4);
        var target = PdfDocument.Create();
        target.ImportPages(source);
        var fonts = target.Pages.Select(p => p.Resources.GetDictionary(PdfName.Font)!["F1"]).Distinct().ToList();
        Assert.Single(fonts);
    }

    [Fact]
    public void SplitCreatesIndependentDocuments()
    {
        var parts = CreateDoc("P", 5).Split(2).ToList();
        Assert.Equal(new[] { 2, 2, 1 }, parts.Select(p => p.PageCount));
        var last = PdfDocument.Open(parts[2].ToArray());
        Assert.Contains("(P-5)", Text(last.GetPage(1)));
    }

    [Fact]
    public void ExtractedPagesDoNotDragInOtherPagesThroughLinks()
    {
        var doc = CreateDoc("L", 3);
        doc.GetPage(1).AddLink(new PdfRectangle(0, 0, 10, 10), doc.GetPage(3));
        doc.GetPage(1).AddLink(new PdfRectangle(0, 20, 10, 30), doc.GetPage(2));
        var extracted = doc.ExtractPages(new[] { 1, 2 });
        var bytes = extracted.ToArray();
        var reopened = PdfDocument.Open(bytes);
        Assert.Equal(2, reopened.PageCount);
        var annots = reopened.GetPage(1).Annotations;
        Assert.Null(reopened.ResolveDestinationPage(annots[0].Dictionary.Get(PdfName.Dest))); // page 3 was not extracted
        Assert.Equal(2, reopened.ResolveDestinationPage(annots[1].Dictionary.Get(PdfName.Dest)));
        Assert.DoesNotContain("(L-3)", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void DuplicatePageWithinDocument()
    {
        var doc = CreateDoc("D", 1);
        doc.ImportPages(doc, new[] { 1 });
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Equal(2, reopened.PageCount);
        Assert.NotSame(reopened.GetPage(1).Dictionary, reopened.GetPage(2).Dictionary);
        Assert.Contains("(D-1)", Text(reopened.GetPage(2)));
    }

    [Fact]
    public void BookmarksRoundTrip()
    {
        var doc = CreateDoc("B", 3);
        var chapter = doc.Outlines.AddChild("Kapitel 1", doc.GetPage(1));
        chapter.AddChild("Abschnitt 1.1", doc.GetPage(2), top: 500).Bold = true;
        chapter.AddChild("Abschnitt 1.2", doc.GetPage(3));
        doc.Outlines.AddChild("Anhang – Ü", doc.GetPage(3)).Color = PdfColor.Red;

        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.True(reopened.HasOutlines);
        var top = reopened.Outlines.Children;
        Assert.Equal(new[] { "Kapitel 1", "Anhang – Ü" }, top.Select(o => o.Title));
        Assert.Equal(2, top[0].Children.Count);
        Assert.Equal(2, top[0].Children[0].PageNumber);
        Assert.True(top[0].Children[0].Bold);
        Assert.Equal(4, reopened.Outlines.Dictionary.GetInt(PdfName.Count));

        top[1].Remove();
        Assert.Single(reopened.Outlines.Children);
        Assert.Equal(3, reopened.Outlines.Dictionary.GetInt(PdfName.Count));
    }

    [Fact]
    public void LinksAndNotes()
    {
        var doc = CreateDoc("N", 2);
        doc.GetPage(1).AddLink(new PdfRectangle(10, 10, 100, 30), "https://example.com");
        doc.GetPage(1).AddTextNote(200, 200, "Kommentar", "Max");
        var page = PdfDocument.Open(doc.ToArray()).GetPage(1);
        Assert.Equal(2, page.Annotations.Count);
        Assert.Equal("https://example.com", page.Annotations[0].Uri);
        Assert.Equal("Kommentar", page.Annotations[1].Contents);
        Assert.True(page.RemoveAnnotation(page.Annotations[0]));
        Assert.Single(page.Annotations);
    }

    [Fact]
    public void WatermarkOnRotatedPageCompensatesRotation()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        page.Rotation = 90;
        page.AddTextWatermark("X", angle: 0);
        // rotation by 90° in user space: cos=0, sin=1
        Assert.Contains("0 1 -1 0 0 0 cm", Text(page));
    }

    [Fact]
    public void PageAsTemplate()
    {
        var source = CreateDoc("T", 1);
        source.GetPage(1).Rotation = 90;
        var target = PdfDocument.Create();
        var template = target.ImportPageAsTemplate(source.GetPage(1));
        Assert.Equal(842, template.Width);
        Assert.Equal(595, template.Height);
        using (var c = target.AddPage(PageSize.Landscape(PageSize.A4)).GetCanvas()) c.DrawXObject(template, 0, 0);
        var reopened = PdfDocument.Open(target.ToArray());
        var xobj = reopened.GetPage(1).Resources.GetDictionary(PdfName.XObject)!.GetStream("Fm1")!;
        Assert.Contains("(T-1)", Encoding.Latin1.GetString(xobj.GetBytes()));
        Assert.NotNull(xobj.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Font));
    }

    [Fact]
    public void IncrementalUpdateAfterStamping()
    {
        var original = CreateDoc("I", 2).ToArray();
        var doc = PdfDocument.Open(original);
        doc.GetPage(2).AddTextWatermark("KOPIE");
        var updated = doc.ToArray(new IO.PdfSaveOptions { Incremental = true });
        Assert.True(updated.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.Contains("(KOPIE)", Text(PdfDocument.Open(updated).GetPage(2)));
        Assert.DoesNotContain("(KOPIE)", Text(PdfDocument.Open(updated).GetPage(1)));
    }
}
