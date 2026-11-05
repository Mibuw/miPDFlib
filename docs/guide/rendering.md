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

**Text** is drawn from the glyph outlines of the embedded fonts: TrueType, OpenType (CFF), CFF/Type1C (also
CID-keyed), Type 1 (PFA/PFB, including seac accents and flex) and Type 3 fonts, simple and composite (Type0). All
text rendering modes are supported, including the clipping modes, together with character and word spacing,
horizontal scaling, rise and TJ adjustments. Fonts that are not embedded – the standard 14 fonts and others – are
drawn with an installed substitute: Arial, Liberation Sans or DejaVu Sans for sans-serif fonts, Times New Roman,
Liberation Serif or DejaVu Serif for serif fonts, Courier New, Liberation Mono or DejaVu Sans Mono for monospaced
fonts (each in the matching weight and style), the Windows Symbol font for Symbol, and Segoe UI Symbol or DejaVu
Sans for ZapfDingbats. If no substitute is installed (e.g. a minimal container image), such text is not drawn; install
fonts such as `fonts-liberation` there. Glyphs are not hinted, so small text looks slightly lighter than in
renderers that use hinting.

Not yet rendered, following in the next previews (see the [roadmap](https://github.com/Mibuw/miPDFlib/blob/main/docs/PROGRESS.md#roadmap)): shadings and
patterns, transparency groups, blend modes and soft masks; Separation and DeviceN colors are approximated as gray,
CMYK is converted without a color profile. JPEG 2000 and JBIG2 images are not drawn.

The output is checked against Ghostscript: for vector graphics and images the mean difference is below one level
of 255; differences are at anti-aliased edges and in CMYK colors (Ghostscript uses a color profile). Text pages
differ mainly by hinting; pages with shadings or spot colors differ until the next stages.
