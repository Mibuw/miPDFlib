using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.PdfA;

namespace MiPdf.Tests.PdfA;

public class PdfATests
{
    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "1", "B", "%PDF-1.4")]
    [InlineData(PdfAConformance.PdfA2B, "2", "B", "%PDF-1.7")]
    [InlineData(PdfAConformance.PdfA3U, "3", "U", "%PDF-1.7")]
    public void WritesMetadataAndOutputIntent(PdfAConformance conformance, string part, string level, string header)
    {
        var doc = PdfDocument.Create();
        doc.Info.Title = "Titel <&> Ü";
        doc.Info.Author = "Autor";
        doc.Conformance = conformance;
        doc.AddPage();
        var bytes = doc.ToArray();
        Assert.StartsWith(header, Encoding.ASCII.GetString(bytes, 0, 8));

        var reopened = PdfDocument.Open(bytes);
        var xmp = Encoding.UTF8.GetString(reopened.GetXmpMetadata()!);
        Assert.Contains($"<pdfaid:part>{part}</pdfaid:part>", xmp);
        Assert.Contains($"<pdfaid:conformance>{level}</pdfaid:conformance>", xmp);
        Assert.Contains("Titel &lt;&amp;&gt; Ü", xmp);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(xmp.Substring(xmp.IndexOf("<x:xmpmeta", StringComparison.Ordinal), xmp.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12 - xmp.IndexOf("<x:xmpmeta", StringComparison.Ordinal))));

        var metadata = reopened.Catalog.GetStream(PdfName.Metadata)!;
        Assert.Empty(metadata.GetFilters()); // metadata must stay uncompressed to be readable by non-PDF tools
        var intent = reopened.Catalog.GetArray("OutputIntents")!.GetDictionary(0)!;
        Assert.Equal("GTS_PDFA1", intent.GetName(PdfName.S)!.Value);
        var icc = intent.GetStream("DestOutputProfile")!.GetBytes();
        Assert.Equal("acsp", Encoding.ASCII.GetString(icc, 36, 4));
        Assert.Equal(icc.Length, (icc[0] << 24) | (icc[1] << 16) | (icc[2] << 8) | icc[3]);
        Assert.Empty(reopened.CheckPdfA(conformance));
    }

    [Fact]
    public void CheckerReportsViolations()
    {
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas())
        {
            c.DrawText("not embedded", PdfStandardFont.Helvetica, 12, 72, 700);
            c.SetOpacity(0.5).SetFillColor(PdfColor.Cmyk(0, 1, 0, 0)).Rectangle(0, 0, 10, 10).Fill();
        }
        doc.GetPage(1).AddAnnotation(new PdfDictionary
        {
            [PdfName.Subtype] = PdfName.Get("Square"),
            [PdfName.Rect] = PdfArray.FromNumbers(100, 100, 120, 120),
            [PdfName.F] = new PdfNumber(4),
        });
        var issues = PdfAChecker.Check(PdfDocument.Open(doc.ToArray()), PdfAConformance.PdfA1B);
        Assert.Contains(issues, i => i.Contains("not embedded"));
        Assert.Contains(issues, i => i.Contains("transparency"));
        Assert.Contains(issues, i => i.Contains("DeviceCMYK"));
        Assert.Contains(issues, i => i.Contains("without appearance"));
        Assert.Contains(issues, i => i.Contains("XMP"));
    }

    [Fact]
    public void PdfA1RejectsObjectStreamsAndEncryption()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        doc.Conformance = PdfAConformance.PdfA1B;
        Assert.Throws<PdfException>(() => doc.ToArray(new PdfSaveOptions { UseObjectStreams = true }));
        doc.Encrypt(new MiPdf.Security.PdfEncryptionOptions { UserPassword = "x" });
        Assert.Throws<PdfException>(() => doc.ToArray());
    }

    [Fact]
    public void PdfA1EmbeddedFontHasCidSet()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null) return;
        var doc = PdfDocument.Create();
        doc.Conformance = PdfAConformance.PdfA1B;
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("CIDSet", PdfTrueTypeFont.FromFile(path), 12, 72, 700);
        var reopened = PdfDocument.Open(doc.ToArray());
        var font = reopened.GetPage(1).Resources.GetDictionary(PdfName.Font)!.GetDictionary("F1")!;
        var descriptor = font.GetArray(PdfName.DescendantFonts)!.GetDictionary(0)!.GetDictionary(PdfName.FontDescriptor)!;
        Assert.NotNull(descriptor.GetStream("CIDSet"));
        Assert.Empty(reopened.CheckPdfA(PdfAConformance.PdfA1B));
    }
}
