# miPDFlib.PostScript

PostScript and EPS for [miPDFlib](https://www.nuget.org/packages/miPDFlib): place EPS graphics in PDF documents and
convert PostScript files to PDF. Pure managed code for .NET 8 and .NET 10, without native dependencies (Apache-2.0).

> In development: EPS import is in place; the converter for whole PostScript files follows. See the
> [roadmap](https://github.com/Mibuw/miPDFlib/blob/main/docs/PROGRESS.md).

## Placing EPS graphics

```csharp
using MiPdf;
using MiPdf.PostScript;

using var document = PdfDocument.Create();
var logo = document.ImportEps(File.ReadAllBytes("logo.eps"));   // a form XObject of the bounding box
var page = document.AddPage();
using (var canvas = page.GetCanvas())
    canvas.DrawXObject(logo, 50, 700, width: 120, height: 120 * logo.Height / logo.Width);
document.Save("out.pdf");
```

The graphic stays vector: paths, text with its fonts (Type 1, Type 3, Type 42, CID-keyed), images, patterns and
shadings are written as PDF. DOS EPS binary files are read without their preview.

PostScript is a programming language: the interpreter runs in a sandbox without access to the file system, with limits
on operations, memory and time (`PostScriptOptions`). A PostScript error throws a `PostScriptException` with the error
name and the offending command, unless `IgnoreErrors` keeps what was painted before it.

Licensed under the **Apache License, Version 2.0**.
