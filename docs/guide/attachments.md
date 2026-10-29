# Attachments and e-invoices (ZUGFeRD / Factur-X)

## Embedded files

```csharp
doc.AddAttachment("data.csv", bytes, "text/csv", "Raw data", AttachmentRelationship.Data);

foreach (var a in doc.Attachments)
    Console.WriteLine($"{a.FileName} {a.MimeType} {a.Size} {a.Relationship}");
byte[] content = doc.GetAttachment("data.csv")!.GetData();

doc.RemoveAttachment("data.csv");
```

Attachments are stored in the `EmbeddedFiles` name tree with MIME type, size, MD5 checksum and dates, and listed
as associated files (`/AF`) with an `/AFRelationship` – as required by PDF/A-3. `CheckPdfA(PdfA3B)` reports
attachments without these entries. PDF/A-1 and PDF/A-2 do not allow arbitrary attachments.

## ZUGFeRD 2.x / Factur-X hybrid invoices

```csharp
using var pdf = PdfDocument.Create();
pdf.Info.Title = "Invoice 2026-0042";
// render the invoice with embedded fonts (PdfTrueTypeFont) – required for PDF/A
FacturX.Attach(pdf, File.ReadAllBytes("factur-x.xml"), FacturXProfile.EN16931);
pdf.Save("invoice.pdf");
```

`FacturX.Attach`

- switches the document to PDF/A-3b (unless PDF/A-3u is already set),
- embeds the XML as `factur-x.xml` (`xrechnung.xml` for the XRechnung profile) with MIME type `text/xml` and
  `/AFRelationship /Alternative` (`/Data` for MINIMUM and BASIC WL),
- writes the Factur-X XMP extension schema (`fx:DocumentType`, `DocumentFileName`, `Version`, `ConformanceLevel`)
  including the PDF/A extension schema declaration,
- rejects XML that is not well-formed (DTDs are not processed).

Reading: `FacturX.GetInvoiceXml(doc)` and `FacturX.GetProfile(doc)` (also finds `zugferd-invoice.xml`).
Creating the CII XML itself (invoice content) is up to the application. The PDF/A-3b output is validated with
veraPDF; check the XML with a ZUGFeRD/XRechnung validator (e.g. Mustang, KoSIT validator).
