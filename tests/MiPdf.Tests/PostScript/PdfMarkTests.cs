using System.Text;
using MiPdf.Objects;
using MiPdf.PostScript;
using MiPdf.PostScript.Interpreter;
using MiPdf.Rendering;

namespace MiPdf.Tests.PostScript;

/// <summary>
/// pdfmark (stage 6). The expected structures and errors were taken from Ghostscript's pdfwrite: during development
/// the PDFs of both were compared object by object (document info, catalog, bookmarks, destinations, annotations,
/// threads, embedded files, named objects and pictures).
/// </summary>
public class PdfMarkTests
{
    private static PdfDocument Convert(string program)
    {
        using var doc = PsConverter.Convert(Encoding.Latin1.GetBytes(program), out var result);
        Assert.Null(result.ErrorName);
        return PdfDocument.Open(doc.ToArray());
    }

    private static string Text(PdfObject? o) => o?.Resolve() is PdfString s ? Encoding.Latin1.GetString(s.Bytes) : "";

    /// <summary>The 1-based number of the page an object refers to (0 if none).</summary>
    private static int PageNumber(PdfDocument doc, PdfObject? o)
    {
        for (int i = 1; i <= doc.PageCount; i++)
            if (o is PdfReference r && r.ObjectNumber == doc.GetPage(i).Reference.ObjectNumber) return i;
        return 0;
    }

    private static double[] Numbers(PdfObject? o) => ((PdfArray)o!.Resolve()!).Select(v => ((PdfNumber)v.Resolve()!).Value).ToArray();

    [Fact]
    public void DocumentInfoAndViewerSettings()
    {
        using var doc = Convert(
            "[ /Title (Report) /Author (Someone) /MyKey (custom) /DOCINFO pdfmark\n" +
            "[ /PageMode /UseOutlines /PageLayout /TwoColumnLeft /Page 2 /View [/Fit] /DOCVIEW pdfmark\n" +
            "showpage showpage");
        Assert.Equal("Report", doc.Info.Title);
        Assert.Equal("Someone", doc.Info.Author);
        Assert.Equal("custom", Text(doc.Info.Dictionary!.Get(PdfName.Get("MyKey"))));
        Assert.Equal("UseOutlines", doc.Catalog.GetName(PdfName.Get("PageMode"))?.Value);
        Assert.Equal("TwoColumnLeft", doc.Catalog.GetName(PdfName.Get("PageLayout"))?.Value);
        var open = doc.Catalog.GetArray(PdfName.Get("OpenAction"))!;
        Assert.Equal(2, PageNumber(doc, open[0]));
        Assert.Equal("Fit", ((PdfName)open[1]).Value);
    }

    [Fact]
    public void BookmarksNestByCount()
    {
        using var doc = Convert(
            "[ /Title (Chapter 1) /Page 1 /View [/XYZ 0 700 null] /Count 2 /OUT pdfmark\n" +
            "[ /Title (Section 1.1) /Page 1 /OUT pdfmark\n" +
            "[ /Title (Section 1.2) /Page 2 /View [/FitH 300] /Count -1 /OUT pdfmark\n" +
            "[ /Title (Deep) /Page 2 /OUT pdfmark\n" +
            "[ /Title (Web) /Action << /Subtype /URI /URI (https://example.com) >> /OUT pdfmark\n" +
            "showpage showpage");
        var root = doc.Catalog.GetDictionary(PdfName.Outlines)!;
        Assert.Equal(4, root.GetInt(PdfName.Count)); // Chapter 1, its two sections, Web (Deep is in a closed section)
        var chapter = root.GetDictionary(PdfName.First)!;
        Assert.Equal("Chapter 1", Text(chapter.Get(PdfName.Title)));
        Assert.Equal(2, chapter.GetInt(PdfName.Count));
        var dest = chapter.GetArray(PdfName.Get("Dest"))!;
        Assert.Equal(1, PageNumber(doc, dest[0]));
        Assert.Equal("XYZ", ((PdfName)dest[1]).Value);
        var section2 = chapter.GetDictionary(PdfName.First)!.GetDictionary(PdfName.Next)!;
        Assert.Equal("Section 1.2", Text(section2.Get(PdfName.Title)));
        Assert.Equal(-1, section2.GetInt(PdfName.Count));
        Assert.Equal("Deep", Text(section2.GetDictionary(PdfName.First)!.Get(PdfName.Title)));
        var web = chapter.GetDictionary(PdfName.Next)!;
        Assert.Equal("URI", web.GetDictionary(PdfName.Get("A"))!.GetName(PdfName.Get("S"))?.Value);
        Assert.Null(web.Get(PdfName.Next));
    }

    [Fact]
    public void AnnotationsAndLinks()
    {
        using var doc = Convert(
            // the rectangle is in the current user space
            "gsave 100 100 translate 2 2 scale [ /Rect [0 0 50 20] /Border [0 0 1] /Color [1 0 0] /Page 3 /View [/Fit] /Subtype /Link /ANN pdfmark grestore\n" +
            "[ /Rect [0 30 50 50] /Action /Launch /File (other.pdf) /LNK pdfmark\n" +
            "[ /Rect [300 300 400 350] /Contents (A note) /Title (Me) /Open true /ANN pdfmark\n" +
            "[ /Rect [300 400 400 420] /Action << /Subtype /URI /URI (https://example.org) >> /Subtype /Link /ANN pdfmark\n" +
            "[ /Rect [300 450 400 470] /Dest /there /Subtype /Link /ANN pdfmark\n" +
            "[ /Rect [72 180 200 200] /SrcPg 2 /Contents (on page two) /ANN pdfmark\n" +
            "showpage showpage showpage");
        var annots = doc.GetPage(1).Dictionary.GetArray(PdfName.Annots)!.Select(a => (PdfDictionary)a.Resolve()!).ToList();
        Assert.Equal(5, annots.Count);
        Assert.Equal(new[] { 100.0, 100, 200, 140 }, Numbers(annots[0].Get(PdfName.Rect)));
        Assert.Equal("Link", annots[0].GetName(PdfName.Subtype)?.Value);
        Assert.Equal(3, PageNumber(doc, annots[0].GetArray(PdfName.Get("Dest"))![0])); // a page not painted yet
        Assert.Equal(new[] { 1.0, 0, 0 }, Numbers(annots[0].Get(PdfName.Get("C"))));
        var launch = annots[1].GetDictionary(PdfName.Get("A"))!;
        Assert.Equal("Launch", launch.GetName(PdfName.Get("S"))?.Value);
        Assert.Equal("other.pdf", Text(launch.Get(PdfName.Get("F"))));
        Assert.Equal("Text", annots[2].GetName(PdfName.Subtype)?.Value);
        Assert.Equal("Me", Text(annots[2].Get(PdfName.Get("T"))));
        Assert.Equal("https://example.org", Text(annots[3].GetDictionary(PdfName.Get("A"))!.Get(PdfName.Get("URI"))));
        Assert.Equal("there", Text(annots[4].Get(PdfName.Get("Dest"))));
        var second = doc.GetPage(2).Dictionary.GetArray(PdfName.Annots)!;
        Assert.Equal("on page two", Text(((PdfDictionary)second[0].Resolve()!).Get(PdfName.Get("Contents"))));
    }

    [Fact]
    public void NamedDestinationsAndPageEntries()
    {
        using var doc = Convert(
            "[ /Dest /there /Page 2 /View [/XYZ 100 200 0] /DEST pdfmark\n" +
            "[ /Dest (there) /DOCVIEW pdfmark\n" +
            "[ /CropBox [0 0 300 300] /PAGE pdfmark [ /Rotate 90 /PAGES pdfmark\n" +
            "showpage [ /Rotate 180 /PAGE pdfmark showpage");
        var names = doc.Catalog.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("Dests"))!.GetArray(PdfName.Get("Names"))!;
        Assert.Equal("there", Text(names[0]));
        var dest = ((PdfDictionary)names[1].Resolve()!).GetArray(PdfName.Get("D"))!;
        Assert.Equal(2, PageNumber(doc, dest[0]));
        Assert.Equal(new[] { 100.0, 200, 0 }, dest.Skip(2).Select(v => ((PdfNumber)v).Value));
        var open = doc.Catalog.GetDictionary(PdfName.Get("OpenAction"))!;
        Assert.Equal("there", Text(open.Get(PdfName.Get("D"))));
        Assert.Equal(new[] { 0.0, 0, 300, 300 }, Numbers(doc.GetPage(1).Dictionary.Get(PdfName.Get("CropBox"))));
        Assert.Equal(90, doc.GetPage(1).Dictionary.GetInt(PdfName.Get("Rotate")));
        Assert.Equal(180, doc.GetPage(2).Dictionary.GetInt(PdfName.Get("Rotate"))); // PAGE wins over PAGES
    }

    [Fact]
    public void NamedObjects()
    {
        using var doc = Convert(
            "[ /_objdef {myobj} /type /dict /OBJ pdfmark\n" +
            "[ {myobj} << /Key (value) /Num 5 >> /PUT pdfmark\n" +
            "[ {Catalog} << /MyEntry {myobj} >> /PUT pdfmark\n" +
            "[ {ThisPage} << /MyPageEntry 7 >> /PUT pdfmark\n" +
            "[ {Page2} /Later true /PUT pdfmark\n" +
            "[ /_objdef {arr} /type /array /OBJ pdfmark [ {arr} 0 [1 2 3] /PUTINTERVAL pdfmark [ {arr} 4 /APPEND pdfmark [ {arr} 0 9 /PUT pdfmark\n" +
            "[ {Catalog} << /MyArray {arr} /MyStream {strm} >> /PUT pdfmark\n" +
            "[ /_objdef {strm} /type /stream /OBJ pdfmark [ {strm} (stream ) /PUT pdfmark [ {strm} (data) /PUT pdfmark [ {strm} << /Extra 1 >> /PUT pdfmark [ {strm} /CLOSE pdfmark\n" +
            "showpage showpage");
        var entry = doc.Catalog.GetDictionary(PdfName.Get("MyEntry"))!;
        Assert.Equal("value", Text(entry.Get(PdfName.Get("Key"))));
        Assert.Equal(5, entry.GetInt(PdfName.Get("Num")));
        Assert.Equal(new[] { 9.0, 2, 3, 4 }, Numbers(doc.Catalog.Get(PdfName.Get("MyArray"))));
        var stream = (PdfStream)doc.Catalog.Get(PdfName.Get("MyStream"))!.Resolve()!;
        Assert.Equal("stream data", Encoding.ASCII.GetString(stream.GetBytes()));
        Assert.Equal(1, stream.GetInt(PdfName.Get("Extra")));
        Assert.Equal(7, doc.GetPage(1).Dictionary.GetInt(PdfName.Get("MyPageEntry")));
        Assert.True(doc.GetPage(2).Dictionary.GetBool(PdfName.Get("Later")));
    }

    [Fact]
    public void ThreadsAndEmbeddedFiles()
    {
        using var doc = Convert(
            "[ /Title (Story) /Author (A) /Rect [72 400 300 500] /ARTICLE pdfmark\n" +
            "[ /Title (Story) /Rect [72 100 300 200] /Page 2 /ARTICLE pdfmark\n" +
            "[ /Name (notes.txt) /FS << /Type /Filespec /F (notes.txt) /EF << /F {notes} >> >> /EMBED pdfmark\n" +
            "[ /_objdef {notes} /type /stream /OBJ pdfmark [ {notes} (Some notes) /PUT pdfmark [ {notes} << /Type /EmbeddedFile >> /PUT pdfmark\n" +
            "showpage showpage");
        var thread = (PdfDictionary)doc.Catalog.GetArray(PdfName.Get("Threads"))![0].Resolve()!;
        Assert.Equal("Story", Text(thread.GetDictionary(PdfName.Get("I"))!.Get(PdfName.Title)));
        var first = thread.GetDictionary(PdfName.Get("F"))!;
        var second = first.GetDictionary(PdfName.Get("N"))!;
        Assert.Equal(2, PageNumber(doc, second[PdfName.P]));
        Assert.Same(first, second.GetDictionary(PdfName.Get("N"))); // the beads form a ring
        var attachment = Assert.Single(doc.Attachments);
        Assert.Equal("notes.txt", attachment.Name);
        Assert.Equal("Some notes", Encoding.ASCII.GetString(attachment.GetData()));
    }

    [Fact]
    public void NamedPictures()
    {
        string program = "<< /PageSize [200 200] >> setpagedevice\n" +
                         "[ /BBox [0 0 50 20] /_objdef {logo} /BP pdfmark 1 0 0 setrgbcolor 0 0 50 20 rectfill [ /EP pdfmark\n" +
                         "gsave 10 10 translate [ {logo} /SP pdfmark grestore gsave 100 100 translate 2 2 scale [ {logo} /SP pdfmark grestore showpage";
        using var doc = Convert(program);
        var xobjects = doc.GetPage(1).Resources.GetDictionary(PdfName.XObject)!;
        Assert.Single(xobjects);
        var page = PdfRenderer.Render(doc.GetPage(1), new PdfRenderOptions { Dpi = 72 });
        (int, int, int) At(int x, int y)
        {
            int i = ((page.Height - 1 - y) * page.Width + x) * 4;
            return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
        }
        Assert.Equal((255, 0, 0), At(15, 15));
        Assert.Equal((255, 255, 255), At(5, 5));     // the picture is not painted where it is defined
        Assert.Equal((255, 0, 0), At(190, 130));
        Assert.Equal((255, 255, 255), At(190, 145));
    }

    [Fact]
    public void PagesReferredToButNotPaintedAreDropped()
    {
        using var doc = Convert("[ /Title (Far) /Page 5 /OUT pdfmark showpage");
        Assert.Equal(1, doc.PageCount);
    }

    [Theory]
    [InlineData("[ /Title /DOCINFO pdfmark", "rangecheck")]
    [InlineData("[ pdfmark", "stackunderflow")]
    [InlineData("[ /Title (x) (DOCINFO) pdfmark", "typecheck")]
    [InlineData("[ /Rect [1 2] /ANN pdfmark", "rangecheck")]
    [InlineData("[ {nothere} << /A 1 >> /PUT pdfmark", "typecheck")]
    [InlineData("[ 5 << /A 1 >> /PUT pdfmark", "rangecheck")]
    [InlineData("[ /SP pdfmark", "rangecheck")]
    [InlineData("[ {nope} /SP pdfmark", "typecheck")]
    [InlineData("[ /EP pdfmark", "undefined")]
    [InlineData("[ /BBox [0 0 1 1] /BP pdfmark", "rangecheck")]
    [InlineData("[ /_objdef {a} /type /foo /OBJ pdfmark", "rangecheck")]
    [InlineData("[ /DEST pdfmark", "rangecheck")]
    [InlineData("[ /FS << >> /EMBED pdfmark", "rangecheck")]
    [InlineData("[ /Title (x) /ARTICLE pdfmark", "rangecheck")]
    public void InvalidPdfMarksAreErrors(string program, string error)
    {
        using var doc = PsConverter.Convert(Encoding.Latin1.GetBytes(program), out var result);
        Assert.Equal(error, result.ErrorName);
    }

    [Fact]
    public void UnknownKindsAndKeysAreIgnored()
    {
        using var doc = PsConverter.Convert(Encoding.Latin1.GetBytes(
            "/pdfmark where { pop (yes) } { (no) } ifelse print ( ) print /setdistillerparams where { pop (yes) } { (no) } ifelse print\n" +
            "[ /X 1 /UNKNOWN pdfmark [ 1 (x) /Title (t) /DOCINFO pdfmark [ /Contents (no rect) /ANN pdfmark\n" +
            "<< /AutoRotatePages /None >> setdistillerparams currentdistillerparams /AutoRotatePages get ( ) print ==\n" +
            "nulldevice [ /Title (ignored) /DOCINFO pdfmark"), out var result);
        Assert.Null(result.ErrorName);
        Assert.Equal("yes yes /None\n", result.Output);
        Assert.Equal("t", doc.Info.Title);
    }
}
