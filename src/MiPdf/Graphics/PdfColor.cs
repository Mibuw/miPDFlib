using System.Globalization;

namespace MiPdf.Graphics;

/// <summary>Device color space of a <see cref="PdfColor"/>.</summary>
public enum PdfColorSpace
{
    Gray,
    Rgb,
    Cmyk,
}

/// <summary>A device color (gray, RGB or CMYK). Components are in the range 0..1.</summary>
public readonly record struct PdfColor
{
    private PdfColor(PdfColorSpace space, double c1, double c2 = 0, double c3 = 0, double c4 = 0)
    {
        ColorSpace = space;
        C1 = Math.Clamp(c1, 0, 1);
        C2 = Math.Clamp(c2, 0, 1);
        C3 = Math.Clamp(c3, 0, 1);
        C4 = Math.Clamp(c4, 0, 1);
    }

    public PdfColorSpace ColorSpace { get; }

    public double C1 { get; }
    public double C2 { get; }
    public double C3 { get; }
    public double C4 { get; }

    public static PdfColor Gray(double gray) => new(PdfColorSpace.Gray, gray);

    public static PdfColor Rgb(double r, double g, double b) => new(PdfColorSpace.Rgb, r, g, b);

    public static PdfColor FromRgb(byte r, byte g, byte b) => new(PdfColorSpace.Rgb, r / 255.0, g / 255.0, b / 255.0);

    public static PdfColor Cmyk(double c, double m, double y, double k) => new(PdfColorSpace.Cmyk, c, m, y, k);

    /// <summary>Parses "#RGB", "#RRGGBB" (the '#' is optional).</summary>
    public static PdfColor FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            throw new FormatException($"Invalid color '{hex}'.");
        return FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static readonly PdfColor Black = Gray(0);
    public static readonly PdfColor White = Gray(1);
    public static readonly PdfColor LightGray = Gray(0.83);
    public static readonly PdfColor DarkGray = Gray(0.4);
    public static readonly PdfColor Red = Rgb(1, 0, 0);
    public static readonly PdfColor Green = Rgb(0, 0.5, 0);
    public static readonly PdfColor Blue = Rgb(0, 0, 1);
    public static readonly PdfColor Yellow = Rgb(1, 1, 0);
    public static readonly PdfColor Orange = Rgb(1, 0.65, 0);

    internal double[] Components => ColorSpace switch
    {
        PdfColorSpace.Gray => new[] { C1 },
        PdfColorSpace.Rgb => new[] { C1, C2, C3 },
        _ => new[] { C1, C2, C3, C4 },
    };

    /// <summary>The operator to set this color: g/rg/k (fill) or G/RG/K (stroke).</summary>
    internal string GetOperator(bool stroke) => (ColorSpace, stroke) switch
    {
        (PdfColorSpace.Gray, false) => "g",
        (PdfColorSpace.Gray, true) => "G",
        (PdfColorSpace.Rgb, false) => "rg",
        (PdfColorSpace.Rgb, true) => "RG",
        (PdfColorSpace.Cmyk, false) => "k",
        _ => "K",
    };
}
