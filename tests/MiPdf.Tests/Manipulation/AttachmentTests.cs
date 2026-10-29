using System.Text;
using MiPdf.Fonts;
using MiPdf.Invoicing;
using MiPdf.Objects;
using MiPdf.PdfA;

namespace MiPdf.Tests.Manipulation;

public class AttachmentTests
{
    private const string Cii = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rsm:CrossIndustryInvoice " +
        "xmlns:rsm=\"urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100\"><rsm:ExchangedDocument/></rsm:CrossIndustryInvoice>";

    [Fact]
    public void AddReadAndRemoveAttachments()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddAttachment("b-data.csv", Encoding.UTF8.GetBytes("a;b\n1;2"), "text/csv", "Daten", AttachmentRelationship.Data);
        doc.AddAttachment("ä-notiz.txt", Encoding.UTF8.GetBytes("Grüße"), "text/plain");
        doc.AddAttachment("b-data.csv", Encoding.UTF8.GetBytes("replaced"), "text/csv"); // replaces

        var reopened = PdfDocument.Open(doc.ToArray());
        var attachments = reopened.Attachments;
        Assert.Equal(2, attachments.Count);
        var csv = reopened.GetAttachment("b-data.csv")!;
        Assert.Equal("replaced", Encoding.UTF8.GetString(csv.GetData()));
        Assert.Equal("text/csv", csv.MimeType);
        Assert.Equal(8, csv.Size);
        Assert.NotNull(csv.ModificationDate);
        var note = reopened.GetAttachment("ä-notiz.txt")!;
        Assert.Equal("Grüße", Encoding.UTF8.GetString(note.GetData()));
        Assert.Equal(AttachmentRelationship.Unspecified, note.Relationship);
        Assert.Equal(2, reopened.Catalog.GetArray(PdfName.Get("AF"))!.Count);

        Assert.True(reopened.RemoveAttachment("b-data.csv"));
        Assert.False(reopened.RemoveAttachment("missing"));
        var third = PdfDocument.Open(reopened.ToArray());
        Assert.Single(third.Attachments);
        Assert.Single(third.Catalog.GetArray(PdfName.Get("AF"))!);
    }

    [Fact]
    public void FacturXCreatesPdfA3WithXmpExtension()
    {
        var fontPath = TestHelpers.FindSystemFont();
        if (fontPath == null) return;
        var doc = PdfDocument.Create();
        doc.Info.Title = "Rechnung";
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("Rechnung", PdfTrueTypeFont.FromFile(fontPath), 14, 72, 700);
        FacturX.Attach(doc, Encoding.UTF8.GetBytes(Cii), FacturXProfile.EN16931);
        Assert.Equal(PdfAConformance.PdfA3B, doc.Conformance);
        Assert.Empty(doc.CheckPdfA(PdfAConformance.PdfA3B));

        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Empty(reopened.CheckPdfA(PdfAConformance.PdfA3B));
        var xml = Encoding.UTF8.GetString(FacturX.GetInvoiceXml(reopened)!);
        Assert.Contains("CrossIndustryInvoice", xml);
        Assert.Equal(FacturXProfile.EN16931, FacturX.GetProfile(reopened));
        var attachment = reopened.GetAttachment("factur-x.xml")!;
        Assert.Equal(AttachmentRelationship.Alternative, attachment.Relationship);
        Assert.Equal("text/xml", attachment.MimeType);

        var xmp = Encoding.UTF8.GetString(reopened.GetXmpMetadata()!);
        Assert.Contains("<fx:ConformanceLevel>EN 16931</fx:ConformanceLevel>", xmp);
        Assert.Contains("<fx:DocumentFileName>factur-x.xml</fx:DocumentFileName>", xmp);
        Assert.Contains("<pdfaSchema:prefix>fx</pdfaSchema:prefix>", xmp);
        Assert.Contains("<pdfaid:part>3</pdfaid:part>", xmp);
        System.Xml.Linq.XDocument.Parse(xmp[xmp.IndexOf("<x:xmpmeta", StringComparison.Ordinal)..(xmp.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12)]);
    }

    [Fact]
    public void FacturXProfilesAndValidation()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        FacturX.Attach(doc, Encoding.UTF8.GetBytes(Cii), FacturXProfile.Minimum);
        Assert.Equal(AttachmentRelationship.Data, doc.GetAttachment("factur-x.xml")!.Relationship);
        FacturX.Attach(doc, Encoding.UTF8.GetBytes(Cii), FacturXProfile.XRechnung);
        Assert.Null(doc.GetAttachment("factur-x.xml"));
        Assert.NotNull(doc.GetAttachment("xrechnung.xml"));
        Assert.Single(doc.XmpExtensions);
        Assert.Equal("BASIC WL", FacturX.ConformanceLevel(FacturXProfile.BasicWL));
        Assert.Throws<ArgumentException>(() => FacturX.Attach(doc, Encoding.UTF8.GetBytes("<not xml"), FacturXProfile.Basic));
        Assert.Throws<ArgumentException>(() => FacturX.Attach(doc,
            Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>"), FacturXProfile.Basic));
    }

    [Fact]
    public void PdfA3CheckerRequiresAssociatedFiles()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddAttachment("x.bin", new byte[] { 1 }, "application/octet-stream");
        doc.Catalog.Remove(PdfName.Get("AF"));
        var issues = PdfDocument.Open(doc.ToArray()).CheckPdfA(PdfAConformance.PdfA3B);
        Assert.Contains(issues, i => i.Contains("not an associated file"));
    }
}
