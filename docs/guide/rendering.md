# Rendering pages to images (preview)

The separate package **miPDFlib.Rendering** renders pages to images – for previews, thumbnails and printing. It is
pure managed code without native dependencies and draws on the same document model as miPDFlib.

```
dotnet add package miPDFlib.Rendering --prerelease
```

```csharp
using MiPdf;
using MiPdf.Rendering;

using var doc = PdfDocument.Open("input.pdf");
var png = PdfRenderer.RenderToPng(doc.GetPage(1), new PdfRenderOptions { Dpi = 150 });
File.WriteAllBytes("page1.png", png);

// pixels for further processing (RGBA, rows from the top)
var page = PdfRenderer.Render(doc.GetPage(1), new PdfRenderOptions { Dpi = 300, Background = null });
Console.WriteLine($"{page.Width}x{page.Height}, first pixel alpha {page.Rgba[3]}");
```

| Option | Meaning |
|---|---|
| `Dpi` | resolution (default 96); 300 or 600 for printing |
| `Background` | page background (default white); `null` for a transparent background |
| `RenderAnnotations` | draw annotations and form fields (default true) |
| `ForPrinting` | draw the annotations as printed: only those with the print flag, also those hidden on screen |

The page is rendered with its crop box and rotation as viewers show it. Printing itself is left to the application:
render at the printer resolution and send the image to the printer.

## What is rendered

This preview renders vector graphics (fills with the nonzero and even-odd rule, strokes with all joins, caps and dash
patterns, minimum line width of one pixel), clipping, colors (gray, RGB, CMYK, Lab, ICC-based by their number of
components, indexed), constant opacity, images (all formats miPDFlib decodes, with soft masks and stencil masks),
form XObjects, annotation appearances and layers (hidden layers are not drawn). Edges are anti-aliased.

Not yet rendered, following in the next previews (see the [roadmap](https://github.com/Mibuw/miPDFlib/blob/main/docs/PROGRESS.md#roadmap)): **text**, shadings and
patterns, transparency groups, blend modes and soft masks; Separation and DeviceN colors are approximated as gray,
CMYK is converted without a color profile. JPEG 2000 and JBIG2 images are not drawn.

The output is checked against Ghostscript: for vector graphics and images the mean difference is below one level
of 255; differences are at anti-aliased edges and in CMYK colors (Ghostscript uses a color profile).
