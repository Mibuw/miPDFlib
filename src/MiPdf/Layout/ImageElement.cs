using MiPdf.Graphics;
using MiPdf.Images;

namespace MiPdf.Layout;

/// <summary>An image in the layout flow. Scaled to the available width if necessary, keeping its aspect ratio.</summary>
public sealed class ImageElement : LayoutElement
{
    public ImageElement(PdfImage image, double? width = null, double? height = null)
    {
        Image = image;
        Width = width;
        Height = height;
    }

    public PdfImage Image { get; }

    /// <summary>Display width in points; default: pixel size at 96 dpi.</summary>
    public double? Width { get; set; }

    public double? Height { get; set; }

    public TextAlignment Alignment { get; set; } = TextAlignment.Left;

    /// <summary>Alternative text describing the image (tagged PDF / PDF/UA requires it for figures).</summary>
    public string? AlternateText { get; set; }

    /// <summary>Purely decorative image: marked as artifact instead of a figure in tagged documents.</summary>
    public bool IsDecorative { get; set; }

    /// <summary>Tags drawing as a figure (or as artifact if decorative) in tagged documents.</summary>
    internal static void DrawFigure(PdfCanvas canvas, LayoutElement element, string? alternateText, bool decorative, double x, double y, double w, double h, Action draw)
    {
        if (canvas.StructureParent is { } parent && !decorative)
        {
            var figure = parent.AddChild(element.StructureType ?? Tagging.StructureType.Figure, alternateText);
            figure.SetAttribute("Layout", "BBox", Objects.PdfArray.FromNumbers(x, y, x + w, y + h));
            canvas.BeginTag(figure);
            draw();
            canvas.EndTag();
        }
        else if (canvas.IsTagged)
        {
            canvas.BeginArtifact(Tagging.ArtifactType.Layout);
            draw();
            canvas.EndArtifact();
        }
        else draw();
    }

    internal override LayoutBox Prepare(double available)
    {
        double w, h;
        if (Width.HasValue && Height.HasValue) (w, h) = (Width.Value, Height.Value);
        else if (Width.HasValue) (w, h) = (Width.Value, Width.Value / Image.AspectRatio);
        else if (Height.HasValue) (w, h) = (Height.Value * Image.AspectRatio, Height.Value);
        else (w, h) = (Image.Width * 0.75, Image.Height * 0.75);
        if (w > available)
        {
            h *= available / w;
            w = available;
        }
        return new Box(this, w, h, available);
    }

    private sealed class Box(ImageElement e, double w, double h, double available) : LayoutBox
    {
        public override double Height => e.SpacingBefore + h + e.SpacingAfter;

        public override LayoutBox? Render(PdfCanvas canvas, double x, double top, double availableHeight, bool force, out double usedHeight)
        {
            if (Height > availableHeight && !force)
            {
                usedHeight = 0;
                return this;
            }
            double dw = w, dh = h;
            if (force && e.SpacingBefore + dh > availableHeight && availableHeight > 0)
            {
                // larger than a whole page: scale down to fit
                double scale = Math.Max(0.01, (availableHeight - e.SpacingBefore) / dh);
                dw *= scale;
                dh *= scale;
            }
            double dx = e.Alignment switch
            {
                TextAlignment.Center => (available - dw) / 2,
                TextAlignment.Right => available - dw,
                _ => 0,
            };
            double ix = x + dx, iy = top - e.SpacingBefore - dh;
            DrawFigure(canvas, e, e.AlternateText, e.IsDecorative, ix, iy, dw, dh, () => canvas.DrawImage(e.Image, ix, iy, dw, dh));
            usedHeight = Math.Min(e.SpacingBefore + dh + e.SpacingAfter, Math.Max(availableHeight, e.SpacingBefore + dh));
            return null;
        }
    }
}
