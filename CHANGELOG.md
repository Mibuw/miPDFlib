# Changelog

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
