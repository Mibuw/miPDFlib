# miPDFlib.PostScript

PostScript and EPS for [miPDFlib](https://www.nuget.org/packages/miPDFlib): place EPS graphics in PDF documents and
convert PostScript files to PDF. Pure managed code for .NET 8 and .NET 10, without native dependencies (Apache-2.0).

> In development: the PostScript interpreter (language core) and the graphics (paths, clipping, colors, pages) are in
> place; fonts, images, EPS import and the converter follow. See the [roadmap](https://github.com/Mibuw/miPDFlib/blob/main/docs/PROGRESS.md).

PostScript is a programming language: the interpreter runs in a sandbox without access to the file system, with
limits on operations, memory and time.

Licensed under the **Apache License, Version 2.0**.
