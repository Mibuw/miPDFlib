using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.Objects;
using MiPdf.Rendering.Raster;

namespace MiPdf.Rendering;

/// <summary>Options for <see cref="PdfRenderer"/>.</summary>
public sealed class PdfRenderOptions
{
    /// <summary>Resolution in dots per inch (default 96: one pixel per 0.75 point).</summary>
    public double Dpi { get; set; } = 96;

    /// <summary>Background of the page (default white); null renders on a transparent background.</summary>
    public PdfColor? Background { get; set; } = PdfColor.White;

    /// <summary>Render the appearances of annotations and form fields (default true).</summary>
    public bool RenderAnnotations { get; set; } = true;

    /// <summary>
    /// Render for printing: only annotations with the print flag, also those not shown on screen (default false:
    /// as viewers show the page).
    /// </summary>
    public bool ForPrinting { get; set; }
}

/// <summary>A rendered page: RGBA pixels (8 bits per channel, not premultiplied, rows from the top).</summary>
public sealed class RenderedPage
{
    internal RenderedPage(int width, int height, byte[] rgba, bool opaque)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
        _opaque = opaque;
    }

    private readonly bool _opaque;

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixels: red, green, blue and alpha per pixel, row by row from the top.</summary>
    public byte[] Rgba { get; }

    /// <summary>Encodes the image as PNG (with alpha channel if rendered on a transparent background).</summary>
    public byte[] ToPng()
    {
        long count = (long)Width * Height;
        var rgb = new byte[count * 3];
        byte[]? alpha = _opaque ? null : new byte[count];
        for (long i = 0; i < count; i++)
        {
            rgb[i * 3] = Rgba[i * 4];
            rgb[i * 3 + 1] = Rgba[i * 4 + 1];
            rgb[i * 3 + 2] = Rgba[i * 4 + 2];
            if (alpha != null) alpha[i] = Rgba[i * 4 + 3];
        }
        return PngEncoder.Encode(Width, Height, rgb, alpha);
    }
}

/// <summary>
/// Renders PDF pages to images: vector graphics, clipping, text, images, shadings, patterns, transparency and
/// annotation appearances.
/// </summary>
public static class PdfRenderer
{
    private const long MaxPixels = 200_000_000;

    /// <summary>Renders a page (its crop box, rotated as viewers show it) at <see cref="PdfRenderOptions.Dpi"/>.</summary>
    public static RenderedPage Render(PdfPage page, PdfRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        options ??= new PdfRenderOptions();
        if (!(options.Dpi > 0) || options.Dpi > 10_000) throw new ArgumentOutOfRangeException(nameof(options), "Dpi must be between 0 and 10000.");
        var crop = page.CropBox;
        double userUnit = page.Dictionary.GetDouble(PdfName.Get("UserUnit"), 1);
        double scale = options.Dpi / 72 * (userUnit > 0 ? userUnit : 1);
        int rotation = ((page.Rotation % 360) + 360) % 360;

        // page space -> rotated (clockwise, as displayed) -> pixels (y down)
        var matrix = Matrix.Rotate(-rotation);
        var corners = new[]
        {
            matrix.Transform(crop.Left, crop.Bottom), matrix.Transform(crop.Right, crop.Bottom),
            matrix.Transform(crop.Left, crop.Top), matrix.Transform(crop.Right, crop.Top),
        };
        double minX = corners.Min(c => c.X), minY = corners.Min(c => c.Y), maxX = corners.Max(c => c.X), maxY = corners.Max(c => c.Y);
        int width = Math.Max(1, (int)Math.Round((maxX - minX) * scale));
        int height = Math.Max(1, (int)Math.Round((maxY - minY) * scale));
        if ((long)width * height > MaxPixels) throw new PdfException($"The rendered page would have {width}x{height} pixels; use a lower Dpi.");
        matrix = matrix.Multiply(Matrix.Translate(-minX, -maxY)).Multiply(Matrix.Scale(scale, -scale));

        (byte, byte, byte)? background = options.Background is { } bg ? ToBytes(ImageDecoder.ToRgb(bg)) : null;
        // the page is rendered on a transparent backdrop (the page group) and then put on the paper: blend modes see
        // no backdrop where nothing was painted, as in Acrobat
        using var diagnostics = DiagnosticContext.Enter(page);
        var surface = new Surface(width, height, null);
        var renderer = new ContentRenderer(surface, page.Document);
        page.Document.PrepareFontsForReading();
        renderer.Render(page.GetContentBytes(), page.GetInheritable(PdfName.Resources) as PdfDictionary, matrix, null);
        if (options.RenderAnnotations) RenderAnnotations(page, renderer, matrix, options);
        if (background is { } paper) Flatten(surface.Pixels, paper);
        return new RenderedPage(width, height, surface.Pixels, background != null);
    }

    /// <summary>Composites the pixels over an opaque color (in place).</summary>
    private static void Flatten(byte[] px, (byte R, byte G, byte B) paper)
    {
        for (long i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a != 255)
            {
                px[i] = (byte)((px[i] * a + paper.R * (255 - a) + 127) / 255);
                px[i + 1] = (byte)((px[i + 1] * a + paper.G * (255 - a) + 127) / 255);
                px[i + 2] = (byte)((px[i + 2] * a + paper.B * (255 - a) + 127) / 255);
            }
            px[i + 3] = 255;
        }
    }

    private static (byte, byte, byte) ToBytes((int R, int G, int B) c) => ((byte)c.R, (byte)c.G, (byte)c.B);

    /// <summary>Renders a page and encodes it as PNG.</summary>
    public static byte[] RenderToPng(PdfPage page, PdfRenderOptions? options = null) => Render(page, options).ToPng();

    private static void RenderAnnotations(PdfPage page, ContentRenderer renderer, Matrix pageMatrix, PdfRenderOptions options)
    {
        foreach (var annotation in page.Annotations)
        {
            var dict = annotation.Dictionary;
            var flags = annotation.Flags;
            if (annotation.Subtype == "Popup" || flags.HasFlag(AnnotationFlags.Hidden)) continue;
            if (options.ForPrinting ? !flags.HasFlag(AnnotationFlags.Print) : flags.HasFlag(AnnotationFlags.NoView)) continue;
            if (page.Document.IsHiddenContent(dict[PdfName.Get("OC")])) continue;
            var normal = dict.GetDictionary(PdfName.AP)?.Get(PdfName.N);
            if (normal is PdfDictionary states and not PdfStream)
                normal = dict.GetName(PdfName.Get("AS")) is { } state ? states.Get(state) : null;
            if (normal is not PdfStream appearance) continue;
            var rect = annotation.Rect;
            var bbox = appearance.GetRectangle(PdfName.BBox);
            if (bbox == null || rect.Width <= 0 || rect.Height <= 0) continue;
            // ISO 32000-2, 12.5.5: the transformed bounding box is mapped onto the annotation rectangle
            var formMatrix = appearance.GetArray(PdfName.Get("Matrix")) is { Count: 6 } m
                ? new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5))
                : Matrix.Identity;
            var box = new[] { formMatrix.Transform(bbox.Value.Left, bbox.Value.Bottom), formMatrix.Transform(bbox.Value.Right, bbox.Value.Bottom),
                formMatrix.Transform(bbox.Value.Left, bbox.Value.Top), formMatrix.Transform(bbox.Value.Right, bbox.Value.Top) };
            double bx0 = box.Min(p => p.X), by0 = box.Min(p => p.Y), bx1 = box.Max(p => p.X), by1 = box.Max(p => p.Y);
            if (bx1 - bx0 <= 0 || by1 - by0 <= 0) continue;
            var a = Matrix.Translate(-bx0, -by0).Multiply(Matrix.Scale(rect.Width / (bx1 - bx0), rect.Height / (by1 - by0)))
                .Multiply(Matrix.Translate(rect.Left, rect.Bottom));
            renderer.RenderForm(appearance, page.GetInheritable(PdfName.Resources) as PdfDictionary, a.Multiply(pageMatrix));
        }
    }
}
