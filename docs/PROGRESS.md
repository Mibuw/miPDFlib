# Progress and roadmap

Last update: 2026-10-29 (release 0.2.2-alpha)

## Phase status (see [PLAN.md](PLAN.md))

| Phase | Status |
|---|---|
| 0 Skeleton, repository, license, CI | ✅ done |
| 1 Object model, parser, filters, writer, repair | ✅ done (real-world PDFs round-tripped, checked with Ghostscript) |
| 2 Document/page model, metadata, incremental saving | ✅ done |
| 3 Canvas, fonts, images, layout | ✅ done (visually checked with Ghostscript) |
| 4 Merge/split/import, stamps, bookmarks, links | ✅ done |
| 5 Text extraction | ✅ done (compared with Ghostscript txtwrite) |
| 6 AcroForms | ✅ done |
| 7 Encryption | ✅ done (interoperability with Ghostscript in both directions) |
| 8 Signatures | ✅ done (PAdES B-B/B-T, external/HSM, two-step, document time-stamps; checked with OpenSSL) |
| 9 PDF/A, NuGet, migration guide | ✅ done (PDF/A-1b/2b/2u/3b/3u, NuGet package with SourceLink, samples, [MIGRATION.md](MIGRATION.md)) |
| 10 Barcodes | ✅ done (QR, EPC QR, Data Matrix incl. GS1, Code 128/GS1-128, EAN/UPC, Code 39, ITF; all decoded with zxing-cpp) |
| 11 Tagged PDF / PDF/UA-1 | ✅ done (structure tree, automatic tagging in the layout engine, artifacts, annotations, XMP, PDF/UA checker) |
| 12 Advanced signature features | ✅ done (custom signed attributes, TSA failover, B-LT/B-LTA, `CmsUtil`, ink appearances, `DrawTextBox`) |
| 13 Hardening | ✅ done (`PdfLimits`, malformed input → `PdfException`, SSRF protection, regression tests, [SECURITY.md](../SECURITY.md); ~6,000 fuzzed documents without crash) |
| 14 Attachments, ZUGFeRD/Factur-X | ✅ done (veraPDF PDF/A-3b passes) |
| 15 More annotation types | ✅ done (markup, free text, stamp, ink, square/circle, line, file attachment, flattening; veraPDF 2b passes) |
| 16 Redaction (true removal) | ✅ done (text per glyph, image pixels, XObjects recursively, vector graphics, annotations/fields; Redact annotations) |
| 17 Layout: lists, row spans, rows across pages, kerning, hyphenation | ✅ done (lists tagged L/LI/Lbl/LBody, veraPDF UA passes) |
| 18 Further features | ✅ done (signature dictionary entries, XFA data, image extraction, free space, push buttons with images, check box styles, PDF/A identification, IgnorePermissions, BMP/GIF) |
| 19 Release | ✅ 0.2.2-alpha: first public release on nuget.org (release workflow on tag `v*`), logo, CHANGELOG |
| 20 Signature algorithms | ✅ done (RSASSA-PSS, ECDSA P-256/P-384/P-521/Brainpool; validated with EU DSS) |
| 21 PDF 2.0 | ✅ reading/saving/signing (PDF Association pdf20examples all open, resave and sign; preamble before `%PDF-`) |
| 22 Field locks (FieldMDP) | ✅ done (244 tests; /Lock + FieldMDP reference, combinable with DocMDP and deferred signing, validator reports changed locked fields; EU DSS reads the lock dictionaries) |

Current state: 244 unit tests on .NET 8 and .NET 10, CI on Linux and Windows.

## Decisions

- **Target frameworks:** net8.0 and net10.0 only, no netstandard2.0 (maintainability).
- **Documentation language:** English.
- **Office → PDF:** not part of the library; LibreOffice via Gotenberg is recommended (see README).

## Roadmap

1. External validation: Adobe Acrobat; signatures from certificates of EU trusted list CAs with EU DSS.
2. Checking the revocation data embedded in documents in `PdfSignatureValidator`.
3. Performance/memory for very large files (stream-based reading instead of loading everything into memory).
4. PDF/A-4, PDF/UA-2 and the PDF 2.0 structure namespaces.

Known gaps: no full evaluation of permitted changes after signing (DocMDP; for FieldMDP the values of locked fields are compared); the tagged structure is not taken
over when importing pages; no text shaping (Arabic/Indic scripts, ligatures); hyphenation patterns are not bundled
(load them from hyph-utf8).

## Verification tools

- PDF/A and PDF/UA: [veraPDF](https://verapdf.org) (`--flavour 1b|2b|2u|3u|ua1`). PDF/A-1b/2b/2u/3u and PDF/UA-1
  pass, also signed with B-LTA and with form fields.
- Signatures: [EU DSS](https://github.com/esig/dss) – all variants cryptographically valid, levels B-B/B-T/B-LT/B-LTA
  recognized (INDETERMINATE only for test CAs that are not on EU trusted lists); OpenSSL `cms -verify`.
- Rendering and syntax: Ghostscript (`-sDEVICE=png16m`, `-sDEVICE=nullpage`).
- A corpus of real-world PDFs is used locally for regression checks; it is not part of the repository.
