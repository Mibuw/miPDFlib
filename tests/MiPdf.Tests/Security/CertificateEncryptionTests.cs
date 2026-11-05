using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MiPdf.Fonts;
using MiPdf.IO;
using MiPdf.Security;

namespace MiPdf.Tests.Security;

public sealed class CertificateEncryptionTests : IDisposable
{
    private readonly X509Certificate2 _alice = CreateCertificate("CN=Alice");
    private readonly X509Certificate2 _bob = CreateCertificate("CN=Bob");
    private readonly X509Certificate2 _eve = CreateCertificate("CN=Eve");

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public void Dispose()
    {
        _alice.Dispose();
        _bob.Dispose();
        _eve.Dispose();
    }

    private byte[] CreateEncrypted(PdfEncryptionAlgorithm algorithm, PdfPermissions permissions = PdfPermissions.All, bool encryptMetadata = true)
    {
        var doc = PdfDocument.Create();
        doc.Info.Title = "Secret title";
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("Secret content", PdfStandardFont.Helvetica, 12, 72, 700);
        // the recipients' certificates without private keys, as they would come from a directory
        var options = new PdfEncryptionOptions { Algorithm = algorithm, Permissions = permissions, EncryptMetadata = encryptMetadata };
        options.Recipients.Add(new X509Certificate2(_alice.RawData));
        options.Recipients.Add(new X509Certificate2(_bob.RawData));
        doc.Encrypt(options);
        return doc.ToArray();
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256, 5)]
    [InlineData(PdfEncryptionAlgorithm.Aes128, 4)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128, 4)]
    public void EveryRecipientCanOpenTheDocument(PdfEncryptionAlgorithm algorithm, int version)
    {
        var bytes = CreateEncrypted(algorithm);
        Assert.DoesNotContain("Secret", Encoding.Latin1.GetString(bytes));
        Assert.Contains("/Adobe.PubSec", Encoding.Latin1.GetString(bytes));

        foreach (var certificate in new[] { _alice, _bob })
        {
            using var doc = PdfDocument.Open(bytes, new PdfOpenOptions { Certificate = certificate });
            Assert.True(doc.IsEncrypted);
            Assert.True(doc.IsCertificateEncrypted);
            Assert.True(doc.IsOwnerAccess);
            Assert.Equal(version, doc.EncryptionRevision);
            Assert.Equal("Secret title", doc.Info.Title);
            Assert.Equal("Secret content", doc.GetPage(1).ExtractText());
        }
    }

    [Fact]
    public void OthersCannotOpenTheDocument()
    {
        var bytes = CreateEncrypted(PdfEncryptionAlgorithm.Aes256);
        var withoutCertificate = Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes));
        Assert.Contains("certificate", withoutCertificate.Message);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, new PdfOpenOptions { Certificate = _eve }));
        // a certificate without its private key cannot decrypt
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, new PdfOpenOptions { Certificate = new X509Certificate2(_alice.RawData) }));
    }

    [Fact]
    public void PermissionsApplyToTheRecipients()
    {
        var bytes = CreateEncrypted(PdfEncryptionAlgorithm.Aes256, PdfPermissions.Print | PdfPermissions.CopyContents);
        using var doc = PdfDocument.Open(bytes, new PdfOpenOptions { Certificate = _bob });
        Assert.Equal(PdfPermissions.Print | PdfPermissions.CopyContents, doc.Permissions);
        Assert.False(doc.IsOwnerAccess);
        Assert.Throws<PdfException>(() => doc.RemoveEncryption());
    }

    [Fact]
    public void UnencryptedMetadataAndIncrementalUpdates()
    {
        var bytes = CreateEncrypted(PdfEncryptionAlgorithm.Aes128, encryptMetadata: false);
        byte[] updated;
        using (var doc = PdfDocument.Open(bytes, new PdfOpenOptions { Certificate = _alice }))
        {
            using (var c = doc.AddPage().GetCanvas()) c.DrawText("Second page", PdfStandardFont.Helvetica, 12, 72, 700);
            updated = doc.ToArray(new PdfSaveOptions { Incremental = true });
        }
        Assert.Equal(bytes, updated.AsSpan(0, bytes.Length).ToArray());
        using var reopened = PdfDocument.Open(updated, new PdfOpenOptions { Certificate = _bob });
        Assert.Equal("Second page", reopened.GetPage(2).ExtractText());
        Assert.Equal("Secret content", reopened.GetPage(1).ExtractText());
    }

    [Fact]
    public void PasswordsAndRecipientsCannotBeCombined()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        var options = new PdfEncryptionOptions { UserPassword = "user" };
        options.Recipients.Add(_alice);
        Assert.Throws<ArgumentException>(() => doc.Encrypt(options));
    }

    [Fact]
    public void OpenedFromStreamAndFile()
    {
        var bytes = CreateEncrypted(PdfEncryptionAlgorithm.Aes256);
        using (var doc = PdfDocument.Open(new MemoryStream(bytes), new PdfOpenOptions { Certificate = _alice }))
            Assert.Equal("Secret content", doc.GetPage(1).ExtractText());
        var path = Path.Combine(Path.GetTempPath(), $"mipdf-cert-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, bytes);
            using var doc = PdfDocument.Open(path, new PdfOpenOptions { Certificate = _alice, ReadFromFile = true });
            Assert.Equal("Secret content", doc.GetPage(1).ExtractText());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
