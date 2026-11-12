# Changelog

## Unreleased

- `PdfGraphicsExtractor.ExtractPaths` returns the vector graphics of a page: every path painted, in painting order,
  with its subpaths (move, line, Bézier curve, close) in user space of the page, whether it is filled and stroked, the
  fill rule, fill and stroke color, opacity, line width, cap, join and dash pattern (scaled to the page) and its bounds
  (curves by their extent). Paths in form XObjects are included; clipping paths, annotation appearances and hidden
  layers by option. Compared with PdfPig on 2,800 pages (700,000 paths): the same paths with the same bounds, painting
  operators and fill colors. `ExtractRules` and the table detection now build on it (results unchanged).
- Layout export to PAGE XML (2019-07-15, as Transkribus, OCR-D and eScriptorium use it): `PdfLayoutExport.ToPageXml`
  writes a document per page with text regions by block kind, lines with baselines, words, tables with a region per
  cell (row, column, spans) and the reading order, in pixels of page images at a chosen resolution; valid against the
  PAGE schema on 2,800 test pages.
- `TextLetter.InkBounds`: the area a glyph actually covers – from its outline in the embedded font program (TrueType,
  CFF, Type 1), the glyph boxes of the Adobe font metrics for standard 14 fonts that are not embedded (also under the
  names of their Windows and URW equivalents), or the bounds a Type 3 glyph declares or paints. Compared with PdfPig on
  7 million letters: 99.5 % within 2 % of the font size. The font program readers moved from miPDFlib.Rendering into
  the core library (internal).
- Rendering and text: composite TrueType fonts whose program has CFF outlines (an OpenType font as FontFile2) now map
  their glyphs through the CIDToGIDMap (before, such glyphs were drawn wrong or not at all).
- Tables without rules: pieces of a row no longer share a column – a cell's span ends where the next cell begins,
  and a piece within the columns of the cell before it is part of that cell.

## 1.4.0

New features for reading and analysing text, no breaking API changes. One change in behaviour: text extraction now
reads pages set in columns column by column (`TextExtractionOptions.DetectColumns`, on by default; set it to false
for the previous line-by-line order). All three packages (miPDFlib, miPDFlib.Rendering, miPDFlib.PostScript) have
the version 1.4.0.

- Incremental saving of damaged files: when the cross-reference data had to be repaired on opening, the update now
  lists every object of the file and has no `/Prev`, so the result opens without repair in any reader (before, the
  update was chained to the damaged data, and a file without `startxref` got `/Prev -1`, which strict readers
  reject). The original bytes are kept as before. Checked with 886 damaged copies of the test corpus, read back by
  miPDFlib, pypdf (strict) and Ghostscript.
- Damaged encrypted files whose objects are in object streams can be opened: the object streams are read once the
  password is known (before: "no catalog found").
- Text extraction: `PdfTextExtractor.ExtractLetters` returns every character with its position (bounds, baseline,
  angle), font (name, size as it appears, bold, italic), fill and stroke color and rendering mode – e.g. to find
  headings, colored text or the invisible OCR layer of scanned pages (`TextLetter.IsVisible`).
- Text extraction reads pages set in columns column by column (`TextExtractionOptions.DetectColumns`, on by default;
  set it to false for the previous line-by-line order). The page is split at its whitespace into columns and sections;
  headers, title blocks, captions across the page and footers are read where they stand, while tables, invoices, lists,
  forms and line numbers keep their rows. On pages of recent arXiv papers the word order matches the order of the LaTeX
  output to 96.9 % on average (line by line: 84.8 %), on IRS forms and instructions to 98.7 % (median; line by line
  50.8 %); the reading order of 151 ZUGFeRD / Factur-X invoices is unchanged.
- Layout analysis: `PdfLayoutAnalyzer.Analyze` returns the text of a page or document as blocks in reading order –
  headings with their levels, paragraphs, list items, tables, captions, running headers and footers – with their
  lines, bounds, font size and weight. Compared with the structure tree of tagged IRS instructions, 89 % of the
  headings, 88 % of the list items and 83 % of the paragraphs are recognised.
- Layout export: `PdfLayoutExport.ToMarkdown` (headings, paragraphs, lists, pipe tables, captions; words hyphenated
  at line ends and paragraphs broken by a page or column end joined again), `ToHocr` (hOCR 1.2) and `ToAlto` (ALTO 4.4,
  valid against its schema), with the positions of blocks, lines and words. Text running in another direction than
  the page's text, such as a stamp in the margin, is a block of the kind `Other`.
- Tables of the layout analysis come with their cells (`LayoutBlock.Table`: rows, columns, text, bounds, row and
  column spans). Tables drawn with rules are found from the grid their lines, thin filled rectangles and cell
  backgrounds form; data rows without rules between them and columns whose rules divide only the head are divided
  again by the text, while form fields and the frames of a page layout are not taken as tables. Tables without rules
  get their columns from the gaps of their rows. Markdown export writes the cells into pipe tables.
- `PdfGraphicsExtractor.ExtractRules` returns the horizontal and vertical rules of a page (stroked lines, thin filled
  rectangles, edges of filled areas, also in form XObjects), with touching pieces joined.
- Text extraction: bold and italic are also recognised for TeX fonts (CMBX, SFTI, ...) and Times fonts named "Medi"
  and "Ital".

## 1.3.0

New features, no breaking changes. A third package, **miPDFlib.PostScript**, converts PostScript to PDF and places
EPS graphics; all three packages (miPDFlib, miPDFlib.Rendering, miPDFlib.PostScript) have the version 1.3.0.

- New package **miPDFlib.PostScript** – a PostScript interpreter (LanguageLevel 3) in pure managed code, without
  Ghostscript:
  - `PostScriptConverter.ToPdf` converts PostScript files to PDF: paths, clipping and all color spaces; text with
    Type 1, Type 3, Type 42 and CID-keyed fonts (embedded fonts are embedded, the standard fonts referenced);
    images in all forms with the LanguageLevel 3 filters (JPEG data unchanged); patterns, shadings (all seven types)
    and forms as vectors; `setpagedevice` with BeginPage, EndPage and Install. `pdfmark` adds document information,
    viewer settings, bookmarks, annotations and links, named destinations, page entries, article threads, embedded
    files, named objects and reusable pictures. The document information is also taken from the DSC comments
    (%%Title, %%Creator, %%For); pages are rotated to be read upright, by the DSC orientation or the direction of
    their text, as Ghostscript's ps2pdf does. EPS files become a page of their bounding box; print files with a PJL
    header and DOS EPS binary files are read.
  - `PdfDocument.ImportEps` places EPS graphics as form XObjects of their bounding box, to be drawn with
    `PdfCanvas.DrawXObject` at any size.
  - The interpreter runs in a sandbox without file system access, with limits on operations, memory and time
    (`PostScriptOptions`, `CancellationToken`); PostScript errors raise `PostScriptException` with the error name,
    the offending command and the program's output, or are ignored with `IgnoreErrors`.
  - Checked page by page against Ghostscript – also through Ghostscript's own ps2write and eps2write output – and
    with randomly generated and mutated programs.
  - Known limits: `charpath` with standard fonts that the program does not embed gives an empty path (the package
    contains no font programs; showing text is not affected); spot colors are converted to their alternate color
    space; binary tokens are not read. See the guide "PostScript and EPS".
- Images: color key masking (`/Mask` arrays) is applied in rendering and image extraction, and soft or stencil masks
  with a higher resolution than their image keep their detail.
- Images: JBIG2 (ITU-T T.88) and JPEG 2000 (ISO/IEC 15444-1) are decoded – in rendering, `ToPng` and redaction
  (partially covered images are blanked like JPEG images instead of being removed). JBIG2: generic, refinement,
  text and halftone regions, symbol and pattern dictionaries, arithmetic and Huffman coding including user tables
  and MMR, `/JBIG2Globals`; compared with Ghostscript on 221 images of real PDFs. JPEG 2000: JP2 files and raw
  codestreams, all progression orders and progression changes, tiles, precincts, packed packet headers, all
  code-block modes, region of interest, 5/3 and 9/7 wavelets, component transforms, subsampling, palettes; the
  color space comes from the image dictionary or the JP2 header, `/SMaskInData` uses the opacity channel. All
  files of the ISO/IEC 15444-4 conformance suite match the reference images within the tolerances of the standard.
  No native libraries are needed.
- CCITT fax (Group 4) decoding takes linear instead of quadratic time for rows with many color changes.
- Barcodes: PDF417 (ISO/IEC 15438) – `Pdf417.Create` with text, numeric and byte compaction, error correction
  levels 0–8, selectable number of columns and row height, UTF-8 text with ECI. Checked by decoding about 500
  symbols with zxing-cpp, also after rendering the PDF with Ghostscript and miPDFlib.Rendering.
- Text shaping for the Indic scripts Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada and
  Malayalam: syllables, reordering of pre-base vowel signs and split vowels, reph, conjuncts, half, below-base and
  post-base forms, ZWJ/ZWNJ, dotted circles for broken clusters and invalid vowel sequences, for fonts with the
  current (`dev2`, ...) and the older (`deva`, ...) OpenType script tags. Glyphs and positions are identical to
  HarfBuzz for about 89,000 generated syllables in Nirmala UI and the Noto Sans fonts. Like right-to-left text, the
  text carries `/ActualText` and is extracted in logical order.
- OpenType layout, for all scripts: feature variations of variable fonts are applied at the default instance; input
  glyphs of a lookup must have the feature (as in HarfBuzz); ZWJ and ZWNJ are skipped in contexts; mark-to-mark
  positioning skips marks outside the lookup's mark filtering set; glyphs added by a multiple substitution inside a
  contextual lookup get their own sequence indices; a ligature of a base and marks stays a base glyph.

## 1.2.0

New features, no breaking changes. **miPDFlib.Rendering** leaves preview: with shadings, patterns, transparency and
colors (1.2.0-preview.2) it renders the full PDF graphics model and its API is now stable as well. Both packages
also contain the changes of the two previews below.

- Text shaping for TrueType/OpenType fonts: Arabic and other joining scripts with their joining forms and required
  ligatures, mark positioning (harakat, Hebrew points, combining accents) and right-to-left text ordered with the
  Unicode Bidirectional Algorithm (UAX #9, passes all 91,707 cases of the Unicode conformance test), in `ShowText`,
  `DrawText`, paragraphs and `GetTextWidth`; OpenType GSUB and GPOS are applied (all lookup types). Right-to-left
  text carries `/ActualText` with its logical text. Optional standard ligatures for Latin text:
  `PdfTrueTypeFont.Ligatures`. Indic scripts are not shaped yet.
- Text extraction uses `/ActualText` of marked content (right-to-left text and ligatures in reading order).
- Diagnostics: problems miPDFlib tolerates instead of failing – a repaired cross-reference table, unreadable objects,
  damaged streams, unreadable ToUnicode maps, images that cannot be decoded and, when rendering, substituted or
  missing fonts and unsupported shadings – are reported as `PdfDiagnostic` (severity, stable code, message, page and
  object number). They are collected in `PdfDocument.Diagnostics` and passed to `PdfOpenOptions.OnDiagnostic`, for
  example to log them; no logging framework is required. See the guide "Error handling and diagnostics".

## 1.2.0-preview.2

Second preview of the rendering package: shadings, patterns, transparency and colors complete the renderer
(miPDFlib itself gets the image color fixes below; the stable version stays 1.1.0).

- miPDFlib.Rendering: shadings (all seven types, with the `sh` operator and as shading patterns), tiling patterns
  (colored and uncolored), PDF functions (sampled, exponential, stitching, PostScript calculator), all blend modes,
  soft masks (alpha and luminosity) and transparency groups including knockout groups. Pages are composited on a
  transparent backdrop like in Acrobat.
- miPDFlib.Rendering: spot colors (Separation, DeviceN) through their tint transforms, the colorant None, CMYK
  approximating the SWOP press profile (coefficients from pdf.js, see NOTICE), Lab with its range.
- Images: `ToPng` and the renderer convert Separation, DeviceN, Lab and Indexed images (also with spot-color or Lab
  bases) through their color spaces and apply any Decode array; CMYK images use the SWOP approximation. Before,
  spot-color images were shown as gray and CMYK colors too bright.

## 1.2.0-preview.1

Preview release of the new rendering package; miPDFlib itself only has the fixes below (the stable version stays
1.1.0, `miPDFlib.Rendering` requires exactly the miPDFlib version of its release).

- New package **miPDFlib.Rendering** (preview): renders pages to PNG or RGBA pixels at any
  resolution (`PdfRenderer.Render` / `RenderToPng`) – vector graphics with anti-aliasing, clipping, colors, opacity,
  images, form XObjects, annotation appearances and layers; text with embedded TrueType, OpenType, CFF, Type 1 and
  Type 3 fonts (all rendering modes), and installed substitutes for fonts that are not embedded. Shadings and
  transparency effects follow.
- Image decoding: soft masks compressed with DCT (JPEG) or CCITT are now applied (before, such images lost their
  transparency in `ToPng`).
- Text extraction: ZapfDingbats text without a ToUnicode map is extracted for all codes of the font (before, only
  common check marks and bullets); word spacing is also recognized for the one-byte code 32 in composite fonts.

## 1.1.0

New features, no breaking changes.

- Changes after signing (DocMDP): `SignatureValidationResult.ChangesAfterSigning` lists what the incremental updates
  after a signature changed – validation data, document time-stamps, signatures, form filling, annotations,
  metadata, page content, pages, ... – and whether each change is permitted by the certification level;
  `HasDisallowedChanges` summarises it (including modified locked fields). Without certification, page content
  changes after a signature are reported as not permitted.
- Merging, splitting and extracting keep the logical structure of tagged pages (`ImportPages`, `Merge`,
  `ExtractPages`, `Split`): the structure elements with content on the copied pages and their ancestors, with a
  rebuilt parent tree; documents are merged below a single `Document` element. Results validated with veraPDF
  (PDF/UA-1, PDF/UA-2). Before, imported pages lost their tags.
- Image decoding: JPEG (baseline, extended and progressive; 1, 3 or 4 components, any subsampling, restart intervals;
  pixels identical to libjpeg-turbo) and CCITT fax images (Group 3 one- and two-dimensional, Group 4). `ToPng` now
  works for these images, and redaction blacks out the covered pixels of scans instead of removing the whole image.
  JPEG 2000 and JBIG2 are still not decoded.
- Layers (optional content): `PdfDocument.Layers` and `AddLayer`, `PdfLayer.IsVisible` / `IsLocked` / `Name` for the
  initial state, `canvas.BeginLayer` / `EndLayer` / `Layer(...)` for content and `PdfAnnotation.Layer` for
  annotations (also set on their appearance streams). `TextExtractionOptions.IncludeHiddenLayers = false` leaves out
  the text of hidden layers. `CheckPdfA` checks the optional content rules (not in PDF/A-1, named configurations).
- Certificate encryption (public-key security handler, `/Adobe.PubSec`): `PdfEncryptionOptions.Recipients` encrypts a
  document for the certificates of its recipients (AES-256, AES-128 or RC4, with permissions);
  `PdfOpenOptions.Certificate` opens such documents with the private key, also from byte arrays and streams (new
  `Open` overloads with `PdfOpenOptions`). `PdfDocument.IsCertificateEncrypted` tells the kind of encryption.
  Cross-checked with Apache PDFBox in both directions.

## 1.0.0

First stable release. The public API is recorded in `PublicAPI.Shipped.txt` and follows Semantic Versioning: no
breaking changes before 2.0.

Breaking changes compared with 0.6.0 (API review before 1.0):

- `PdfAnnotation.Flags` is an `AnnotationFlags` enum instead of `int`, and `PdfAnnotation.Color` a `PdfColor?`
  instead of `double[]` (consistent with form fields and the rest of the API).
- The stream filter classes (`MiPdf.Filters`: `FlateFilter`, `LzwFilter`, `Ascii85Filter`, ...) are internal;
  use `PdfStream.GetDecodedData()`.
- The obsolete members `PdfFormField.Tooltip` (use `ToolTip`), `PdfString.FromText` and `PdfString.FromBytes` (use
  the constructors) are removed.

- PDF/A-4 and PDF/A-4f (`PdfAConformance.PdfA4`, `PdfA4F`): written as PDF 2.0 with `pdfaid:rev`, without the document
  information dictionary (the information is in the XMP metadata only). `CheckPdfA` knows the attachment rules of
  PDF/A-4 and -4f. Validated with veraPDF.
- PDF/UA-2 (`PdfUAConformance.PdfUA2`, can be combined with PDF/A-4): PDF 2.0 structure namespace for the structure
  elements, `pdfuaid:rev`, `/Contents` for form fields; `CheckPdfUA` checks the PDF/UA-2 rules for namespaces, list
  numbering and notes, and no longer reports skipped heading levels for PDF/UA-2. Validated with veraPDF.
- PDF 2.0 structure types: `StructureType.Title`, `Aside`, `FENote`, `Sub`, `Emphasis`, `Strong`, `DocumentFragment`.
- Lists produced by the layout engine get the `ListNumbering` attribute.
- Links in running text: `paragraph.AddLink("text", "https://...")` or `TextRun.Uri` place a link annotation over the
  text on every line it covers (also in table cells); in tagged documents the text and the annotations form a `Link`
  element inside the paragraph, as PDF/UA requires.
- Headings of the layout engine (`AsHeading`) get bookmarks nested by level, as accessibility checkers (PAC) expect
  for documents with headings (`LayoutDocument.HeadingBookmarks`, default true). Documents with headings now open
  with the bookmarks panel. In tagged documents the bookmarks point to the heading's structure element (structure
  destination, required by PDF/UA-2); `CheckPdfUA` reports bookmarks and internal links without one in PDF/UA-2.
- Signature validation: `TrustedRoots` also accepts CA certificates that are not self-signed, such as the trust
  anchors of EU trusted lists (the chain is trusted when it reaches one of them). Tested with a qualified signature
  from the Austrian trusted list, also after B-LT/B-LTA, a form change and a second signature with miPDFlib.
- Fixed: a certificate chain that could not be built set `IsIntegrityValid` to false. Chain problems now only
  affect `IsCertificateTrusted` (and are listed in `Errors`), as documented.
- `doc.Info` of documents without a document information dictionary (PDF 2.0, PDF/A-4) is read from the XMP metadata.
- Saving an opened PDF/A document also writes changed title, author, subject, keywords and creator into its XMP
  metadata (before, only the dates and the producer were updated, so the information and the XMP metadata differed).
- Signature fields in tagged documents get the description as `/Contents` (PDF/UA-2).
- The public API is recorded in `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (Microsoft.CodeAnalysis.PublicApiAnalyzers,
  build-time only, no runtime dependency): unintended API changes fail the build.

## 0.6.0

- Performance: large documents need far less memory. Stream data of opened files is no longer copied (it is shared
  with the file data until it is changed), `Save(path)` writes directly to a temporary file that then replaces the
  target, and `ToArray()` no longer copies the output repeatedly while it grows. For a 300 MB file with images the
  peak memory drops from 1.2 GB to 330 MB for an incremental save, from 1.6 GB to 330 MB for a full save and from
  3.4 GB to 630 MB for merging it with itself.
- Signatures are created and verified from the digest of the signed byte ranges instead of a copy of them: signing
  the 300 MB file needs 635 MB instead of 1.5 GB (input and output), validating 330 MB instead of 930 MB. The CMS is
  assembled directly (signed attributes, signature value, unsigned attributes, time-stamp) instead of with
  `SignedCms`; the structure is unchanged (verified with EU DSS for RSA, RSASSA-PSS, ECDSA, PKCS#7 and B-T). The
  validator checks the message digest and the signature value itself (RSA PKCS#1 v1.5 and PSS, ECDSA).
- Large files: `PdfDocument.Open(path, new PdfOpenOptions { ReadFromFile = true })` reads objects from the file when
  they are needed instead of loading the file into memory; files larger than 2 GB (which could not be opened before)
  are always read this way. The file stays open until the document is disposed; saving over the own file works.
  `PdfSigner.SignFile(input, output, ...)` and `PdfSignatureValidator.Validate(path)` sign and validate files without
  loading them. A 2.5 GB file is opened with about 35 MB, saved and signed with under 50 MB.
- `PdfDocument.Dispose` closes the file of a document read on demand (it did nothing before).
- `Save(path)` leaves an existing file unchanged if saving fails (it used to be truncated).
- `PdfDocument.Open(Stream)` reads seekable streams once into an array of the final size.
- Text extraction allocates a third less memory and is faster.
- Benchmarks: `benchmarks/MiPdf.Benchmarks` measures time and memory for large documents.
- Page labels (#10): `doc.PageLabels.Add(startPage, PdfPageLabelStyle.LowerRoman, prefix, startNumber)` and
  `doc.GetPageLabel(n)` write and read /PageLabels (number trees with /Kids are read).
- Viewer settings (#8): `doc.PageLayout`, `doc.PageMode` and `doc.ViewerPreferences` (HideToolbar, HideMenubar,
  HideWindowUI, FitWindow, CenterWindow, DisplayDocTitle).
- Page sizes (#7): `PageSize.A0`, `A1`, `A2`, `A6`, `B4`, `B5`, `Tabloid`, `Ledger`, `Executive`.
- Samples (#9): redacting an IBAN and splitting/extracting pages.
- `PdfCanvas.DrawTextBox` justifies text with `TextAlignment.Justify`; `PdfColor.Lime`; `StructureType` constants for
  all standard structure types (`Article`, `Code`, `Reference`, `Index`, `H`, Ruby and Warichu types, ...);
  `ContentParser.Parse(content, strict: true)` throws instead of ending silently; `Spacer.Height`;
  `PdfWidget.IsHidden` is settable.

### API cleanup before 1.0 (#13)

Changes that can affect existing code:

- `PdfChoiceField.Value` returns the first selected value instead of all values joined with ", ", so it round-trips;
  use `SelectedValues` for all of them (also for `PdfAcroForm.GetValues`).
- `PdfTextField.Alignment = TextAlignment.Justify` throws instead of silently writing left alignment.
- `PdfCanvas.RestoreState()` restores the font that was current at the matching `SaveState()` (it used to forget the
  font, so text after a `DrawText` call needed `SetFont` again).
- `PdfFormXObject.Matrix` is a `Graphics.Matrix?` instead of `double[]?`.
- `PageSize.Landscape` keeps the lower-left corner of rectangles that do not start at (0, 0).
- `PdfPage.TrimBox`, `BleedBox` and `ArtBox` default to the crop box (ISO 32000) instead of returning null; they are
  no longer nullable.
- `PdfNumber.LongValue` and `IntValue` round a real number like the constructor does instead of truncating it.
- `ListStyle.LowerAlpha` renders "a." like the other numbered styles (it rendered "a)"; use `LabelFormatter` for that).
- `Barcode.TotalHeightModules` was removed from linear barcodes (it was always 0); `MatrixBarcode.TotalHeightModules`
  stays.
- `RedactionResult.Warnings` is an `IReadOnlyList<string>`; `RedactionOptions` properties are settable like the
  other options classes.
- Obsolete (still working): `PdfFormField.Tooltip` (use `ToolTip`), `PdfString.FromText` and `FromBytes` (use the
  constructors).
- Documented: field flags set on a field no longer inherit from the parent; `SetDashPattern(3, 2)` versus
  `SetDashPattern(new[] { 3.0 }, 2)`; `PdfColor` factories (`Rgb` 0..1, `FromRgb` 0..255) and `PdfColor.Green` (CSS
  green); `PdfArray.Contains` compares arrays and dictionaries by reference; `CertificateSigner.SignHash` ignores the
  RSA padding for EC keys.

## 0.5.0

- Signatures: `PdfSignatureValidator` evaluates the stored revocation data – OCSP responses and CRLs from the
  Document Security Store and from the `adbe-revocationInfoArchival` attribute – and reports the revocation status
  at the time of signing for every certificate of the chain (`Revocation`, `RevocationDetails`; also for the
  time-stamping authority of document time-stamps). Only revocation data signed by the issuer or an authorised OCSP
  responder counts.
- Forms: export and import (merge) field values as XFDF and FDF – `PdfAcroForm.ExportXfdf`, `ImportXfdf`,
  `ExportFdf`, `ImportFdf` with `FormDataImportResult` (fields set, unknown fields, invalid values).
- Fix: `PdfRedactor.ApplyRedactions` kept only the first /OverlayText of a page and ignored the overlay texts of the
  Redact annotations entirely when options were passed. Every mark now keeps its own text; it takes precedence over
  `RedactionOptions.OverlayText`.
- Fix: `PdfSignatureValidator.Validate(PdfDocument)` reported "(the signed revision cannot be read)" for field locks of
  encrypted documents; the password the document was opened with is now used.
- Fix: a CMYK outline color (`PdfOutline.Color`) was stored as gray from its cyan component; it is converted to RGB.
- `HttpTimestampClient.Password` is no longer publicly readable (breaking: the getter was removed; the constructor is
  unchanged).
- Samples: ZUGFeRD / Factur-X invoice (`08-zugferd-invoice.pdf`); CI validates it with Mustang (PDF/A-3, EN 16931
  rules) and the KoSIT validator on every push.
- Docs: API reference and guides on GitHub Pages (https://mibuw.github.io/miPDFlib/), generated with DocFX.
- Docs: every public type and member has an XML documentation comment (IntelliSense and API reference); the
  compiler warning for missing comments is enabled again.

## 0.4.0

- E-invoices: `FacturX.CheckConsistency(doc)` checks whether the invoice number, issue date, seller and buyer, VAT
  identifier, IBAN and totals of the embedded XML appear in the visible PDF (plausibility check, e.g. for incoming
  invoices).
- E-invoices: `FacturX.Attach` rejects a profile that contradicts the guideline ID (BT-24) in the XML, for example
  `FacturXProfile.EN16931` for a MINIMUM invoice (behaviour change: this used to be accepted). XML without a
  guideline ID or with an unknown one is accepted as before. New: `FacturX.DetectProfile(xml)`.
- Docs: kerning is supported (removed from the "not supported" list in the creating documents guide).
- Docs: article "ZUGFeRD in .NET" linked in both readmes; generating the e-invoice XML stays out of scope
  (see the design decisions in `docs/PROGRESS.md`).

## 0.3.1

- Saving updates the document information: the modification date is set to now and, for documents opened from a
  file, "; modified with miPDFlib" is appended to the producer (once). Existing XMP metadata is kept in sync, so PDF/A
  documents stay valid. Options: `PdfSaveOptions.UpdateModificationDate`, `UpdateProducer`. Signing and LTV updates
  do not change the document information.
- Docs: comparison with iText and Syncfusion PDF (`docs/COMPARISON.md`), "Why miPDFlib?" and article links in both
  readmes, more descriptive package description.
- Docs: "Used by" section in both readmes.
- Docs: how-to guides for PAdES signing, ZUGFeRD / XRechnung invoices and replacing iText (`docs/howto`).
- Samples: ASP.NET Core web API (`samples/MiPdf.WebApi`) with Dockerfile – create, sign and validate PDFs.
- Docs fixes: signature options table in the signatures guide; barcodes and PAdES LTV are no longer listed as
  "not covered" in the migration guide.

## 0.3.0

- First stable (non-prerelease) release: installs without `--prerelease`. Same code as 0.2.8-alpha. Releases follow
  Semantic Versioning; until 1.0 the public API may still change between minor versions.

## 0.2.8-alpha

- Forms: radio groups, combo boxes and list boxes no longer set a white background (/MK /BG) either; `AddRadioGroup`,
  `AddComboBox` and `AddListBox` take `border`, `background` and `borderWidth` like the other field types. An invalid
  border width is rejected before the field is added.

## 0.2.7-alpha

- Forms: `AddCheckBox` no longer sets a white background (/MK /BG), like `AddTextField`, so a check box does not cover
  the page content below it; the new `background` parameter sets one.

## 0.2.6-alpha

- Forms: `AddTextField` and `AddCheckBox` take a `borderWidth` (0 = no border). `PdfWidget.Rotation` (/MK /R),
  `BorderColor`, `BackgroundColor` and `BorderWidth` change an existing widget and rebuild its appearance.
- Forms: check boxes and radio buttons rebuild their on/off appearances (`RegenerateAppearance`) from /MK, including
  the rotation /MK /R, and keep their state; their appearances now honor /MK /R.
- Signatures: the visible appearance is drawn upright on rotated pages (`PdfSignatureAppearance.KeepUpright`, default
  true) and honors /MK /R of an existing signature field. Code that rotated the appearance itself must drop that.

## 0.2.5-alpha

- Separate package readme for nuget.org (`src/MiPdf/NUGET-README.md`): nuget.org shows HTML as text and decoded
  the GitHub README incorrectly. The GitHub README uses absolute links and logo URL.

## 0.2.4-alpha

- The NuGet package is now published as `miPDFlib` (previously `MiPdf`, deprecated). Only the package reference
  changes: namespace and assembly stay `MiPdf`, the API is unchanged.

## 0.2.3-alpha

- Signatures: field locks (FieldMDP). `PdfSignatureOptions.FieldLock` / `PdfSignatureField.Lock` with
  `PdfFieldLock.All()`, `Include(..)`, `Exclude(..)` write /Lock and a FieldMDP signature reference; the validator
  reports the lock (`FieldLock`) and locked fields changed after signing (`ModifiedLockedFields`).

## 0.2.2-alpha

- First public release, published on nuget.org (Trusted Publishing).
- Contributing guide, Contributor License Agreement with CLA bot, issue and pull request templates, Dependabot.
- `NOTICE` with third-party attributions, included in the package.

## 0.2.1-alpha

- Signatures: RSASSA-PSS (`PdfSignatureOptions.RsaPadding`, `IExternalSigner.SignHash(hash, alg, padding)`,
  `CmsUtil.AssembleSignedData(..., rsaPadding)`), certificates issued with RSASSA-PSS in chain building and LTV.
- Tests and EU DSS validation for ECDSA P-384, P-521 and Brainpool P256r1/P384r1/P512r1.
- Fix: a raw ECDSA value (r || s) whose first byte is 0x30 was taken for DER in `CmsUtil.AssembleSignedData`
  (about one signature in 256 became invalid).
- PDF 2.0: files with data before the `%PDF-` header and offsets relative to the header are opened without repair
  and can be saved incrementally and signed; tested with the PDF Association's PDF 2.0 example files.

## 0.2.0-alpha

- Barcodes: QR Code, EPC QR (GiroCode), Data Matrix incl. GS1, Code 128/GS1-128, EAN-13/8, UPC-A, Code 39, ITF.
- Tagged PDF and PDF/UA-1 (layout engine tags headings, paragraphs, lists, tables, figures; PDF/UA checker).
- Signatures: PAdES B-LT/B-LTA (DSS with OCSP/CRL, document time-stamps), custom signed CMS attributes and
  signature dictionary entries, time-stamp failover, `SignDetailed`, `CmsUtil` (deferred signing with raw RSA/ECDSA
  values, adding time-stamps to foreign CMS), ink appearances, `SignatureCreator`.
- Hardening: `PdfLimits` (decompression bombs, image size, CMap entries), `PdfException` for malformed input,
  SSRF-safe revocation fetching, fuzz tests, `SECURITY.md`.
- Attachments and ZUGFeRD / Factur-X / XRechnung hybrid invoices (PDF/A-3).
- Annotations: text markup, free text, stamps, ink, shapes, lines, file attachments, flattening.
- Redaction with true removal of text, image pixels, vector graphics, annotations and form fields.
- Layout: lists, row spans, rows split across pages, kerning, soft hyphens and pattern hyphenation.
- Images: BMP and GIF input, image extraction (PNG/JPEG), free-space detection.
- Forms: push buttons with images, check box styles, XFA data, `ToolTip`.
- `IgnorePermissions`, PDF/A identification, text extraction before saving.
- Validated with veraPDF (PDF/A-1b/2b/2u/3b/3u, PDF/UA-1) and EU DSS (PAdES B-B to B-LTA).

## 0.1.0-alpha

- Core (parser, writer, incremental saving, repair), content creation, fonts, images, layout, manipulation,
  text extraction, forms, encryption, PAdES B-B/B-T signatures, PDF/A.
