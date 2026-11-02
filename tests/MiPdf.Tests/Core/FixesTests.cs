using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.IO;
using MiPdf.Redaction;
using MiPdf.Security;
using MiPdf.Signatures;
using MiPdf.Tests.Signatures;

namespace MiPdf.Tests.Core;

/// <summary>Regression tests for issues found while documenting the public API.</summary>
public class FixesTests : IClassFixture<TestPki>
{
    private readonly TestPki _pki;

    public FixesTests(TestPki pki) => _pki = pki;

    [Fact]
    public void EveryRedactMarkKeepsItsOverlayTextAlsoWithOptions()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        using (var c = page.GetCanvas())
        {
            c.DrawText("Name: Max Mustermann", PdfStandardFont.Helvetica, 12, 72, 700);
            c.DrawText("IBAN: DE02120300000000202051", PdfStandardFont.Helvetica, 12, 72, 650);
        }
        PdfRedactor.MarkForRedaction(page, new PdfRectangle(110, 695, 260, 715), "PERSON");
        PdfRedactor.MarkForRedaction(page, new PdfRectangle(105, 645, 300, 665), "ACCOUNT");
        PdfRedactor.ApplyRedactions(doc, new RedactionOptions { FillColor = PdfColor.Black });

        string text = PdfDocument.Open(doc.ToArray()).GetPage(1).ExtractText();
        Assert.Contains("PERSON", text);
        Assert.Contains("ACCOUNT", text);
        Assert.DoesNotContain("Mustermann", text);
        Assert.DoesNotContain("DE0212", text);
    }

    [Fact]
    public void OutlineColorsAreConvertedToRgb()
    {
        var doc = PdfDocument.Create();
        var item = doc.Outlines.AddChild("Chapter", doc.AddPage());
        item.Color = PdfColor.Cmyk(0, 1, 1, 0);
        Assert.Equal((1.0, 0.0, 0.0), (item.Color!.Value.C1, item.Color.Value.C2, item.Color.Value.C3));
        item.Color = PdfColor.Gray(0.5);
        Assert.Equal((0.5, 0.5, 0.5), (item.Color!.Value.C1, item.Color.Value.C2, item.Color.Value.C3));
    }

    [Fact]
    public void FieldLocksOfEncryptedDocumentsAreCheckedWithTheOpenPassword()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        form.AddTextField(page, "name", new PdfRectangle(72, 700, 300, 720), "Max Mustermann");
        form.AddTextField(page, "comment", new PdfRectangle(72, 640, 300, 660));
        form.AddSignatureField(page, "Sig1", new PdfRectangle(72, 500, 272, 560));
        doc.Encrypt(new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner" });
        var signed = PdfSigner.Sign(doc.ToArray(), _pki.Rsa,
            new PdfSignatureOptions { Password = "owner", FieldName = "Sig1", FieldLock = PdfFieldLock.Include("name") });

        var edit = PdfDocument.Open(signed, "owner");
        edit.GetForm()!.GetField("comment")!.Value = "approved";
        var filled = edit.ToArray(new PdfSaveOptions { Incremental = true });

        var result = Assert.Single(PdfSignatureValidator.Validate(PdfDocument.Open(filled, "owner")));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        Assert.Empty(result.ModifiedLockedFields);   // used to report "(the signed revision cannot be read)"
    }
}
