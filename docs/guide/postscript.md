# PostScript and EPS

The separate package **miPDFlib.PostScript** converts PostScript files to PDF and places EPS graphics in PDF
documents. It contains a PostScript interpreter (LanguageLevel 3) in pure managed code, without Ghostscript or other
native dependencies.

```
dotnet add package miPDFlib.PostScript
```

## Converting PostScript to PDF

```csharp
using MiPdf;
using MiPdf.PostScript;

using var document = PostScriptConverter.ToPdf(File.ReadAllBytes("report.ps"));
document.Save("report.pdf");
```

The result is an ordinary `PdfDocument`: it can be changed, signed or saved as PDF/A like any other document.

| Option (`PostScriptOptions`) | Meaning |
|---|---|
| `PageSize` | the page size the program starts with (default US Letter); programs can change it with `setpagedevice` |
| `AutoRotatePages` | rotate pages so that they are read upright (default true) |
| `Timeout`, `MaxOperations`, `MaxMemory` | limits of the sandbox (see below) |
| `IgnoreErrors` | keep the pages shown before a PostScript error instead of throwing |

```csharp
var options = new PostScriptOptions { PageSize = PageSize.A4, Timeout = TimeSpan.FromSeconds(30) };
using var document = PostScriptConverter.ToPdf(stream, options);
```

**What is converted.** Paths, clipping and all color spaces (device colors are kept; CIE-based, Separation and DeviceN
colors are converted to device colors). Text keeps its fonts: Type 1, Type 3, Type 42 and CID-keyed fonts embedded in
the program are embedded in the PDF, the standard fonts (Helvetica, Times, Courier, ...) are referenced. Images in all
forms (`image`, `colorimage`, `imagemask`, ImageType 1, 3 and 4, 1 to 16 bits) with the filters of LanguageLevel 3,
JPEG data unchanged. Patterns, shadings (all seven types) and forms stay vectors. `setpagedevice` with the page size
and the BeginPage, EndPage and Install procedures.

**pdfmark** adds what PostScript itself cannot express: document information (DOCINFO), viewer settings (DOCVIEW),
bookmarks (OUT), annotations and links (ANN, LNK), named destinations (DEST), page entries (PAGE, PAGES), article
threads (ARTICLE), embedded files (EMBED), named objects (OBJ, PUT, ...) and reusable pictures (BP, EP, SP).

**DSC comments.** The document information is also taken from the comments `%%Title`, `%%Creator` and `%%For` (as
the author); pdfmark DOCINFO takes precedence. With `AutoRotatePages`, a page is rotated by `%%PageOrientation` or
`%%Orientation` (Portrait, Landscape), otherwise by the direction most of its text runs in, as Ghostscript's ps2pdf
does.

**Input files.** EPS files become one page of the size of their bounding box. Print files written by printer drivers
are read with their PJL job header and control-D characters; DOS EPS binary files without their preview.

## Placing EPS graphics

`ImportEps` runs an EPS file and returns what it paints as a form XObject. The form is as large as the bounding box
(`%%HiResBoundingBox` if present, otherwise `%%BoundingBox`), with its lower left corner at 0, 0, and can be drawn on
any page, as often as needed and at any size:

```csharp
using MiPdf;
using MiPdf.PostScript;

using var document = PdfDocument.Create();
var logo = document.ImportEps(File.ReadAllBytes("logo.eps"));
var page = document.AddPage(PageSize.A4);
using (var canvas = page.GetCanvas())
{
    canvas.DrawXObject(logo, 50, 750);                                  // original size
    canvas.DrawXObject(logo, 50, 500, width: 100, height: 100 * logo.Height / logo.Width);
}
document.Save("letter.pdf");
```

As the EPS specification asks of an importing application, `showpage` and the page-level operators (`erasepage`,
`setpagedevice`, `copypage`) do not erase or resize the graphic. The form does not depend on the graphics state it is
drawn with. Document-level pdfmarks (DOCINFO, OUT, ANN, ...) are ignored in an EPS file.

## Sandbox and errors

PostScript is a programming language, and a file can loop forever or allocate without end. The interpreter has no
access to the file system or the network (`file` only opens `%stdin`, `%stdout` and `%stderr`, `run` and
`deletefile` fail), and it ends a program that exceeds a limit with a `PdfException`:

| Limit | Default |
|---|---|
| `MaxOperations` | 1,000,000,000 operations (roughly a minute) |
| `Timeout` | none |
| `MaxMemory` | 1 GB, measured as the growth of the managed heap (approximate; jobs running at the same time count for each other) |

Converting files from untrusted sources, set a `Timeout` and pass a `CancellationToken` if the conversion runs in a
request.

A PostScript error that ends the program (such as `undefined` or `typecheck`) throws a `PostScriptException` with
the error name, the offending command and the text the program wrote to its output – what a printer would print on
its error page:

```csharp
try
{
    using var document = PostScriptConverter.ToPdf(data);
}
catch (PostScriptException ex)
{
    Console.WriteLine($"{ex.ErrorName} in {ex.OffendingCommand}: {ex.Output}");
}
```

With `IgnoreErrors = true` the pages shown before the error are kept (the page begun at the error is dropped, as
with Ghostscript); for EPS import, what was painted before the error.

## Limits

- `charpath` with a standard font that the program does not embed (Helvetica, Times, ...) gives an empty path, since
  the package contains no font programs for them; text shown with these fonts is not affected.
- Spot colors (Separation, DeviceN) are converted to their alternate color space, not kept as spot colors.
- CIEBasedDEF and CIEBasedDEFG tables are not evaluated, binary tokens and binary object sequences are not read.
- The output is compared with Ghostscript page by page; differences are at anti-aliased edges and where Ghostscript
  aligns pattern cells to its pixels.
