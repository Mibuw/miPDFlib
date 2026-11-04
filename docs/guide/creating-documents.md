# Creating documents

## Minimal example

```csharp
using MiPdf;
using MiPdf.Fonts;
using MiPdf.Graphics;

using var doc = PdfDocument.Create();
doc.Info.Title = "Hello";
var page = doc.AddPage(PageSize.A4);
using (var canvas = page.GetCanvas())
{
    canvas.DrawText("Hello, PDF!", PdfStandardFont.HelveticaBold, 24, 72, 760);
}
doc.Save("hello.pdf");
```

Coordinates are PDF user space: points (1/72 inch), origin at the **bottom-left** corner of the page.
`PageSize` contains the ISO sizes A0–A6, B4 and B5, the North American sizes Letter, Legal, Tabloid, Ledger and
Executive, `Landscape(...)` (width and height swapped) and `FromMillimeters(...)`.

## Fonts

| Font type | Usage | Characters |
|---|---|---|
| `PdfStandardFont.Helvetica`, `TimesRoman`, `Courier` (+ Bold/Italic variants), `Symbol`, `ZapfDingbats` | not embedded, smallest files | WinAnsi (Western European incl. umlauts, €) |
| `PdfTrueTypeFont.FromFile("arial.ttf")` | embedded as subset (Type0/Identity-H) | full Unicode (Latin, Greek, Cyrillic, CJK, ...) |

```csharp
var arial = PdfTrueTypeFont.FromFile(@"C:\Windows\Fonts\arial.ttf");
canvas.DrawText("Zażółć gęślą jaźń · Ελληνικά", arial, 12, 72, 700);
double width = arial.GetTextWidth("measure me", 12);  // points
```

Font objects are independent of documents and can be reused (e.g. as static fields). Each document
embeds only the glyphs it actually uses. `.ttc` collections: `FromFile(path, collectionIndex)`.
CFF-based OpenType fonts (`.otf`) are embedded completely. Fonts whose license forbids embedding are
rejected unless `ignoreLicenseRestrictions: true` is passed.

Not supported yet: complex script shaping (Arabic, Indic), ligatures, right-to-left text.

## Canvas (`page.GetCanvas()`)

The canvas collects drawing operations and appends them to the page when disposed.
`page.GetCanvas(background: true)` draws *below* the existing content.

| Area | Methods |
|---|---|
| State | `SaveState`, `RestoreState`, `Translate`, `Scale`, `Rotate(deg, cx, cy)`, `Transform` |
| Style | `SetFillColor`, `SetStrokeColor` (`PdfColor.Rgb/Cmyk/Gray/FromHex`), `SetLineWidth`, `SetDashPattern`, `SetLineCap`, `SetLineJoin`, `SetOpacity` |
| Paths | `MoveTo`, `LineTo`, `CurveTo`, `Rectangle`, `RoundedRectangle`, `Ellipse`, `Circle`, `ClosePath`, `Stroke`, `Fill`, `FillAndStroke`, `Clip` |
| Shortcuts | `DrawLine`, `DrawRectangle`, `DrawText` (left/center/right), `DrawImage`, `DrawXObject` |
| Text (low level) | `BeginText`, `SetFont`, `SetTextPosition`, `ShowText`, `SetCharacterSpacing`, `SetTextRenderingMode`, `EndText` |
| Raw | `WriteRaw("...")` writes content stream operators directly |

## Images

```csharp
var logo = PdfImage.FromFile("logo.png");         // PNG (incl. transparency) or JPEG
canvas.DrawImage(logo, 72, 700, 120, 40);         // exact rectangle
canvas.DrawImage(logo, new PdfRectangle(72, 600, 300, 700)); // fit into box, keep aspect ratio
```

JPEGs are embedded unchanged. PNGs are decoded; alpha channels become soft masks.
An image used on many pages is embedded only once.

## Reusable content (form XObjects)

```csharp
var stamp = new PdfFormXObject(doc, new PdfRectangle(0, 0, 200, 50));
using (var c = stamp.GetCanvas()) c.DrawRectangle(0, 0, 200, 50, PdfColor.Red);
foreach (var p in doc.Pages)
    using (var c = p.GetCanvas()) c.DrawXObject(stamp, 300, 50);
```

## Flow layout (`MiPdf.Layout`)

`LayoutDocument` places paragraphs, tables and images from top to bottom and creates pages as needed.

```csharp
using var pdf = PdfDocument.Create();
using (var layout = new LayoutDocument(pdf, PageSize.A4, new Thickness(50)))
{
    layout.Footer = ctx => ctx.Canvas.DrawText($"Page {ctx.PageNumber} of {ctx.TotalPages}",
        PdfStandardFont.Helvetica, 8, ctx.ContentArea.Right, 30, alignment: TextAlignment.Right);

    layout.Add(new Paragraph("Invoice", PdfStandardFont.HelveticaBold, 20) { SpacingAfter = 10 });

    var text = new Paragraph { Alignment = TextAlignment.Justify };
    text.Add("Normal text, ");
    text.Add(new TextRun("bold and underlined", PdfStandardFont.HelveticaBold) { Underline = true });
    text.Add(", a ").AddLink("link", "https://example.com");   // blue, underlined, clickable
    text.Add(" and a\nhard line break.");
    layout.Add(text);

    var table = new Table(1, 4, 1) { AlternateRowBackground = PdfColor.Gray(0.95) };
    table.AddHeaderCell("Pos"); table.AddHeaderCell("Description"); table.AddHeaderCell("Amount");
    table.AddCell("1"); table.AddCell("Consulting"); table.AddCell(new Cell("100.00") { TextAlignment = TextAlignment.Right });
    table.AddCell(new Cell("Total", colSpan: 2)); table.AddCell("100.00");
    layout.Add(table);

    layout.Add(new ImageElement(PdfImage.FromFile("logo.png"), width: 150));
    layout.Add(new PageBreak());
}
pdf.Save("invoice.pdf");
```

| Element | Features |
|---|---|
| `Paragraph` | runs with individual font/size/color/underline/strikethrough, alignment incl. justify, line spacing, indents, spacing before/after; kerning (TrueType/OpenType, default on), soft hyphens and hyphenation; splits across pages line by line |
| `Table` | relative column widths, header rows repeated on each page, column and **row spans**, rows taller than a page are split across pages (`SplitRowsAcrossPages`), padding, borders, backgrounds, zebra rows, vertical alignment, nested elements in cells |
| `ListElement` | bullet, dash, decimal, a), A., i., I. or custom labels, nested lists, any content per item, continues across pages |
| `BarcodeElement` | QR, Data Matrix, Code 128, EAN, ... (see [barcodes](barcodes.md)) |
| `ImageElement` | natural size (96 dpi), fixed width/height, alignment; scaled down if larger than the page |
| `HorizontalLine`, `Spacer`, `PageBreak` | |

`TextLayout.BreakLines(text, font, size, width)` and `TextLayout.MeasureHeight(...)` are available for
custom layouts. Headers/footers are rendered when the layout is closed, so `TotalPages` is known.

### Lists

```csharp
var list = new ListElement(ListStyle.Decimal) { Font = font, FontSize = 11 };
list.AddItem("First item");
list.AddItem("Second item").Add(new ListElement(ListStyle.LowerAlpha) { Font = font }.AddItems("a", "b"));
list.AddItem(new Paragraph("Paragraph"), new ImageElement(image, 100));   // any content
layout.Add(list);
```

Styles: `Bullet`, `Dash`, `Decimal` (1.), `LowerAlpha` (a.), `UpperAlpha` (A.), `LowerRoman` (i.), `UpperRoman` (I.) and
`None`. Other labels come from `LabelFormatter`, e.g. `n => $"{(char)('a' + n - 1)})"` for "a)".

### Row spans and long rows

```csharp
table.AddCell(new Cell("Group A", rowSpan: 3) { VerticalAlignment = VerticalAlignment.Middle });
table.AddCell(new Cell("spanning two columns", colSpan: 2));
```

Rows connected by row spans stay on one page. A row that does not fit on the current page moves to the next page;
a row taller than a whole page is split (content continues in the same cells, the header row is repeated).

### Kerning and hyphenation

- Kerning (GPOS or `kern` table) is applied for TrueType/OpenType fonts (`Paragraph.Kerning`, default true;
  `canvas.DrawText(..., kerning: true)`, `font.GetTextWidth(text, size, kerning: true)`). The standard 14 fonts have
  no kerning data.
- Soft hyphens (U+00AD) in the text are used as break points; hard hyphens are break points as well.
- Automatic hyphenation with TeX patterns (Liang's algorithm). The pattern files are not bundled – get them from the
  hyph-utf8 project (e.g. `hyph-de-1996.tex`, MIT licensed):

```csharp
TextLayout.DefaultHyphenator = PatternHyphenator.FromTex(File.ReadAllText("hyph-de-1996.tex"));
// or per paragraph: paragraph.Hyphenator = ...
```

### Image formats

`PdfImage.FromBytes` accepts JPEG (embedded unchanged), PNG (all color types, transparency), BMP (1/4/8/16/24/32 bit,
RLE, bit fields with alpha) and GIF (first frame, transparency, interlacing).

## PDF/A (long-term archiving)

```csharp
var doc = PdfDocument.Create();
doc.Info.Title = "Annual report 2026";
doc.Conformance = PdfAConformance.PdfA2B;   // or PdfA1B, PdfA2U, PdfA3B, PdfA3U, PdfA4, PdfA4F
// ... content with embedded fonts (PdfTrueTypeFont); the standard 14 fonts are not embedded
var issues = doc.CheckPdfA(PdfAConformance.PdfA2B);   // basic checks; use veraPDF for a full validation
doc.Save("report.pdf");
```

Saving writes the XMP metadata with the PDF/A identification and an sRGB output intent, and sets the PDF version the
part requires:

| Level | Based on | Notes |
|---|---|---|
| `PdfA1B` | PDF 1.4 | no transparency, no object streams |
| `PdfA2B`, `PdfA2U` | PDF 1.7 | `U`: all text mappable to Unicode (always the case with `PdfTrueTypeFont`) |
| `PdfA3B`, `PdfA3U` | PDF 1.7 | like PDF/A-2, arbitrary attachments allowed (ZUGFeRD / Factur-X) |
| `PdfA4` | PDF 2.0 | attachments must be PDF/A files |
| `PdfA4F` | PDF 2.0 | arbitrary attachments, at least one required |

PDF/A-4 does not allow the document information dictionary: miPDFlib writes title, author and dates only into the
XMP metadata, and `doc.Info` of an opened PDF/A-4 (or other PDF 2.0) document reads them from there. Changes to
`doc.Info` of an opened PDF/A document are written into its XMP metadata on saving, so the document stays conforming
without setting `Conformance` again. PDF/A-4 can be combined with PDF/UA-2 (see [accessibility](accessibility.md)),
PDF/A-1 to -3 with PDF/UA-1.
