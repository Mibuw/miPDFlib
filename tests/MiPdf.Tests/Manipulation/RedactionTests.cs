using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.Redaction;

namespace MiPdf.Tests.Manipulation;

public class RedactionTests
{
    private static string AllDecodedStreams(byte[] pdf)
    {
        var doc = PdfDocument.Open(pdf);
        var sb = new StringBuilder();
        foreach (var n in doc.GetObjectNumbers())
            if (doc.GetObject(n) is PdfStream s) sb.Append(Encoding.Latin1.GetString(s.GetBytes()));
        return sb.ToString();
    }

    private static byte[] CreateDocument()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var header = new PdfFormXObject(doc, new PdfRectangle(0, 0, 300, 30));
        using (var t = header.GetCanvas()) t.DrawText("Briefkopf Max Mustermann GmbH", PdfStandardFont.Helvetica, 12, 0, 10);
        using (var c = page.GetCanvas())
        {
            c.DrawXObject(header, 72, 780);
            c.DrawText("Kunde: Max Mustermann, Wien", PdfStandardFont.Helvetica, 12, 72, 740);
            c.DrawText("IBAN AT61 1904 3002 3457 3201 bitte", PdfStandardFont.Helvetica, 12, 72, 720);
            c.SetFillColor(PdfColor.Red).Rectangle(400, 700, 20, 20).Fill();
            c.DrawImage(PdfImage.FromPixels(4, 4, Enumerable.Repeat((byte)200, 48).ToArray(), 3), 72, 560, 200, 100);
        }
        page.AddTextNote(405, 705, "geheime Notiz");
        doc.GetOrCreateForm().AddTextField(page, "secret", new PdfRectangle(400, 640, 520, 660), "Geheimwert");
        using (var layout = new LayoutDocument(doc, PageSize.A4, new Thickness(72, 400, 72, 72)))
            layout.Add(new Paragraph("Blocksatz zwischen Max Mustermann und der Firma wird hiermit geschlossen und gilt ab sofort für alle.") { Alignment = TextAlignment.Justify });
        return doc.ToArray();
    }

    [Fact]
    public void RedactedTextIsRemovedAndOtherTextKeepsItsPosition()
    {
        var original = CreateDocument();
        var before = PdfDocument.Open(original).GetPage(1);
        var wien = before.FindText("Wien")[0];
        var bitte = before.FindText("bitte")[0];

        var doc = PdfDocument.Open(original);
        var names = doc.RedactText("Mustermann");
        var iban = doc.RedactText(new Regex(@"AT\d{2}(?: \d{4}){4}"));
        Assert.Equal(3 * 10, names.GlyphsRemoved);   // header XObject, customer line, layout paragraph
        Assert.Equal(24, iban.GlyphsRemoved);
        var bytes = doc.ToArray();

        var after = PdfDocument.Open(bytes);
        var text = after.ExtractText();
        Assert.DoesNotContain("Mustermann", text);
        Assert.DoesNotContain("3457", text);
        Assert.Contains("Briefkopf", text);
        Assert.Contains("Wien", text);
        var page = after.GetPage(1);
        Assert.Equal(wien.Left, page.FindText("Wien")[0].Left, 3);
        Assert.Equal(bitte.Left, page.FindText("bitte")[0].Left, 3);
        Assert.DoesNotContain("Mustermann", AllDecodedStreams(bytes)); // also not in unused objects
    }

    [Fact]
    public void AreaRedactionRemovesGraphicsImagesAnnotationsAndFields()
    {
        var doc = PdfDocument.Open(CreateDocument());
        var result = doc.GetPage(1).Redact(new[] { new PdfRectangle(390, 630, 530, 730), new PdfRectangle(150, 590, 300, 630) },
            new RedactionOptions { OverlayText = "X" });
        Assert.Equal(1, result.PathsRemoved);
        Assert.Equal(1, result.ImagesModified);
        Assert.Equal(2, result.AnnotationsRemoved);
        var bytes = doc.ToArray();
        var reopened = PdfDocument.Open(bytes);
        Assert.Empty(reopened.GetPage(1).Annotations);
        Assert.Null(reopened.GetForm()?.GetField("secret"));
        var all = AllDecodedStreams(bytes) + Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("Geheimwert", all);
        Assert.DoesNotContain("geheime", all);

        // image pixels inside the area are black, outside unchanged
        var image = reopened.GetPage(1).Resources.GetDictionary(PdfName.XObject)!.Select(kv => kv.Value.Resolve()).OfType<PdfStream>()
            .Single(s => s.GetName(PdfName.Subtype) == PdfName.Image);
        var pixels = image.GetBytes();
        Assert.Equal(200, pixels[0]);                 // top-left pixel (outside)
        Assert.Contains(pixels, b => b == 0);         // some pixels blacked out
    }

    [Fact]
    public void ImagesCompletelyInsideAreRemovedAndJpegsCannotBePartiallyRedacted()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var jpeg = TestHelpers.CreateJpegHeader(100, 50, 3);
        using (var c = page.GetCanvas())
        {
            c.DrawImage(PdfImage.FromPixels(2, 2, new byte[12], 3), 72, 600, 50, 50);
            c.DrawImage(PdfImage.FromBytes(jpeg), 300, 600, 100, 50);
        }
        var result = PdfDocument.Open(doc.ToArray()) is var d
            ? d.GetPage(1).Redact(new[] { new PdfRectangle(60, 590, 130, 660), new PdfRectangle(350, 610, 360, 620) })
            : null!;
        Assert.Equal(2, result.ImagesRemoved);
        Assert.Single(result.Warnings);
        Assert.Empty(d.GetPage(1).Resources.GetDictionary(PdfName.XObject)!.Keys);
    }

    [Fact]
    public void RedactAnnotationsAreAppliedAndIncrementalSaveIsRejected()
    {
        var doc = PdfDocument.Open(CreateDocument());
        var page = doc.GetPage(1);
        page.MarkForRedaction(page.FindText("Wien")[0], "geschwärzt");
        Assert.Contains(PdfDocument.Open(doc.ToArray(new IO.PdfSaveOptions())).GetPage(1).Annotations, a => a.Subtype == "Redact");
        var result = doc.ApplyRedactions();
        Assert.Equal(4, result.GlyphsRemoved);
        Assert.Throws<PdfException>(() => doc.ToArray(new IO.PdfSaveOptions { Incremental = true }));
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.DoesNotContain("Wien", reopened.GetPage(1).ExtractText());
        Assert.DoesNotContain(reopened.GetPage(1).Annotations, a => a.Subtype == "Redact");
    }

    [Fact]
    public void TrueTypeAndTwoByteFontsAreRedacted()
    {
        var fontPath = TestHelpers.FindSystemFont();
        if (fontPath == null) return;
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas())
            c.DrawText("Grüße an Erika Müller aus Wien", PdfTrueTypeFont.FromFile(fontPath), 12, 72, 700);
        var reopened = PdfDocument.Open(doc.ToArray());
        var wien = reopened.GetPage(1).FindText("Wien")[0];
        Assert.Equal(12, reopened.RedactText("Erika Müller").GlyphsRemoved);
        var final = PdfDocument.Open(reopened.ToArray()).GetPage(1);
        var text = final.ExtractText();
        Assert.DoesNotContain("Erika", text);
        Assert.StartsWith("Grüße an", text);
        Assert.Contains("aus Wien", text);
        Assert.Equal(wien.Left, final.FindText("Wien")[0].Left, 3);
    }
}
