namespace MiPdf;

/// <summary>An axis-aligned rectangle in PDF user space (points, origin bottom-left).</summary>
public readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    /// <summary>Width in points (<see cref="Right"/> - <see cref="Left"/>).</summary>
    public double Width => Right - Left;

    /// <summary>Height in points (<see cref="Top"/> - <see cref="Bottom"/>).</summary>
    public double Height => Top - Bottom;

    /// <summary>Creates a normalized rectangle from two opposite corners given in any order.</summary>
    public static PdfRectangle FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    /// <summary>Creates a rectangle from its lower-left corner (<paramref name="x"/>, <paramref name="y"/>) and its size.
    /// Negative sizes are normalized.</summary>
    public static PdfRectangle FromSize(double x, double y, double width, double height) =>
        FromCorners(x, y, x + width, y + height);

    /// <summary>Returns true if the point lies inside the rectangle or on its border.</summary>
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    /// <summary>Returns a rectangle enlarged by <paramref name="d"/> points on every side (shrunk if negative).</summary>
    public PdfRectangle Inflate(double d) => new(Left - d, Bottom - d, Right + d, Top + d);
}

/// <summary>Common page sizes in points (1 pt = 1/72 inch).</summary>
public static class PageSize
{
    /// <summary>ISO A0 portrait (841 x 1189 mm, 2384 x 3370 pt).</summary>
    public static readonly PdfRectangle A0 = new(0, 0, 2384, 3370);
    /// <summary>ISO A1 portrait (594 x 841 mm, 1684 x 2384 pt).</summary>
    public static readonly PdfRectangle A1 = new(0, 0, 1684, 2384);
    /// <summary>ISO A2 portrait (420 x 594 mm, 1191 x 1684 pt).</summary>
    public static readonly PdfRectangle A2 = new(0, 0, 1191, 1684);
    /// <summary>ISO A3 portrait (297 x 420 mm, 842 x 1191 pt).</summary>
    public static readonly PdfRectangle A3 = new(0, 0, 842, 1191);
    /// <summary>ISO A4 portrait (210 x 297 mm, 595 x 842 pt).</summary>
    public static readonly PdfRectangle A4 = new(0, 0, 595, 842);
    /// <summary>ISO A5 portrait (148 x 210 mm, 420 x 595 pt).</summary>
    public static readonly PdfRectangle A5 = new(0, 0, 420, 595);
    /// <summary>ISO A6 portrait (105 x 148 mm, 298 x 420 pt).</summary>
    public static readonly PdfRectangle A6 = new(0, 0, 298, 420);
    /// <summary>ISO B4 portrait (250 x 353 mm, 709 x 1001 pt).</summary>
    public static readonly PdfRectangle B4 = new(0, 0, 709, 1001);
    /// <summary>ISO B5 portrait (176 x 250 mm, 499 x 709 pt).</summary>
    public static readonly PdfRectangle B5 = new(0, 0, 499, 709);
    /// <summary>US Letter portrait (8.5 x 11 in, 612 x 792 pt).</summary>
    public static readonly PdfRectangle Letter = new(0, 0, 612, 792);
    /// <summary>US Legal portrait (8.5 x 14 in, 612 x 1008 pt).</summary>
    public static readonly PdfRectangle Legal = new(0, 0, 612, 1008);
    /// <summary>US Tabloid (11 x 17 in, 792 x 1224 pt).</summary>
    public static readonly PdfRectangle Tabloid = new(0, 0, 792, 1224);
    /// <summary>US Ledger, Tabloid in landscape (17 x 11 in, 1224 x 792 pt).</summary>
    public static readonly PdfRectangle Ledger = new(0, 0, 1224, 792);
    /// <summary>US Executive portrait (7.25 x 10.5 in, 522 x 756 pt).</summary>
    public static readonly PdfRectangle Executive = new(0, 0, 522, 756);

    /// <summary>Returns <paramref name="size"/> with width and height swapped, keeping its lower-left corner.</summary>
    public static PdfRectangle Landscape(PdfRectangle size) =>
        new(size.Left, size.Bottom, size.Left + size.Height, size.Bottom + size.Width);

    /// <summary>Creates a page size at the origin from a width and height in millimeters (converted to points).</summary>
    public static PdfRectangle FromMillimeters(double width, double height) =>
        new(0, 0, width * 72 / 25.4, height * 72 / 25.4);
}
