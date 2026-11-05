using MiPdf.Objects;

namespace MiPdf.Graphics;

/// <summary>An 8-bit RGB color.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);

    public static Rgb FromUnit(double r, double g, double b) => new(ToByte(r), ToByte(g), ToByte(b));

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1) * 255);
}

/// <summary>
/// Color spaces (ISO 32000-2, 8.6) converted to sRGB for display: gray, RGB, CMYK (approximating a SWOP press
/// profile), CIE L*a*b*, ICC-based spaces by their number of components, Indexed through its lookup table,
/// Separation and DeviceN through their tint transforms into the alternate space. Pattern spaces carry the
/// underlying space of uncolored tiling patterns.
/// </summary>
internal abstract class ColorSpace
{
    private const int MaxDepth = 8;

    public abstract int Components { get; }

    public virtual double[] Initial => new double[Components];

    public abstract Rgb ToRgb(ReadOnlySpan<double> c);

    /// <summary>True for pattern color spaces (the color is a pattern, see <see cref="Underlying"/>).</summary>
    public virtual bool IsPattern => false;

    /// <summary>The underlying color space of a pattern space (for uncolored tiling patterns), or null.</summary>
    public virtual ColorSpace? Underlying => null;

    /// <summary>True for Separation and DeviceN spaces of the colorant None: painting with them leaves no marks.</summary>
    public virtual bool IsNone => false;

    /// <summary>The default decode range of image samples (ISO 32000-2, 8.9.5.2), per component (min, max).</summary>
    public virtual double[] DefaultDecode(int bitsPerComponent)
    {
        var decode = new double[2 * Components];
        for (int i = 0; i < Components; i++) decode[2 * i + 1] = 1;
        return decode;
    }

    public static readonly ColorSpace Gray = new DeviceGray();
    public static readonly ColorSpace Rgb = new DeviceRgb();
    public static readonly ColorSpace Cmyk = new DeviceCmyk();

    /// <summary>Resolves a color space operand or entry (names are looked up in the /ColorSpace resources).</summary>
    public static ColorSpace Resolve(PdfObject? value, PdfDictionary? resources, int depth = 0)
    {
        value = value?.Resolve();
        if (depth > MaxDepth || value == null) return Gray;
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
            case "DeviceGray" or "CalGray" or "G": return Gray;
            case "DeviceRGB" or "CalRGB" or "RGB": return Rgb;
            case "DeviceCMYK" or "CMYK": return Cmyk;
            case "Lab":
                return new Lab(array.Count > 1 && array[1].Resolve() is PdfDictionary lab ? lab.GetArray(PdfName.Get("Range")) : null);
            case "ICCBased":
            {
                var profile = array.Count > 1 ? array[1].Resolve() as PdfStream : null;
                return (profile?.GetInt(PdfName.N) ?? 0) switch
                {
                    1 => Gray,
                    3 => Rgb,
                    4 => Cmyk,
                    _ => profile?.Get(PdfName.Get("Alternate")) is { } alt ? Resolve(alt, resources, depth + 1) : Rgb,
                };
            }
            case "Indexed" or "I":
            {
                if (array.Count < 4) return Gray;
                var baseSpace = Resolve(array[1], resources, depth + 1);
                int hival = Math.Clamp((array[2].Resolve() as PdfNumber)?.IntValue ?? 0, 0, 255);
                byte[] lookup = array[3].Resolve() switch
                {
                    PdfString s => s.Bytes,
                    PdfStream st => st.GetBytes(),
                    _ => Array.Empty<byte>(),
                };
                return new Indexed(baseSpace, hival, lookup);
            }
            case "Separation":
            case "DeviceN":
            {
                bool separation = family.Value == "Separation";
                var names = separation
                    ? new[] { (array.Count > 1 ? array[1].Resolve() as PdfName : null)?.Value }
                    : (array.Count > 1 ? array[1].Resolve() as PdfArray : null)?.Select(n => (n.Resolve() as PdfName)?.Value).ToArray() ?? new string?[] { null };
                int count = Math.Clamp(names.Length, 1, 32);
                if (names.All(n => n == "None")) return new NoneSpace(count);
                var alternate = array.Count > 2 ? Resolve(array[2], resources, depth + 1) : Gray;
                var tint = array.Count > 3 ? PdfFunction.Load(array[3]) : null;
                return tint != null && !alternate.IsPattern ? new TintSpace(count, alternate, tint) : new TintApproximation(count);
            }
            case "Pattern":
                return new PatternSpace(array.Count > 1 ? Resolve(array[1], resources, depth + 1) : null);
            default:
                return Gray;
        }
    }

    private sealed class DeviceGray : ColorSpace
    {
        public override int Components => 1;

        public override Rgb ToRgb(ReadOnlySpan<double> c) => Graphics.Rgb.FromUnit(c[0], c[0], c[0]);
    }

    private sealed class DeviceRgb : ColorSpace
    {
        public override int Components => 3;

        public override Rgb ToRgb(ReadOnlySpan<double> c) => Graphics.Rgb.FromUnit(c[0], c[1], c[2]);
    }

    private sealed class DeviceCmyk : ColorSpace
    {
        public override int Components => 4;

        public override double[] Initial => new double[] { 0, 0, 0, 1 };

        public override Rgb ToRgb(ReadOnlySpan<double> c) => CmykToRgb(c[0], c[1], c[2], c[3]);
    }

    /// <summary>
    /// CMYK to sRGB approximating the U.S. Web Coated (SWOP) press profile, which PDF viewers commonly use for
    /// DeviceCMYK. The polynomial and its coefficients are taken from pdf.js (src/core/colorspace.js, DeviceCmykCS;
    /// Copyright 2012 Mozilla Foundation, Apache License 2.0), where they were fitted to sampled SWOP colors.
    /// </summary>
    internal static Rgb CmykToRgb(double c, double m, double y, double k)
    {
        c = Math.Clamp(double.IsFinite(c) ? c : 0, 0, 1);
        m = Math.Clamp(double.IsFinite(m) ? m : 0, 0, 1);
        y = Math.Clamp(double.IsFinite(y) ? y : 0, 0, 1);
        k = Math.Clamp(double.IsFinite(k) ? k : 0, 0, 1);
        double r = 255
            + c * (-4.387332384609988 * c + 54.48615194189176 * m + 18.82290502165302 * y + 212.25662451639585 * k - 285.2331026137004)
            + m * (1.7149763477362134 * m - 5.6096736904047315 * y - 17.873870861415444 * k - 5.497006427196366)
            + y * (-2.5217340131683033 * y - 21.248923337353073 * k + 17.5119270841813)
            + k * (-21.86122147463605 * k - 189.48180835922747);
        double g = 255
            + c * (8.841041422036149 * c + 60.118027045597366 * m + 6.871425592049007 * y + 31.159100130055922 * k - 79.2970844816548)
            + m * (-15.310361306967817 * m + 17.575251261109482 * y + 131.35250912493976 * k - 190.9453302588951)
            + y * (4.444339102852739 * y + 9.8632861493405 * k - 24.86741582555878)
            + k * (-20.737325471181034 * k - 187.80453709719578);
        double b = 255
            + c * (0.8842522430003296 * c + 8.078677503112928 * m + 30.89978309703729 * y - 0.23883238689178934 * k - 14.183576799673286)
            + m * (10.49593273432072 * m + 63.02378494754052 * y + 50.606957656360734 * k - 112.23884253719248)
            + y * (0.03296041114873217 * y + 115.60384449646641 * k - 193.58209356861505)
            + k * (-22.33816807309886 * k - 180.12613974708367);
        return new Rgb(Clamp(r), Clamp(g), Clamp(b));
    }

    private static byte Clamp(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));

    private sealed class Lab(PdfArray? range) : ColorSpace
    {
        private readonly double[] _range = range is { Count: 4 } r
            ? r.Select(v => (v.Resolve() as PdfNumber)?.Value ?? 0).ToArray()
            : new double[] { -100, 100, -100, 100 };

        public override int Components => 3;

        public override double[] DefaultDecode(int bitsPerComponent) => new[] { 0, 100, _range[0], _range[1], _range[2], _range[3] };

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            // CIE L*a*b* (D50 white point assumed) to sRGB
            double a = Math.Clamp(c[1], _range[0], _range[1]), bb = Math.Clamp(c[2], _range[2], _range[3]);
            double fy = (c[0] + 16) / 116, fx = fy + a / 500, fz = fy - bb / 200;
            static double F(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
            double x = 0.9642 * F(fx), y = F(fy), z = 0.8249 * F(fz);
            double r = 3.1339 * x - 1.6169 * y - 0.4906 * z, g = -0.9788 * x + 1.9161 * y + 0.0335 * z, b = 0.0719 * x - 0.2290 * y + 1.4052 * z;
            static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4) - 0.055;
            return Graphics.Rgb.FromUnit(Gamma(r), Gamma(g), Gamma(b));
        }
    }

    private sealed class Indexed(ColorSpace baseSpace, int hival, byte[] lookup) : ColorSpace
    {
        private Rgb[]? _palette;

        public override int Components => 1;

        public override double[] DefaultDecode(int bitsPerComponent) => new double[] { 0, (1 << bitsPerComponent) - 1 };

        /// <summary>The colors of all indices (computed once).</summary>
        public Rgb[] Palette => _palette ??= Enumerable.Range(0, hival + 1).Select(Lookup).ToArray();

        private Rgb Lookup(int index)
        {
            int n = baseSpace.Components;
            Span<double> values = stackalloc double[Math.Max(1, n)];
            // the lookup table holds bytes mapped to the base space's range
            var decode = baseSpace.DefaultDecode(8);
            for (int i = 0; i < n; i++)
            {
                int offset = index * n + i;
                double v = offset < lookup.Length ? lookup[offset] / 255.0 : 0;
                values[i] = decode[2 * i] + v * (decode[2 * i + 1] - decode[2 * i]);
            }
            return baseSpace.ToRgb(values[..n]);
        }

        public override Rgb ToRgb(ReadOnlySpan<double> c) => Palette[Math.Clamp((int)Math.Round(double.IsFinite(c[0]) ? c[0] : 0), 0, hival)];
    }

    /// <summary>Separation and DeviceN: the tint transform maps the tints to the alternate space.</summary>
    private sealed class TintSpace(int components, ColorSpace alternate, PdfFunction tint) : ColorSpace
    {
        private readonly Dictionary<long, Rgb> _cache = new();

        public override int Components => components;

        public override double[] Initial => Enumerable.Repeat(1.0, components).ToArray();

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            // tints quantized to 8 bits for the cache (up to 8 components)
            long key = 0;
            bool cacheable = components <= 8;
            for (int i = 0; i < Math.Min(components, c.Length) && cacheable; i++)
                key = (key << 8) | (byte)Math.Round(Math.Clamp(double.IsFinite(c[i]) ? c[i] : 0, 0, 1) * 255);
            if (cacheable && _cache.TryGetValue(key, out var cached)) return cached;
            Span<double> output = stackalloc double[Math.Max(tint.Outputs, alternate.Components)];
            output.Clear();
            Span<double> input = stackalloc double[components];
            for (int i = 0; i < components; i++) input[i] = i < c.Length ? c[i] : 0;
            tint.Evaluate(input, output[..tint.Outputs]);
            var rgb = alternate.ToRgb(output);
            if (cacheable && _cache.Count < 65536) _cache[key] = rgb;
            return rgb;
        }
    }

    /// <summary>Separation and DeviceN without a usable tint transform: the strongest tint as gray.</summary>
    private sealed class TintApproximation(int components) : ColorSpace
    {
        public override int Components => components;

        public override double[] Initial => Enumerable.Repeat(1.0, components).ToArray();

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            double tint = 0;
            foreach (double v in c) tint = Math.Max(tint, v);
            return Graphics.Rgb.FromUnit(1 - tint, 1 - tint, 1 - tint);
        }
    }

    /// <summary>The colorant None: nothing is painted (ISO 32000-2, 8.6.6.4).</summary>
    private sealed class NoneSpace(int components) : ColorSpace
    {
        public override int Components => components;

        public override double[] Initial => Enumerable.Repeat(1.0, components).ToArray();

        public override bool IsNone => true;

        public override Rgb ToRgb(ReadOnlySpan<double> c) => Graphics.Rgb.Black;
    }

    private sealed class PatternSpace(ColorSpace? underlying = null) : ColorSpace
    {
        public override int Components => underlying?.Components ?? 0;

        public override double[] Initial => new double[Components];

        public override bool IsPattern => true;

        public override ColorSpace? Underlying => underlying;

        public override Rgb ToRgb(ReadOnlySpan<double> c) => Graphics.Rgb.Black;
    }
}
