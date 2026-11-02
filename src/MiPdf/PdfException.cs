namespace MiPdf;

/// <summary>Base exception for errors raised by miPDFlib.</summary>
public class PdfException : Exception
{
    /// <summary>Creates an exception with the given message.</summary>
    public PdfException(string message) : base(message) { }

    /// <summary>Creates an exception with the given message and the exception that caused it.</summary>
    public PdfException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// True for exceptions that indicate malformed input reaching code that assumed well-formed data
    /// (they are converted into <see cref="PdfException"/> at the public entry points).
    /// </summary>
    internal static bool IsUnexpectedInputError(Exception ex) => ex is not PdfException &&
        ex is IndexOutOfRangeException or ArgumentException or InvalidCastException or NullReferenceException or FormatException
            or OverflowException or InvalidOperationException or IOException or KeyNotFoundException or DivideByZeroException
            or System.Formats.Asn1.AsnContentException or System.Security.Cryptography.CryptographicException;
}

/// <summary>Raised when a PDF file cannot be parsed.</summary>
public class PdfParseException : PdfException
{
    /// <summary>Creates a parse exception; a non-negative <paramref name="position"/> is appended to the message.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="position">The byte offset in the file where the error occurred, or -1 if unknown.</param>
    public PdfParseException(string message, long position = -1)
        : base(position >= 0 ? $"{message} (at offset {position})" : message) => Position = position;

    /// <summary>The byte offset in the file where the error occurred, or -1 if unknown.</summary>
    public long Position { get; }
}

/// <summary>Raised when an encrypted document is opened without a valid password.</summary>
public class PdfPasswordException : PdfException
{
    /// <summary>Creates an exception with the given message.</summary>
    public PdfPasswordException(string message) : base(message) { }
}
