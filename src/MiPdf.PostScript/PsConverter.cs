using MiPdf.PostScript.Graphics;
using MiPdf.PostScript.Interpreter;

namespace MiPdf.PostScript;

/// <summary>Runs a PostScript program and writes the pages it shows to a PDF document (internal until the public API).</summary>
internal static class PsConverter
{
    public static PdfDocument Convert(byte[] program, out PsJobResult result, PsLimits? limits = null, double width = 612, double height = 792)
    {
        var document = PdfDocument.Create();
        var device = new PsPageDevice(document, width, height);
        var interpreter = new PsInterpreter(limits) { PageDevice = device };
        interpreter.InitGraphics(device);
        result = interpreter.Run(program);
        interpreter.EndJob();
        device.Finish();
        return document;
    }
}
