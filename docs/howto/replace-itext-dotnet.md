# How to replace iText with an Apache-2.0 library in .NET

iText for .NET is mature and powerful, but licensed under the AGPLv3 or a commercial licence: an application that
uses it under the AGPL and is distributed or offered over a network must publish its own source code. For a closed
product that means a commercial licence. [miPDFlib](https://github.com/Mibuw/miPDFlib) is an alternative under the
Apache-2.0 licence – free in closed commercial products, without revenue limits or licence keys – for the typical
iText tasks: forms, digital signatures (PAdES), text extraction, merging, stamping and PDF/A.

This how-to shows the most common tasks side by side. The complete API mapping is in the
[migration guide](../MIGRATION.md); an honest comparison of the libraries is in [COMPARISON.md](../COMPARISON.md).

```bash
dotnet remove package itext            # and itext.bouncy-castle-adapter, itext7.* packages
dotnet add package miPDFlib
```

## Differences to keep in mind

- **One document class:** iText separates `PdfReader` and `PdfWriter`; miPDFlib has `PdfDocument.Create()` and
  `PdfDocument.Open(..)`, saved with `Save(..)` or `ToArray()` – as often as needed.
- **Incremental saving:** iText's append mode (`StampingProperties.UseAppendMode()`) is
  `new PdfSaveOptions { Incremental = true }`. Signing is always incremental.
- **No BouncyCastle:** signatures use the CMS support in .NET; the `itext.bouncy-castle-adapter` package and the
  BouncyCastle key wrappers are no longer needed. Keys come as `X509Certificate2` or through `IExternalSigner`.
- **Coordinates:** both use points with the origin at the bottom left, so rectangles can be taken over unchanged.
- **Font size:** passed per call (`DrawText(text, font, size, ..)`); font objects are size-independent.

## Fill and flatten a form

```csharp
// iText: PdfAcroForm.GetAcroForm(pdf, true) -> GetField("name").SetValue(..) -> FlattenFields()
using var doc = PdfDocument.Open("application.pdf");
var form = doc.GetForm()!;
form.SetValue("name", "Erika Mustermann");
form.GetField<PdfCheckBox>("accepted")!.IsChecked = true;
form.Flatten();
doc.Save("application-filled.pdf");
```

## Add a stamp without breaking existing signatures

```csharp
// iText: new PdfDocument(reader, writer, new StampingProperties().UseAppendMode()) + PdfCanvas
using var doc = PdfDocument.Open("signed.pdf");
using (var canvas = doc.GetPage(1).GetCanvas())
    canvas.DrawText("Received 2026-11-02", PdfStandardFont.HelveticaBold, 10, 400, 800, PdfColor.Red);
doc.Save("signed-stamped.pdf", new PdfSaveOptions { Incremental = true });
```

The original bytes stay untouched; the change is appended as a new revision.

## Sign with PAdES and a time-stamp

```csharp
// iText: PdfSigner + IExternalSignature (PrivateKeySignature with BouncyCastle key) + TSAClientBouncyCastle
byte[] signed = PdfSigner.Sign(File.ReadAllBytes("contract.pdf"), CertificateSigner.FromPfx("signer.pfx", "password"),
    new PdfSignatureOptions
    {
        Reason = "Approved",
        Rectangle = new PdfRectangle(350, 50, 550, 110),
        TimestampClient = new HttpTimestampClient("http://timestamp.digicert.com"),
    });
byte[] archived = PdfLtv.MakeLta(signed, new HttpTimestampClient("http://timestamp.digicert.com")).Pdf;   // B-LTA
```

HSM and cloud signers implement `IExternalSigner` (the counterpart of iText's `IExternalSignature`); deferred
signing (`IExternalSignatureContainer` + `SignDeferred`) is `PdfSigner.PrepareExternalSignature` +
`CompleteExternalSignature`. Details: [How to sign a PDF with PAdES in .NET](sign-pdf-pades-dotnet.md).

## Extract text

```csharp
// iText: PdfTextExtractor.GetTextFromPage(pdf.GetPage(1))
using var doc = PdfDocument.Open("report.pdf");
string text = doc.GetPage(1).ExtractText();
var hits = doc.GetPage(1).FindText("Total");          // positions of a search term
```

## Merge documents

```csharp
// iText: new PdfMerger(pdf).Merge(source, 1, source.GetNumberOfPages())
using var merged = PdfDocument.Merge("cover.pdf", "report.pdf", "appendix.pdf");
merged.Save("complete.pdf");
```

## What miPDFlib does not do

HTML to PDF (iText pdfHTML), rendering PDF pages to images and OCR are not part of miPDFlib; the
[migration guide](../MIGRATION.md#not-yet-covered-by-mipdflib) lists alternatives. Office documents can be converted
with LibreOffice via Gotenberg (see the [README](../../README.md#office-documents-word-excel-powerpoint)).

## Checklist

1. Replace the packages and namespaces (`MiPdf`, `MiPdf.Forms`, `MiPdf.Signatures`, ..).
2. Map the APIs with the [migration guide](../MIGRATION.md).
3. Remove BouncyCastle code that only served iText (key wrappers, `Pkcs12Store`).
4. Run your tests with your own documents – especially forms and documents with existing signatures.
5. Check signed output with an EU DSS based validator and PDF/A output with [veraPDF](https://verapdf.org).
6. Keep the Apache-2.0 `LICENSE` and `NOTICE` when you redistribute miPDFlib.
