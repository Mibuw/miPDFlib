namespace MiPdf;

/// <summary>An axis-aligned rectangle in PDF user space (points, origin bottom-left).</summary>
public readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public static PdfRectangle FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public static PdfRectangle FromSize(double x, double y, double width, double height) =>
        FromCorners(x, y, x + width, y + height);

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    public PdfRectangle Inflate(double d) => new(Left - d, Bottom - d, Right + d, Top + d);
}

/// <summary>Common page sizes in points (1 pt = 1/72 inch).</summary>
public static class PageSize
{
    public static readonly PdfRectangle A3 = new(0, 0, 842, 1191);
    public static readonly PdfRectangle A4 = new(0, 0, 595, 842);
    public static readonly PdfRectangle A5 = new(0, 0, 420, 595);
    public static readonly PdfRectangle Letter = new(0, 0, 612, 792);
    public static readonly PdfRectangle Legal = new(0, 0, 612, 1008);

    /// <summary>Returns the landscape variant of <paramref name="size"/>.</summary>
    public static PdfRectangle Landscape(PdfRectangle size) => new(size.Bottom, size.Left, size.Top, size.Right);

    public static PdfRectangle FromMillimeters(double width, double height) =>
        new(0, 0, width * 72 / 25.4, height * 72 / 25.4);
}
