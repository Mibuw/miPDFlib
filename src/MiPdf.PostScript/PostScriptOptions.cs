using MiPdf.PostScript.Interpreter;

namespace MiPdf.PostScript;

/// <summary>
/// Options for running PostScript: the limits of the sandbox and how errors are handled. PostScript is a programming
/// language, so a file can run for a long time or allocate a lot of memory; the limits end such a job with a
/// <see cref="PdfException"/>.
/// </summary>
public sealed class PostScriptOptions
{
    /// <summary>The longest time a program may run; null (the default) for no time limit.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>The most operations a program may execute (default one billion, roughly a minute).</summary>
    public long MaxOperations { get; set; } = 1_000_000_000;

    /// <summary>
    /// The most memory a program may use, in bytes (default 1 GB). It is measured as the growth of the managed heap,
    /// so it is approximate, and jobs running at the same time count for each other.
    /// </summary>
    public long MaxMemory { get; set; } = 1L << 30;

    /// <summary>
    /// When a PostScript error ends the program, keep what was painted before it instead of throwing a
    /// <see cref="PostScriptException"/> (default false). A printer prints the page in this case.
    /// </summary>
    public bool IgnoreErrors { get; set; }

    internal PsLimits ToLimits()
    {
        if (MaxOperations <= 0) throw new ArgumentOutOfRangeException(nameof(MaxOperations));
        if (MaxMemory <= 0) throw new ArgumentOutOfRangeException(nameof(MaxMemory));
        if (Timeout is { } t && t <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
        return new PsLimits { MaxOperations = MaxOperations, MaxMemory = MaxMemory, Timeout = Timeout };
    }
}

/// <summary>A PostScript error that ended the program (PLRM 3.11), such as <c>undefined</c> or <c>typecheck</c>.</summary>
public sealed class PostScriptException : PdfException
{
    internal PostScriptException(PsJobResult result)
        : base($"PostScript error {result.ErrorName}; offending command: {result.OffendingCommand}.")
    {
        ErrorName = result.ErrorName ?? "unknown";
        OffendingCommand = result.OffendingCommand ?? "";
        Output = result.Output;
    }

    /// <summary>The name of the error, e.g. <c>undefined</c>.</summary>
    public string ErrorName { get; }

    /// <summary>The operator or object that was executed when the error occurred.</summary>
    public string OffendingCommand { get; }

    /// <summary>The text the program wrote to its standard output until then (as a printer reports it).</summary>
    public string Output { get; }
}
