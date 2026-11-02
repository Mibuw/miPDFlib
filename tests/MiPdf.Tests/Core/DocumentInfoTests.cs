using System.Text;
using MiPdf.IO;
using MiPdf.PdfA;
using MiPdf.Signatures;
using MiPdf.Tests.Signatures;

namespace MiPdf.Tests.Core;

public class DocumentInfoTests : IClassFixture<TestPki>
{
    private static readonly DateTimeOffset Created = new(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(1));

    private readonly TestPki _pki;

    public DocumentInfoTests(TestPki pki) => _pki = pki;

    /// <summary>A document as written by another producer.</summary>
    private static byte[] Foreign(string? producer = "Word Processor 2.0", bool xmp = false, bool attributeForm = false)
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        doc.Info.Producer = producer;
        doc.Info.CreationDate = Created;
        doc.Info.ModificationDate = Created;
        if (xmp)
        {
            string properties = attributeForm
                ? "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" " +
                  "xmp:ModifyDate=\"2020-01-02T03:04:05+01:00\" pdf:Producer=\"Word Processor 2.0\"/>"
                : "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">" +
                  "<xmp:ModifyDate>2020-01-02T03:04:05+01:00</xmp:ModifyDate><pdf:Producer>Word Processor 2.0</pdf:Producer></rdf:Description>";
            doc.SetXmpMetadata(Encoding.UTF8.GetBytes(
                "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
                "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" + properties + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>"));
        }
        return doc.ToArray(new PdfSaveOptions { UpdateModificationDate = false, UpdateProducer = false });
    }

    [Fact]
    public void SavingAnOpenedDocumentMarksTheProducerAndModificationDate()
    {
        var before = DateTimeOffset.Now.AddSeconds(-2);
        var saved = PdfDocument.Open(Foreign()).ToArray();
        var info = PdfDocument.Open(saved).Info;
        Assert.Equal("Word Processor 2.0; modified with miPDFlib", info.Producer);
        Assert.True(info.ModificationDate > before);
        Assert.Equal(Created, info.CreationDate);

        // saving again does not repeat the note
        Assert.Equal("Word Processor 2.0; modified with miPDFlib", PdfDocument.Open(PdfDocument.Open(saved).ToArray()).Info.Producer);
        Assert.Equal("miPDFlib", PdfDocument.Open(PdfDocument.Open(Foreign(producer: null)).ToArray()).Info.Producer);

        // incremental saves are marked as well
        var incremental = PdfDocument.Open(Foreign()).ToArray(new PdfSaveOptions { Incremental = true });
        Assert.Equal("Word Processor 2.0; modified with miPDFlib", PdfDocument.Open(incremental).Info.Producer);
    }

    [Fact]
    public void NewDocumentsAndDisabledOptions()
    {
        var created = PdfDocument.Create();
        created.AddPage();
        var info = PdfDocument.Open(created.ToArray()).Info;
        Assert.Equal("miPDFlib", info.Producer);
        Assert.NotNull(info.ModificationDate);

        var unchanged = PdfDocument.Open(PdfDocument.Open(Foreign()).ToArray(new PdfSaveOptions { UpdateModificationDate = false, UpdateProducer = false })).Info;
        Assert.Equal("Word Processor 2.0", unchanged.Producer);
        Assert.Equal(Created, unchanged.ModificationDate);
    }

    [Fact]
    public void SigningAndLtvKeepTheDocumentInformation()
    {
        var signed = PdfSigner.Sign(Foreign(), _pki.Rsa, new PdfSignatureOptions { FieldName = "Sig1" });
        var info = PdfDocument.Open(signed).Info;
        Assert.Equal("Word Processor 2.0", info.Producer);
        Assert.Equal(Created, info.ModificationDate);

        var ltv = PdfLtv.AddValidationData(signed, new LtvOptions { RevocationSource = new EmptyRevocationSource() });
        Assert.Equal("Word Processor 2.0", PdfDocument.Open(ltv.Pdf).Info.Producer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingXmpMetadataIsKeptInSync(bool attributeForm)
    {
        var saved = PdfDocument.Open(Foreign(xmp: true, attributeForm: attributeForm)).ToArray();
        var doc = PdfDocument.Open(saved);
        var xmp = Encoding.UTF8.GetString(doc.GetXmpMetadata()!);
        string date = doc.Info.ModificationDate!.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("Word Processor 2.0; modified with miPDFlib", xmp);
        Assert.Contains(date, xmp);
        Assert.Contains("MetadataDate", xmp);
        Assert.DoesNotContain("2020-01-02", xmp);
        Assert.StartsWith("<?xpacket begin=", xmp.TrimStart('﻿'));
        Assert.EndsWith("<?xpacket end=\"w\"?>", xmp);
    }

    [Fact]
    public void PdfADocumentsStayConsistent()
    {
        var pdfa = PdfDocument.Create();
        pdfa.AddPage();
        pdfa.Info.Producer = "Archive Writer";
        pdfa.Conformance = PdfAConformance.PdfA2B;
        var reopened = PdfDocument.Open(PdfDocument.Open(pdfa.ToArray()).ToArray());
        Assert.Equal(new PdfAIdentification(2, "B"), reopened.GetPdfAIdentification());
        var xmp = Encoding.UTF8.GetString(reopened.GetXmpMetadata()!);
        Assert.Contains("<pdf:Producer>Archive Writer; modified with miPDFlib</pdf:Producer>", xmp);
        Assert.Contains("<xmp:ModifyDate>" + reopened.Info.ModificationDate!.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture), xmp);
    }

    private sealed class EmptyRevocationSource : IRevocationSource
    {
        public IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> GetIssuers(
            System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => [];

        public byte[]? GetOcspResponse(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
            System.Security.Cryptography.X509Certificates.X509Certificate2 issuer) => null;

        public IReadOnlyList<byte[]> GetCrls(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => [];
    }
}
