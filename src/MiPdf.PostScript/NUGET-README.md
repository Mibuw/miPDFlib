# miPDFlib.PostScript

PostScript and EPS for [miPDFlib](https://www.nuget.org/packages/miPDFlib): convert PostScript files to PDF and place
EPS graphics in PDF documents. Pure managed code for .NET 8 and .NET 10, without native dependencies (Apache-2.0).

## Converting PostScript to PDF

```csharp
using MiPdf;
using MiPdf.PostScript;

using var document = PostScriptConverter.ToPdf(File.ReadAllBytes("report.ps"),
    new PostScriptOptions { PageSize = PageSize.A4, Timeout = TimeSpan.FromMinutes(1) });
document.Save("report.pdf");
```

Text keeps its fonts (Type 1, Type 3, Type 42, CID-keyed), and graphics, images, patterns and shadings stay vectors.
`pdfmark` adds document information, bookmarks, links, annotations, named destinations, article threads and embedded
files. The document information is also taken from the DSC comments (%%Title, %%Creator, %%For). Pages are rotated
to be read upright, by the orientation in the DSC comments or by the direction of their text (`AutoRotatePages`).
EPS files become a page of their bounding box; print files with a PJL header and DOS EPS binary files are read too.

## Placing EPS graphics

```csharp
var logo = document.ImportEps(File.ReadAllBytes("logo.eps"));   // a form XObject of the bounding box
var page = document.AddPage();
using (var canvas = page.GetCanvas())
    canvas.DrawXObject(logo, 50, 700, width: 120, height: 120 * logo.Height / logo.Width);
```

## Sandbox and errors

PostScript is a programming language: the interpreter runs in a sandbox without access to the file system, with
limits on operations, memory and time (`PostScriptOptions`). A PostScript error throws a `PostScriptException` with
the error name, the offending command and the program's output, unless `IgnoreErrors` keeps what was painted before
it (as a printer prints the pages up to the error).

Licensed under the **Apache License, Version 2.0**.
