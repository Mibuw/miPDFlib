# miPDFlib compared with other PDF libraries

A short, practical comparison: first with the commercial libraries iText and Syncfusion PDF for the tasks that
matter in form and signing projects, then with the open-source and community alternatives
[Apache PDFBox, PdfPig, PDFsharp/MigraDoc and QuestPDF](#open-source-and-community-alternatives). It is not a
benchmark. A
detailed version with background and examples is the article
[iText or Syncfusion PDF? A practical comparison for forms and digital signatures in .NET](https://www.mitterbucher.com/en/articles/itext-vs-syncfusion-pdf/)
([German](https://www.mitterbucher.com/artikel/itext-vs-syncfusion-pdf/)). For switching code over, see the
[migration guide](MIGRATION.md).

iText and Syncfusion PDF are mature, widely used libraries with a much larger feature set and commercial support.
miPDFlib is young; its strengths are the licence, signatures without BouncyCastle and archiving formats.

## Licence

| | iText | Syncfusion PDF | miPDFlib |
|---|---|---|---|
| Licence | AGPLv3 or commercial | commercial; free Community License for small companies (revenue, team and capital limits) | Apache-2.0 |
| Closed commercial product | commercial licence required | commercial licence, or Community License within its limits | allowed, free of charge |
| Obligations | AGPL: disclose the source of your application; producer line in every PDF | licence key registration, otherwise a trial watermark | keep LICENSE and NOTICE when redistributing |
| Commercial support | yes | yes | no |

## PDF handling

| | iText | Syncfusion PDF | miPDFlib |
|---|---|---|---|
| Incremental saving | `StampingProperties.UseAppendMode()` | `PdfFileStructure.IncrementalUpdate = true` | `new PdfSaveOptions { Incremental = true }`, also for encrypted and repaired files; signing is always incremental |
| Office documents to PDF | – | in the same suite | – (recommended: LibreOffice via Gotenberg, see README) |

## Forms (AcroForms)

| Task | iText | Syncfusion PDF | miPDFlib |
|---|---|---|---|
| Load the form | `PdfAcroForm.GetAcroForm(doc, …)` | `PdfLoadedDocument.Form` | `doc.GetForm()` |
| Read fields | `GetAllFormFields()` | typed classes such as `PdfLoadedTextBoxField` | `form.Fields`, typed via `GetField<PdfTextField>("name")` |
| Create fields | builder classes, e.g. `TextFormFieldBuilder` | a class per field type, e.g. `PdfTextBoxField` | `form.AddTextField(..)`, `AddCheckBox(..)`, `AddSignatureField(..)` |
| Flatten | via the AcroForm object | `Flatten = true` / `FlattenFields()` | `form.Flatten()` |
| Data exchange | field API | additionally FDF import/export | `form.GetValues()`, FDF and XFDF import/export, XFA data |

## Digital signatures

| Topic | iText | Syncfusion PDF | miPDFlib |
|---|---|---|---|
| Signing | `PdfSigner`, CMS or CAdES | `PdfSignature`, CMS or CAdES | `PdfSigner.Sign`, CMS or CAdES (default: PAdES) |
| PAdES levels | `PdfPadesSigner`: B, T, LT, LTA | timestamps, LTV, archive timestamps | B-B, B-T; B-LT and B-LTA via `PdfLtv` |
| External signing (HSM, cloud) | `IExternalSignature`, `IExternalSignatureContainer` | `ComputeHash` event, `IPdfExternalSigner` | `IExternalSigner`; two-step `PrepareExternalSignature` / `CompleteExternalSignature` |
| Custom signed attributes | via your own CMS container | via `ComputeHash` and your own CMS | `AdditionalSignedAttributes` |
| Certification and field locks | DocMDP, FieldMDP | DocMDP | DocMDP (`Certification`), FieldMDP (`FieldLock`) |
| Validation | validation module (since 9) | built in | `PdfSignatureValidator` (integrity, chain, coverage, timestamp, PAdES level, field locks, revocation status from the embedded OCSP responses and CRLs) |
| Cryptography | BouncyCastle (mandatory adapter) | own implementation; BouncyCastle or .NET for custom CMS | .NET (`System.Security.Cryptography.Pkcs`), no BouncyCastle |

## Open-source and community alternatives

Apache PDFBox is the established open-source library in the Java world; PdfPig, PDFsharp/MigraDoc and QuestPDF are
widely used in .NET. Each has a different focus, and in several areas they are ahead of miPDFlib.

| | Apache PDFBox 3.0 | PdfPig 0.1 | PDFsharp 6.5 / MigraDoc | QuestPDF 2026.9 | miPDFlib |
|---|---|---|---|---|---|
| Platform, licence | Java, Apache-2.0 | .NET, Apache-2.0 | .NET, AGPL-3.0 or commercial (since 6.5; MIT up to 6.2) | .NET, Community License (free below USD 1M revenue, with exclusions) or commercial | .NET, Apache-2.0 |
| Native dependencies | none | none | none | Skia, qpdf, HarfBuzz (bundled) | none |
| Create PDFs | low-level content | low-level, simple text | low-level (PDFsharp), layout engine (MigraDoc) | layout engine | low-level canvas and layout engine |
| Modify, merge, split | yes, incremental saving | merge; incremental updates only in prerelease builds (objects added or replaced by hand; not for encrypted or repaired files) | yes | merge, overlay via qpdf | yes; incremental saving with automatic change detection, also of encrypted and repaired files |
| Text extraction | with positions | with positions and layout analysis | – | – | with positions, reading order (also across columns); letters with font, colors and rendering mode; layout analysis (headings, paragraphs, lists, tables, headers and footers) |
| Render pages to images, print | **yes** | – | – | only its own documents | to images and PNG with the package miPDFlib.Rendering; printing is left to the application |
| AcroForms | fill, create, flatten | read only | fill, flatten | – | fill, create, flatten, FDF/XFDF |
| Signatures | signing framework; PAdES levels and LTV only as example code with BouncyCastle; no validation | – | CMS (`adbe.pkcs7.detached`), optional time-stamp; no LTV, no validation | – | PAdES B-B to B-LTA, LTV, validation, no BouncyCastle |
| Encryption | password (AES-256), certificate | decrypt only | password (AES-256) | password via qpdf | password (AES-256), certificate |
| PDF/A output | manual (example for PDF/A-1b) | A-1, A-2, A-3 (a/b) | `SetPdfA()`, level not selectable | A-2, A-3 (a/b/u) | A-1b, A-2b/u, A-3b/u, **A-4/4f** |
| PDF/UA | manual tagging | – | manual tagging, PDF/UA-1 | semantic API, PDF/UA-1 | automatic tagging by the layout engine, **PDF/UA-1 and PDF/UA-2** |
| Optional content (layers) | **yes** | read | – | – | create, read, switch |
| CCITT / JBIG2 / JPEG 2000 images | decoded (JBIG2/JPX via plugins) | CCITT only | – | – | **all decoded** (and JPEG), built in, no native libraries |
| Complex scripts (Arabic, Indic) | partly (some Indic scripts) | – | – | **yes** (HarfBuzz) | **yes**: Arabic, Hebrew and other right-to-left scripts, nine Indic scripts (own managed shaper, bidi, GSUB/GPOS; Indic output identical to HarfBuzz in tests) |
| ZUGFeRD, redaction, barcodes | – | – | barcodes | PDF/A-3 attachments for ZUGFeRD | all three |
| PostScript and EPS to PDF | – | – | – | – | **yes**, with the package miPDFlib.PostScript (LanguageLevel 3, sandboxed, managed code) |

In short: **PDFBox** is the choice for rendering and printing and the most complete low-level toolkit, but in Java
and with signatures assembled from examples. **PdfPig** is strong at reading and analysing text. **MigraDoc** and
**QuestPDF** are good at document generation; QuestPDF also handles complex scripts, but needs native libraries and a
paid licence above its revenue limit, and since 6.5 PDFsharp is AGPL or commercial. miPDFlib covers creating,
modifying, forms, signatures and archiving in one managed library; it renders pages with the package
miPDFlib.Rendering, converts PostScript and EPS with the package miPDFlib.PostScript and shapes Arabic, Hebrew and
Indic text without native libraries.

Information as of November 2026, from the projects' documentation, release notes and source code; the binding
licence terms are those of the projects.

## When to choose what

- **Open-source project under the AGPL:** iText – mature, with very comprehensive signature support.
- **Closed product of a small company within the Community License limits:** Syncfusion PDF.
- **Office conversion in the same workflow:** Syncfusion.
- **Commercial support, FIPS or the widest feature set:** iText or Syncfusion with a commercial licence.
- **Rendering PDFs to images or printing:** PDFBox (Java); in .NET miPDFlib.Rendering (managed code only) or a
  PDFium-based library.
- **Reading and analysing text in .NET:** PdfPig (several layout analysis algorithms, export to ALTO, hOCR and PAGE),
  or miPDFlib (reading order across columns, layout analysis with headings, lists and tables, next to everything
  else in the same library).
- **PostScript or EPS to PDF without Ghostscript (AGPL) and without native code:** miPDFlib.PostScript.
- **Document generation with Arabic, Hebrew or Indic scripts:** QuestPDF within its licence terms, or miPDFlib
  (managed code only).
- **Closed product without licence costs and without revenue or size limits, focused on forms, PAdES signatures,
  PDF/A, PDF/UA or e-invoices:** miPDFlib – test it with your own documents before going live.

Information on iText and Syncfusion as of October 2026, from the vendors' documentation; the binding licence terms
are those of the vendors.
