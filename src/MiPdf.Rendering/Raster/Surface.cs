namespace MiPdf.Rendering.Raster;

/// <summary>An RGBA pixel buffer (8 bits per channel, not premultiplied, rows from the top) with source-over compositing.</summary>
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

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>Paints a color with the coverage of a row (from the rasterizer), the opacity and the clip mask.</summary>
    public void BlendRow(int y, int x0, int x1, float[] coverage, byte[]? clip, Rgb color, double alpha)
    {
        long row = (long)y * Width;
        for (int x = x0; x <= x1; x++)
        {
            double a = coverage[x] * alpha;
            if (clip != null) a *= clip[row + x] / 255.0;
            if (a > 0) Blend(row + x, color.R, color.G, color.B, a);
        }
    }

    /// <summary>Source-over compositing of one pixel (<paramref name="alpha"/> 0..1).</summary>
    public void Blend(long index, byte r, byte g, byte b, double alpha)
    {
        long i = index * 4;
        if (alpha >= 1)
        {
            Pixels[i] = r;
            Pixels[i + 1] = g;
            Pixels[i + 2] = b;
            Pixels[i + 3] = 255;
            return;
        }
        double da = Pixels[i + 3] / 255.0;
        double outAlpha = alpha + da * (1 - alpha);
        if (outAlpha <= 0) return;
        double dw = da * (1 - alpha) / outAlpha, sw = alpha / outAlpha;
        Pixels[i] = (byte)Math.Round(r * sw + Pixels[i] * dw);
        Pixels[i + 1] = (byte)Math.Round(g * sw + Pixels[i + 1] * dw);
        Pixels[i + 2] = (byte)Math.Round(b * sw + Pixels[i + 2] * dw);
        Pixels[i + 3] = (byte)Math.Round(outAlpha * 255);
    }
}

/// <summary>An 8-bit RGB color.</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);

    public static Rgb FromUnit(double r, double g, double b) => new(ToByte(r), ToByte(g), ToByte(b));

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1) * 255);
}
