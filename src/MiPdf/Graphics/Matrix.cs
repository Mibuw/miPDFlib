namespace MiPdf.Graphics;

/// <summary>An affine transformation matrix [a b c d e f] as used by PDF (row vector convention).</summary>
public readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    public static Matrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Matrix Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static Matrix Rotate(double degrees)
    {
        double r = degrees * Math.PI / 180, cos = Math.Cos(r), sin = Math.Sin(r);
        return new Matrix(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>Returns this × <paramref name="other"/> (first apply this, then <paramref name="other"/>).</summary>
    public Matrix Multiply(Matrix other) => new(
        A * other.A + B * other.C,
        A * other.B + B * other.D,
        C * other.A + D * other.C,
        C * other.B + D * other.D,
        E * other.A + F * other.C + other.E,
        E * other.B + F * other.D + other.F);

    public (double X, double Y) Transform(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
}
