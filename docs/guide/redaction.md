# Redaction

Redaction **removes** content – it does not just paint black boxes over it. Namespace `MiPdf.Redaction`.

```csharp
using var doc = PdfDocument.Open("contract.pdf");

doc.RedactText("John Smith");                                             // every occurrence
doc.RedactText(new Regex(@"AT\d{2}(?: ?\d{4}){4}"));                      // IBANs, e-mail addresses, ...
doc.GetPage(2).Redact(new[] { new PdfRectangle(72, 500, 300, 560) },     // areas in page coordinates
    new RedactionOptions { OverlayText = "REDACTED", FillColor = PdfColor.Black });

doc.Save("contract-redacted.pdf");                                        // always a complete save
```

Two-step workflow (review before applying):

```csharp
page.MarkForRedaction(page.FindText("Confidential")[0], "removed");   // Redact annotation, visible in viewers
doc.Save("marked.pdf");
// ... later
doc.ApplyRedactions();
```

## What is removed

| Content | Behaviour |
|---|---|
| Text | Glyphs whose box overlaps an area (default ≥ 30 %, `GlyphOverlapThreshold`) are removed from the content stream; the remaining text keeps its exact position. Works for simple and CID fonts, invisible text, `Tj`/`TJ`/`'`/`"`. |
| Form XObjects (letterheads, stamps) | redacted recursively; a redacted copy replaces the original for this page |
| Images | completely covered: removed; partially covered: the pixels inside the area are set to black (Flate/LZW/uncompressed images with 1, 8 or 16 bits per component, stencil masks become transparent). Images that cannot be decoded (JPEG, JPEG 2000, JBIG2, CCITT) are removed completely – reported in `RedactionResult.Warnings`. Inline images touching an area are removed. |
| Vector graphics | paths completely inside an area are removed; partially overlapping paths stay (covered by the box) |
| Annotations, form fields | removed when they intersect an area (`RemoveAnnotations`) |
| Resources | originals that are no longer drawn are dropped from the page, so they are not written to the file |

`RedactionResult` reports the removed glyphs, images, paths and annotations.

## Important

- Redacted documents must be saved **completely**. An incremental save would keep the original content in the file;
  miPDFlib therefore throws if `PdfSaveOptions.Incremental` is used after redacting.
- Redaction works on page content and annotations. Text in bookmarks, document information, XMP metadata,
  attachments or JavaScript is not changed – check or remove these separately (`doc.Info`, `doc.Outlines`,
  `doc.RemoveAttachment`).
- Signatures covering redacted pages become invalid (the content changes), as with any other tool.
