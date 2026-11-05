using System.Security.Cryptography.X509Certificates;

namespace MiPdf;

/// <summary>Options for <see cref="PdfDocument.Open(string, PdfOpenOptions)"/>.</summary>
public sealed class PdfOpenOptions
{
    /// <summary>Password for encrypted documents (user or owner password).</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Certificate with private key for documents encrypted for certificates (see
    /// <see cref="Security.PdfEncryptionOptions.Recipients"/>).
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>
    /// Reads the objects from the file when they are needed instead of loading the whole file into memory (default
    /// false). The file stays open for reading until the document is disposed; other programs can read it, and saving
    /// the document over its own file is possible. Large streams such as images are copied from the file when the
    /// document is saved, without loading them. Files larger than 2 GB are always read this way.
    /// </summary>
    /// <remarks>
    /// Dispose the document when done. Objects copied into other documents (<see cref="PdfDocument.ImportPages"/>,
    /// <see cref="PdfDocument.Merge(IEnumerable{PdfDocument})"/>) read their stream data from this file, so keep the
    /// document open until those documents are saved.
    /// </remarks>
    public bool ReadFromFile { get; set; }

    /// <summary>
    /// Called for each problem miPDFlib tolerates while the document is read and used (repaired file structure,
    /// unreadable objects, damaged streams, substituted fonts when rendering, ...), for example to log it. The same
    /// diagnostics are collected in <see cref="PdfDocument.Diagnostics"/>. The callback runs synchronously on the
    /// thread that does the work.
    /// </summary>
    public Action<PdfDiagnostic>? OnDiagnostic { get; set; }
}
