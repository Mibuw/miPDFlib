using MiPdf.Objects;

namespace MiPdf;

/// <summary>How serious a <see cref="PdfDiagnostic"/> is.</summary>
public enum PdfDiagnosticSeverity
{
    /// <summary>Worth knowing, the result is as intended (e.g. a standard font drawn with an installed substitute).</summary>
    Information,

    /// <summary>Damaged or unsupported input was tolerated; the result may differ from what the author intended.</summary>
    Warning,
}

/// <summary>
/// A problem miPDFlib tolerated instead of failing: a repaired file structure, an object or stream that could not be
/// read, a substituted font, an image or shading that was not drawn. Errors that stop an operation are still
/// reported as <see cref="PdfException"/>.
/// </summary>
public sealed class PdfDiagnostic
{
    internal PdfDiagnostic(PdfDiagnosticSeverity severity, string code, string message, int? pageNumber, int? objectNumber)
    {
        Severity = severity;
        Code = code;
        Message = message;
        PageNumber = pageNumber;
        ObjectNumber = objectNumber;
    }

    /// <summary>The severity.</summary>
    public PdfDiagnosticSeverity Severity { get; }

    /// <summary>A stable identifier of the kind of problem (see <see cref="PdfDiagnosticCode"/>).</summary>
    public string Code { get; }

    /// <summary>A description in English.</summary>
    public string Message { get; }

    /// <summary>The page (1-based) the problem occurred on, or null if it is not page-specific.</summary>
    public int? PageNumber { get; }

    /// <summary>The number of the indirect object concerned, or null if unknown.</summary>
    public int? ObjectNumber { get; }

    /// <summary>For example "Warning ImageNotDecoded (page 3, object 12): ...".</summary>
    public override string ToString()
    {
        var where = string.Join(", ", new[] { PageNumber is { } p ? $"page {p}" : null, ObjectNumber is { } o ? $"object {o}" : null }.Where(s => s != null));
        return where.Length > 0 ? $"{Severity} {Code} ({where}): {Message}" : $"{Severity} {Code}: {Message}";
    }
}

/// <summary>The codes of <see cref="PdfDiagnostic.Code"/>. New codes may be added in later versions.</summary>
public static class PdfDiagnosticCode
{
    /// <summary>The cross-reference table was damaged; the objects were located by scanning the file.</summary>
    public const string XrefRepaired = "XrefRepaired";

    /// <summary>An indirect object could not be parsed and is treated as null.</summary>
    public const string ObjectUnreadable = "ObjectUnreadable";

    /// <summary>The data of a stream could not be fully decoded (damaged or truncated compressed data).</summary>
    public const string StreamDamaged = "StreamDamaged";

    /// <summary>A content stream contains a syntax error; the operators after it were ignored.</summary>
    public const string ContentDamaged = "ContentDamaged";

    /// <summary>The ToUnicode map of a font could not be read; extracted text of that font may be wrong.</summary>
    public const string ToUnicodeDamaged = "ToUnicodeDamaged";

    /// <summary>A font is not embedded and was drawn with an installed substitute (rendering).</summary>
    public const string FontSubstituted = "FontSubstituted";

    /// <summary>A font is not embedded and no substitute is installed: its text was not drawn (rendering).</summary>
    public const string FontMissing = "FontMissing";

    /// <summary>An embedded font program is damaged; a substitute was used (rendering).</summary>
    public const string FontDamaged = "FontDamaged";

    /// <summary>An image could not be decoded (damaged data, unsupported color space) and was not drawn or converted.</summary>
    public const string ImageNotDecoded = "ImageNotDecoded";

    /// <summary>A shading or pattern is invalid or unsupported and was not painted (rendering).</summary>
    public const string PaintNotSupported = "PaintNotSupported";

    /// <summary>Form XObjects, patterns or Type 3 glyphs are nested too deeply or recursively; the inner ones were skipped.</summary>
    public const string NestingTooDeep = "NestingTooDeep";
}

/// <summary>Collects the diagnostics of a document (deduplicated, bounded) and passes new ones to the callback.</summary>
internal sealed class DiagnosticSink
{
    private const int MaxEntries = 1000;

    private readonly List<PdfDiagnostic> _items = new();
    private readonly HashSet<(string, string, int?, int?)> _seen = new();
    private readonly object _lock = new();

    public Action<PdfDiagnostic>? Callback { get; set; }

    /// <summary>Resolves the object number of an object (set by the document).</summary>
    public Func<PdfObject, int?>? ObjectNumberOf { get; set; }

    public IReadOnlyList<PdfDiagnostic> Items
    {
        get
        {
            lock (_lock) return _items.ToArray();
        }
    }

    public void Report(PdfDiagnosticSeverity severity, string code, string message, int? page = null, int? objectNumber = null)
    {
        PdfDiagnostic diagnostic;
        lock (_lock)
        {
            if (_items.Count >= MaxEntries || !_seen.Add((code, message, page, objectNumber))) return;
            diagnostic = new PdfDiagnostic(severity, code, message, page, objectNumber);
            _items.Add(diagnostic);
        }
        Callback?.Invoke(diagnostic);
    }
}

/// <summary>
/// The diagnostics context of the current thread: public operations (rendering, text and image extraction) enter it
/// with their document and page, so that code without a document reference (filters, parsers) can report.
/// </summary>
internal static class DiagnosticContext
{
    [ThreadStatic] private static Scope? _current;

    private sealed class Scope(DiagnosticSink sink, int? page, Scope? outer) : IDisposable
    {
        public DiagnosticSink Sink { get; } = sink;
        public int? Page { get; } = page;
        public Scope? Outer { get; } = outer;

        public void Dispose() => _current = Outer;
    }

    /// <summary>Enters the context of a document (and page); dispose to leave it.</summary>
    public static IDisposable Enter(PdfDocument document, int? page = null) => _current = new Scope(document.DiagnosticSink, page, _current);

    /// <summary>Enters the context of a page of a document.</summary>
    public static IDisposable Enter(PdfPage page) => Enter(page.Document, page.Document.PageNumberOf(page));

    public static void Information(string code, string message, PdfObject? about = null) =>
        Report(PdfDiagnosticSeverity.Information, code, message, about);

    /// <summary>Reports to the current context (ignored outside of one).</summary>
    public static void Report(PdfDiagnosticSeverity severity, string code, string message, PdfObject? about = null, int? objectNumber = null)
    {
        if (_current is not { } scope) return;
        objectNumber ??= about != null ? scope.Sink.ObjectNumberOf?.Invoke(about) : null;
        scope.Sink.Report(severity, code, message, scope.Page, objectNumber);
    }

    public static void Warning(string code, string message, PdfObject? about = null, int? objectNumber = null) =>
        Report(PdfDiagnosticSeverity.Warning, code, message, about, objectNumber);
}
