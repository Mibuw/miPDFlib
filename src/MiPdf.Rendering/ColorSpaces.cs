using MiPdf.Objects;
using MiPdf.Rendering.Raster;

namespace MiPdf.Rendering;

/// <summary>
/// Color spaces (ISO 32000-2, 8.6) converted to RGB: device and CIE-based spaces by their number of components,
/// Indexed through its lookup table. Separation and DeviceN use an approximation until PDF functions are evaluated
/// (the tint as gray). Patterns are not painted yet.
/// </summary>
internal abstract class ColorSpace
{
    public abstract int Components { get; }

    public virtual double[] Initial => new double[Components];

    public abstract Rgb ToRgb(ReadOnlySpan<double> c);

    /// <summary>True for pattern color spaces (not painted yet).</summary>
    public virtual bool IsPattern => false;

    public static readonly ColorSpace Gray = new DeviceGray();
    public static readonly ColorSpace Rgb = new DeviceRgb();
    public static readonly ColorSpace Cmyk = new DeviceCmyk();

    /// <summary>Resolves a color space operand or entry (names are looked up in the /ColorSpace resources).</summary>
    public static ColorSpace Resolve(PdfObject? value, PdfDictionary? resources, int depth = 0)
    {
        value = value?.Resolve();
        if (depth > 8 || value == null) return Gray;
        if (value is PdfName name)
        {
            switch (name.Value)
            {
                case "DeviceGray" or "G" or "CalGray": return Gray;
                case "DeviceRGB" or "RGB" or "CalRGB": return Rgb;
                case "DeviceCMYK" or "CMYK": return Cmyk;
                case "Pattern": return new PatternSpace();
            }
            var named = resources?.GetDictionary(PdfName.ColorSpace)?.Get(name);
            return named != null ? Resolve(named, resources, depth + 1) : Gray;
        }
        if (value is not PdfArray { Count: > 0 } array || array[0].Resolve() is not PdfName family) return Gray;
        switch (family.Value)
        {
            case "CalGray": return Gray;
            case "CalRGB": return Rgb;
            case "Lab": return new Lab();
            case "ICCBased":
                int n = (array.Count > 1 ? (array[1].Resolve() as PdfStream)?.GetInt(PdfName.N) : 0) ?? 0;
                return n switch
                {
                    1 => Gray,
                    4 => Cmyk,
                    3 => Rgb,
                    _ => array.Count > 1 && (array[1].Resolve() as PdfStream)?.Get(PdfName.Get("Alternate")) is { } alt ? Resolve(alt, resources, depth + 1) : Rgb,
                };
            case "Indexed" or "I":
                if (array.Count < 4) return Gray;
                var baseSpace = Resolve(array[1], resources, depth + 1);
                int hival = (array[2].Resolve() as PdfNumber)?.IntValue ?? 0;
                byte[] lookup = array[3].Resolve() switch
                {
                    PdfString s => s.Bytes,
                    PdfStream st => st.GetBytes(),
                    _ => Array.Empty<byte>(),
                };
                return new Indexed(baseSpace, hival, lookup);
            case "Separation":
                return new Separation(1);
            case "DeviceN":
                int count = array.Count > 1 && array[1].Resolve() is PdfArray names ? names.Count : 1;
                return new Separation(Math.Max(1, count));
            case "Pattern":
                return new PatternSpace();
            default:
                return Gray;
        }
    }

    private sealed class DeviceGray : ColorSpace
    {
        public override int Components => 1;

        public override Rgb ToRgb(ReadOnlySpan<double> c) => Raster.Rgb.FromUnit(c[0], c[0], c[0]);
    }

    private sealed class DeviceRgb : ColorSpace
    {
        public override int Components => 3;

        public override Rgb ToRgb(ReadOnlySpan<double> c) => Raster.Rgb.FromUnit(c[0], c[1], c[2]);
    }

    private sealed class DeviceCmyk : ColorSpace
    {
        public override int Components => 4;

        public override double[] Initial => new double[] { 0, 0, 0, 1 };

        public override Rgb ToRgb(ReadOnlySpan<double> c) =>
            Raster.Rgb.FromUnit((1 - c[0]) * (1 - c[3]), (1 - c[1]) * (1 - c[3]), (1 - c[2]) * (1 - c[3]));
    }

    private sealed class Lab : ColorSpace
    {
        public override int Components => 3;

        public override double[] Initial => new double[] { 0, 0, 0 };

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            // CIE L*a*b* (D50 white point assumed) to sRGB
            double fy = (c[0] + 16) / 116, fx = fy + c[1] / 500, fz = fy - c[2] / 200;
            static double F(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
            double x = 0.9642 * F(fx), y = F(fy), z = 0.8249 * F(fz);
            double r = 3.1339 * x - 1.6169 * y - 0.4906 * z, g = -0.9788 * x + 1.9161 * y + 0.0335 * z, b = 0.0719 * x - 0.2290 * y + 1.4052 * z;
            static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4) - 0.055;
            return Raster.Rgb.FromUnit(Gamma(r), Gamma(g), Gamma(b));
        }
    }

    private sealed class Indexed(ColorSpace baseSpace, int hival, byte[] lookup) : ColorSpace
    {
        public override int Components => 1;

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            int index = Math.Clamp((int)Math.Round(c[0]), 0, Math.Max(0, hival));
            int n = baseSpace.Components;
            Span<double> values = stackalloc double[n];
            for (int i = 0; i < n; i++)
            {
                int offset = index * n + i;
                values[i] = offset < lookup.Length ? lookup[offset] / 255.0 : 0;
            }
            return baseSpace.ToRgb(values);
        }
    }

    /// <summary>Separation and DeviceN until tint transforms are evaluated: the strongest tint as gray.</summary>
    private sealed class Separation(int components) : ColorSpace
    {
        public override int Components => components;

        public override double[] Initial => Enumerable.Repeat(1.0, components).ToArray();

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            double tint = 0;
            foreach (double v in c) tint = Math.Max(tint, v);
            return Raster.Rgb.FromUnit(1 - tint, 1 - tint, 1 - tint);
        }
    }

    private sealed class PatternSpace : ColorSpace
    {
        public override int Components => 1;

        public override bool IsPattern => true;

        public override Rgb ToRgb(ReadOnlySpan<double> c) => Raster.Rgb.Black;
    }
}
