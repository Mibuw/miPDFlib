namespace MiPdf;

/// <summary>Options for <see cref="PdfDocument.Open(string, PdfOpenOptions)"/>.</summary>
public sealed class PdfOpenOptions
{
    /// <summary>Password for encrypted documents (user or owner password).</summary>
    public string? Password { get; set; }

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
}
