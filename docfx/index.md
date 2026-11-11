---
_layout: landing
---

# miPDFlib

Open-source (Apache-2.0) PDF library for .NET 8 and .NET 10 – pure managed code, no native dependencies.
Create, read, modify, merge, fill forms, extract text, redact, encrypt and digitally sign (PAdES B-B to B-LTA)
PDF documents, produce PDF/A, PDF/UA and ZUGFeRD / XRechnung e-invoices.

```bash
dotnet add package miPDFlib
```

Optional packages: **miPDFlib.Rendering** renders pages to images, **miPDFlib.PostScript** converts PostScript and
EPS to PDF – both in managed code as well.

```csharp
using MiPdf;
using MiPdf.Fonts;
using MiPdf.Layout;

using var pdf = PdfDocument.Create();
using (var layout = new LayoutDocument(pdf, PageSize.A4, new Thickness(50)))
    layout.Add(new Paragraph("Hello miPDFlib", PdfStandardFont.HelveticaBold, 20));
pdf.Save("hello.pdf");
```

## Where to start

- **[Guides](../docs/guide/creating-documents.md)** – creating documents, forms, signatures, text extraction, PDF/A,
  e-invoices and more, with code samples.
- **[How-to guides](../docs/howto/sign-pdf-pades-dotnet.md)** – step-by-step: PAdES signatures, ZUGFeRD invoices,
  replacing iText.
- **[API reference](api/index.md)** – every namespace, class and method, searchable.

Source code, issues and discussions: [github.com/Mibuw/miPDFlib](https://github.com/Mibuw/miPDFlib) ·
package: [nuget.org/packages/miPDFlib](https://www.nuget.org/packages/miPDFlib)
