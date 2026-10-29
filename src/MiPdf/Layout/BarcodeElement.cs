using MiPdf.Barcodes;
using MiPdf.Graphics;

namespace MiPdf.Layout;

/// <summary>A barcode in the layout flow (also usable in table cells).</summary>
public sealed class BarcodeElement : LayoutElement
{
    /// <param name="barcode">The barcode.</param>
    /// <param name="width">Width in points including the quiet zone (default: 2D codes 72 pt, linear codes 1 pt per module).</param>
    /// <param name="height">Height in points (default: 2D codes square, linear codes 40 pt).</param>
    public BarcodeElement(Barcode barcode, double? width = null, double? height = null)
    {
        Barcode = barcode;
        Width = width;
        Height = height;
    }

    public Barcode Barcode { get; }

    public double? Width { get; set; }

    public double? Height { get; set; }

    public TextAlignment Alignment { get; set; } = TextAlignment.Left;

    /// <summary>Alternative text for accessibility (tagged PDF / PDF/UA); defaults to the encoded text.</summary>
    public string? AlternateText { get; set; }

    internal override LayoutBox Prepare(double available)
    {
        bool matrix = Barcode.TotalHeightModules > 0;
        double w = Width ?? (matrix ? 72 : Barcode.TotalWidthModules);
        double h = Height ?? (matrix ? w * Barcode.TotalHeightModules / Barcode.TotalWidthModules : 40);
        if (w > available)
        {
            if (matrix) h *= available / w;
            w = available;
        }
        return new Box(this, w, h, available);
    }

    private sealed class Box(BarcodeElement e, double w, double h, double available) : LayoutBox
    {
        public override double Height => e.SpacingBefore + h + e.SpacingAfter;

        public override LayoutBox? Render(PdfCanvas canvas, double x, double top, double availableHeight, bool force, out double usedHeight)
        {
            if (Height > availableHeight && !force)
            {
                usedHeight = 0;
                return this;
            }
            double dx = e.Alignment switch
            {
                TextAlignment.Center => (available - w) / 2,
                TextAlignment.Right => available - w,
                _ => 0,
            };
            double bx = x + dx, by = top - e.SpacingBefore - h;
            ImageElement.DrawFigure(canvas, e, e.AlternateText ?? e.Barcode.Text, false, bx, by, w, h, () => e.Barcode.Draw(canvas, bx, by, w, h));
            usedHeight = Math.Min(Height, Math.Max(availableHeight, e.SpacingBefore + h));
            return null;
        }
    }
}
