# miPDFlib.Rendering

Renders PDF pages to images (PNG or RGBA pixels) at any resolution – for previews, thumbnails and printing. Pure
managed code for .NET 8 and .NET 10, without native dependencies, based on [miPDFlib](https://www.nuget.org/packages/miPDFlib)
(Apache-2.0).

```csharp
using MiPdf;
using MiPdf.Rendering;

using var doc = PdfDocument.Open("input.pdf");
var png = PdfRenderer.RenderToPng(doc.GetPage(1), new PdfRenderOptions { Dpi = 150 });
File.WriteAllBytes("page1.png", png);
```

> Rendered: vector graphics, clipping, images and text (embedded TrueType, OpenType, CFF, Type 1 and Type 3 fonts;
> substitutes for non-embedded fonts) are rendered, as well as shadings, patterns, blend modes, soft masks,
> transparency groups and spot colors (Separation, DeviceN); CMYK approximates a SWOP press profile. See the [roadmap](https://github.com/Mibuw/miPDFlib/blob/main/docs/PROGRESS.md).

Licensed under the **Apache License, Version 2.0**.
