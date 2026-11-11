# miPDFlib

**Open-source PDF library for .NET - create, edit, sign and validate PDFs without native dependencies.**

[![NuGet](https://img.shields.io/nuget/v/miPDFlib.svg)](https://www.nuget.org/packages/miPDFlib/)
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://github.com/Mibuw/miPDFlib/blob/main/LICENSE)
[![CI](https://github.com/Mibuw/miPDFlib/actions/workflows/ci.yml/badge.svg)](https://github.com/Mibuw/miPDFlib/actions/workflows/ci.yml)

An open-source (Apache-2.0) PDF library for .NET 8 / .NET 10: pure managed code, no native dependencies.
Create, read, modify, merge, fill forms, extract text, encrypt and digitally sign (PAdES) PDF documents.

```bash
dotnet add package miPDFlib
```

The namespace is `MiPdf` (`using MiPdf;`). Versions up to 0.2.3-alpha were published under the package ID `MiPdf`,
which is deprecated; switch the package reference to `miPDFlib`, no code changes are needed.

**Why miPDFlib?**

- **Apache-2.0:** free in closed commercial products - no AGPL obligations, no revenue limits, no licence key.
- **eIDAS-ready signatures:** PAdES B-B to B-LTA, HSM/cloud signers and two-step signing, built on .NET cryptography
  without BouncyCastle.
- **Archiving and e-invoices:** PDF/A, PDF/UA and ZUGFeRD / Factur-X / XRechnung hybrid invoices.
- **Coming from iText or Syncfusion?** See the [comparison](https://github.com/Mibuw/miPDFlib/blob/main/docs/COMPARISON.md)
  (also with PDFBox, PdfPig, PDFsharp/MigraDoc and QuestPDF) and the [migration guide](https://github.com/Mibuw/miPDFlib/blob/main/docs/MIGRATION.md).

> **Versioning:** all planned feature areas are implemented and tested. Releases follow Semantic Versioning; since 1.0
> the public API is stable, breaking changes only in a new major version (see the [changelog](https://github.com/Mibuw/miPDFlib/blob/main/CHANGELOG.md)).

## Features

| Area | Highlights |
|---|---|
| **Core** | tolerant parser (xref tables/streams, object streams, automatic repair), lazy loading, full and **incremental** saving, compact output with object streams, files of any size read on demand (also > 2 GB); **diagnostics** for tolerated problems in damaged files |
| **Creating content** | canvas (paths, colors RGB/CMYK/gray, transparency, transformations), standard 14 fonts, **TrueType/OpenType embedding with subsetting** (full Unicode), JPEG/PNG images incl. alpha |
| **Layout** | paragraphs with mixed formatting, justification, kerning and hyphenation; Arabic, Hebrew and other right-to-left text (shaping, bidi), Indic scripts (Devanagari, Bengali, Tamil, ...), optional ligatures; lists; tables with repeated headers, column and row spans and rows split across pages; images, barcodes, automatic page breaks, header/footer with "page x of y" |
| **Manipulation** | merge, split, import/reorder/rotate/delete pages, page templates (letterheads), watermarks, bookmarks, links, **layers** (optional content), **annotations** (highlight/underline/strike-out, free text, stamps, ink, shapes, file attachments, flattening) |
| **Redaction** | true removal of text, image pixels, vector graphics, annotations and form fields in areas, by text or regular expression |
| **Text extraction** | reading order, rotated text, ToUnicode/encodings/glyph names, words and lines with positions, text search (string/regex), **image extraction** (JPEG, JPEG 2000, JBIG2 and CCITT fax images decoded), **free-space detection** |
| **Rendering** | separate package **miPDFlib.Rendering**: pages to PNG or RGBA pixels at any resolution – vector graphics, text (embedded fonts or substitutes), images, shadings, patterns, blend modes, soft masks, transparency groups, spot colors; managed code only |
| **PostScript / EPS** | separate package **miPDFlib.PostScript**: PostScript to PDF (LanguageLevel 3 with fonts, images, patterns, shadings, pdfmark) and EPS graphics placed as vectors; sandboxed interpreter, managed code only |
| **Forms** | read/fill/create AcroForm fields (text, check box, radio, combo, list, push button, signature) with appearance generation, flattening, merging; FDF/XFDF import and export; XFA data |
| **Encryption** | open and create encrypted PDFs: AES-256, AES-128, RC4; passwords and permissions, or encryption for the certificates of recipients |
| **Signatures** | PAdES B-B/B-T/**B-LT/B-LTA**, RFC 3161 and document time-stamps, visible signatures, certification (DocMDP), field locks (FieldMDP), **HSM/cloud signers**, deferred two-step signing, multiple signatures, verification |
| **PDF/A** | PDF/A-1b/2b/2u/3b/3u/4/4f output (XMP, sRGB output intent) and a basic conformance checker |
| **Accessibility** | tagged PDF, **PDF/UA-1** and **PDF/UA-2**: automatic tagging by the layout engine, manual tagging API, PDF/UA checker |
| **Attachments / e-invoices** | embedded files with PDF/A-3 associated files, ZUGFeRD 2.x / Factur-X / XRechnung hybrid invoices, consistency check of the visible invoice against the XML |
| **Hardening** | resource limits against decompression bombs and CPU exhaustion, SSRF-safe revocation fetching, fuzz-tested parser |
| **Barcodes** | QR Code, EPC/GiroCode, Data Matrix (incl. GS1), PDF417, Code 128/GS1-128, EAN-13/8, UPC-A, Code 39, ITF (vector output) |

The only dependency is `System.Security.Cryptography.Pkcs` (MIT, Microsoft) for CMS signatures.

## Quick start

```csharp
using MiPdf;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Layout;
using MiPdf.Signatures;

// create
using var pdf = PdfDocument.Create();
using (var layout = new LayoutDocument(pdf, PageSize.A4, new Thickness(50)))
{
    layout.Add(new Paragraph("Hello miPDFlib", PdfStandardFont.HelveticaBold, 20));
    var table = new Table(1, 3);
    table.AddHeaderCell("Pos"); table.AddHeaderCell("Description");
    table.AddCell("1"); table.AddCell("Consulting");
    layout.Add(table);
}
pdf.Save("hello.pdf");

// modify
using var doc = PdfDocument.Open("hello.pdf");
doc.GetPage(1).AddTextWatermark("DRAFT");
doc.Save("hello-draft.pdf");

// sign
byte[] signed = PdfSigner.Sign(File.ReadAllBytes("hello.pdf"), CertificateSigner.FromPfx("me.pfx", "pw"),
    new PdfSignatureOptions { Reason = "Approved", TimestampClient = new HttpTimestampClient("http://timestamp.digicert.com") });
```

## Documentation

**[API reference and guides](https://mibuw.github.io/miPDFlib/)** – every namespace, class and method, searchable.

**How-to guides:**
[Sign a PDF with PAdES in .NET](https://github.com/Mibuw/miPDFlib/blob/main/docs/howto/sign-pdf-pades-dotnet.md) ·
[Create a ZUGFeRD / XRechnung invoice in C#](https://github.com/Mibuw/miPDFlib/blob/main/docs/howto/zugferd-xrechnung-invoice-csharp.md) ·
[Replace iText with an Apache-2.0 library](https://github.com/Mibuw/miPDFlib/blob/main/docs/howto/replace-itext-dotnet.md)

Sample ASP.NET Core web API with Dockerfile: [samples/MiPdf.WebApi](https://github.com/Mibuw/miPDFlib/blob/main/samples/MiPdf.WebApi/README.md)

- [Creating documents](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/creating-documents.md): fonts, canvas, images, flow layout, tables
- [Opening and manipulating](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/manipulating-documents.md): save options, pages, merge/split, stamps, bookmarks, links
- [Text extraction](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/text-extraction.md): text, words, positions, search
- [Redaction](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/redaction.md): removing confidential content
- [Forms](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/forms.md): read, fill, create and flatten form fields
- [Security](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/security.md): passwords, permissions, AES/RC4 encryption
- [Signatures](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/signatures.md): PAdES signing, HSM/external signers, time-stamps, verification
- [Accessibility / PDF/UA](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/accessibility.md): tagged PDF, PDF/UA, checker
- [Attachments and e-invoices](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/attachments.md): embedded files, ZUGFeRD / Factur-X
- [Barcodes](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/barcodes.md): QR, EPC payment codes, Data Matrix, PDF417, Code 128, EAN
- [Migration from iText / Syncfusion](https://github.com/Mibuw/miPDFlib/blob/main/docs/MIGRATION.md) and [comparison](https://github.com/Mibuw/miPDFlib/blob/main/docs/COMPARISON.md)
- Articles: [ZUGFeRD in .NET](https://www.mitterbucher.com/en/articles/zugferd-e-invoice-dotnet/) (e-invoices),
  [miPDFlib or aiPDFlib?](https://www.mitterbucher.com/en/articles/mipdflib-aipdflib/) (how it was built) and
  [iText or Syncfusion PDF?](https://www.mitterbucher.com/en/articles/itext-vs-syncfusion-pdf/) (now including miPDFlib)
- [Office documents to PDF](https://github.com/Mibuw/miPDFlib#office-documents-word-excel-powerpoint): recommended conversion with LibreOffice via Gotenberg

## Used by

- [miPDFsign Community](https://github.com/Mibuw/miPDFsign-Community) - open-source (Apache-2.0) signature app for
  Windows tablets: handwritten signatures with biometric data as PAdES B to LTA
- miPDFsign and miPDFsign Cloud - commercial signing app and service by the author
  ([mitterbucher.com](https://www.mitterbucher.com))

## License

Licensed under the **Apache License, Version 2.0**. See [LICENSE](https://github.com/Mibuw/miPDFlib/blob/main/LICENSE)
and [NOTICE](https://github.com/Mibuw/miPDFlib/blob/main/NOTICE). Copyright 2026 Wolfgang Mitterbucher.

Source code, issues and contributions: [github.com/Mibuw/miPDFlib](https://github.com/Mibuw/miPDFlib).

## Author

**Wolfgang Mitterbucher**, Software Engineering & Digital Identity, Leonding (Austria):
[www.mitterbucher.com](https://www.mitterbucher.com) · [LinkedIn](https://at.linkedin.com/in/wolfgangmitterbucher) · office@mitterbucher.com
