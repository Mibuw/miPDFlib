using System.Text;
using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf.Tests.Core;

public class DocumentRoundTripTests
{
    private static PdfDocument CreateSample(int pages = 3)
    {
        var doc = PdfDocument.Create();
        doc.Info.Title = "Round trip – Prüfung";
        doc.Info.Author = "miPDFlib";
        for (int i = 0; i < pages; i++)
        {
            var page = doc.AddPage(i % 2 == 0 ? PageSize.A4 : PageSize.Landscape(PageSize.A4));
            page.SetContent(Encoding.ASCII.GetBytes($"0 0 1 rg 10 10 {50 + i * 10} 50 re f % page {i + 1}"));
        }
        return doc;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateSaveAndReopen(bool objectStreams)
    {
        var bytes = CreateSample().ToArray(new PdfSaveOptions { UseObjectStreams = objectStreams });
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));

        var doc = PdfDocument.Open(bytes);
        Assert.False(doc.WasRepaired);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal("Round trip – Prüfung", doc.Info.Title);
        Assert.Equal(PageSize.A4, doc.GetPage(1).MediaBox);
        Assert.Equal(842, doc.GetPage(2).Width);
        Assert.Contains("page 2", Encoding.ASCII.GetString(doc.GetPage(2).GetContentBytes()));
    }

    [Fact]
    public void ObjectStreamsProduceSmallerFiles()
    {
        var classic = CreateSample(30).ToArray();
        var compact = CreateSample(30).ToArray(new PdfSaveOptions { UseObjectStreams = true });
        Assert.True(compact.Length < classic.Length, $"{compact.Length} >= {classic.Length}");
    }

    [Fact]
    public void ResaveOfOpenedDocumentKeepsContent()
    {
        var first = CreateSample().ToArray(new PdfSaveOptions { UseObjectStreams = true });
        var second = PdfDocument.Open(first).ToArray();
        var third = PdfDocument.Open(second).ToArray(new PdfSaveOptions { UseObjectStreams = true });
        var doc = PdfDocument.Open(third);
        Assert.Equal(3, doc.PageCount);
        Assert.Contains("page 3", Encoding.ASCII.GetString(doc.GetPage(3).GetContentBytes()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncrementalUpdateAppendsOnlyChanges(bool sourceUsesObjectStreams)
    {
        var original = CreateSample().ToArray(new PdfSaveOptions { UseObjectStreams = sourceUsesObjectStreams });
        var doc = PdfDocument.Open(original);
        doc.Info.Title = "Changed";
        doc.GetPage(1).Rotation = 90;
        var updated = doc.ToArray(new PdfSaveOptions { Incremental = true });

        Assert.True(updated.AsSpan(0, original.Length).SequenceEqual(original), "original bytes must be preserved");
        var appended = Encoding.Latin1.GetString(updated, original.Length, updated.Length - original.Length);
        Assert.Contains("/Prev", appended);
        // only the info dictionary and page 1 have changed (+ xref stream object if applicable)
        int objCount = appended.Split(" obj\n").Length - 1;
        Assert.Equal(sourceUsesObjectStreams ? 3 : 2, objCount);

        var reopened = PdfDocument.Open(updated);
        Assert.Equal("Changed", reopened.Info.Title);
        Assert.Equal(90, reopened.GetPage(1).Rotation);
        Assert.Equal(0, reopened.GetPage(2).Rotation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataBeforeHeaderWithOffsetsRelativeToHeader(bool xrefStreams)
    {
        // PDF 2.0 7.5.2: the PDF data may start later in the file; offsets count from %PDF-
        var pdf = CreateSample().ToArray(new PdfSaveOptions { UseObjectStreams = xrefStreams, Version = "2.0" });
        var preamble = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("# printer control data\r\n", 20)));
        var file = preamble.Concat(pdf).ToArray();

        var doc = PdfDocument.Open(file);
        Assert.False(doc.WasRepaired);
        Assert.Equal("2.0", doc.Version);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal("Round trip – Prüfung", doc.Info.Title);

        doc.Info.Title = "changed";
        var updated = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Incremental = true }));
        Assert.False(updated.WasRepaired);
        Assert.Equal("changed", updated.Info.Title);

        // signing keeps the PDF data byte for byte and drops the foreign preamble
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var certificate = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Preamble", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var signed = MiPdf.Signatures.PdfSigner.Sign(file, certificate);
        Assert.True(signed.AsSpan(0, pdf.Length).SequenceEqual(pdf));
        Assert.True(Assert.Single(MiPdf.Signatures.PdfSignatureValidator.Validate(signed)).IsIntegrityValid);
    }

    [Fact]
    public void IncrementalUpdateWithoutChangesAppendsNoObjects()
    {
        var original = CreateSample().ToArray();
        var doc = PdfDocument.Open(original);
        _ = doc.GetPage(2).GetContentBytes(); // loading objects is not a modification
        var updated = doc.ToArray(new PdfSaveOptions { Incremental = true });
        var appended = Encoding.Latin1.GetString(updated, original.Length, updated.Length - original.Length);
        Assert.DoesNotContain(" obj\n", appended);
    }

    [Fact]
    public void PageOperations()
    {
        var doc = PdfDocument.Open(CreateSample(4).ToArray());
        doc.MovePage(3, 0);
        doc.RemovePage(1);
        doc.InsertPage(1, PageSize.A5);
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Equal(4, reopened.PageCount);
        Assert.Contains("page 4", Encoding.ASCII.GetString(reopened.GetPage(1).GetContentBytes()));
        Assert.Equal(PageSize.A5, reopened.GetPage(2).MediaBox);
        Assert.Contains("page 2", Encoding.ASCII.GetString(reopened.GetPage(3).GetContentBytes()));
    }

    [Fact]
    public void InheritedAttributesAreResolved()
    {
        const string pdf = "%PDF-1.4\n" +
            "1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n" +
            "2 0 obj <</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 100]/Rotate 90/Resources<</Font<<>>>>>> endobj\n" +
            "3 0 obj <</Type/Pages/Parent 2 0 R/Kids[4 0 R]/Count 1>> endobj\n" +
            "4 0 obj <</Type/Page/Parent 3 0 R>> endobj\n" +
            "trailer <</Root 1 0 R>>\n%%EOF";
        var doc = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf));
        Assert.True(doc.WasRepaired); // no xref table at all
        var page = doc.GetPage(1);
        Assert.Equal(new PdfRectangle(0, 0, 200, 100), page.MediaBox);
        Assert.Equal(90, page.Rotation);
        Assert.Equal(100, page.Width);

        doc.AddPage(); // forces a page tree rebuild, inherited values must be kept
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Equal(90, reopened.GetPage(1).Rotation);
        Assert.Equal(new PdfRectangle(0, 0, 200, 100), reopened.GetPage(1).MediaBox);
        Assert.Equal(0, reopened.GetPage(2).Rotation);
    }

    [Fact]
    public void RepairsWrongXrefOffsets()
    {
        var bytes = CreateSample().ToArray();
        var text = Encoding.Latin1.GetString(bytes);
        // shift all objects by inserting garbage after the header -> every xref offset is wrong
        int insertAt = text.IndexOf("1 0 obj", StringComparison.Ordinal);
        var broken = text[..insertAt] + "% some garbage inserted by a broken tool\n" + text[insertAt..];
        var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(broken));
        Assert.True(doc.WasRepaired);
        Assert.Equal(3, doc.PageCount);
        Assert.Contains("page 3", Encoding.ASCII.GetString(doc.GetPage(3).GetContentBytes()));
    }

    [Fact]
    public void WrongStreamLengthIsCorrected()
    {
        const string pdf = "%PDF-1.4\n" +
            "1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n" +
            "2 0 obj <</Type/Pages/Kids[3 0 R]/Count 1>> endobj\n" +
            "3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 10 10]/Contents 4 0 R>> endobj\n" +
            "4 0 obj <</Length 999>>\nstream\nBT ET\nendstream\nendobj\n" +
            "trailer <</Root 1 0 R>>\n%%EOF";
        var doc = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf));
        Assert.Equal("BT ET", Encoding.ASCII.GetString(doc.GetPage(1).GetContentBytes()));
    }

    [Fact]
    public void DirectStreamsAreWrittenAsIndirectObjects()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        page.Dictionary[PdfName.Contents] = new PdfStream(Encoding.ASCII.GetBytes("q Q")); // direct (invalid) stream
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.IsType<PdfReference>(reopened.GetPage(1).Dictionary[PdfName.Contents]);
        Assert.Equal("q Q", Encoding.ASCII.GetString(reopened.GetPage(1).GetContentBytes()));
    }

    [Fact]
    public void ForeignReferencesAreRejected()
    {
        var a = PdfDocument.Create();
        var b = PdfDocument.Create();
        var foreign = b.AddObject(new PdfDictionary());
        a.AddPage().Dictionary["Foreign"] = foreign;
        Assert.Throws<PdfException>(() => a.ToArray());
    }
}
