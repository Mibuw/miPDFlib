using MiPdf.PostScript.Interpreter;

namespace MiPdf.PostScript.Graphics;

internal enum PsColorFamily : byte
{
    DeviceGray,
    DeviceRGB,
    DeviceCMYK,
    CIEBasedA,
    CIEBasedABC,
    CIEBasedDEF,
    CIEBasedDEFG,
    Indexed,
    Separation,
    DeviceN,
    Pattern,
}

/// <summary>
/// A device color: the components of DeviceGray, DeviceRGB or DeviceCMYK, written natively to PDF. A pattern color has
/// a <see cref="Pattern"/>: the family Pattern for a colored pattern, for an uncolored one the family and components
/// of the color the pattern is painted with.
/// </summary>
internal readonly record struct PsDeviceColor(PsColorFamily Family, double C0, double C1, double C2, double C3)
{
    public static readonly PsDeviceColor Black = new(PsColorFamily.DeviceGray, 0, 0, 0, 0);

    /// <summary>The pattern painted (null for a plain color).</summary>
    public PsPatternPaint? Pattern { get; init; }

    public static PsDeviceColor Gray(double g) => new(PsColorFamily.DeviceGray, Clamp(g), 0, 0, 0);
    public static PsDeviceColor Rgb(double r, double g, double b) => new(PsColorFamily.DeviceRGB, Clamp(r), Clamp(g), Clamp(b), 0);
    public static PsDeviceColor Cmyk(double c, double m, double y, double k) => new(PsColorFamily.DeviceCMYK, Clamp(c), Clamp(m), Clamp(y), Clamp(k));

    private static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

    /// <summary>The color as RGB (PLRM 7.2 conversions).</summary>
    public (double R, double G, double B) ToRgb() => Family switch
    {
        PsColorFamily.DeviceGray => (C0, C0, C0),
        PsColorFamily.DeviceRGB => (C0, C1, C2),
        _ => (1 - Math.Min(1, C0 + C3), 1 - Math.Min(1, C1 + C3), 1 - Math.Min(1, C2 + C3)),
    };

    public double ToGray() => Family switch
    {
        PsColorFamily.DeviceGray => C0,
        PsColorFamily.DeviceRGB => 0.3 * C0 + 0.59 * C1 + 0.11 * C2,
        _ => 1 - Math.Min(1, 0.3 * C0 + 0.59 * C1 + 0.11 * C2 + C3),
    };

    /// <summary>The color as CMYK with the default black generation and undercolor removal (k = min, full removal).</summary>
    public (double C, double M, double Y, double K) ToCmyk()
    {
        if (Family == PsColorFamily.DeviceCMYK) return (C0, C1, C2, C3);
        var (r, g, b) = ToRgb();
        double c = 1 - r, m = 1 - g, y = 1 - b, k = Math.Min(c, Math.Min(m, y));
        return (c - k, m - k, y - k, k);
    }
}

/// <summary>
/// A color space (PLRM 4.8): the family, the number of components and what is needed to reduce a color to a device
/// color: the base space and lookup table of Indexed, the alternate space and tint transform of Separation and
/// DeviceN, the dictionary of the CIE-based spaces.
/// </summary>
internal sealed class PsColorSpace
{
    public PsColorFamily Family { get; init; }
    public int Components { get; init; }
    public PsObject Definition { get; init; } = PsObject.Null;
    public PsColorSpace? Base { get; init; }
    public PsObject Lookup { get; init; } = PsObject.Null;
    public int HighValue { get; init; }
    public PsObject TintTransform { get; init; } = PsObject.Null;
    public PsDict? Cie { get; init; }

    public static readonly PsColorSpace Gray = new() { Family = PsColorFamily.DeviceGray, Components = 1 };
    public static readonly PsColorSpace Rgb = new() { Family = PsColorFamily.DeviceRGB, Components = 3 };
    public static readonly PsColorSpace Cmyk = new() { Family = PsColorFamily.DeviceCMYK, Components = 4 };

    /// <summary>The initial color of the space (PLRM 4.8: black, index 0, full tints, CIE components clipped to range).</summary>
    public double[] InitialColor() => Family switch
    {
        PsColorFamily.DeviceCMYK => new double[] { 0, 0, 0, 1 },
        PsColorFamily.Separation or PsColorFamily.DeviceN => Enumerable.Repeat(1.0, Components).ToArray(),
        _ => new double[Components],
    };
}
