# Opening and manipulating documents

## Open, change, save

```csharp
using var doc = PdfDocument.Open("input.pdf");          // also Stream or byte[]
Console.WriteLine($"{doc.PageCount} pages, title: {doc.Info.Title}");
doc.Info.Title = "New title";
doc.GetPage(1).Rotation = 90;
doc.Save("output.pdf");
```

Damaged files (wrong xref offsets, missing trailer, wrong stream lengths) are repaired automatically;
`doc.WasRepaired` tells whether this happened.

### Large files

By default the file is read into memory; objects are parsed only when they are used. With `ReadFromFile` the
objects are read from the file when needed instead, so memory stays small whatever the file size (files larger than
2 GB are always read this way):

```csharp
using var doc = PdfDocument.Open("scan-archive.pdf", new PdfOpenOptions { ReadFromFile = true });
doc.Info.Title = "Archive 2026";
doc.Save("scan-archive.pdf", new PdfSaveOptions { Incremental = true });  // saving over the own file works
```

The file stays open for reading until the document is disposed. Saving copies unchanged streams (images, fonts)
straight from the file. Pages imported into another document read their data from this file, so keep the source
open until the target is saved. For signing and validating large files without loading them see
`PdfSigner.SignFile` and `PdfSignatureValidator.Validate(path)` in the [signatures guide](signatures.md).

Measured with a 2.5 GB file: opening needs about 35 MB, saving and signing under 50 MB
([benchmarks](https://github.com/Mibuw/miPDFlib/blob/main/benchmarks/README.md)).

### Save options

```csharp
doc.Save("small.pdf", new PdfSaveOptions { UseObjectStreams = true }); // compact (PDF 1.5+)
doc.Save("same.pdf",  new PdfSaveOptions { Incremental = true });      // append changes only
```

| Option | Effect |
|---|---|
| default | complete rewrite, unused objects are dropped, uncompressed streams get compressed |
| `UseObjectStreams` | additionally packs objects into compressed object streams – typically 10–40 % smaller |
| `Incremental` | original bytes stay untouched, only changed objects are appended. **Required for signed documents.** |
| `Version` | override the header version |
| `UpdateModificationDate` | sets the modification date to now (default true) |
| `UpdateProducer` | documents opened from a file: appends "; modified with miPDFlib" to the producer (default true) |

### Incremental updates

An incremental save appends an update to the unchanged original bytes: the objects changed, added or deleted since
opening – found automatically, no need to register them – and a cross-reference section for them. Earlier
revisions stay intact, so existing signatures remain valid, and the update is small.

- The update uses the format of the original: a cross-reference table, or a cross-reference stream for files with
  object streams.
- Encrypted documents stay encrypted with their original key (changing the encryption needs a full save).
- Damaged files whose cross-reference data had to be repaired when opening (`doc.WasRepaired`) are updated as well:
  the update then lists every object of the file and does not refer back to the damaged data, so the result opens
  without repair in any reader – while the original bytes are still kept.
- `Save(path)` may write over the file the document was opened from; `ToArray` returns the whole file.

### Document information on saving

Saving sets the modification date (`/ModDate`) and, for a document opened from a file, notes the edit in the
producer: "Word; modified with miPDFlib" (or "miPDFlib" if there was none; the note is added only once). Existing
XMP metadata is updated accordingly (`xmp:ModifyDate`, `xmp:MetadataDate`, `pdf:Producer`; for PDF/A documents also
title, author, subject, keywords and creator), so PDF/A documents stay consistent. Documents without a document
information dictionary (PDF 2.0 deprecates it, PDF/A-4 forbids it) are read from the XMP metadata. New documents get
the producer "miPDFlib"; the creating application belongs in `doc.Info.Creator`.

```csharp
doc.Info.Producer = "MyApp 2.1";                                     // your own text ...
doc.Save("out.pdf", new PdfSaveOptions { UpdateProducer = false });  // ... kept as it is
```

Signatures, document time-stamps and long-term validation data (`PdfSigner`, `PdfLtv`) never change the document
information – signing is not an edit of the content. The signing application is recorded in the signature itself
(`PdfSignatureOptions.SignatureCreator`, default "miPDFlib").

### PDF 2.0

PDF 2.0 (ISO 32000-2) files are read, edited, saved and signed like any other PDF; `doc.Version` reports "2.0".
Covered and tested with the PDF Association's PDF 2.0 example files:

- UTF-8 text strings (in addition to PDFDocEncoding and UTF-16BE),
- data before the `%PDF-` header with offsets counted from the header (the preamble is not part of the PDF and is
  dropped when the document is saved or signed),
- page-level output intents, black point compensation and other new entries (kept as they are),
- AES-256 encryption (revision 6), PAdES signatures, DSS and document time-stamps, associated files (`/AF`).

New documents are written as PDF 1.7 by default; set `doc.Version = "2.0"` or `PdfSaveOptions.Version` for a
PDF 2.0 header. AES-256 encryption raises the version to 2.0 automatically, as do PDF/A-4 and PDF/UA-2
(`PdfAConformance.PdfA4`, `PdfUAConformance.PdfUA2`, which also writes the PDF 2.0 structure namespace).

## Pages

```csharp
doc.AddPage(PageSize.A4);
doc.InsertPage(0, PageSize.Landscape(PageSize.A4));
doc.RemovePage(3);                 // 0-based
doc.MovePage(fromIndex: 5, toIndex: 0);
doc.ReorderPages(new[] { 2, 0, 1 });
var page = doc.GetPage(1);         // 1-based
Console.WriteLine($"{page.Width} x {page.Height}, rotation {page.Rotation}");
page.MediaBox = PageSize.A4; page.CropBox = new PdfRectangle(20, 20, 575, 822);
```

## Merge, split, extract

```csharp
using var merged = PdfDocument.Merge("a.pdf", "b.pdf", "c.pdf");
merged.Save("merged.pdf");

var target = PdfDocument.Open("target.pdf");
target.ImportPages(PdfDocument.Open("appendix.pdf"), pageNumbers: new[] { 1, 3 }, insertIndex: 2);

using var firstTwo = doc.ExtractPages(new[] { 1, 2 });
int n = 0;
foreach (var part in doc.Split(pagesPerDocument: 10)) part.Save($"part{++n}.pdf");
```

Resources shared by imported pages (fonts, images) are copied once. Links between imported pages are
kept; links to pages that are not imported are removed so the rest of the source is not copied along.

Tagged pages keep their logical structure (see [accessibility](accessibility.md#merging-splitting-and-extracting)).

## Stamps, watermarks, letterheads

Every page (also of opened documents) has a canvas. Existing content is wrapped in `q … Q`, so it
cannot influence the new drawing.

```csharp
foreach (var p in doc.Pages)
{
    p.AddTextWatermark("VERTRAULICH", fontSize: 60, opacity: 0.2);     // honours page rotation
    using var canvas = p.GetCanvas();
    canvas.DrawText("Eingang 28.10.2026", PdfStandardFont.HelveticaBold, 10, 400, 20, PdfColor.Red);
}

// another PDF page as background (letterhead)
var letterhead = doc.ImportPageAsTemplate(PdfDocument.Open("briefpapier.pdf").GetPage(1));
using (var c = doc.GetPage(1).GetCanvas(background: true)) c.DrawXObject(letterhead, 0, 0);
```

## Bookmarks

```csharp
var chapter = doc.Outlines.AddChild("Chapter 1", doc.GetPage(1));
chapter.AddChild("Section 1.1", doc.GetPage(2), top: 500).Bold = true;

foreach (var item in doc.Outlines.Children)
    Console.WriteLine($"{item.Title} -> page {item.PageNumber}");
```

Creating the bookmark tree also sets the page mode to show the bookmarks panel.

## Page labels

Page labels are the logical page numbers viewers show, for example "i, ii, iii" for a preface followed by "1, 2, 3":

```csharp
doc.PageLabels.Add(startPage: 1, PdfPageLabelStyle.LowerRoman);
doc.PageLabels.Add(startPage: 5, PdfPageLabelStyle.Decimal);
doc.PageLabels.Add(startPage: 40, PdfPageLabelStyle.UpperLetters, prefix: "Appendix ");
string label = doc.GetPageLabel(7);   // "3"
```

A range lasts until the next one; pages without labels get their page number. Labels belong to page positions:
removing, inserting, importing or merging pages does not shift them.

## Viewer settings

How a viewer shows the document when it is opened:

```csharp
doc.PageLayout = PdfPageLayout.TwoColumnLeft;   // SinglePage (default), OneColumn, TwoPageLeft, ...
doc.PageMode = PdfPageMode.UseThumbs;           // UseNone (default), UseOutlines, FullScreen, UseAttachments, ...
doc.ViewerPreferences.HideToolbar = true;       // also HideMenubar, HideWindowUI, FitWindow, CenterWindow,
doc.ViewerPreferences.DisplayDocTitle = true;   //   DisplayDocTitle (set automatically for PDF/UA)
```

Opened documents return their settings; missing entries read as the PDF defaults.

## Links and notes

```csharp
page.AddLink(new PdfRectangle(72, 700, 200, 715), "https://example.com");
page.AddLink(new PdfRectangle(72, 680, 200, 695), doc.GetPage(5), top: 800);
page.AddTextNote(500, 800, "Please check", author: "QA");
foreach (var a in page.Annotations) Console.WriteLine($"{a.Subtype} {a.Rect} {a.Uri ?? a.Contents}");
```

## Layers

Layers (optional content groups) are content that viewers can show or hide – language versions, watermarks,
dimensions of a drawing, comments.

```csharp
var english = doc.AddLayer("English");
var german = doc.AddLayer("Deutsch", visible: false);     // hidden when the document is opened
using (var canvas = page.GetCanvas())
{
    canvas.Layer(english, c => c.DrawText("Invoice", font, 16, 40, 780));
    canvas.Layer(german, c => c.DrawText("Rechnung", font, 16, 40, 780));
}
page.AddStamp(new PdfRectangle(40, 300, 300, 360), "DRAFT").Layer = german;   // annotations, too

foreach (var layer in doc.Layers)
    Console.WriteLine($"{layer.Name}: visible {layer.IsVisible}, locked {layer.IsLocked}");
doc.Layers[0].IsVisible = false;                           // switch the initial state of existing documents
```

`IsVisible` and `IsLocked` are the state of the default configuration that viewers apply when the document is opened.
Text extraction includes all layers by default; `new TextExtractionOptions { IncludeHiddenLayers = false }` returns
only the text of visible layers. Layers are allowed in PDF/A-2 and later (`CheckPdfA` reports them for PDF/A-1).

## Comments and markup annotations

All annotations are created with an appearance stream (so they look the same in every viewer, print and are
PDF/A compatible) and the print flag.

```csharp
var opt = new AnnotationOptions { Author = "Max", Contents = "Please check", Opacity = 1, Locked = false };

page.HighlightText("Vertragsstrafe", options: opt);                                   // all occurrences
page.AddTextMarkup(TextMarkupType.Underline, page.FindText("Frist"));                 // Highlight, Underline, StrikeOut, Squiggly
page.AddFreeText(new PdfRectangle(72, 600, 300, 650), "Note", font, 10, PdfColor.Blue, border: PdfColor.Blue);
page.AddStamp(new PdfRectangle(350, 600, 540, 650), "APPROVED", PdfColor.Rgb(0, 0.5, 0), font: font);
page.AddImageStamp(new PdfRectangle(350, 500, 450, 560), sealImage);
page.AddInk(strokes, PdfColor.Blue, lineWidth: 2);                                     // freehand drawing
page.AddRectangleAnnotation(rect, stroke: PdfColor.Red, fill: PdfColor.Rgb(1, 0.9, 0.9));
page.AddEllipseAnnotation(rect);
page.AddLineAnnotation(72, 450, 300, 450, end: LineEnding.ClosedArrow);
page.AddFileAttachmentAnnotation(72, 420, "data.csv", bytes, "text/csv", "Raw data"); // PDF/A-3 only

page.FlattenAnnotations();   // burn annotations into the page content (form fields: form.Flatten())
```

For PDF/A and PDF/UA pass an embedded font (`PdfTrueTypeFont`) to `AddFreeText` and `AddStamp`. Text drawn into a
new document can be searched (`FindText`, `HighlightText`) before it is saved.

## Low-level access

The complete object model is available for anything not covered by the high-level API:

```csharp
var catalog = doc.Catalog;                                    // PdfDictionary
catalog["PageLayout"] = PdfName.Get("TwoColumnLeft");
var fonts = doc.GetPage(1).Resources.GetDictionary(PdfName.Font);
var copy = targetDoc.ImportObject(sourceDoc.Catalog.Get("Metadata")!);   // deep copy between documents
```
