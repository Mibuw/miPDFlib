using System.IO.Compression;
using System.Text;
using MiPdf.Rendering;
using MiPdf.Tests.Security;

namespace MiPdf.Tests.Core;

/// <summary>Tolerated problems are reported as diagnostics (callback and <see cref="PdfDocument.Diagnostics"/>).</summary>
public class DiagnosticsTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Stream(string dict, byte[] data) => [.. B($"<< {dict} /Length {data.Length} >>\nstream\n"), .. data, .. B("\nendstream")];

    private static Dictionary<int, byte[]> Page(byte[] contents, string resources = "<< >>") => new()
    {
        [1] = B("<< /Type /Catalog /Pages 2 0 R >>"),
        [2] = B("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        [3] = B("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources 5 0 R >>"),
        [4] = contents,
        [5] = B(resources),
    };

    private static (PdfDocument Document, List<PdfDiagnostic> Reported) Open(byte[] data)
    {
        var reported = new List<PdfDiagnostic>();
        return (PdfDocument.Open(data, new PdfOpenOptions { OnDiagnostic = reported.Add }), reported);
    }

    [Fact]
    public void IntactDocumentsHaveNoDiagnostics()
    {
        var (doc, reported) = Open(HardeningTests.BuildPdf(Page(Stream("", B("0 0 1 rg 10 10 50 50 re f")))));
        using (doc)
        {
            doc.GetPage(1).ExtractText();
            PdfRenderer.Render(doc.GetPage(1));
            Assert.Empty(reported);
            Assert.Empty(doc.Diagnostics);
        }
    }

    [Fact]
    public void RepairedCrossReferenceTable()
    {
        var data = HardeningTests.BuildPdf(Page(Stream("", B("0 g 0 0 10 10 re f"))));
        // point startxref into nowhere
        var text = Encoding.Latin1.GetString(data);
        int at = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
        data = B(text[..at] + "999999\n%%EOF\n");
        var (doc, reported) = Open(data);
        using (doc)
        {
            Assert.True(doc.WasRepaired);
            var diagnostic = Assert.Single(reported);
            Assert.Equal(PdfDiagnosticCode.XrefRepaired, diagnostic.Code);
            Assert.Equal(PdfDiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Null(diagnostic.PageNumber);
            Assert.Equal(reported, doc.Diagnostics);
            Assert.StartsWith("Warning XrefRepaired: ", diagnostic.ToString());
        }
    }

    [Fact]
    public void UnreadableObjectsAndDamagedStreams()
    {
        // the content stream is Flate data with damaged bytes in the middle
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(B(string.Concat(Enumerable.Repeat("BT /F1 12 Tf 10 50 Td (Hello) Tj ET\n", 50))));
        var damagedData = compressed.ToArray();
        for (int i = damagedData.Length / 3; i < damagedData.Length / 3 + 8; i++) damagedData[i] ^= 0xFF;
        var objects = Page(Stream("/Filter /FlateDecode", damagedData), "<< /Font << /F1 6 0 R >> >>");
        objects[6] = B("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var (doc, reported) = Open(HardeningTests.BuildPdf(objects));
        using (doc)
        {
            doc.GetPage(1).ExtractText();
            var damaged = Assert.Single(reported, d => d.Code == PdfDiagnosticCode.StreamDamaged);
            Assert.Equal(1, damaged.PageNumber); // reported in the context of the page
        }

        // object 6 is not where the cross-reference table says (its header has another number)
        objects = Page(Stream("", B("BT /F1 12 Tf 10 50 Td (Hello) Tj ET")), "<< /Font << /F1 6 0 R >> >>");
        objects[6] = B("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var data = Encoding.Latin1.GetString(HardeningTests.BuildPdf(objects)).Replace("\n6 0 obj\n", "\n9 0 obj\n");
        (doc, reported) = Open(B(data));
        using (doc)
        {
            doc.GetPage(1).ExtractText();
            var unreadable = Assert.Single(reported, d => d.Code == PdfDiagnosticCode.ObjectUnreadable);
            Assert.Equal(6, unreadable.ObjectNumber);
        }
    }

    [Fact]
    public void UnreadableToUnicodeMap()
    {
        var objects = Page(Stream("", B("BT /F1 12 Tf 10 50 Td (Hello) Tj ET")), "<< /Font << /F1 6 0 R >> >>");
        objects[6] = B("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 7 0 R >>");
        objects[7] = Stream("/Filter /NoSuchDecode", B("garbage"));
        var (doc, reported) = Open(HardeningTests.BuildPdf(objects));
        using (doc)
        {
            Assert.Contains("Hello", doc.GetPage(1).ExtractText()); // falls back to the encoding
            var diagnostic = Assert.Single(reported, d => d.Code == PdfDiagnosticCode.ToUnicodeDamaged);
            Assert.Equal(6, diagnostic.ObjectNumber);
            Assert.Equal(1, diagnostic.PageNumber);
        }
    }

    [Fact]
    public void RenderingReportsFontsAndImages()
    {
        // a non-embedded font, a JPEG 2000 image (not decoded) and an invalid shading
        var objects = Page(Stream("", B("BT /F1 12 Tf 10 50 Td (Hi) Tj ET q 50 0 0 50 100 20 cm /Im1 Do Q /Sh sh")),
            "<< /Font << /F1 6 0 R >> /XObject << /Im1 7 0 R >> /Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 0] >> >> >>");
        objects[6] = B("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects[7] = Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode", new byte[] { 0, 0, 0, 12 });
        var (doc, reported) = Open(HardeningTests.BuildPdf(objects));
        using (doc)
        {
            PdfRenderer.Render(doc.GetPage(1));
            PdfRenderer.Render(doc.GetPage(1)); // each problem is reported once
            Assert.Contains(reported, d => d.Code is PdfDiagnosticCode.FontSubstituted or PdfDiagnosticCode.FontMissing && d.ObjectNumber == 6);
            var image = Assert.Single(reported, d => d.Code == PdfDiagnosticCode.ImageNotDecoded);
            Assert.Equal(7, image.ObjectNumber);
            Assert.Equal(1, image.PageNumber);
            Assert.Contains("JPXDecode", image.Message);
            Assert.Single(reported, d => d.Code == PdfDiagnosticCode.PaintNotSupported);
            Assert.Equal(reported.Count, doc.Diagnostics.Count);
        }
    }

    [Fact]
    public void RecursiveFormsAreReported()
    {
        var objects = Page(Stream("", B("/X Do")), "<< /XObject << /X 6 0 R >> >>");
        objects[6] = Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /X 6 0 R >> >>", B("0 g 0 0 5 5 re f /X Do"));
        var (doc, reported) = Open(HardeningTests.BuildPdf(objects));
        using (doc)
        {
            PdfRenderer.Render(doc.GetPage(1));
            Assert.Contains(reported, d => d.Code == PdfDiagnosticCode.NestingTooDeep);
        }
    }
}
