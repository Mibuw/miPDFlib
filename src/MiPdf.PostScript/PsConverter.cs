using MiPdf.PostScript.Graphics;
using MiPdf.PostScript.Interpreter;

namespace MiPdf.PostScript;

/// <summary>Converts PostScript files to PDF, as Ghostscript's ps2pdf does.</summary>
public static class PostScriptConverter
{
    /// <summary>
    /// Runs a PostScript program and returns a PDF document with the pages it shows. Text keeps its fonts, and
    /// graphics, images, patterns and shadings stay vectors; pdfmark operators add document information, bookmarks,
    /// links and the like. The document information is also taken from the DSC comments (%%Title, %%Creator, %%For as
    /// the author). EPS files become a page of their bounding box. Print files with a PJL header and DOS EPS binary
    /// files are read too.
    /// </summary>
    /// <param name="postScript">The PostScript (or EPS) file.</param>
    /// <param name="options">The limits of the sandbox, the page size and the error handling (defaults if null).</param>
    /// <param name="cancellationToken">Ends the program early.</param>
    /// <exception cref="PdfException">The program shows no page, or it exceeds a limit.</exception>
    /// <exception cref="PostScriptException">A PostScript error ended the program (unless <see cref="PostScriptOptions.IgnoreErrors"/>).</exception>
    /// <example><code>
    /// using var document = PostScriptConverter.ToPdf(File.ReadAllBytes("report.ps"));
    /// document.Save("report.pdf");
    /// </code></example>
    public static PdfDocument ToPdf(byte[] postScript, PostScriptOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postScript);
        options ??= new PostScriptOptions();
        var limits = options.ToLimits();
        PdfDocument? document = null;
        try
        {
            document = PsConverter.Convert(postScript, out var result, limits, options.PageSize.Width, options.PageSize.Height,
                options.AutoRotatePages, cancellationToken);
            if (result.ErrorName != null && !options.IgnoreErrors) throw new PostScriptException(result);
            if (document.PageCount == 0) throw new PdfException("The PostScript program shows no page.");
            var converted = document;
            document = null;
            return converted;
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex))
        {
            throw new PdfException("The PostScript file could not be converted: " + ex.Message, ex);
        }
        finally
        {
            document?.Dispose();
        }
    }

    /// <summary>Reads a PostScript file from a stream and converts it (see <see cref="ToPdf(byte[], PostScriptOptions?, CancellationToken)"/>).</summary>
    public static PdfDocument ToPdf(Stream postScript, PostScriptOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postScript);
        using var buffer = new MemoryStream();
        postScript.CopyTo(buffer);
        return ToPdf(buffer.ToArray(), options, cancellationToken);
    }
}

/// <summary>The conversion itself; the result of the job is returned instead of thrown (the tests look at it).</summary>
internal static class PsConverter
{
    public static PdfDocument Convert(byte[] data, out PsJobResult result, PsLimits? limits = null, double width = 612, double height = 792,
        bool autoRotate = false, CancellationToken cancellationToken = default)
    {
        var program = DscComments.PostScript(data);
        var dsc = new DscComments(program);
        var document = PdfDocument.Create();
        if (dsc.Title != null) document.Info.Title = dsc.Title;
        if (dsc.Creator != null) document.Info.Creator = dsc.Creator;
        if (dsc.For != null) document.Info.Author = dsc.For;
        double left = 0, bottom = 0;
        if (dsc.IsEps && dsc.BoundingBox is { } box)
            (left, bottom, width, height) = (box.Left, box.Bottom, box.Width, box.Height);
        var device = new PsPageDevice(document, width, height, left, bottom)
        {
            AutoRotate = autoRotate, DocumentOrientation = dsc.DocumentOrientation, PageOrientations = dsc.PageOrientations,
        };
        var interpreter = new PsInterpreter(limits, cancellationToken) { PageDevice = device };
        interpreter.InitGraphics(device);
        result = interpreter.Run(program);
        interpreter.EndJob();
        device.Finish();
        return document;
    }
}
