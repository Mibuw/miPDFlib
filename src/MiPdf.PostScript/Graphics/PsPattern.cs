using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.PostScript.Graphics;

/// <summary>
/// A pattern ready to be painted: its PDF pattern object for a content stream. The pattern matrix of PDF maps pattern
/// space to the default space of the content stream the pattern is used in, so there is one PDF pattern for each
/// transformation from device space to such a default space (the page, a form XObject, a pattern cell).
/// </summary>
internal sealed class PsPatternPaint
{
    private readonly Func<Matrix, PdfReference> _create;
    private readonly Dictionary<Matrix, PdfReference> _objects = new();

    /// <param name="colored">A colored pattern (or a shading pattern); false for an uncolored tiling pattern.</param>
    /// <param name="create">Creates the PDF pattern for a transformation from device space to a content stream's space.</param>
    public PsPatternPaint(bool colored, Func<Matrix, PdfReference> create)
    {
        Colored = colored;
        _create = create;
    }

    public bool Colored { get; }

    /// <summary>The PDF pattern for a content stream whose default space is reached from device space by <paramref name="deviceToContent"/>.</summary>
    public PdfReference Resolve(Matrix deviceToContent)
    {
        if (!_objects.TryGetValue(deviceToContent, out var reference))
        {
            reference = _create(deviceToContent);
            if (_objects.Count < 1000) _objects[deviceToContent] = reference;
        }
        return reference;
    }

    /// <summary>A matrix as PDF array.</summary>
    public static PdfArray ToArray(Matrix m) =>
        new(new PdfNumber(m.A), new PdfNumber(m.B), new PdfNumber(m.C), new PdfNumber(m.D), new PdfNumber(m.E), new PdfNumber(m.F));
}
