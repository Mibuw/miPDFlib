# miPDFlib – project plan and approach

Date: 2026-10-28 · Status: in progress

## 1. Goal

A self-contained, open-source .NET PDF library (**Apache-2.0**) that can replace the commercial or AGPL-licensed
libraries **iText 7/8** and **Syncfusion PDF** in .NET applications.

Guidelines:

- **Clean-room implementation** based solely on public specifications (ISO 32000-1/-2, ETSI EN 319 142 (PAdES),
  RFC 5652 (CMS), RFC 3161 (time-stamps), OpenType/TrueType specification). No code from iText (AGPL) or Syncfusion.
- **No native dependencies.** The only external dependency is `System.Security.Cryptography.Pkcs` (Microsoft, MIT)
  for CMS/PKCS#7 and RFC 3161 time-stamps.
- **Target platforms:** .NET 8 (LTS) and .NET 10 (LTS). Runs on Windows, Linux, macOS and in containers.
- **Tolerant reading, strict writing:** broken PDFs are repaired where possible, generated PDFs conform to the
  specification.

## 2. Scope (compared with iText / Syncfusion)

| Area | Typical iText/Syncfusion usage | miPDFlib phase |
|---|---|---|
| PDF object model, parser, writer | internal | 1 |
| Xref tables, xref streams, object streams, repair | internal | 1 |
| Compression (Flate, predictors, LZW, ASCII85, ASCIIHex, RunLength) | internal | 1 |
| Open/create/save documents, incremental saving | `PdfDocument`, `PdfLoadedDocument` | 2 |
| Pages: add, delete, rotate, reorder, page sizes | `PdfPage` | 2 |
| Metadata (info dictionary, XMP) | `PdfDocumentInfo` | 2 |
| Drawing: lines, rectangles, paths, colors, transformations | `PdfCanvas`, `PdfGraphics` | 3 |
| Text with the standard 14 fonts (WinAnsi incl. umlauts) | `PdfFont`, `PdfStandardFont` | 3 |
| TrueType/OpenType embedding + subsetting (full Unicode) | `PdfTrueTypeFont` | 3 |
| Images: JPEG, PNG (incl. transparency) | `ImageData`, `PdfBitmap` | 3 |
| Text layout: line breaking, alignment, paragraphs, simple tables | `Paragraph`, `Table`, `PdfGrid` | 3 |
| Merge, split, import pages | `PdfMerger`, `ImportPage` | 4 |
| Stamps / watermarks / overlays on existing pages | `PdfCanvas` on an existing page | 4 |
| Bookmarks (outlines), links, annotations | `PdfOutline`, `PdfLinkAnnotation` | 4 |
| Text extraction | `PdfTextExtractor`, `ExtractText()` | 5 |
| AcroForms: read, fill, create, flatten fields | `PdfAcroForm`, `PdfLoadedForm` | 6 |
| Read/write encryption (RC4, AES-128, AES-256), permissions | `WriterProperties.SetStandardEncryption`, `PdfSecurity` | 7 |
| Digital signatures (PKCS#7, CAdES/PAdES B-B, B-T with time-stamp) | `PdfSigner`, `PdfSignature` | 8 |
| Visible signatures, multiple signatures, external signing (HSM/remote) | `IExternalSignature`, `IExternalSignatureContainer` | 8 |
| Signature verification (integrity, certificate, coverage) | `SignatureUtil` | 8 |
| PDF/A basics (XMP, output intent, embedding requirements) | `PdfADocument`, `PdfConformanceLevel` | 9 |

**Deliberately not in the first scope** (separate projects or later phases): rendering PDF → image, HTML → PDF,
Office → PDF, OCR, PAdES B-LT/B-LTA (DSS/VRI, LTV), complete PDF/A and PDF/UA validation, XFA forms.
(B-LT/B-LTA, PDF/UA and XFA data were added later, see [PROGRESS.md](PROGRESS.md).)

## 3. Architecture

```
MiPdf
├── Objects/      basic PDF objects: Null, Boolean, Number, String, Name, Array,
│                 Dictionary, Stream, Reference (+ serialization)
├── IO/           lexer, object parser, reader (xref, repair), writer (full / incremental)
├── Filters/      Flate (+PNG/TIFF predictors), LZW, ASCII85, ASCIIHex, RunLength
├── Document/     PdfDocument, PdfPage, page tree, document info, outlines
├── Content/      content stream parsing
├── Graphics/     PdfCanvas (content stream generation)
├── Fonts/        standard 14 (metrics), TrueType parser, subsetter, CID fonts, ToUnicode
├── Images/       JPEG and PNG decoding/embedding
├── Layout/       line breaking, paragraphs, tables
├── Text/         text extraction
├── Forms/        AcroForm model, appearance generation, flattening
├── Security/     standard security handler (R2–R6), RC4, AES
└── Signatures/   signing (CMS/CAdES), time-stamps, external signing, verification
```

Core principles:

- **One object model for reading and writing.** An opened document is loaded lazily; changed objects are tracked so
  that both full and **incremental** saving are possible (required for signatures that must not break earlier
  signatures).
- **Layers:** a low-level API (objects/dictionaries, full control like iText's `PdfDictionary`) and a high-level API
  (pages, canvas, forms, signer).
- **Streams first:** input and output via `Stream`, convenience methods for file paths and `byte[]`.

## 4. Approach

Development proceeds **phase by phase, bottom-up**. Every phase ends with:

1. an error-free build for all target frameworks,
2. unit tests (xUnit) for the new functionality, including **round-trip tests**
   (create → save → read again with the own parser → check),
3. updated documentation (`docs/`, XML documentation in code, README),
4. a commit to the repository.

Quality assurance across all phases:

- Generated test documents are additionally checked with external viewers/validators (Ghostscript, Adobe Reader,
  pdf.js, qpdf `--check`, veraPDF for PDF/A and PDF/UA, EU DSS for signatures).
- A corpus of real-world PDFs serves as regression test for the parser (kept locally, not in the repository).
- Migration guide (`docs/MIGRATION.md`) mapping iText/Syncfusion → miPDFlib.

## 5. Phases and milestones

| Phase | Content | Result |
|---|---|---|
| 0 | Repository, license, solution, test project, plan, CI | skeleton |
| 1 | Object model, lexer/parser, filters, writer, reader incl. xref streams and repair | read and rewrite PDFs losslessly |
| 2 | Document/page model, metadata, incremental saving | create and modify documents |
| 3 | Canvas, fonts (standard 14 + TrueType subsetting), images, text layout, tables | generate reports/documents |
| 4 | Merge/split/import, stamps, bookmarks, links, annotations | document manipulation |
| 5 | Text extraction | read text |
| 6 | Read/fill/create/flatten AcroForms | form processing |
| 7 | Encryption (read and write) | protected PDFs |
| 8 | Signatures (PAdES B-B/B-T, visible, external, multiple, verification) | legally compliant signatures |
| 9 | PDF/A basics, polishing, NuGet package, migration guide | release 1.0 candidate |

## 6. Risks

| Risk | Mitigation |
|---|---|
| Broken/exotic PDFs from third-party systems | tolerant parser with xref repair, corpus of real files |
| Font complexity (CFF, complex scripts, bidi) | TrueType first, CFF/OpenType embedded as a whole; shaping later |
| Signature conformance (Adobe/EU DSS validation) | follow ETSI EN 319 142, validate with EU DSS and Adobe Reader |
| Size of iText's layout engine | implement only the required layout features (paragraphs, tables, lists) |
