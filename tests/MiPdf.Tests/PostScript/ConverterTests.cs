using System.Text;
using MiPdf.Objects;
using MiPdf.PostScript;
using MiPdf.Rendering;

namespace MiPdf.Tests.PostScript;

/// <summary>
/// The converter for whole PostScript files (stage 8): pages and their sizes, document information from the DSC
/// comments, automatic page rotation, EPS files, print files and errors. Rotations and document information were
/// compared with Ghostscript's pdfwrite (ps2pdf).
/// </summary>
public class ConverterTests
{
    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private static PdfDocument Convert(string program, PostScriptOptions? options = null) => PostScriptConverter.ToPdf(Bytes(program), options);

    private static int[] Rotations(PdfDocument doc) =>
        Enumerable.Range(1, doc.PageCount).Select(i => doc.GetPage(i).Rotation).ToArray();

    private const string Text = "/Helvetica findfont 20 scalefont setfont ";

    [Fact]
    public void PagesAndTheirSizes()
    {
        using var doc = Convert("%!PS\n0 0 100 100 rectfill showpage\n<< /PageSize [300 200] >> setpagedevice 0 0 10 10 rectfill showpage\n" +
                                "a4 0 0 10 10 rectfill showpage\n");
        Assert.Equal(3, doc.PageCount);
        Assert.Equal(new PdfRectangle(0, 0, 612, 792), doc.GetPage(1).MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 300, 200), doc.GetPage(2).MediaBox);
        Assert.Equal(new PdfRectangle(0, 0, 595, 842), doc.GetPage(3).MediaBox);
        Assert.Equal("miPDFlib", doc.Info.Producer);

        using var a4 = Convert("%!PS\n0 0 100 100 rectfill showpage\n", new PostScriptOptions { PageSize = PageSize.A4 });
        Assert.Equal(PageSize.A4.Width, a4.GetPage(1).MediaBox.Width);
        // reopened and rendered
        using var reopened = PdfDocument.Open(doc.ToArray());
        var page = PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 });
        Assert.Equal(0, page.Rgba[((page.Height - 50) * page.Width + 50) * 4]);
    }

    [Fact]
    public void DocumentInformationFromTheDscComments()
    {
        using (var doc = Convert("%!PS-Adobe-3.0\n%%Title: (My \\(test\\) title)\n%%Creator: Some App 1.0\n%%For: (Jane Doe)\n%%EndComments\n" +
                                 "0 0 10 10 rectfill showpage\n%%Title: not in the header\n"))
        {
            Assert.Equal("My (test) title", doc.Info.Title);
            Assert.Equal("Some App 1.0", doc.Info.Creator);
            Assert.Equal("Jane Doe", doc.Info.Author);
        }
        // pdfmark wins; hexadecimal UTF-16, UTF-8 and Latin-1 text
        using (var doc = PostScriptConverter.ToPdf(Encoding.Latin1.GetBytes("%!PS-Adobe-3.0\n%%Title: <FEFF00C400D6>\n%%Creator: café\n%%For: ")
                   .Concat(Encoding.UTF8.GetBytes("Jürgen")).Concat(Bytes("\n%%EndComments\n[ /Creator (from pdfmark) /DOCINFO pdfmark 0 0 10 10 rectfill showpage\n")).ToArray()))
        {
            Assert.Equal("ÄÖ", doc.Info.Title);
            Assert.Equal("from pdfmark", doc.Info.Creator);
            Assert.Equal("Jürgen", doc.Info.Author);
        }
        using (var doc = Convert("%!PS-Adobe-3.0\n%%Title: \n%%EndComments\n0 0 10 10 rectfill showpage\n"))
            Assert.Null(doc.Info.Title);
    }

    [Fact]
    public void PagesAreRotatedToBeReadUpright()
    {
        // by the direction most of the text runs in: diagonal text does not count
        using (var doc = Convert("%!PS\n" + Text + "300 400 moveto 180 rotate (upside down text) show showpage " +
                                 Text + "300 400 moveto -90 rotate (text going down) show (more) show showpage " +
                                 Text + "300 400 moveto 45 rotate (diagonal text) show showpage " +
                                 Text + "100 100 moveto (a) show 90 rotate (bbbbbbbbbbbbbbbbbbbb) show showpage\n"))
            Assert.Equal(new[] { 180, 270, 0, 90 }, Rotations(doc));
        // the orientation in the DSC comments comes first: of the document, of single pages (only Portrait and Landscape)
        using (var doc = Convert("%!PS-Adobe-3.0\n%%Orientation: Landscape\n%%EndComments\n%%Page: 1 1\n" + Text + "100 100 moveto (portrait) show showpage\n" +
                                 "%%Page: 2 2\n%%PageOrientation: Portrait\n0 0 10 10 rectfill showpage\n%%Page: 3 3\n%%BeginDocument: x.eps\n%%Page: 1 1\n" +
                                 "%%PageOrientation: Portrait\n%%EndDocument\n0 0 10 10 rectfill showpage\n%%Page: 4 4\n%%PageOrientation: Seascape\nshowpage\n"))
            Assert.Equal(new[] { 90, 0, 90, 90 }, Rotations(doc));
        // a rotation set with pdfmark is kept; turned off
        using (var doc = Convert("%!PS\n[ {ThisPage} << /Rotate 180 >> /PUT pdfmark " + Text + "90 rotate 100 -100 moveto (sideways) show showpage\n"))
            Assert.Equal(new[] { 180 }, Rotations(doc));
        using (var doc = Convert("%!PS-Adobe-3.0\n%%Orientation: Landscape\n%%EndComments\n" + Text + "90 rotate 100 -100 moveto (sideways) show showpage\n",
                   new PostScriptOptions { AutoRotatePages = false }))
            Assert.Equal(new[] { 0 }, Rotations(doc));
    }

    [Fact]
    public void EpsFilesBecomeAPageOfTheirBoundingBox()
    {
        using var doc = Convert("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 100 100 300 200\n%%EndComments\n" +
                                "1 0 0 setrgbcolor 100 100 50 50 rectfill [ /Rect [100 100 150 150] /Contents (note) /ANN pdfmark showpage\n");
        Assert.Equal(new PdfRectangle(0, 0, 200, 100), doc.GetPage(1).MediaBox);
        // the annotation is moved with the graphic
        var annotation = (PdfDictionary)doc.GetPage(1).Dictionary.GetArray(PdfName.Annots)![0].Resolve()!;
        Assert.Equal(new[] { 0.0, 0, 50, 50 }, annotation.GetArray(PdfName.Rect)!.ToDoubleArray());
        using var reopened = PdfDocument.Open(doc.ToArray());
        var page = PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 });
        int i = ((page.Height - 25) * page.Width + 25) * 4;
        Assert.Equal((255, 0, 0), (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]));
        // a PostScript file that is not an EPS keeps the page size
        using var ps = Convert("%!PS-Adobe-3.0\n%%BoundingBox: 100 100 300 200\n%%EndComments\n0 0 10 10 rectfill showpage\n");
        Assert.Equal(612, ps.GetPage(1).MediaBox.Width);
    }

    [Fact]
    public void PrintFiles()
    {
        // a PJL job header and control-D characters, as print-to-file output has them
        using (var doc = Convert("\x1B%-12345X@PJL JOB NAME = \"x\"\r\n@PJL ENTER LANGUAGE = POSTSCRIPT\r\n\x04%!PS-Adobe-3.0\n%%Title: job\n%%EndComments\n" +
                                 "0 0 10 10 rectfill showpage\n\x04\n0 0 10 10 rectfill showpage\x04\x1B%-12345X@PJL EOJ\r\n\x1B%-12345X"))
        {
            Assert.Equal(2, doc.PageCount);
            Assert.Equal("job", doc.Info.Title);
        }
        // control-D separates tokens and stays in strings (as in Ghostscript)
        using (var doc = PsConverter.Convert(Bytes("(a\u0004b) length == 1 2\u0004add == 0 0 1 1 rectfill showpage"), out var result))
            Assert.Equal("3\n3\n", result.Output);
    }

    [Fact]
    public void ErrorsAndProgramsWithoutPages()
    {
        const string program = "%!PS\n(before) print 0 0 100 100 rectfill showpage 50 50 10 10 rectfill nosuchoperator 0 0 5 5 rectfill showpage\n";
        var ex = Assert.Throws<PostScriptException>(() => Convert(program));
        Assert.Equal("undefined", ex.ErrorName);
        Assert.Equal("nosuchoperator", ex.OffendingCommand);
        Assert.StartsWith("before", ex.Output);
        // the pages shown before the error are kept, the one begun is not (as with Ghostscript)
        using (var doc = Convert(program, new PostScriptOptions { IgnoreErrors = true }))
            Assert.Equal(1, doc.PageCount);
        Assert.Throws<PdfException>(() => Convert("%!PS\n(nothing painted) print\n"));
        Assert.Throws<PdfException>(() => Convert("%!PS\n{ } loop", new PostScriptOptions { MaxOperations = 1_000_000 }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PostScriptConverter.ToPdf(Bytes("{ } loop"), cancellationToken: cancelled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => Convert("showpage", new PostScriptOptions { PageSize = new PdfRectangle(0, 0, 0, 100) }));
        // a program without showpage keeps what it painted
        using (var doc = PostScriptConverter.ToPdf(new MemoryStream(Bytes("%!PS\n0 0 10 10 rectfill\n"))))
            Assert.Equal(1, doc.PageCount);
    }
}
