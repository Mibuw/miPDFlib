# Progress and roadmap

Last update: 2026-11-05

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
| 19 Release | ✅ 0.2.2-alpha: first public release on nuget.org (release workflow on tag `v*`), logo, CHANGELOG; 0.3.0: first stable (non-prerelease) release |
| 20 Signature algorithms | ✅ done (RSASSA-PSS, ECDSA P-256/P-384/P-521/Brainpool; validated with EU DSS) |
| 21 PDF 2.0 | ✅ reading/saving/signing (PDF Association pdf20examples all open, resave and sign; preamble before `%PDF-`) |
| 22 Field locks (FieldMDP) | ✅ done (244 tests; /Lock + FieldMDP reference, combinable with DocMDP and deferred signing, validator reports changed locked fields; EU DSS reads the lock dictionaries; Acrobat flags a changed locked field as invalid) |
| 23 Widget borders and rotation | ✅ done (253 tests; fields without border, widget rotation/colors with rebuilt appearances incl. check boxes and radio buttons, signature appearance upright on rotated pages) |
| 24 E-invoice checks | ✅ done (273 tests; `FacturX.CheckConsistency` compares the embedded XML with the visible text, `Attach` rejects a profile that contradicts the guideline ID, `FacturX.DetectProfile`) |
| 25 Stored revocation data | ✅ done (281 tests; validator evaluates DSS and adbe-revocationInfoArchival OCSP/CRL with signature checks of the evidence and delegated responders; tested with real DigiCert OCSP responses and CRLs) |
| 26 Form data exchange | ✅ done (286 tests; FDF and XFDF export/import with hierarchical names, multiple selections, unknown-field and error reporting; XFDF without DTD processing) |
| 27 API reference | ✅ DocFX site on GitHub Pages (https://mibuw.github.io/miPDFlib/), rebuilt on every push to main |
| 28 API review fixes | ✅ done (289 tests; redaction overlay texts per mark, field locks of encrypted documents, CMYK outline colors, time-stamp password no longer readable) |
| 29 Memory for large files | ✅ done (320 tests; shared stream data, saving without in-memory copies, signing and validation from the digest of the byte ranges, reading on demand from the file with 64-bit offsets: a 2.5 GB file is opened with 35 MB and saved/signed/validated with under 50 MB, see `benchmarks/`; EU DSS: RSA, PSS, ECDSA, PKCS#7, B-T and `SignFile` valid) |
| 30 API cleanup and small features (0.6.0) | ✅ done (340 tests; issues #7–#10 and #13: page labels, viewer settings, page sizes, redaction and split samples, consistent API before 1.0) |
| 31 PDF/A-4, PDF/UA-2, API baseline (1.0.0) | ✅ done (359 tests; PDF/A-4/4f and PDF/UA-2 with the PDF 2.0 structure namespace, validated with veraPDF also after signing; public API recorded with PublicApiAnalyzers; links in running text; bookmarks for headings; trust anchors from EU trusted lists) |
| 32 Changes after signing (DocMDP) | ✅ done (368 tests; `ChangesAfterSigning` / `HasDisallowedChanges`: object-level comparison of the signed revision with the file, classified and evaluated by certification level; checked with signatures of miPDFlib and PDF-Over) |
| 33 Structure on page import | ✅ done (374 tests; `ImportPages`/`Merge`/`ExtractPages`/`Split` copy the structure elements of the pages with a rebuilt parent tree; veraPDF PDF/UA-1/2 conformant; no marked content without owner after extract/merge of the tagged corpus files) |
| 34 Certificate encryption | ✅ done (382 tests; public-key security handler `/Adobe.PubSec` with `adbe.pkcs7.s5`: encrypting for recipients' certificates and opening with the private key, AES-256/AES-128/RC4, permissions per document; cross-checked with Apache PDFBox 3.0.8 in both directions, also its older V1/V2 format and restricted permissions) |
| 35 Layers (optional content) | ✅ done (388 tests; create, read and switch layers, content and annotations on layers, text extraction without hidden layers, PDF/A rules; veraPDF PDF/A-2b conformant, Ghostscript hides the hidden layers) |
| 36 JPEG and CCITT decoding (1.1.0) | ✅ done (409 tests; JPEG baseline/progressive identical to libjpeg-turbo for the test variants and all 17 corpus images, 12 MP in about 0.3 s; CCITT G3 1D/2D and G4 identical to Ghostscript; partial redaction of scans, PNG export) |
| 37 Rendering, stage 1 (core) | ✅ done (package `miPDFlib.Rendering`, preview: anti-aliased rasterizer, fills, strokes with joins/caps/dashes, clipping, colors, opacity, images, form XObjects, annotations, layers; mean difference to Ghostscript below one level; 85 corpus pages without errors, about 30 ms per page at 150 dpi) |
| 38 Rendering, stage 2 (fonts and text) | ✅ done (429 tests; glyph outlines of TrueType/OpenType, CFF (also CID-keyed), Type 1 and Type 3 fonts, all text rendering modes, substitutes for non-embedded fonts; corpus compared with Ghostscript: text pages identical apart from hinting; fixed DCT-encoded soft masks, crop box offsets and page size rounding; ZapfDingbats Unicode table for text extraction) |

Current state: 409 unit tests on .NET 8 and .NET 10, CI on Linux and Windows.

## Decisions

- **Target frameworks:** net8.0 and net10.0 only, no netstandard2.0 (maintainability).
- **Documentation language:** English.
- **Names:** NuGet package ID `miPDFlib` (the project name; up to 0.2.3-alpha `MiPdf`, now deprecated), namespace
  and assembly `MiPdf` following the .NET naming guidelines.
- **Office → PDF:** not part of the library; LibreOffice via Gotenberg is recommended (see README).
- **E-invoice XML:** not generated by the library; the application (or a dedicated CII library) creates the
  XML, miPDFlib embeds it, checks profile and consistency with the visible invoice, and signs.

## Roadmap

1.0.0 is released; since then, changes after signing (DocMDP) are evaluated and page import keeps the tagged
structure, documents can be encrypted for certificates, layers are supported and JPEG and CCITT images are
decoded (1.1.0, released). Next steps:

1. **Rendering** (pages to images, e.g. for previews and printing) as a separate package `miPDFlib.Rendering` in
   this repository: managed code only, no native dependencies and no System.Drawing; output as bitmaps/PNG at any
   resolution (printing itself is left to the application). Checked page by page against Ghostscript renderings
   of the corpus (pixel comparison). In stages:
   1. Core: anti-aliased rasterizer (nonzero/even-odd), stroking with joins, caps and dashes, clipping,
      transformations, opacity, images (with the image decoders of the core library).
   2. Fonts: glyph outlines of TrueType, CFF, Type1 and Type3 fonts; substitutes for non-embedded standard fonts.
   3. Transparency groups, blend modes, soft masks, shadings (types 1-7), patterns, annotation appearances.
   4. Color: CMYK, Separation/DeviceN with PDF functions (types 0, 2, 3, 4), ICC-based color approximated.

   Stages 1 and 2 are released first as a preview.
2. **Diagnostics**: today errors raise `PdfException` (`PdfParseException`, `PdfPasswordException`), while damaged
   input that can be tolerated is handled silently (repaired cross-reference table, substituted fonts, skipped
   images or content). Planned: an opt-in diagnostics channel that reports these tolerated problems (warnings with
   page and object number), without a dependency on a logging framework, plus a small adapter for
   `Microsoft.Extensions.Logging` users.
3. **Text shaping** for complex scripts: Unicode bidirectional algorithm (Hebrew, Arabic), Arabic joining forms and
   standard ligatures (OpenType GSUB), mark positioning (GPOS); Indic scripts last.
4. **PostScript to PDF**: to be evaluated after rendering (the graphics model and the fonts can be shared; the
   PostScript interpreter itself is a separate project).

On demand: decoding of JBIG2 and JPEG 2000 images (JPEG 2000 can already be exported as a file).

Known gaps: see the roadmap above; in addition, hyphenation patterns are not bundled (load them from hyph-utf8).

## Verification tools

- PDF/A and PDF/UA: [veraPDF](https://verapdf.org) (`--flavour 1b|2b|2u|3u|4|4f|ua1|ua2`). PDF/A-1b/2b/2u/3u/4/4f,
  PDF/UA-1 and PDF/UA-2 pass, also signed and with form fields.
- Adobe Acrobat: accessibility check without errors (only the manual checks for reading order and color contrast),
  signatures and time-stamps intact, page labels and viewer settings shown, forms, links in running text, encryption
  and redaction correct.
  Acrobat does not show the PDF/A-4 and PDF/UA-2 identification in its Standards pane (PDF/A-2u and PDF/UA-1 are shown).
- [PAC](https://pac.pdf-accessibility.org) (PDF/UA-1): the accessible sample passes without findings; documents with
  form fields and images only get the heuristic warning "possibly inappropriate use" of Figure/Form. PAC reads PDF 1.7
  only, so PDF/UA-2 is checked with veraPDF.
- Signatures: [EU DSS](https://github.com/esig/dss) – all variants cryptographically valid, levels B-B/B-T/B-LT/B-LTA
  recognized (INDETERMINATE only for test CAs that are not on EU trusted lists); OpenSSL `cms -verify`.
- Qualified signature (ID Austria, signed with PDF-Over on documents created by miPDFlib, PDF 1.7 and PDF 2.0):
  validated as trusted with the CA certificate of the Austrian trusted list as trust anchor; stays valid after
  B-LT/B-LTA, a form change and a second signature by miPDFlib. EU DSS: QESig, TOTAL_PASSED in all variants (format
  PAdES-BES because PDF-Over adds the signing-time attribute, which PAdES baseline does not allow).
- Rendering and syntax: Ghostscript (`-sDEVICE=png16m`, `-sDEVICE=nullpage`).
- A corpus of real-world PDFs is used locally for regression checks; it is not part of the repository.
