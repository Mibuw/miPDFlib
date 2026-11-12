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
    DetectColumns = true,       // read pages set in columns column by column (default; see below)
    IncludeXObjects = true,     // text in form XObjects (stamps, letterheads); default true
    IncludeAnnotations = true,  // text in annotation appearances, e.g. filled form fields
};
string text = page.ExtractText(options);
```

### Columns

Pages set in two or three columns are read column by column (`DetectColumns`, on by default): the page is split at
its whitespace, columns are read one after the other, and a header, a title or author block, a caption across the
page and a footer are read where they stand. Tables, an invoice's description next to its amounts, bulleted and
numbered lists, forms and line numbers keep their rows, and tables set several times side by side (like a tax table)
are read one after the other. This applies to `ExtractText`, `ExtractLines`, `ExtractWords` and `ExtractLetters`;
`DetectColumns = false` reads line by line across the whole page, as Ghostscript and most tools do (and as miPDFlib
did up to 1.3).

How well it works was measured against a reference order – the order in which LaTeX wrote the text of a paper, or the
content order of tagged (accessible) forms – as the share of words in the right order:

| Documents | Line by line | Column by column |
|---|---|---|
| 42 recent arXiv papers (held out while developing it) | 84.8 % | 96.9 % |
| 42 further arXiv papers | 76.2 % | 95.5 % |
| IRS forms and instructions (median of 160 pages) | 50.8 % | 98.7 % |
| 151 ZUGFeRD / Factur-X invoices (289 pages) | – | same order as line by line |

## Layout analysis: headings, paragraphs, lists, tables

`PdfLayoutAnalyzer` returns the text of a page as blocks in reading order and tells what each block is – for
document processing, search indexes or language-model pipelines that need the structure, not just the text:

```csharp
foreach (var page in PdfLayoutAnalyzer.Analyze(document))
    foreach (var block in page.Blocks)
    {
        switch (block.Kind)
        {
            case LayoutBlockKind.Heading: Console.WriteLine($"{new string('#', block.Level)} {block.Text}"); break;
            case LayoutBlockKind.ListItem: Console.WriteLine($"- {block.Text}"); break;
            case LayoutBlockKind.Header or LayoutBlockKind.Footer: break;   // running heads, page numbers
            default: Console.WriteLine(block.Text); break;
        }
    }
```

| Kind | Recognised by |
|---|---|
| `Heading` (with `Level`) | short blocks set larger than the running text, or bold where the text is not, or numbered titles ("2.1 Method") set a little larger; levels rank the sizes across the document |
| `Paragraph` | running text, split at wider line gaps, indented first lines and changes of font size; a bold beginning that runs on stays in its paragraph |
| `ListItem` | a bullet or a number ("1.", "a)", "(iv)") at the start, with its indented further lines |
| `Table` (with `Table`) | a grid of rules with text in its cells (see below), or consecutive rows of cells separated by wide gaps, with a bold head row |
| `Caption` | "Figure 3.", "Table 1:", "Abbildung 2", "Tabelle 1" … at the start |
| `Header`, `Footer` | blocks in the top or bottom tenth of the page that repeat on the pages of the document (with their numbers left out), page numbers, and a smaller outermost line |

`Analyze(page)` analyses a single page; `Analyze(document)` also finds headers and footers by their repetition and
ranks the headings of the whole document. Each block has its `Lines` (with words and positions), `Bounds`,
`FontSize` and `IsBold`. Blocks are found within the columns of the page (see above).

Compared with the structure tree of tagged instructions (IRS), 89 % of the headings, 88 % of the list items and
83 % of the paragraphs are recognised; on forms, whose labels are hard to tell apart, less. The analysis takes about
5 ms per page (arXiv papers, IRS forms and invoices, including reading the file).

### Tables

A block of the kind `Table` has its cells in `Table`: rows and columns, the text of each cell (its lines separated by
`'\n'`), its bounds and how many rows and columns it spans:

```csharp
foreach (var block in PdfLayoutAnalyzer.Analyze(page).Blocks.Where(b => b.Kind == LayoutBlockKind.Table))
{
    var table = block.Table!;
    for (int row = 0; row < table.RowCount; row++)
        Console.WriteLine(string.Join(" | ", table.Cells.Where(c => c.Row == row).Select(c => c.Text)));
}
```

Tables drawn with rules (`HasRules`) are found from the page's graphics: stroked lines, thin filled rectangles and the
edges of cell backgrounds that cross each other form a grid, and grid cells are joined where a rule between them is
missing – a cell spanning rows or columns. Where the text shows that rules were simply left out, cells are divided
again: data rows without rules between them are split at their lines (a wrapped description stays in its cell), and
columns whose rules divide only the head are split where a clear gap runs along the column boundary. Not every grid is
a table: the fields of a form and the frames of a page layout (an address frame over several rows and columns) are left
to the other blocks. A line running through several tables side by side is split between them.

Tables without rules are read from their rows: each row's cells are separated by gaps of 1.5 em or more, and the
columns are the stretches the cells of the fullest rows cover.

The rules themselves are available through `PdfGraphicsExtractor.ExtractRules(page)` – horizontal and vertical
`PdfRule`s with their ends, thickness and whether they are the edge of a filled area.

Checked on the line items of the ZUGFeRD and Factur-X sample invoices, the tax, credit and worksheet tables of the IRS
instructions and the tables of arXiv papers; forms such as 1040, 1099 and W-9 are not taken as tables. Data rows in
cells that span several rows of the grid, and tables nested in a cell, are not divided further.

### Export: Markdown, hOCR, ALTO, PAGE

`PdfLayoutExport` writes the analysed layout in four formats:

```csharp
string markdown = PdfLayoutExport.ToMarkdown(document);   // for language models, search indexes, readers
string hocr = PdfLayoutExport.ToHocr(document);           // hOCR 1.2 (XHTML), as OCR engines write it
string alto = PdfLayoutExport.ToAlto(document);           // ALTO 4.4 XML, as digitisation projects use it
IReadOnlyList<string> page = PdfLayoutExport.ToPageXml(document); // PAGE XML, one document per page
```

**Markdown** writes headings as `#` to `######`, paragraphs with their lines joined, list items as `-` or with their
numbers, tables as pipe tables (a spanning cell in the first of its columns and rows) and captions in italics, and escapes characters Markdown would read as markup. Words
hyphenated at the end of a line are joined; a hyphen that belongs to the word ("self-employed") is kept – the
document's own words decide, and otherwise a hyphen after a lower-case letter is taken as a break. A paragraph broken
by the end of a page or column is joined again. Headers, footers and text outside the flow (a stamp running up the
margin) are left out unless `MarkdownExportOptions.IncludeHeadersAndFooters` is set; `PageSeparator` writes a line
such as `---` between pages.

**hOCR** has an `ocr_page` per page, an `ocr_carea` with an `ocr_par` (or `ocr_header`, `ocr_footer`, `ocr_caption`)
per block, `ocr_line`s and `ocrx_word`s with their bounding boxes; headings, list items and tables carry a `data-kind`
attribute. **ALTO** has per page a `PrintSpace` with `TextBlock`, `TextLine`, `String` and `SP` elements, headers and
footers in `TopMargin` and `BottomMargin`, block kinds as layout tags (`TAGREFS`) and fonts as text styles; its output
validates against the ALTO 4.4 schema. Both measure from the top left corner of the page: hOCR in points
(`scan_res 72 72`), ALTO in 1/1200 inch (`inch1200`).

**PAGE** (2019-07-15, the format of Transkribus, OCR-D and eScriptorium) has one document per page: a `TextRegion` per
block with its kind as region type (`heading`, `paragraph`, `caption`, `header`, `footer`; list items and heading levels
in the `custom` attribute), `TextLine`s with their baselines, `Word`s and the text as `TextEquiv`, tables as
`TableRegion`s with a `TextRegion` per cell and its row, column and spans (`TableCellRole`), and the reading order of
the regions. PAGE measures in pixels of a page image: `PageXmlExportOptions.Resolution` (default 300 dpi) and
`ImageFileName` (default `page-1.png`, ...) name the images, which miPDFlib.Rendering can produce at the same
resolution. The output of 2,800 pages of papers, forms and invoices validates against the PAGE schema.

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

## Letters: font, color and rendering mode

`ExtractLetters` returns every character as it is drawn, in the same order as the lines – for example to find
headings by size and weight, to pick out colored or highlighted text, or to separate the invisible OCR text layer of a
scanned page from visible text:

```csharp
foreach (var letter in PdfTextExtractor.ExtractLetters(page))
{
    // position: Bounds, BaselineStart, BaselineEnd, Angle (degrees; 90 = text running upwards)
    // font: FontName, FontSize (as it appears, in points), IsBold, IsItalic
    // drawing: FillColor, StrokeColor, RenderingMode, IsVisible
    if (letter.IsBold && letter.FontSize >= 14) Console.Write(letter.Text);
}

bool hasOcrLayer = PdfTextExtractor.ExtractLetters(page).Any(l => !l.IsVisible);
```

Colors in gray, RGB and CMYK are returned as they are in the file (ICC-based colors by their number of components);
spot colors (Separation, DeviceN), Indexed and Lab colors are converted to RGB, and text painted with a pattern has
no single color (`null`). Bold and italic come from the font descriptor (weight, flags, italic angle) or the font name.
`Bounds` is the glyph cell (the advance along the baseline, 0.8 em above and 0.2 em below it), the same box the words
and the text search use.

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

## Vector graphics: paths

`PdfGraphicsExtractor.ExtractPaths` returns the paths painted on a page – lines, rectangles, curves, the bars and
axes of charts, underlines, form boxes – in painting order, with their geometry in user space of the page and how they
are painted:

```csharp
foreach (var path in PdfGraphicsExtractor.ExtractPaths(page))
{
    Console.WriteLine($"{(path.IsFilled ? "filled" : "")} {(path.IsStroked ? "stroked" : "")} {path.Bounds}");
    Console.WriteLine($"  fill {path.FillColor}, stroke {path.StrokeColor}, width {path.LineWidth}");
    foreach (var subpath in path.Subpaths)
        foreach (var segment in subpath.Segments)   // MoveTo, LineTo, CurveTo (with control points), ClosePath
            Console.WriteLine($"  {segment.Kind} {segment.Point}");
}
```

Each path has its fill rule (nonzero or even-odd), fill and stroke color (as for letters: gray, RGB and CMYK as in
the file, other color spaces converted to RGB, null for patterns), opacity, line width, cap, join and dash pattern – the
widths and dashes as they appear on the page, scaled by the transformations of the content. `Bounds` covers curves by
their extent, not by their control points. Paths in form XObjects are included; `PathExtractionOptions` adds clipping
paths (`IncludeClippingPaths`) and the appearances of annotations (`IncludeAnnotations`), or leaves out hidden layers
(`IncludeHiddenLayers = false`). Text, images and shadings are not paths.

`PdfGraphicsExtractor.ExtractRules` reduces the paths to the horizontal and vertical rules a table or a form is drawn
with (see [Tables](#tables)).

Compared with PdfPig on 2,800 pages of papers, forms and invoices (700,000 paths), the same paths are found with the
same bounds, painting operators and fill colors; extracting them takes about 1.5 ms per page.

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
