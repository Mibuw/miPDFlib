using System.Globalization;

namespace MiPdf.Graphics;

/// <summary>Device color space of a <see cref="PdfColor"/>.</summary>
public enum PdfColorSpace
{
    /// <summary>DeviceGray: one component (0 = black, 1 = white).</summary>
    Gray,
    /// <summary>DeviceRGB: three components (red, green, blue).</summary>
    Rgb,
    /// <summary>DeviceCMYK: four components (cyan, magenta, yellow, black).</summary>
    Cmyk,
}

/// <summary>
/// A device color (gray, RGB or CMYK). Components are stored in the range 0..1. <see cref="Gray"/>, <see cref="Rgb"/>
/// and <see cref="Cmyk"/> take components in that range; the <c>From...</c> methods convert other notations:
/// <see cref="FromRgb"/> 8-bit values (0..255) and <see cref="FromHex"/> hex strings such as "#1F4E79".
/// The named colors follow the CSS color names.
/// </summary>
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

    /// <summary>The device color space the components refer to.</summary>
    public PdfColorSpace ColorSpace { get; }

    /// <summary>First component: gray level, red or cyan, depending on <see cref="ColorSpace"/>.</summary>
    public double C1 { get; }
    /// <summary>Second component: green or magenta (0 for gray).</summary>
    public double C2 { get; }
    /// <summary>Third component: blue or yellow (0 for gray).</summary>
    public double C3 { get; }
    /// <summary>Fourth component: black for CMYK (0 otherwise).</summary>
    public double C4 { get; }

    /// <summary>Creates a gray color (0 = black, 1 = white); the value is clamped to 0..1.</summary>
    public static PdfColor Gray(double gray) => new(PdfColorSpace.Gray, gray);

    /// <summary>Creates an RGB color from components in the range 0..1 (clamped).</summary>
    public static PdfColor Rgb(double r, double g, double b) => new(PdfColorSpace.Rgb, r, g, b);

    /// <summary>Creates an RGB color from 8-bit components (0..255).</summary>
    public static PdfColor FromRgb(byte r, byte g, byte b) => new(PdfColorSpace.Rgb, r / 255.0, g / 255.0, b / 255.0);

    /// <summary>Creates a CMYK color from components in the range 0..1 (clamped).</summary>
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

    /// <summary>Black (gray 0).</summary>
    public static readonly PdfColor Black = Gray(0);
    /// <summary>White (gray 1).</summary>
    public static readonly PdfColor White = Gray(1);
    /// <summary>Light gray (gray 0.83).</summary>
    public static readonly PdfColor LightGray = Gray(0.83);
    /// <summary>Dark gray (gray 0.4).</summary>
    public static readonly PdfColor DarkGray = Gray(0.4);
    /// <summary>Red (RGB 1, 0, 0).</summary>
    public static readonly PdfColor Red = Rgb(1, 0, 0);
    /// <summary>Green as in CSS (RGB 0, 0.5, 0); for full green use <see cref="Lime"/>.</summary>
    public static readonly PdfColor Green = Rgb(0, 0.5, 0);
    /// <summary>Full green (RGB 0, 1, 0), named lime as in CSS.</summary>
    public static readonly PdfColor Lime = Rgb(0, 1, 0);
    /// <summary>Blue (RGB 0, 0, 1).</summary>
    public static readonly PdfColor Blue = Rgb(0, 0, 1);
    /// <summary>Yellow (RGB 1, 1, 0).</summary>
    public static readonly PdfColor Yellow = Rgb(1, 1, 0);
    /// <summary>Orange (RGB 1, 0.65, 0).</summary>
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
