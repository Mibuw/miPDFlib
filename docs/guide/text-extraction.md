# Text extraction

```csharp
using var doc = PdfDocument.Open("input.pdf");
string all = doc.ExtractText();                 // pages separated by '\f'
string first = doc.GetPage(1).ExtractText();    // lines separated by '\n'
```

## Options

```csharp
var options = new TextExtractionOptions
{
    SortByPosition = true,      // reading order by position (default); false = content stream order
    DetectParagraphs = true,    // empty line for large vertical gaps
    IncludeXObjects = true,     // text in form XObjects (stamps, letterheads); default true
    IncludeAnnotations = true,  // text in annotation appearances, e.g. filled form fields
};
string text = page.ExtractText(options);
```

## Words, lines and positions

```csharp
foreach (var line in PdfTextExtractor.ExtractLines(page))
{
    Console.WriteLine($"{line.Bounds}: {line.Text}");
    foreach (var word in line.Words)
        Console.WriteLine($"  {word.Text} at {word.Bounds} ({word.FontName} {word.FontSize}pt)");
}
```

Coordinates are in PDF user space (points, origin bottom-left).

## Searching text

`FindText` returns the bounding boxes of all matches – useful to place stamps or signature fields at a
placeholder in a generated document:

```csharp
foreach (var hit in page.FindText("{{SIGNATURE}}"))
    Console.WriteLine($"found at {hit}");
```

## Images

```csharp
foreach (var image in page.ExtractImages())                 // incl. images in form XObjects and inline images
{
    var file = image.ToFile(out var extension);             // JPEG/JPEG 2000 as is, everything else as PNG
    if (file != null) File.WriteAllBytes($"image{extension}", file);
    Console.WriteLine($"{image.Width}x{image.Height} at {image.Bounds}");
}
// image of a visible signature or push button:
var png = widget.ExtractImages()[0].ToPng(background: PdfColor.White);
```

PNG output includes transparency from soft masks, stencil masks and image masks; DeviceGray/RGB/CMYK, ICC based,
indexed, Separation/DeviceN images with 1-16 bits are decoded, also JPEG (baseline and progressive), JPEG 2000,
JBIG2 and CCITT fax images (Group 3 and 4); JBIG2 and CCITT are typical for scans and faxes. `ToFile` returns JPEG
and JPEG 2000 data unchanged when the image has no transparency.

## Free space

`page.FindFreeSpaces(minWidth, minHeight)` returns maximal empty rectangles (largest first) that are not covered by
text, images, vector graphics or annotations – e.g. to place a signature without covering content.

## What is supported

- Simple fonts (Type1, TrueType, Type3) with standard encodings (WinAnsi, MacRoman, Standard, PDFDoc),
  `/Differences` arrays and glyph names (`Adieresis`, `uni20AC`, `f_f`, `a.sc`, ...)
- Composite fonts (Type0) with Identity and embedded CMaps
- `/ToUnicode` CMaps (`bfchar`, `bfrange` incl. array form)
- Identity-encoded embedded TrueType fonts **without** ToUnicode: the font's own cmap is used as fallback
- Text state: `Tc`, `Tw`, `Tz`, `Ts`, `TL`, `TJ` adjustments, rotation and arbitrary transformations
- Form XObjects (recursively), inline images are skipped correctly
- Reading order across rotated text (lines are grouped by orientation)

Replacement text of marked content (`/ActualText`, ISO 32000-2, 14.9.4) is used instead of the glyphs it covers – for
example right-to-left text written by miPDFlib or ligatures marked by word processors – so such text is extracted in
reading order.

Limitations: no OCR (scanned pages without text layer return nothing), right-to-left text without `/ActualText` is
extracted in visual order, predefined CJK CMaps other than Identity are treated as 2-byte encodings.
