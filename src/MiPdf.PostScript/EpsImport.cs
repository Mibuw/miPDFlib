using MiPdf.Graphics;
using MiPdf.PostScript.Graphics;
using MiPdf.PostScript.Interpreter;

namespace MiPdf.PostScript;

/// <summary>
/// Imports EPS (Encapsulated PostScript) graphics into a PDF document as form XObjects, which can then be drawn on any
/// page with <see cref="PdfCanvas.DrawXObject"/>, like a page imported with
/// <see cref="PdfDocument.ImportPageAsTemplate"/>.
/// </summary>
public static class EpsImport
{
    /// <summary>
    /// Runs an EPS file and returns what it paints as a form XObject of <paramref name="document"/>. The form is as
    /// large as the bounding box of the graphic (its lower left corner at 0, 0) and clips to it; text, images, patterns
    /// and shadings stay vectors. DOS EPS binary files are read without their preview.
    /// </summary>
    /// <param name="document">The document the form XObject is added to.</param>
    /// <param name="eps">The EPS file.</param>
    /// <param name="options">The limits of the sandbox and the error handling (defaults if null).</param>
    /// <param name="cancellationToken">Ends the program early.</param>
    /// <exception cref="PdfException">The file has no valid %%BoundingBox, or the program exceeds a limit.</exception>
    /// <exception cref="PostScriptException">A PostScript error ended the program (unless <see cref="PostScriptOptions.IgnoreErrors"/>).</exception>
    /// <example><code>
    /// var logo = document.ImportEps(File.ReadAllBytes("logo.eps"));
    /// using (var canvas = page.GetCanvas()) canvas.DrawXObject(logo, 50, 700, width: 120);
    /// </code></example>
    public static PdfFormXObject ImportEps(this PdfDocument document, byte[] eps, PostScriptOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(eps);
        options ??= new PostScriptOptions();
        var limits = options.ToLimits();
        try
        {
            var program = EpsFile.PostScript(eps);
            var box = EpsFile.BoundingBox(program) ?? throw new PdfException("The EPS file has no valid %%BoundingBox comment.");
            var form = new PdfFormXObject(document, new PdfRectangle(0, 0, box.Width, box.Height));
            var device = new PsFormDevice(form, box.Left, box.Bottom);
            var interpreter = new PsInterpreter(limits, cancellationToken) { Encapsulated = true };
            interpreter.InitGraphics(device);
            var result = interpreter.Run(program);
            if (result.ErrorName != null && !options.IgnoreErrors) throw new PostScriptException(result);
            device.Finish();
            return form;
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex))
        {
            throw new PdfException("The EPS file could not be imported: " + ex.Message, ex);
        }
    }

    /// <summary>Reads an EPS file from a stream and imports it (see <see cref="ImportEps(PdfDocument, byte[], PostScriptOptions?, CancellationToken)"/>).</summary>
    public static PdfFormXObject ImportEps(this PdfDocument document, Stream eps, PostScriptOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eps);
        using var buffer = new MemoryStream();
        eps.CopyTo(buffer);
        return document.ImportEps(buffer.ToArray(), options, cancellationToken);
    }
}
