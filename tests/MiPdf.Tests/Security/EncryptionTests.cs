using System.Text;
using MiPdf.Fonts;
using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.Security;

namespace MiPdf.Tests.Security;

public class EncryptionTests
{
    private static byte[] CreateEncrypted(PdfEncryptionAlgorithm algorithm, string user = "user", string owner = "owner",
        PdfPermissions permissions = PdfPermissions.Print, bool objectStreams = false)
    {
        var doc = PdfDocument.Create();
        doc.Info.Title = "Secret title";
        using (var c = doc.AddPage().GetCanvas()) c.DrawText("Secret content", PdfStandardFont.Helvetica, 12, 72, 700);
        doc.Encrypt(new PdfEncryptionOptions { UserPassword = user, OwnerPassword = owner, Algorithm = algorithm, Permissions = permissions });
        return doc.ToArray(new PdfSaveOptions { UseObjectStreams = objectStreams });
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256, false)]
    [InlineData(PdfEncryptionAlgorithm.Aes256, true)]
    [InlineData(PdfEncryptionAlgorithm.Aes128, false)]
    [InlineData(PdfEncryptionAlgorithm.Aes128, true)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128, false)]
    public void RoundTripWithUserAndOwnerPassword(PdfEncryptionAlgorithm algorithm, bool objectStreams)
    {
        var bytes = CreateEncrypted(algorithm, objectStreams: objectStreams);
        Assert.DoesNotContain("Secret", Encoding.Latin1.GetString(bytes));

        var asUser = PdfDocument.Open(bytes, "user");
        Assert.True(asUser.IsEncrypted);
        Assert.False(asUser.IsOwnerAccess);
        Assert.Equal(PdfPermissions.Print, asUser.Permissions);
        Assert.Equal("Secret title", asUser.Info.Title);
        Assert.Equal("Secret content", asUser.GetPage(1).ExtractText());

        var asOwner = PdfDocument.Open(bytes, "owner");
        Assert.True(asOwner.IsOwnerAccess);
        Assert.Equal("Secret content", asOwner.GetPage(1).ExtractText());
    }

    [Fact]
    public void WrongOrMissingPasswordThrows()
    {
        var bytes = CreateEncrypted(PdfEncryptionAlgorithm.Aes256);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes));
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, "wrong"));
    }

    [Fact]
    public void EmptyUserPasswordOpensWithoutPassword()
    {
        var bytes = CreateEncrypted(PdfEncryptionAlgorithm.Aes128, user: "", permissions: PdfPermissions.Print | PdfPermissions.CopyContents);
        var doc = PdfDocument.Open(bytes);
        Assert.False(doc.IsOwnerAccess);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.CopyContents, doc.Permissions);
        Assert.Throws<PdfException>(() => doc.RemoveEncryption());
    }

    [Fact]
    public void RemoveEncryptionWithOwnerPassword()
    {
        var doc = PdfDocument.Open(CreateEncrypted(PdfEncryptionAlgorithm.Aes256), "owner");
        doc.RemoveEncryption();
        var bytes = doc.ToArray();
        Assert.Contains("Secret content", PdfDocument.Open(bytes).GetPage(1).ExtractText());
        Assert.False(PdfDocument.Open(bytes).IsEncrypted);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void IncrementalUpdateKeepsEncryption(PdfEncryptionAlgorithm algorithm)
    {
        var original = CreateEncrypted(algorithm);
        var doc = PdfDocument.Open(original, "owner");
        doc.Info.Title = "Changed title";
        var updated = doc.ToArray(new PdfSaveOptions { Incremental = true });
        Assert.True(updated.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.DoesNotContain("Changed title", Encoding.Latin1.GetString(updated));
        var reopened = PdfDocument.Open(updated, "user");
        Assert.Equal("Changed title", reopened.Info.Title);
    }

    [Fact]
    public void ChangingEncryptionRequiresFullSave()
    {
        var doc = PdfDocument.Open(CreateEncrypted(PdfEncryptionAlgorithm.Aes128), "owner");
        doc.Encrypt(new PdfEncryptionOptions { UserPassword = "new", Algorithm = PdfEncryptionAlgorithm.Aes256 });
        Assert.Throws<PdfException>(() => doc.ToArray(new PdfSaveOptions { Incremental = true }));
        var bytes = doc.ToArray();
        Assert.Equal("Secret content", PdfDocument.Open(bytes, "new").GetPage(1).ExtractText());
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, "user"));
    }

    [Fact]
    public void EncryptDictionaryIsNotEncrypted()
    {
        var doc = PdfDocument.Open(CreateEncrypted(PdfEncryptionAlgorithm.Aes256), "user");
        var encrypt = doc.Trailer.GetDictionary(PdfName.Encrypt)!;
        Assert.Equal(6, encrypt.GetInt("R"));
        Assert.Equal(48, encrypt.GetString("U")!.Bytes.Length);
        Assert.Equal(32, encrypt.GetString("UE")!.Bytes.Length);
    }

    [Fact]
    public void Rc4TestVector() =>
        Assert.Equal(Convert.FromHexString("BBF316E8D940AF0AD3"), Rc4.Apply(Encoding.ASCII.GetBytes("Key"), Encoding.ASCII.GetBytes("Plaintext")));

    [Fact]
    public void UnicodePasswordsWithAes256()
    {
        var bytes = CreateEncrypted(PdfEncryptionAlgorithm.Aes256, user: "Pässwört€", owner: "Владелец");
        Assert.Equal("Secret content", PdfDocument.Open(bytes, "Pässwört€").GetPage(1).ExtractText());
        Assert.True(PdfDocument.Open(bytes, "Владелец").IsOwnerAccess);
    }
}
