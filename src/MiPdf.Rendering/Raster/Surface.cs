using MiPdf.Graphics;
namespace MiPdf.Rendering.Raster;

/// <summary>Blend modes (ISO 32000-2, 11.3.5).</summary>
internal enum BlendMode
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight, Difference, Exclusion,
    Hue, Saturation, Color, Luminosity,
}

/// <summary>
/// An RGBA pixel buffer (8 bits per channel, not premultiplied, rows from the top) with compositing in all blend
/// modes (ISO 32000-2, 11.3).
/// </summary>
internal sealed class Surface
{
    public Surface(int width, int height, (byte R, byte G, byte B)? background)
    {
        Width = width;
        Height = height;
        Pixels = new byte[(long)width * height * 4];
        if (background is { } bg)
            for (long i = 0; i < Pixels.Length; i += 4)
            {
                Pixels[i] = bg.R;
                Pixels[i + 1] = bg.G;
                Pixels[i + 2] = bg.B;
                Pixels[i + 3] = 255;
            }
    }

    /// <summary>A copy of another surface (the start of a non-isolated knockout group).</summary>
    public Surface(Surface source)
    {
        Width = source.Width;
        Height = source.Height;
        Pixels = (byte[])source.Pixels.Clone();
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>
    /// True for the surface of a knockout group (ISO 32000-2, 11.4.6.2): each object is composited with the group's
    /// initial backdrop (<see cref="Backdrop"/>, transparent if null) and replaces what is below it by its shape.
    /// </summary>
    public bool Knockout { get; init; }

    public byte[]? Backdrop { get; init; }

    /// <summary>The group shape (union of the painted shapes) of a transparency group surface, or null.</summary>
    public byte[]? Shape { get; init; }

    /// <summary>Paints one pixel with a shape (coverage and clip) and an opacity (alpha constant, soft mask, image alpha).</summary>
    public void Paint(long index, byte r, byte g, byte b, double shape, double opacity, BlendMode mode)
    {
        if (Shape != null && shape > 0)
        {
            double s = Shape[index] / 255.0;
            Shape[index] = ToByte(s + shape - s * shape);
        }
        if (!Knockout)
        {
            Blend(index, r, g, b, shape * opacity, mode);
            return;
        }
        if (shape <= 0) return;
        long i = index * 4;
        // the object over the initial backdrop ...
        double ba = Backdrop != null ? Backdrop[i + 3] / 255.0 : 0;
        double br = Backdrop != null ? Backdrop[i] : 0, bg = Backdrop != null ? Backdrop[i + 1] : 0, bb = Backdrop != null ? Backdrop[i + 2] : 0;
        var (nr, ng, nb, na) = Composite(br, bg, bb, ba, r, g, b, opacity, mode);
        // ... replaces the current content by the shape (interpolated with premultiplied colors)
        double ca = Pixels[i + 3] / 255.0;
        double outAlpha = ca * (1 - shape) + na * shape;
        if (outAlpha <= 0)
        {
            Pixels[i + 3] = 0;
            return;
        }
        Pixels[i] = ToByte((Pixels[i] * ca * (1 - shape) + nr * na * shape) / outAlpha / 255);
        Pixels[i + 1] = ToByte((Pixels[i + 1] * ca * (1 - shape) + ng * na * shape) / outAlpha / 255);
        Pixels[i + 2] = ToByte((Pixels[i + 2] * ca * (1 - shape) + nb * na * shape) / outAlpha / 255);
        Pixels[i + 3] = ToByte(outAlpha);
    }

    /// <summary>Source (alpha 0..1) composited over a backdrop color (0..255, alpha 0..1): color 0..255 and alpha.</summary>
    private static (double R, double G, double B, double A) Composite(double br, double bg, double bb, double ba, byte r, byte g, byte b, double alpha, BlendMode mode)
    {
        double sr = r, sg = g, sb = b;
        if (mode != BlendMode.Normal && ba > 0)
        {
            var (xr, xg, xb) = BlendColor(mode, br / 255, bg / 255, bb / 255, r / 255.0, g / 255.0, b / 255.0);
            sr = ((1 - ba) * r / 255.0 + ba * Math.Clamp(xr, 0, 1)) * 255;
            sg = ((1 - ba) * g / 255.0 + ba * Math.Clamp(xg, 0, 1)) * 255;
            sb = ((1 - ba) * b / 255.0 + ba * Math.Clamp(xb, 0, 1)) * 255;
        }
        double outAlpha = alpha + ba * (1 - alpha);
        if (outAlpha <= 0) return (0, 0, 0, 0);
        double dw = ba * (1 - alpha) / outAlpha, sw = alpha / outAlpha;
        return (sr * sw + br * dw, sg * sw + bg * dw, sb * sw + bb * dw, outAlpha);
    }

    /// <summary>Composites one pixel (<paramref name="alpha"/> 0..1, the source shape and opacity) in a blend mode.</summary>
    public void Blend(long index, byte r, byte g, byte b, double alpha, BlendMode mode = BlendMode.Normal)
    {
        if (alpha <= 0) return;
        long i = index * 4;
        double da = Pixels[i + 3] / 255.0;
        if (mode != BlendMode.Normal && da > 0)
        {
            // Cs' = (1 - ab) Cs + ab B(Cb, Cs)
            var (br, bg, bb) = BlendColor(mode, Pixels[i] / 255.0, Pixels[i + 1] / 255.0, Pixels[i + 2] / 255.0, r / 255.0, g / 255.0, b / 255.0);
            r = ToByte((1 - da) * r / 255.0 + da * br);
            g = ToByte((1 - da) * g / 255.0 + da * bg);
            b = ToByte((1 - da) * b / 255.0 + da * bb);
        }
        if (alpha >= 1)
        {
            Pixels[i] = r;
            Pixels[i + 1] = g;
            Pixels[i + 2] = b;
            Pixels[i + 3] = 255;
            return;
        }
        double outAlpha = alpha + da * (1 - alpha);
        if (outAlpha <= 0) return;
        double dw = da * (1 - alpha) / outAlpha, sw = alpha / outAlpha;
        Pixels[i] = (byte)Math.Round(r * sw + Pixels[i] * dw);
        Pixels[i + 1] = (byte)Math.Round(g * sw + Pixels[i + 1] * dw);
        Pixels[i + 2] = (byte)Math.Round(b * sw + Pixels[i + 2] * dw);
        Pixels[i + 3] = (byte)Math.Round(outAlpha * 255);
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    /// <summary>The blend function B(Cb, Cs) for one pixel (components 0..1).</summary>
    private static (double R, double G, double B) BlendColor(BlendMode mode, double br, double bg, double bb, double sr, double sg, double sb)
    {
        switch (mode)
        {
            case BlendMode.Hue:
                return SetLum(SetSat(sr, sg, sb, Sat(br, bg, bb)), Lum(br, bg, bb));
            case BlendMode.Saturation:
                return SetLum(SetSat(br, bg, bb, Sat(sr, sg, sb)), Lum(br, bg, bb));
            case BlendMode.Color:
                return SetLum((sr, sg, sb), Lum(br, bg, bb));
            case BlendMode.Luminosity:
                return SetLum((br, bg, bb), Lum(sr, sg, sb));
            default:
                return (Separable(mode, br, sr), Separable(mode, bg, sg), Separable(mode, bb, sb));
        }
    }

    private static double Separable(BlendMode mode, double cb, double cs) => mode switch
    {
        BlendMode.Multiply => cb * cs,
        BlendMode.Screen => cb + cs - cb * cs,
        BlendMode.Overlay => HardLight(cs, cb),
        BlendMode.Darken => Math.Min(cb, cs),
        BlendMode.Lighten => Math.Max(cb, cs),
        BlendMode.ColorDodge => cb == 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs)),
        BlendMode.ColorBurn => cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs),
        BlendMode.HardLight => HardLight(cb, cs),
        BlendMode.SoftLight => cs <= 0.5
            ? cb - (1 - 2 * cs) * cb * (1 - cb)
            : cb + (2 * cs - 1) * ((cb <= 0.25 ? ((16 * cb - 12) * cb + 4) * cb : Math.Sqrt(cb)) - cb),
        BlendMode.Difference => Math.Abs(cb - cs),
        BlendMode.Exclusion => cb + cs - 2 * cb * cs,
        _ => cs,
    };

    private static double HardLight(double cb, double cs) => cs <= 0.5 ? cb * 2 * cs : cb + (2 * cs - 1) - cb * (2 * cs - 1);

    private static double Lum(double r, double g, double b) => 0.3 * r + 0.59 * g + 0.11 * b;

    private static double Sat(double r, double g, double b) => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

    private static (double, double, double) SetLum((double R, double G, double B) c, double l)
    {
        double d = l - Lum(c.R, c.G, c.B);
        double r = c.R + d, g = c.G + d, b = c.B + d;
        double lum = Lum(r, g, b), min = Math.Min(r, Math.Min(g, b)), max = Math.Max(r, Math.Max(g, b));
        if (min < 0 && lum != min)
        {
            r = lum + (r - lum) * lum / (lum - min);
            g = lum + (g - lum) * lum / (lum - min);
            b = lum + (b - lum) * lum / (lum - min);
        }
        if (max > 1 && max != lum)
        {
            r = lum + (r - lum) * (1 - lum) / (max - lum);
            g = lum + (g - lum) * (1 - lum) / (max - lum);
            b = lum + (b - lum) * (1 - lum) / (max - lum);
        }
        return (r, g, b);
    }

    private static (double, double, double) SetSat(double r, double g, double b, double s)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max <= min) return (0, 0, 0);
        double Scale(double c) => c == max ? s : c == min ? 0 : (c - min) * s / (max - min);
        // the middle component scales, the largest becomes s, the smallest 0
        return (Scale(r), Scale(g), Scale(b));
    }

    public static BlendMode ParseBlendMode(string? name) => name switch
    {
        "Multiply" => BlendMode.Multiply,
        "Screen" => BlendMode.Screen,
        "Overlay" => BlendMode.Overlay,
        "Darken" => BlendMode.Darken,
        "Lighten" => BlendMode.Lighten,
        "ColorDodge" => BlendMode.ColorDodge,
        "ColorBurn" => BlendMode.ColorBurn,
        "HardLight" => BlendMode.HardLight,
        "SoftLight" => BlendMode.SoftLight,
        "Difference" => BlendMode.Difference,
        "Exclusion" => BlendMode.Exclusion,
        "Hue" => BlendMode.Hue,
        "Saturation" => BlendMode.Saturation,
        "Color" => BlendMode.Color,
        "Luminosity" => BlendMode.Luminosity,
        _ => BlendMode.Normal,
    };

    /// <summary>True for the blend mode names miPDFlib knows (unknown entries of a /BM array are skipped).</summary>
    public static bool IsKnownBlendMode(string name) => name is "Normal" or "Compatible" || ParseBlendMode(name) != BlendMode.Normal;
}
