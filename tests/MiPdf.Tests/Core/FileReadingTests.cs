using System.Text;
using MiPdf.Fonts;
using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.Security;
using MiPdf.Signatures;
using MiPdf.Tests.Signatures;
using MiPdf.Text;

namespace MiPdf.Tests.Core;

/// <summary>
/// Documents read on demand from a file (<see cref="PdfOpenOptions.ReadFromFile"/>) behave like documents in memory.
/// Tiny windows force every parse to grow its window and the repair scan to work in many blocks.
/// </summary>
public sealed class FileReadingTests : IClassFixture<TestPki>, IDisposable
{
    private readonly TestPki _pki;
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mipdf-file-");

    public FileReadingTests(TestPki pki) => _pki = pki;

    public void Dispose() => _dir.Delete(recursive: true);

    private string Write(byte[] data, string name = "doc.pdf")
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] CreateDocument(bool objectStreams = false, bool encrypted = false)
    {
        var doc = PdfDocument.Create();
        for (int i = 1; i <= 3; i++)
        {
            var page = doc.AddPage();
            using (var c = page.GetCanvas())
                c.DrawText($"Page {i}: file reading test with some text", PdfStandardFont.Helvetica, 12, 72, 700);
            var image = new PdfStream();
            image.SetRawBytes(Enumerable.Range(0, 20_000).Select(k => (byte)(k * i)).ToArray());
            image[PdfName.Type] = PdfName.XObject;
            image[PdfName.Subtype] = PdfName.Get("Image");
            image[PdfName.Filter] = PdfName.Get("DCTDecode");
            page.Dictionary.GetDictionary(PdfName.Resources)![PdfName.XObject] = new PdfDictionary { [PdfName.Get("Im0")] = doc.AddObject(image) };
        }
        doc.Info.Title = "File reading";
        if (encrypted) doc.Encrypt(new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner" });
        return doc.ToArray(new PdfSaveOptions { UseObjectStreams = objectStreams });
    }

    private static byte[] Image(PdfDocument doc, int page) =>
        ((PdfStream)doc.GetPage(page).Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.XObject)!.Get(PdfName.Get("Im0"))!.Resolve()!).RawBytes;

    public static TheoryData<string> Kinds => new() { "classic", "object-streams", "preamble", "broken-xref", "no-xref", "wrong-length", "encrypted" };

    private static byte[] Sample(string kind)
    {
        switch (kind)
        {
            case "object-streams": return CreateDocument(objectStreams: true);
            case "encrypted": return CreateDocument(encrypted: true);
            case "preamble":
                // data before %PDF-, offsets counted from the header
                return Encoding.ASCII.GetBytes("PJL job control data\n").Concat(CreateDocument()).ToArray();
            case "broken-xref":
            {
                var text = Encoding.Latin1.GetString(CreateDocument());
                int at = text.IndexOf("1 0 obj", StringComparison.Ordinal);
                return Encoding.Latin1.GetBytes(text[..at] + "% garbage that shifts every object\n" + text[at..]);
            }
            case "no-xref":
                return Encoding.ASCII.GetBytes("%PDF-1.4\n" +
                    "1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n" +
                    "2 0 obj <</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 100]>> endobj\n" +
                    "3 0 obj <</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>> endobj\n" +
                    "4 0 obj <</Length 999>>\nstream\nBT /F1 12 Tf (x) Tj ET\nendstream\nendobj\n" +
                    "trailer <</Root 1 0 R>>\n%%EOF");
            case "wrong-length":
            {
                var text = Encoding.Latin1.GetString(CreateDocument());
                int at = text.IndexOf("/Length 20000", StringComparison.Ordinal);
                return Encoding.Latin1.GetBytes(text[..at] + "/Length 19000" + text[(at + 13)..]);
            }
            default: return CreateDocument();
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ReadsLikeADocumentInMemory(string kind)
    {
        var data = Sample(kind);
        string? password = kind == "encrypted" ? "user" : null;
        var path = Write(data);
        var expected = PdfDocument.Open(data, password);
        foreach (int? window in new int?[] { null, 7, 61 })
        {
            using var doc = PdfDocument.Open(PdfFileData.OpenFile(path, window), password);
            Assert.Equal(expected.WasRepaired, doc.WasRepaired);
            Assert.Equal(expected.PageCount, doc.PageCount);
            Assert.Equal(PdfTextExtractor.ExtractText(expected), PdfTextExtractor.ExtractText(doc));
            Assert.Equal(expected.Info.Title, doc.Info.Title);
            if (kind != "no-xref")
                for (int p = 1; p <= doc.PageCount; p++) Assert.Equal(Image(expected, p), Image(doc, p));

            // both saves read the unchanged data from the file
            foreach (bool incremental in new[] { false, true })
            {
                if (incremental && doc.WasRepaired) continue;
                var saved = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Incremental = incremental }), password);
                Assert.Equal(PdfTextExtractor.ExtractText(expected), PdfTextExtractor.ExtractText(saved));
                if (kind != "no-xref") Assert.Equal(Image(expected, 3), Image(saved, 3));
            }
        }
    }

    [Fact]
    public void LargeFilesAreReadFromTheFileAutomaticallyOnlyAboveTwoGigabytes()
    {
        var path = Write(CreateDocument());
        using var inMemory = PdfDocument.Open(path);
        Assert.Null(inMemory.Store.File.FilePath);
        using var fromFile = PdfDocument.Open(path, new PdfOpenOptions { ReadFromFile = true });
        Assert.Equal(path, fromFile.Store.File.FilePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavesOverItsOwnFile(bool incremental)
    {
        var original = CreateDocument();
        var path = Write(original);
        using (var doc = PdfDocument.Open(path, new PdfOpenOptions { ReadFromFile = true }))
        {
            doc.Info.Title = "Saved over itself";
            doc.Save(path, new PdfSaveOptions { Incremental = incremental });
            // the open document keeps reading the old data
            Assert.Equal(Image(PdfDocument.Open(original), 2), Image(doc, 2));
            doc.Save(Path.Combine(_dir.FullName, "copy.pdf"));
        }
        var saved = PdfDocument.Open(path);
        Assert.Equal("Saved over itself", saved.Info.Title);
        Assert.Equal(3, saved.PageCount);
        if (incremental) Assert.Equal(original, File.ReadAllBytes(path).AsSpan(0, original.Length).ToArray());
        Assert.Equal(new[] { "copy.pdf", "doc.pdf" }, _dir.GetFiles().Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        File.Delete(path); // the handle is closed
    }

    [Fact]
    public void ImportedPagesReadTheirDataFromTheOpenSource()
    {
        var path = Write(CreateDocument());
        using var source = PdfDocument.Open(path, new PdfOpenOptions { ReadFromFile = true });
        var target = PdfDocument.Create();
        target.ImportPages(source);
        var merged = PdfDocument.Open(target.ToArray());
        Assert.Equal(3, merged.PageCount);
        Assert.Equal(Image(source, 2), Image(merged, 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignsAndValidatesFiles(bool inPlace)
    {
        var original = CreateDocument();
        var input = Write(original);
        var output = inPlace ? input : Path.Combine(_dir.FullName, "signed.pdf");
        var result = PdfSigner.SignFile(input, output, _pki.Rsa, new PdfSignatureOptions { Reason = "File test" });
        Assert.Empty(result.Pdf);
        Assert.Equal("Signature1", result.FieldName);

        var signed = File.ReadAllBytes(output);
        Assert.Equal(original, signed.AsSpan(0, original.Length).ToArray());
        if (!inPlace) Assert.Equal(original, File.ReadAllBytes(input));
        foreach (var validation in new[] { PdfSignatureValidator.Validate(output), PdfSignatureValidator.Validate(signed) })
        {
            var signature = Assert.Single(validation);
            Assert.True(signature.IsIntegrityValid, string.Join("; ", signature.Errors));
            Assert.True(signature.CoversWholeDocument);
            Assert.Equal("File test", signature.Reason);
        }

        // a second signature on the signed file keeps the first one valid
        PdfSigner.SignFile(output, output, _pki.Rsa);
        Assert.All(PdfSignatureValidator.Validate(output), s => Assert.True(s.IsIntegrityValid, string.Join("; ", s.Errors)));
        Assert.Equal(2, PdfSignatureValidator.Validate(output).Count);
    }

    [Fact]
    public void SigningAFileWritesTheSameUpdateAsSigningBytes()
    {
        var original = CreateDocument();
        var path = Write(original);
        var options = new PdfSignatureOptions { SigningTime = DateTimeOffset.UtcNow };
        PdfSigner.SignFile(path, Path.Combine(_dir.FullName, "a.pdf"), _pki.Rsa, options);
        var fromBytes = PdfSigner.Sign(original, _pki.Rsa, options);
        var fromFile = File.ReadAllBytes(Path.Combine(_dir.FullName, "a.pdf"));
        // the same update (only the random second /ID and therefore the signature value differ)
        Assert.Equal(fromBytes.Length, fromFile.Length);
        long[] ByteRange(byte[] pdf) => PdfSignatureValidator.Validate(pdf).Single().SignatureDictionary!
            .GetArray(PdfName.ByteRange)!.Select(o => ((PdfNumber)o).LongValue).ToArray();
        Assert.Equal(ByteRange(fromBytes), ByteRange(fromFile));
    }
}
