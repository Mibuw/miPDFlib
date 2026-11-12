using MiPdf.Objects;

namespace MiPdf.Graphics;

/// <summary>
/// Colors of content streams as the extraction APIs report them (<see cref="Text.TextLetter"/>,
/// <see cref="Content.PdfPath"/>): device colors (and ICC-based colors by their number of components) as they are,
/// other spaces converted to RGB, patterns null. Damaged color spaces and tint functions cost the color, not the
/// content.
/// </summary>
internal static class ContentColor
{
    public static PdfColor? ToColor(ColorSpace space, IReadOnlyList<PdfObject> operands)
    {
        try
        {
            return Convert(space, operands);
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex) || ex is PdfException)
        {
            return null;
        }
    }

    public static ColorSpace ResolveSpace(PdfObject operand, PdfDictionary? resources)
    {
        try
        {
            return ColorSpace.Resolve(operand, resources);
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex) || ex is PdfException)
        {
            return ColorSpace.Gray;
        }
    }

    /// <summary>The initial color of a color space (black for device spaces), as <see cref="ToColor"/> reports it.</summary>
    public static PdfColor? InitialColor(ColorSpace space) =>
        ToColor(space, space.Initial.Select(v => (PdfObject)new PdfNumber(v)).ToList());

    private static PdfColor? Convert(ColorSpace space, IReadOnlyList<PdfObject> operands)
    {
        if (space.IsPattern) return null;
        var c = new double[space.Components];
        for (int i = 0; i < c.Length && i < operands.Count; i++) c[i] = operands[i] is PdfNumber n ? n.Value : 0;
        if (ReferenceEquals(space, ColorSpace.Gray)) return PdfColor.Gray(Unit(c[0]));
        if (ReferenceEquals(space, ColorSpace.Rgb)) return PdfColor.Rgb(Unit(c[0]), Unit(c[1]), Unit(c[2]));
        if (ReferenceEquals(space, ColorSpace.Cmyk)) return PdfColor.Cmyk(Unit(c[0]), Unit(c[1]), Unit(c[2]), Unit(c[3]));
        var rgb = space.ToRgb(c);
        return PdfColor.FromRgb(rgb.R, rgb.G, rgb.B);

        // rounded to the precision of PDF numbers (0.3 is read as 0.30000000000000004)
        static double Unit(double v) => double.IsFinite(v) ? Math.Round(Math.Clamp(v, 0, 1), 5) : 0;
    }
}
