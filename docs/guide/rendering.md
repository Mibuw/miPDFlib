# Rendering pages to images

The separate package **miPDFlib.Rendering** renders pages to images – for previews, thumbnails and printing. It is
pure managed code without native dependencies and draws on the same document model as miPDFlib.

```
dotnet add package miPDFlib.Rendering
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

miPDFlib.Rendering renders vector graphics (fills with the nonzero and even-odd rule, strokes with all joins, caps and dash
patterns, minimum line width of one pixel), clipping, images (all formats miPDFlib decodes, with soft masks and
stencil masks), form XObjects, annotation appearances and layers (hidden layers are not drawn). Edges are
anti-aliased.

**Colors** are converted to sRGB: gray, RGB, CMYK (approximating the U.S. Web Coated (SWOP) press profile that PDF
viewers commonly use, so that pure cyan or black ink look as on paper), CIE L\*a\*b\*, ICC-based colors by their
number of components (gray, RGB or CMYK; the embedded profile itself is not evaluated), Indexed, and spot colors:
Separation and DeviceN through their tint transforms into the alternate color space. The colorant `None` paints
nothing. The same conversion applies to images, also when they are exported with `ToPng`.

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

**Shadings and patterns**: all shading types – function-based (1), axial (2), radial (3), free-form and
lattice-form triangle meshes (4, 5), Coons and tensor-product patch meshes (6, 7) – with the `sh` operator and as
shading patterns, and tiling patterns (colored and uncolored, any matrix). PDF functions of all types are evaluated:
sampled (0), exponential (2), stitching (3) and PostScript calculator functions (4).

**Transparency**: constant opacity, all 16 blend modes, soft masks (alpha and luminosity, with backdrop color and
transfer function) and transparency groups, isolated and non-isolated, including knockout groups. As in Acrobat, the
page itself is composited on a transparent backdrop before it is put on the paper, so blend modes leave the source
color where nothing was painted below.

Not rendered: JPEG 2000 and JBIG2 images (not decoded yet), overprint, and color management with the embedded ICC
profiles.

The output is checked against Ghostscript: for vector graphics and images the mean difference is below one level
of 255; differences are at anti-aliased edges. CMYK colors differ by a few levels because Ghostscript uses its own
default press profile. Text pages differ mainly by hinting. Shadings, patterns and transparency are also checked with the shading and transparency
test files of pdf.js: most match Ghostscript within one or two levels; tiling patterns are drawn anti-aliased and at
their exact position, where Ghostscript snaps tiles to whole pixels and draws them without anti-aliasing.
