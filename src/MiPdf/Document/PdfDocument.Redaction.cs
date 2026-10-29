using System.Text.RegularExpressions;
using MiPdf.IO;
using MiPdf.Redaction;

namespace MiPdf;

public sealed partial class PdfDocument
{
    private bool _redacted;

    internal void MarkRedacted() => _redacted = true;

    /// <summary>Redacts every occurrence of <paramref name="text"/> (see <see cref="PdfRedactor"/>).</summary>
    public RedactionResult RedactText(string text, StringComparison comparison = StringComparison.Ordinal, RedactionOptions? options = null) =>
        PdfRedactor.RedactText(this, text, comparison, options);

    /// <summary>Redacts every match of <paramref name="pattern"/>, e.g. IBANs or e-mail addresses.</summary>
    public RedactionResult RedactText(Regex pattern, RedactionOptions? options = null) => PdfRedactor.RedactText(this, pattern, options);

    /// <summary>Applies all Redact annotations (see <see cref="PdfRedactor.MarkForRedaction"/>).</summary>
    public RedactionResult ApplyRedactions(RedactionOptions? options = null) => PdfRedactor.ApplyRedactions(this, options);

    private void CheckRedactedSave(PdfSaveOptions options)
    {
        if (_redacted && options.Incremental)
            throw new PdfException("Redacted documents must be saved completely (Incremental = false); an incremental update would keep the removed content in the file.");
    }
}

public sealed partial class PdfPage
{
    /// <summary>Removes all content inside <paramref name="areas"/> and covers them (see <see cref="PdfRedactor"/>).</summary>
    public RedactionResult Redact(IEnumerable<PdfRectangle> areas, RedactionOptions? options = null) => PdfRedactor.Redact(this, areas, options);

    /// <summary>Marks an area for later redaction with <see cref="PdfDocument.ApplyRedactions"/>.</summary>
    public PdfAnnotation MarkForRedaction(PdfRectangle area, string? overlayText = null) => PdfRedactor.MarkForRedaction(this, area, overlayText);
}
