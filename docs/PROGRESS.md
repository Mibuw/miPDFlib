# Progress and roadmap

Last update: 2026-11-10

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
| 39 Rendering, stage 3 (shadings, patterns, transparency) | ✅ done (451 tests; shading types 1-7, tiling and shading patterns, PDF functions 0/2/3/4, all blend modes, soft masks, isolated/non-isolated and knockout groups, page group on a transparent backdrop; checked with Ghostscript and 56 shading/transparency test files of pdf.js; shadings limited to the clip bounds: a page with 2,000 clipped shadings from 12 s to 0.5 s) |
| 40 Rendering, stage 4 (color) | ✅ done (455 tests; Separation/DeviceN through tint transforms, colorant None, CMYK approximating SWOP (pdf.js coefficients; naive formula was off by 25 levels on average), Lab range, ICC-based by component count; images in the core decoder through the same color spaces with full Decode arrays; Ghostscript spots2 from 9.6 to 1.9, CMYK example from 4.7 to 1.5) |
| 41 Diagnostics | ✅ done (461 tests; `PdfDiagnostic` with severity, stable code, page and object number; `PdfDocument.Diagnostics` and `PdfOpenOptions.OnDiagnostic`; reported: repaired xref, unreadable objects, damaged streams and ToUnicode maps, undecodable images, substituted/missing/damaged fonts, unsupported shadings and patterns, too deep nesting; no false positives on the corpus and the pdf.js files) |
| 42 Text shaping (Arabic, Hebrew, bidi) | ✅ done (470 tests; OpenType GSUB lookup types 1-8 and GPOS 1-9 with GDEF, Arabic joining forms, mark attachment, UAX #9 bidi passing all 91,707 Unicode conformance cases, mirrored brackets, ActualText for right-to-left runs and in text extraction, bidi-aware paragraph lines; checked with Arial and DejaVu Sans, Ghostscript renders identically, veraPDF PDF/UA-1 passes) |
| 43 Text shaping (Indic scripts) | ✅ done (479 tests; Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam: syllable grammar, decomposition of split matras, initial and final reordering, reph, pre-base forms, basic and presentation features per syllable, ZWJ/ZWNJ, dotted circles, invalid vowel sequences, old and new OpenType script tags; Unicode 16.0 Indic categories generated; glyphs and positions identical to HarfBuzz 15 for about 89,000 generated syllables in Nirmala UI, the nine Noto Sans fonts and FreeSerif/FreeSans also with old-style tags only; OpenType engine aligned with HarfBuzz for all scripts: feature variations, input masks, joiners, mark filtering sets in mark-to-mark, sequence indices after multiple substitutions; veraPDF PDF/UA-1 and PDF/A-2u pass) |
| 44 PDF417 | ✅ done (481 tests; `Pdf417` with text/numeric/byte compaction, ECI UTF-8, error correction levels 0-8 (Reed-Solomon over GF(929), ISO 15438 example reproduced), columns and row height; codeword patterns from ZXing; about 500 symbols decoded with zxing-cpp, also through PDF rendered by Ghostscript and miPDFlib.Rendering) |
| 45 JBIG2 decoding | ✅ done (523 tests; generic regions (templates 0-3, TPGDON, MMR), refinement (TPGRON), symbol dictionaries and text regions with arithmetic and Huffman coding, refinement/aggregation, pattern dictionaries and halftone regions, standard and user Huffman tables, striped pages, `/JBIG2Globals`, MQ decoder shared with JPEG 2000; all 40 SerenityOS test streams decoded to the expected image; identical to Ghostscript (jbig2dec) on 221 images of real PDFs; used in rendering, `ToPng` and redaction) |
| 46 JPEG 2000 decoding | ✅ done (546 tests; JP2 boxes (colr, pclr, cmap, cdef) and raw codestreams; all progression orders and POC, tiles and tile-parts, precincts, PPM/PPT, SOP/EPH, all code-block modes, ROI max-shift, scalar quantization, 5/3 and 9/7 wavelets, RCT/ICT, subsampling, sYCC; ISO/IEC 15444-4 conformance suite: 34 of 53 files exact, all others within the tolerances of the standard or differing only by the reference's edge convention/ICC conversion; generated files identical to OpenJPEG; fuzzed together with JBIG2 (hangs, overflows and decompression bombs fixed; JBIG2 decoding work bounded by PdfLimits.MaxDecodedStreamSize); in rendering, `ToPng` and redaction, color space from the dictionary or the JP2 header, `/SMaskInData`) |
| 47 PostScript, stage 1 (language core) | ✅ done (562 tests; new package miPDFlib.PostScript, not published yet; scanner with all Level 1-3 token forms except binary tokens, 64-bit integers and single-precision reals as in Ghostscript, about 200 operators (stack, arithmetic, relational, types and conversions, arrays, packed arrays, strings, dictionaries, control, loops, stop/stopped, errordict and $error, save/restore of local VM with global VM, bind, files, filters ASCIIHex/ASCII85/RunLength/SubFile/Flate on the program file); sandbox without file system, limits for operations, time, memory and stack depths; a 162-line test program gives the same output as Ghostscript 10.07) |
| 48 PostScript, stage 2 (graphics) | ✅ done (578 tests; graphics state with gsave/grestore, save/restore and gstate objects, CTM and all matrix operators, paths (lines, curves, arc/arcn/arct/arcto, closepath, flattenpath, reversepath, strokepath, pathbbox, pathforall, user paths), clipping (clip/eoclip/rectclip/initclip/clipsave), fill/eofill/stroke/rectfill/rectstroke, line parameters and dashes, colors (DeviceGray/RGB/CMYK native, HSB, Indexed, Separation and DeviceN through their tint transforms, CIEBasedA/ABC to sRGB), pages with setpagedevice and the Level 1 page size operators, nulldevice; written as PDF content at 720 dpi device resolution like Ghostscript's pdfwrite, with lazy graphics state and clip handling; test pages, about 100 random graphics programs and Ghostscript's ps2write/eps2write output (a PDF interpreter written in PostScript) render like Ghostscript rendering the PostScript; a 3 MB page with 120,000 objects converts in 0.5 s) |
| 49 PostScript, stage 3 (text and fonts) | ✅ done (588 tests; font dictionaries, definefont/findfont/scalefont/makefont/selectfont, eexec, the show operators (show, ashow, widthshow, awidthshow, kshow, cshow, xshow, yshow, xyshow, glyphshow), stringwidth, charpath; Type 1 fonts are embedded as subsets rebuilt from their dictionaries (own charstring interpreter for widths and outlines, flex, hint replacement, seac), Type 3 fonts become PDF Type 3 fonts with the captured glyph procedures, Type 42 fonts Type 0 fonts with a TrueType subset, undefined fonts are substituted by the standard 14 fonts; composite fonts (FMapType 2, 3, 4, 5, 8, 9), CMaps (CIDInit procset, Identity-H/V), composefont, CIDFontType 0 (GlyphData, GlyphDirectory, StartData; written as Type 1 subsets per FDArray entry), 1 and 2; ToUnicode for all fonts; text pages with Ghostscript's URW fonts, Type 3, Type 42, composite and CID-keyed fonts and ps2write output render like Ghostscript; also filters LZWDecode, CCITTFaxDecode, DCTDecode and predictors, user and system parameters) |
| 50 PostScript, stage 4 (images) | ✅ done (605 tests; image, imagemask and colorimage in the operand and dictionary forms, ImageType 1, 3 (masks interleaved by sample, by row or in a separate source) and 4 (color key masking); 1 to 16 bits per component; data from strings (reused when exhausted), procedures and filtered files, single or multiple sources; samples in device color spaces are written as they are, Indexed images keep their lookup table, Separation, DeviceN and CIE-based images are converted; JPEG data read through DCTDecode is written unchanged; identical images are written once, small ones inline; image pages, bitmap fonts and random image programs render like Ghostscript, also through ps2write and eps2write output; also fixed: ASCII85Decode now stops at ~>, filters consume their end-of-data marker after an image as Ghostscript's buffering filters do, srand as Ghostscript, Ghostscript's min/max operators; renderer and image extraction: color key masks (/Mask arrays) are applied, masks finer than their image keep their resolution) |
| 51 PostScript, stage 5 (patterns, shadings, forms, page device) | ✅ done (655 tests; makepattern, setpattern and the Pattern color space: colored and uncolored tiling patterns become PDF tiling patterns whose cell is what the PaintProc paints, shading patterns become PDF shading patterns; shfill with all seven shading types, function types 0, 2 and 3 as PDF functions, mesh data from arrays, strings and files; shadings in Separation, DeviceN, Indexed and CIE-based spaces are converted to device colors like all other colors; execform writes a form XObject that is painted again while the graphics state it depends on is the same; setpagedevice calls the BeginPage, EndPage and Install procedures as Ghostscript does (EndPage can drop pages or collect several on one sheet); errors as in Ghostscript; the test pages render like Ghostscript, also through ps2write and eps2write output; also fixed: bind makes nested procedures read-only, so a later bind does not bind them again) |
| 52 PostScript, stage 6 (pdfmark) | ✅ done (678 tests; document information (DOCINFO), catalog and open action (DOCVIEW), bookmarks with nesting by /Count (OUT), annotations and links with their rectangles in the current user space, destinations and actions (ANN, LNK), named destinations (DEST), page entries (PAGE, PAGES), article threads (ARTICLE), embedded files (EMBED), named objects (OBJ, PUT, PUTINTERVAL, APPEND, CLOSE with {Catalog}, {DocInfo}, {ThisPage}, {PrevPage}, {NextPage}, {PageN}) and named pictures as form XObjects (BP, EP, SP); pages referred to before they are painted are created ahead; setdistillerparams and currentdistillerparams; the resulting objects and the errors were compared with Ghostscript's pdfwrite) |
| 53 PostScript, stage 7 (EPS import) | ✅ done (689 tests; first public API of miPDFlib.PostScript: `PdfDocument.ImportEps` returns a form XObject of the bounding box (%%HiResBoundingBox preferred, (atend) read from the trailer), drawn with `PdfCanvas.DrawXObject`; DOS EPS binary files without their preview; showpage, erasepage, setpagedevice and the page size operators neither erase nor resize the graphic, as the EPSF specification asks; the form does not depend on the graphics state it is drawn with; `PostScriptOptions` (time, operation and memory limits, IgnoreErrors) and `PostScriptException`; compared with Ghostscript's -dEPSCrop rendering, also for the EPS files Ghostscript's eps2write made of the test pages of the earlier stages) |
| 54 PostScript, stage 8 (converter) | ✅ done (695 tests; `PostScriptConverter.ToPdf` with the page size, automatic page rotation and the sandbox limits in `PostScriptOptions`; document information from the DSC comments (%%Title, %%Creator, %%For as author; strings in parentheses and hexadecimal UTF-16 decoded), pdfmark wins; pages rotated by %%PageOrientation, %%Orientation or the direction of most of their text, as Ghostscript's AutoRotatePages /PageByPage; EPS files become a page of their bounding box; PJL job headers, control-D and DOS EPS binary files; the rotations and the document information were compared with Ghostscript's ps2pdf, and all test files of the earlier stages convert as before) |
| 55 Incremental updates of damaged files | ✅ done (705 tests; after a repair the update lists every object and has no /Prev, so the result needs no repair in any reader; damaged encrypted files with object streams open; checked with 886 damaged copies of the test corpus, read back by miPDFlib, pypdf (strict) and Ghostscript) |
| 56 Letters with font, colors and rendering mode | ✅ done (708 tests; `PdfTextExtractor.ExtractLetters`: bounds, baseline, angle, font name and size, bold and italic, fill and stroke color (device colors as in the file, spot, Indexed and Lab colors as RGB, patterns null), rendering mode and visibility; checked on 1,134 corpus pages: the letters give exactly the text of ExtractText, as fast) |
| 57 Layout analysis, stage 1 (columns and reading order) | ✅ done (714 tests; `TextExtractionOptions.DetectColumns`: lines cut at wide gaps, the page split at vertical channels found in a profile of the text height (a few spanning fragments allowed: header, title and author block, captions, footer) and at horizontal gaps; a channel separates columns only beside running text, so tables, invoices, lists and line numbers keep their rows. Measured against the LaTeX content order of 84 recent arXiv papers: 95.4 % on the 42 papers used while developing it (line by line 76.2 %, PdfPig's layout pipeline 93.7 %), 96.9 % on 42 held-out papers (84.8 %, PdfPig 96.1 %); all synthetic pages 100 %; 0.8 ms more per page) |
| 58 Column reading on business documents, now the default | ✅ done (715 tests; checked on 151 ZUGFeRD / Factur-X invoices (289 pages: same order as line by line, every line item still on one line with its amount), IRS forms and instructions (median 98.7 % instead of 50.8 %) and business letters (unchanged); tables of text-like cells, numbered lists, side notes and joined bands made stricter, tables set side by side read one after the other; `DetectColumns` is on by default) |
| 59 Layout analysis, stage 2 (blocks and their kinds) | ✅ done (719 tests; `PdfLayoutAnalyzer.Analyze` for a page or a document: blocks in reading order within the columns, split at wider gaps, size changes, list markers, indented first lines and rows of cells; kinds heading (with levels ranked across the document), paragraph, list item, table, caption, header and footer (by position, page numbers and repetition on the pages); compared with the structure tree of tagged IRS instructions: headings 89 %, list items 88 %, paragraphs 83 %; generated documents of the layout engine 97-100 % per kind; 579 corpus files without errors, about 3 ms per page) |
| 60 Layout analysis, stage 3 (export) | ✅ done (723 tests; `PdfLayoutExport.ToMarkdown` (headings, paragraphs, lists, pipe tables, captions; hyphenated words joined with the document's own words deciding, paragraphs broken by page or column ends joined), `ToHocr` (hOCR 1.2) and `ToAlto` (ALTO 4.4); ALTO of arXiv papers, IRS forms, invoices and generated documents validated against the ALTO 4.4 schema, hOCR word boxes drawn over the rendered pages; new block kind `Other` for text outside the flow) |
| 61 Layout analysis, stage 4 (tables) | ✅ done (730 tests; `LayoutBlock.Table` with rows, columns, cell text, bounds and spans; `PdfGraphicsExtractor.ExtractRules` (stroked lines, thin filled rectangles, edges of fills, form XObjects); grids of crossing rules, cells joined where rules are missing and divided again where the text shows data rows or column gaps; form fields and page frames rejected; lines through tables side by side split; tables without rules from the gaps of their rows; Markdown pipe tables from the cells; checked on ZUGFeRD/Factur-X invoices, IRS instructions and forms and arXiv papers; about 1 ms per page more for reading the rules) |
| 62 Path extraction | ✅ done (734 tests; `PdfGraphicsExtractor.ExtractPaths` with `PathExtractionOptions`: subpaths with move, line, Bézier and close segments in user space of the page, fill/stroke, fill rule, colors (as for letters), opacity, line width, cap, join and dash scaled to the page, bounds with curve extrema; form XObjects, optionally clipping paths, annotation appearances, hidden layers; color reading shared with the text extractor; `ExtractRules` and the table detection built on it with unchanged results on 247 corpus files; compared with PdfPig 0.1.16 on 2,784 pages (700,000 paths): same paths per page, bounds, operators and fill colors – PdfPig also reports stroked paths without segments and leaves out a point-sized fill, converts spot colors naively and reports unscaled line widths; 1.5 ms per page) |
| 63 PAGE XML export | ✅ done (735 tests; `PdfLayoutExport.ToPageXml` with `PageXmlExportOptions` (resolution, image file names, creation time): TextRegion per block with region type and custom structure (heading level, list item), TextLines with baselines, Words, TextEquiv, TextStyle; TableRegion with a TextRegion per cell and TableCellRole; ReadingOrder; 2,784 pages of arXiv papers, IRS forms and invoices valid against the PAGE 2019-07-15 schema; tables without rules: pieces of a row no longer share a column) |
| 64 Glyph ink boxes | ✅ done (739 tests; `TextLetter.InkBounds` from the outlines of embedded TrueType/CFF/Type 1 programs (curve extrema), Adobe Core 14 AFM glyph boxes for non-embedded standard fonts (with Windows and URW name aliases; data generated with notices, NOTICE entry), Type 3 d1 bounds or the bounds of the paths a d0 glyph paints; font program readers (SfntFont, CffFont, Type1Font, PathGeometry) moved from miPDFlib.Rendering into the core as internal `FontOutlines`, system font substitution stays in Rendering; outlines cached per font dictionary; compared with PdfPig on 2,784 pages / 7 million letters: 99.5 % within 2 % of the font size (deviations: PdfPig flat boxes for some Type 1 math fonts and ligatures, loose font boxes for Type 3, TrueType control points); also fixed: composite TrueType fonts with CFF outlines map glyphs through CIDToGIDMap (rendering too); ExtractLetters about 4 ms/page slower than ExtractLines) |

Current state: 739 unit tests on .NET 8 and .NET 10, CI on Linux and Windows.

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
decoded (1.1.0, released). 1.2.0 (released) adds the rendering package miPDFlib.Rendering, diagnostics and text
shaping for Arabic, Hebrew and other right-to-left scripts (items 1–3 below). 1.3.0 (released) adds the
PostScript package miPDFlib.PostScript (item 4), shaping of the Indic scripts, JBIG2 and JPEG 2000 decoding and
PDF417 barcodes. 1.4.0 adds the reading order across columns, letters with fonts and colors, the layout analysis with
tables and its export to Markdown, hOCR and ALTO (item 5), and incremental updates of repaired files. Next steps:

1. **Rendering** (pages to images, e.g. for previews and printing) as a separate package `miPDFlib.Rendering` in
   this repository: managed code only, no native dependencies and no System.Drawing; output as bitmaps/PNG at any
   resolution (printing itself is left to the application). Checked page by page against Ghostscript renderings
   of the corpus (pixel comparison). In stages:
   1. Core: anti-aliased rasterizer (nonzero/even-odd), stroking with joins, caps and dashes, clipping,
      transformations, opacity, images (with the image decoders of the core library).
   2. Fonts: glyph outlines of TrueType, CFF, Type1 and Type3 fonts; substitutes for non-embedded standard fonts.
   3. Transparency groups, blend modes, soft masks, shadings (types 1-7), patterns, annotation appearances.
   4. Color: CMYK, Separation/DeviceN with PDF functions (types 0, 2, 3, 4), ICC-based color approximated.

   Stages 1 and 2 are released as preview `1.2.0-preview.1` (miPDFlib and miPDFlib.Rendering); stages 3 and 4 in
   `1.2.0-preview.2`; the package is stable since `1.2.0` (together with diagnostics and text shaping). PDF
   functions moved from stage 4 to stage 3 (shadings need them); color
   spaces and functions are part of the core library, so that image export uses the same colors.
2. **Diagnostics** ✅: errors raise `PdfException`; tolerated problems are reported as `PdfDiagnostic` in
   `PdfDocument.Diagnostics` and through `PdfOpenOptions.OnDiagnostic` (see
   [guide](guide/diagnostics.md); `ILogger` users connect it with one line, so no adapter package is needed).
3. **Text shaping** ✅ for Arabic, Hebrew and other right-to-left scripts: Unicode bidirectional algorithm, Arabic
   joining forms and ligatures (OpenType GSUB), mark positioning (GPOS), optional Latin ligatures. Indic scripts ✅
   (the nine scripts of the Indic shaper, compared with HarfBuzz). Still open: Sinhala and the scripts of the
   Universal Shaping Engine (Khmer, Myanmar, Tibetan, ...), only on demand.
4. **PostScript and EPS** (package miPDFlib.PostScript): EPS import (EPS graphics as form XObjects in own
   documents) and the converter for whole PostScript files. Stages: 1 language core ✅, 2 graphics ✅ (paths,
   graphics state, clipping, colors), 3 text and fonts ✅ (Type 1, Type 3, Type 42, CID), 4 images and filters ✅, 5 Level
   2/3 ✅ (shadings, patterns, forms, `setpagedevice`), 6 `pdfmark` ✅, 7 EPS import API ✅, 8 converter ✅.
5. **Layout analysis** (done, 1.4.0): text blocks, columns and reading order of whole pages, for document processing and
   AI pipelines. A measurement with pages of known reading order (November 2026) showed: words are extracted
   completely (100 % in all cases, PdfPig 89–100 % depending on its extractor), tables are read row by row, but pages
   with two or three columns are read line by line across the columns (order 56 % and 38 %); PdfPig's layout
   pipeline (Docstrum, unsupervised reading order) gets columns right but breaks tables apart (43 %). Stages:
   1. Blocks and reading order ✅ (`DetectColumns`, the default since it was also checked on invoices, forms and
      letters): segmentation by whitespace (columns, headers and footers, side notes) and the order of the blocks,
      keeping tables row by row.
   2. API `PdfLayoutAnalyzer.Analyze(page)` ✅: blocks with lines, bounds and kind (heading, paragraph, list item,
      table, caption, header, footer), using font size and weight; headers and footers recognised across pages.
   3. Export ✅: Markdown (for language models), hOCR and ALTO.
   4. Tables ✅: cells from text alignment and ruling lines (needs the paths of the page).

   Checked with the synthetic pages of the measurement (extended) and a corpus of real multi-column documents.

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
