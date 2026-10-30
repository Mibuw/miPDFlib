<p align="center">
  <img src="https://raw.githubusercontent.com/Mibuw/miPDFlib/main/assets/miPDFlib_logo.png" width="140" alt="miPDFlib logo">
</p>

<h1 align="center">miPDFlib</h1>

<p align="center">
  <strong>Open-source PDF library for .NET – create, edit, sign and validate PDFs without native dependencies.</strong>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/miPDFlib/"><img src="https://img.shields.io/nuget/v/miPDFlib.svg" alt="NuGet"></a>
  <a href="https://www.nuget.org/packages/miPDFlib/"><img src="https://img.shields.io/nuget/dt/miPDFlib.svg" alt="NuGet downloads"></a>
  <a href="https://github.com/Mibuw/miPDFlib/blob/main/LICENSE"><img src="https://img.shields.io/badge/License-Apache_2.0-blue.svg" alt="License"></a>
  <a href="https://github.com/Mibuw/miPDFlib/actions/workflows/ci.yml"><img src="https://github.com/Mibuw/miPDFlib/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <img src="https://img.shields.io/badge/.NET-8.0_%7C_10.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 8 | 10">
</p>

**On NuGet:** `dotnet add package miPDFlib`

An open-source (Apache-2.0) PDF library for .NET 8 / .NET 10 – pure managed code, no native dependencies.
Create, read, modify, merge, fill forms, extract text, encrypt and digitally sign (PAdES) PDF documents.

> Status: **0.3.0** – first stable release: all planned feature areas are implemented and tested (254 unit tests, real-world
> corpus checks with Ghostscript/OpenSSL, PDF/A and PDF/UA output validated with veraPDF). See [docs/PROGRESS.md](https://github.com/Mibuw/miPDFlib/blob/main/docs/PROGRESS.md).

## Features

| Area | Highlights |
|---|---|
| **Core** | tolerant parser (xref tables/streams, object streams, automatic repair), lazy loading, full and **incremental** saving, compact output with object streams |
| **Creating content** | canvas (paths, colors RGB/CMYK/gray, transparency, transformations), standard 14 fonts, **TrueType/OpenType embedding with subsetting** (full Unicode), JPEG/PNG images incl. alpha |
| **Layout** | paragraphs with mixed formatting, justification, kerning and hyphenation; lists (nested, numbered); tables with repeated headers, column and row spans and rows split across pages; images, barcodes, automatic page breaks, header/footer with "page x of y" |
| **Manipulation** | merge, split, import/reorder/rotate/delete pages, page templates (letterheads), watermarks, bookmarks, links, **annotations** (highlight/underline/strike-out, free text, stamps, ink, shapes, file attachments, flattening) |
| **Redaction** | true removal of text, image pixels, vector graphics, annotations and form fields in areas, by text or regular expression |
| **Text extraction** | reading order, rotated text, ToUnicode/encodings/glyph names, words and lines with positions, text search (string/regex), **image extraction** (PNG/JPEG with transparency), **free-space detection** |
| **Forms** | read/fill/create AcroForm fields (text, check box with styles, radio, combo, list, push button with image, signature) with appearance generation, flattening, merging; XFA data |
| **Encryption** | open and create encrypted PDFs: AES-256, AES-128, RC4; passwords and permissions |
| **Signatures** | PAdES B-B/B-T/**B-LT/B-LTA** (CAdES detached, signing-certificate-v2, DSS with OCSP/CRL), custom signed attributes, handwritten ink appearances, TSA failover, RFC 3161 time-stamps, document time-stamps, visible signatures, certification (DocMDP), field locks (FieldMDP), **HSM/cloud signers**, deferred two-step signing, multiple signatures, verification |
| **PDF/A** | PDF/A-1b/2b/2u/3b/3u output (XMP, sRGB output intent) and a basic conformance checker |
| **Accessibility** | tagged PDF and **PDF/UA-1**: automatic tagging by the layout engine (headings, paragraphs, tables, figures, artifacts), manual tagging API, PDF/UA checker |
| **Attachments / e-invoices** | embedded files with PDF/A-3 associated files, ZUGFeRD 2.x / Factur-X / XRechnung hybrid invoices |
| **Hardening** | resource limits against decompression bombs and CPU exhaustion, SSRF-safe revocation fetching, fuzz-tested parser – see [SECURITY.md](https://github.com/Mibuw/miPDFlib/blob/main/SECURITY.md) |
| **Barcodes** | QR Code, EPC/GiroCode, Data Matrix (incl. GS1), Code 128/GS1-128, EAN-13/8, UPC-A, Code 39, ITF – vector output |

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

More examples: [samples/MiPdf.Samples](https://github.com/Mibuw/miPDFlib/blob/main/samples/MiPdf.Samples/Program.cs) (`dotnet run --project samples/MiPdf.Samples`).

## Office documents (Word, Excel, PowerPoint)

miPDFlib is a PDF library and does not convert Office formats. For converting `.docx`, `.xlsx`, `.pptx`
(also `.doc`, `.xls`, `.ppt`, `.rtf`, `.odt`, ...) to PDF we recommend **LibreOffice via
[Gotenberg](https://gotenberg.dev)** (MIT, LibreOffice MPL-2.0): a Docker container with a simple HTTP API.
Convert the document there and process or sign the resulting PDF with miPDFlib:

```csharp
// docker run --rm -p 3000:3000 gotenberg/gotenberg:8
using var http = new HttpClient { BaseAddress = new Uri("http://gotenberg:3000"), Timeout = TimeSpan.FromMinutes(2) };
using var form = new MultipartFormDataContent();
form.Add(new ByteArrayContent(File.ReadAllBytes("contract.docx")), "files", "contract.docx");
form.Add(new StringContent("PDF/A-2b"), "pdfa");                       // optional: PDF/A output
var response = await http.PostAsync("/forms/libreoffice/convert", form);
response.EnsureSuccessStatusCode();
byte[] pdf = await response.Content.ReadAsByteArrayAsync();

byte[] signed = PdfSigner.Sign(pdf, signer, options);
```

Notes:

- Run the container in an internal network without internet access, limit file size and conversion time.
- Install the fonts used by your documents in the container (or metric-compatible ones such as Carlito for
  Calibri and Caladea for Cambria) – missing fonts are the most common cause of layout differences.
- LibreOffice renders Office documents very well, but not always pixel-identical to Microsoft Office.

## Documentation

- [Creating documents](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/creating-documents.md) – fonts, canvas, images, flow layout, tables
- [Opening and manipulating](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/manipulating-documents.md) – save options, pages, merge/split, stamps, bookmarks, links
- [Text extraction](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/text-extraction.md) – text, words, positions, search
- [Redaction](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/redaction.md) – removing confidential content
- [Forms](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/forms.md) – read, fill, create and flatten form fields
- [Security](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/security.md) – passwords, permissions, AES/RC4 encryption
- [Signatures](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/signatures.md) – PAdES signing, HSM/external signers, time-stamps, verification
- [Accessibility / PDF/UA](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/accessibility.md) – tagged PDF, PDF/UA, checker
- [Attachments and e-invoices](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/attachments.md) – embedded files, ZUGFeRD / Factur-X
- [Barcodes](https://github.com/Mibuw/miPDFlib/blob/main/docs/guide/barcodes.md) – QR, EPC payment codes, Data Matrix, Code 128, EAN
- [Migration from iText / Syncfusion](https://github.com/Mibuw/miPDFlib/blob/main/docs/MIGRATION.md)
- [Architecture](https://github.com/Mibuw/miPDFlib/blob/main/docs/ARCHITECTURE.md) – internal structure for contributors
- [Project plan](https://github.com/Mibuw/miPDFlib/blob/main/docs/PLAN.md) and [progress](https://github.com/Mibuw/miPDFlib/blob/main/docs/PROGRESS.md)

## Installation

The package [`miPDFlib`](https://www.nuget.org/packages/miPDFlib/) is published on nuget.org:

```bash
dotnet add package miPDFlib
```

The namespace is `MiPdf` (`using MiPdf;`). Versions up to 0.2.3-alpha were published under the package ID `MiPdf`,
which is deprecated; switch the package reference to `miPDFlib`, no code changes are needed.

Every release is also available as a [GitHub release](https://github.com/Mibuw/miPDFlib/releases) with the
`.nupkg` and symbol package attached.

> **Versioning:** releases follow [Semantic Versioning](https://semver.org). Until 1.0 the public API may still change
> between minor versions (0.3 → 0.4); such changes are listed in
> [CHANGELOG.md](https://github.com/Mibuw/miPDFlib/blob/main/CHANGELOG.md). There is no guaranteed support.

## Building

```bash
dotnet build
dotnet test
dotnet pack src/MiPdf -c Release   # NuGet package incl. symbols (SourceLink)
```

## Contributing

Bug reports, ideas and pull requests are welcome – see [CONTRIBUTING.md](https://github.com/Mibuw/miPDFlib/blob/main/CONTRIBUTING.md). Pull requests require
a one-time [Contributor License Agreement](https://github.com/Mibuw/miPDFlib/blob/main/CLA.md) (accepted by a comment on the pull request). Security problems
are reported privately as described in [SECURITY.md](https://github.com/Mibuw/miPDFlib/blob/main/SECURITY.md).

## License

Licensed under the **Apache License, Version 2.0**. See [LICENSE](https://github.com/Mibuw/miPDFlib/blob/main/LICENSE) and [NOTICE](https://github.com/Mibuw/miPDFlib/blob/main/NOTICE).

Copyright 2026 Wolfgang Mitterbucher.

miPDFlib is a clean-room implementation based on the public ISO 32000, ISO 19005, ETSI EN 319 142 and IETF
specifications; it contains no code from iText or Syncfusion. The only dependency
(`System.Security.Cryptography.Pkcs`, MIT) is compatible with this license.

---

## Author

**Wolfgang Mitterbucher** — Software Engineering & Digital Identity, Leonding (Austria)

🌐 [www.mitterbucher.com](https://www.mitterbucher.com) · 💼 [LinkedIn](https://at.linkedin.com/in/wolfgangmitterbucher) · ✉️ office@mitterbucher.com

**More open-source projects:** [miPDFconvert](https://github.com/Mibuw/miPDFconvert) · [miPDFvalidator](https://github.com/Mibuw/miPDFvalidator) · [miEUDIverifier](https://github.com/Mibuw/miEUDIverifier)
