using MiPdf.Graphics;

namespace MiPdf.Layout;

/// <summary>Spacing around a box (points).</summary>
public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Creates a uniform thickness on all four sides.</summary>
    public Thickness(double all) : this(all, all, all, all) { }

    /// <summary>Creates a thickness with <paramref name="horizontal"/> for left and right and <paramref name="vertical"/> for top and bottom.</summary>
    public Thickness(double horizontal, double vertical) : this(horizontal, vertical, horizontal, vertical) { }

    /// <summary>Sum of <see cref="Left"/> and <see cref="Right"/>.</summary>
    public double Horizontal => Left + Right;

    /// <summary>Sum of <see cref="Top"/> and <see cref="Bottom"/>.</summary>
    public double Vertical => Top + Bottom;

    /// <summary>Converts a number to a uniform thickness on all four sides.</summary>
    public static implicit operator Thickness(double all) => new(all);
}

/// <summary>Base class of elements that can be added to a <see cref="LayoutDocument"/> or table cells.</summary>
public abstract class LayoutElement
{
    /// <summary>Space above the element in points.</summary>
    public double SpacingBefore { get; set; }

    /// <summary>Space below the element in points.</summary>
    public double SpacingAfter { get; set; }

    /// <summary>
    /// Structure type used when the document is tagged (e.g. "H1" for a heading, see <see cref="Tagging.StructureType"/>).
    /// Null uses the element's default ("P" for paragraphs, "Table", "Figure").
    /// </summary>
    public string? StructureType { get; set; }

    /// <summary>Measures the element for the given width. The result can be rendered and split across pages.</summary>
    internal abstract LayoutBox Prepare(double width);
}

/// <summary>A measured element.</summary>
internal abstract class LayoutBox
{
    /// <summary>Total height including spacing.</summary>
    public abstract double Height { get; }

    /// <summary>Distance from the top edge to the baseline of the first text line (null if the box has no text).</summary>
    public virtual double? FirstBaseline => null;

    /// <summary>
    /// Renders as much as fits into <paramref name="availableHeight"/> starting at the top edge <paramref name="top"/>.
    /// Returns the part that did not fit (or null). If <paramref name="force"/> is set, at least one unit
    /// (line, row, ...) must be rendered even if it does not fit (to guarantee progress on an empty page).
    /// </summary>
    public abstract LayoutBox? Render(PdfCanvas canvas, double x, double top, double availableHeight, bool force, out double usedHeight);

    /// <summary>Renders the whole box (used inside table cells).</summary>
    public void RenderAll(PdfCanvas canvas, double x, double top) => Render(canvas, x, top, double.MaxValue, true, out _);
}

/// <summary>Vertical empty space.</summary>
public sealed class Spacer : LayoutElement
{
    /// <summary>Creates a spacer of <paramref name="height"/> points.</summary>
    public Spacer(double height) => Height = height;

    /// <summary>The vertical space in points (added to <see cref="LayoutElement.SpacingBefore"/> and
    /// <see cref="LayoutElement.SpacingAfter"/>, which are 0 by default).</summary>
    public double Height { get; set; }

    internal override LayoutBox Prepare(double width) => new Box(SpacingBefore + Height + SpacingAfter);

    private sealed class Box(double height) : LayoutBox
    {
        public override double Height => height;

        public override LayoutBox? Render(PdfCanvas canvas, double x, double top, double availableHeight, bool force, out double usedHeight)
        {
            usedHeight = Math.Min(height, availableHeight);
            return null;
        }
    }
}

/// <summary>Forces the following content onto a new page.</summary>
public sealed class PageBreak : LayoutElement
{
    internal override LayoutBox Prepare(double width) => throw new InvalidOperationException("Page breaks are handled by the layout document.");
}

/// <summary>A horizontal rule.</summary>
public sealed class HorizontalLine : LayoutElement
{
    /// <summary>Creates a rule across the available width with 4 points spacing above and below.</summary>
    /// <param name="lineWidth">Line width in points.</param>
    /// <param name="color">Line color (default black).</param>
    public HorizontalLine(double lineWidth = 0.5, PdfColor? color = null)
    {
        LineWidth = lineWidth;
        Color = color ?? PdfColor.Black;
        SpacingBefore = SpacingAfter = 4;
    }

    /// <summary>Line width in points.</summary>
    public double LineWidth { get; set; }

    /// <summary>Line color.</summary>
    public PdfColor Color { get; set; }

    internal override LayoutBox Prepare(double width) => new Box(this, width);

    private sealed class Box(HorizontalLine line, double width) : LayoutBox
    {
        public override double Height => line.SpacingBefore + line.LineWidth + line.SpacingAfter;

        public override LayoutBox? Render(PdfCanvas canvas, double x, double top, double availableHeight, bool force, out double usedHeight)
        {
            double y = top - line.SpacingBefore - line.LineWidth / 2;
            if (canvas.IsTagged) canvas.BeginArtifact(Tagging.ArtifactType.Layout);
            canvas.DrawLine(x, y, x + width, y, line.Color, line.LineWidth);
            if (canvas.IsTagged) canvas.EndArtifact();
            usedHeight = Height;
            return null;
        }
    }
}
