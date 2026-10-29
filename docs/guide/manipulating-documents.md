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

### PDF 2.0

PDF 2.0 (ISO 32000-2) files are read, edited, saved and signed like any other PDF; `doc.Version` reports "2.0".
Covered and tested with the PDF Association's PDF 2.0 example files:

- UTF-8 text strings (in addition to PDFDocEncoding and UTF-16BE),
- data before the `%PDF-` header with offsets counted from the header (the preamble is not part of the PDF and is
  dropped when the document is saved or signed),
- page-level output intents, black point compensation and other new entries (kept as they are),
- AES-256 encryption (revision 6), PAdES signatures, DSS and document time-stamps, associated files (`/AF`).

New documents are written as PDF 1.7 by default; set `doc.Version = "2.0"` or `PdfSaveOptions.Version` for a
PDF 2.0 header. AES-256 encryption raises the version to 2.0 automatically. Not supported yet: PDF/A-4, PDF/UA-2
and the PDF 2.0 structure namespaces.

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

## Links and notes

```csharp
page.AddLink(new PdfRectangle(72, 700, 200, 715), "https://example.com");
page.AddLink(new PdfRectangle(72, 680, 200, 695), doc.GetPage(5), top: 800);
page.AddTextNote(500, 800, "Please check", author: "QA");
foreach (var a in page.Annotations) Console.WriteLine($"{a.Subtype} {a.Rect} {a.Uri ?? a.Contents}");
```

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
