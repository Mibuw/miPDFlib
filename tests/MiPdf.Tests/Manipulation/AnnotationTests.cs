using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.PdfA;

namespace MiPdf.Tests.Manipulation;

public class AnnotationTests
{
    private static (PdfDocument Doc, PdfPage Page) Create()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        using (var c = page.GetCanvas())
        {
            c.DrawText("Dieser Vertrag wird genehmigt.", PdfStandardFont.Helvetica, 12, 72, 760);
            c.DrawText("Wichtiger Hinweis und noch ein Hinweis.", PdfStandardFont.Helvetica, 12, 72, 740);
        }
        return (doc, page);
    }

    [Fact]
    public void TextMarkupAnnotationsCoverFoundText()
    {
        var (doc, page) = Create();
        var highlights = page.HighlightText("Hinweis", options: new AnnotationOptions { Author = "Max", Contents = "prüfen" });
        Assert.Equal(2, highlights.Count);
        page.AddTextMarkup(TextMarkupType.StrikeOut, page.FindText("genehmigt"));
        page.AddTextMarkup(TextMarkupType.Underline, page.FindText("Vertrag"));
        page.AddTextMarkup(TextMarkupType.Squiggly, page.FindText("Wichtiger"));

        var reopened = PdfDocument.Open(doc.ToArray()).GetPage(1);
        var annots = reopened.Annotations;
        Assert.Equal(new[] { "Highlight", "Highlight", "StrikeOut", "Underline", "Squiggly" }, annots.Select(a => a.Subtype));
        var first = annots[0];
        Assert.Equal("Max", first.Author);
        Assert.Equal("prüfen", first.Contents);
        var quads = first.Dictionary.GetArray(PdfName.Get("QuadPoints"))!;
        Assert.Equal(8, quads.Count);
        var bounds = page.FindText("Hinweis")[0];
        Assert.Equal(bounds.Left, quads.GetDouble(0), 3);
        Assert.Equal(bounds.Top, quads.GetDouble(1), 3);
        Assert.All(annots, a => Assert.NotNull(a.Dictionary.GetDictionary(PdfName.AP)?.GetStream(PdfName.N)));
        Assert.Equal(4, annots[0].Flags);
    }

    [Fact]
    public void ShapesStampsInkAndAttachments()
    {
        var (doc, page) = Create();
        page.AddFreeText(new PdfRectangle(72, 600, 300, 650), "Freitext", fontSize: 11, border: PdfColor.Blue);
        page.AddStamp(new PdfRectangle(350, 600, 540, 650), "GENEHMIGT", options: new AnnotationOptions { Locked = true });
        page.AddImageStamp(new PdfRectangle(350, 540, 400, 590), MiPdf.Images.PdfImage.FromPixels(1, 1, new byte[3], 3));
        page.AddInk(new[] { new[] { (72.0, 500.0), (100.0, 520.0), (130.0, 500.0) }, new[] { (150.0, 510.0) } });
        page.AddRectangleAnnotation(new PdfRectangle(200, 480, 260, 520), fill: PdfColor.Gray(0.9));
        page.AddEllipseAnnotation(new PdfRectangle(280, 480, 340, 520));
        page.AddLineAnnotation(72, 450, 300, 450, start: LineEnding.Circle, end: LineEnding.ClosedArrow);
        page.AddFileAttachmentAnnotation(72, 420, "daten.csv", Encoding.UTF8.GetBytes("a;b"), "text/csv", "Rohdaten");
        page.AddTextNote(120, 420, "Notiz");

        var reopened = PdfDocument.Open(doc.ToArray()).GetPage(1);
        var annots = reopened.Annotations;
        Assert.Equal(new[] { "FreeText", "Stamp", "Stamp", "Ink", "Square", "Circle", "Line", "FileAttachment", "Text" }, annots.Select(a => a.Subtype));
        Assert.All(annots, a => Assert.NotNull(a.Dictionary.GetDictionary(PdfName.AP)?.GetStream(PdfName.N)));
        Assert.Equal("Freitext", annots[0].Contents);
        Assert.Equal(4 | 128, annots[1].Flags);
        Assert.Equal("GENEHMIGT", annots[1].Dictionary.GetName(PdfName.Name)!.Value);
        Assert.Equal(2, annots[3].Dictionary.GetArray(PdfName.Get("InkList"))!.Count);
        Assert.Equal(new[] { "Circle", "ClosedArrow" }, annots[6].Dictionary.GetArray(PdfName.Get("LE"))!.Select(n => ((PdfName)n).Value));
        var fs = annots[7].Dictionary.GetDictionary(PdfName.Get("FS"))!;
        Assert.Equal("a;b", Encoding.UTF8.GetString(fs.GetDictionary(PdfName.Get("EF"))!.GetStream(PdfName.F)!.GetBytes()));
        Assert.Contains("Supplement", fs.GetName(PdfName.Get("AFRelationship"))!.Value);
    }

    [Fact]
    public void FlattenAnnotationsDrawsAppearances()
    {
        var (doc, page) = Create();
        page.AddFreeText(new PdfRectangle(72, 600, 300, 650), "Flach gemacht");
        page.AddLink(new PdfRectangle(72, 700, 200, 720), "https://example.com");
        page.HighlightText("Vertrag");
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Equal(3, reopened.GetPage(1).FlattenAnnotations());
        var final = PdfDocument.Open(reopened.ToArray()).GetPage(1);
        Assert.Empty(final.Annotations);
        Assert.Contains("Flach gemacht", final.ExtractText());
    }

    [Fact]
    public void AnnotationsInPdfADocuments()
    {
        var fontPath = TestHelpers.FindSystemFont();
        if (fontPath == null) return;
        var font = PdfTrueTypeFont.FromFile(fontPath);
        var doc = PdfDocument.Create();
        doc.Conformance = PdfAConformance.PdfA2B;
        var page = doc.AddPage();
        using (var c = page.GetCanvas()) c.DrawText("Text", font, 12, 72, 700);
        page.HighlightText("Text");
        page.AddFreeText(new PdfRectangle(72, 600, 300, 650), "Freitext", font);
        page.AddStamp(new PdfRectangle(350, 600, 540, 650), "OK", font: font);
        page.AddTextNote(72, 550, "Notiz");
        Assert.Empty(doc.CheckPdfA(PdfAConformance.PdfA2B));
        page.AddFileAttachmentAnnotation(72, 500, "x.txt", new byte[] { 65 });
        Assert.Contains(doc.CheckPdfA(PdfAConformance.PdfA2B), i => i.Contains("require PDF/A-3"));
    }

    [Fact]
    public void TextOfUnsavedDocumentsCanBeExtracted()
    {
        var fontPath = TestHelpers.FindSystemFont();
        if (fontPath == null) return;
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        using (var c = page.GetCanvas()) c.DrawText("Grüße aus Wien", PdfTrueTypeFont.FromFile(fontPath), 12, 72, 700);
        Assert.Equal("Grüße aus Wien", page.ExtractText().Trim());
        Assert.Single(page.FindText("aus Wien"));
        using (var c = page.GetCanvas()) c.DrawText("Zweite Zeile", PdfTrueTypeFont.FromFile(fontPath), 12, 72, 680);
        Assert.Contains("Zweite Zeile", page.ExtractText());
        Assert.Contains("Zweite Zeile", PdfDocument.Open(doc.ToArray()).GetPage(1).ExtractText());
    }
}
