using MiPdf.Text;

namespace MiPdf;

public sealed partial class PdfPage
{
    /// <summary>Extracts the text of the page in reading order. See <see cref="PdfTextExtractor"/> for positions.</summary>
    public string ExtractText(TextExtractionOptions? options = null) => PdfTextExtractor.ExtractText(this, options);

    /// <summary>Finds occurrences of <paramref name="text"/> and returns their bounding boxes.</summary>
    public IReadOnlyList<PdfRectangle> FindText(string text, StringComparison comparison = StringComparison.Ordinal) =>
        PdfTextExtractor.FindText(this, text, comparison);

    /// <summary>Images drawn on the page with their position (see <see cref="Images.PdfImageExtractor"/>).</summary>
    public IReadOnlyList<Images.PdfExtractedImage> ExtractImages() => Images.PdfImageExtractor.Extract(this);

    /// <summary>
    /// Rectangles of free (empty) space on the page that are at least <paramref name="minWidth"/> x
    /// <paramref name="minHeight"/> points (see <see cref="Content.FreeSpaceFinder"/>).
    /// </summary>
    public IReadOnlyList<PdfRectangle> FindFreeSpaces(double minWidth, double minHeight, bool includeAnnotations = true) =>
        Content.FreeSpaceFinder.Find(this, minWidth, minHeight, includeAnnotations);

    /// <summary>Returns the bounds of all regular expression matches on the page (matched per text line).</summary>
    public IReadOnlyList<PdfRectangle> FindText(System.Text.RegularExpressions.Regex pattern) => PdfTextExtractor.FindText(this, pattern);
}

public sealed partial class PdfDocument
{
    /// <summary>Extracts the text of all pages; pages are separated by form feeds ('\f').</summary>
    public string ExtractText(TextExtractionOptions? options = null) => PdfTextExtractor.ExtractText(this, options);
}
