namespace MiPdf;

/// <summary>
/// Resource limits that protect against malicious or broken documents (decompression bombs, huge images,
/// CPU-intensive structures). Exceeding a limit raises a <see cref="PdfException"/>.
/// </summary>
/// <example>
/// <code>
/// // process-wide defaults (e.g. at application start)
/// PdfLimits.Default = PdfLimits.Default with { MaxDecodedStreamSize = 64 * 1024 * 1024 };
///
/// // stricter limits for one request (flows with async calls)
/// using (PdfLimits.Use(PdfLimits.Default with { MaxDecodedStreamSize = 16 * 1024 * 1024 }))
/// {
///     using var doc = PdfDocument.Open(upload);
///     text = doc.ExtractText();
/// }
/// </code>
/// </example>
public sealed record PdfLimits
{
    private static readonly AsyncLocal<PdfLimits?> Scoped = new();

    /// <summary>Process-wide limits used when no scoped limits are active.</summary>
    public static PdfLimits Default { get; set; } = new();

    /// <summary>The limits in effect for the current execution context.</summary>
    public static PdfLimits Current => Scoped.Value ?? Default;

    /// <summary>Applies <paramref name="limits"/> to the current execution context until the returned object is disposed.</summary>
    public static IDisposable Use(PdfLimits limits)
    {
        var previous = Scoped.Value;
        Scoped.Value = limits;
        return new Restore(previous);
    }

    /// <summary>Maximum size of a decoded stream in bytes (default 256 MB).</summary>
    public long MaxDecodedStreamSize { get; init; } = 256L * 1024 * 1024;

    /// <summary>Maximum number of pixels of an image that is decoded (default 100 megapixels).</summary>
    public long MaxImagePixels { get; init; } = 100_000_000;

    /// <summary>Maximum number of code mappings a single CMap may define, including ranges (default 1,000,000).</summary>
    public long MaxCMapEntries { get; init; } = 1_000_000;

    /// <summary>Maximum number of filters applied to one stream (default 8).</summary>
    public int MaxFiltersPerStream { get; init; } = 8;

    internal void CheckDecodedSize(long size)
    {
        if (size > MaxDecodedStreamSize)
            throw new PdfException($"Decoded stream exceeds the limit of {MaxDecodedStreamSize} bytes (PdfLimits.MaxDecodedStreamSize).");
    }

    internal void CheckImage(long width, long height)
    {
        if (width <= 0 || height <= 0 || width * height > MaxImagePixels)
            throw new PdfException($"Image size {width}x{height} is invalid or exceeds the limit of {MaxImagePixels} pixels (PdfLimits.MaxImagePixels).");
    }

    private sealed class Restore(PdfLimits? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Scoped.Value = previous;
        }
    }
}
