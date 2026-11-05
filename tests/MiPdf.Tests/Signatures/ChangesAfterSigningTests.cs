using MiPdf.Fonts;
using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.Signatures;

namespace MiPdf.Tests.Signatures;

public class ChangesAfterSigningTests : IClassFixture<TestPki>
{
    private readonly TestPki _pki;

    public ChangesAfterSigningTests(TestPki pki) => _pki = pki;

    private static byte[] CreateForm()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        using (var canvas = page.GetCanvas()) canvas.DrawText("Signed content", PdfStandardFont.Helvetica, 12, 72, 750);
        var form = doc.GetOrCreateForm();
        form.AddTextField(page, "name", new PdfRectangle(72, 650, 300, 670));
        form.AddSignatureField(page, "Signature2", new PdfRectangle(300, 100, 500, 150));
        return doc.ToArray();
    }

    private byte[] Sign(byte[] pdf, CertificationLevel? level = null, string? field = null, PdfFieldLock? fieldLock = null) =>
        PdfSigner.Sign(pdf, _pki.Rsa, new PdfSignatureOptions { Certification = level, FieldName = field, FieldLock = fieldLock });

    private static byte[] Update(byte[] pdf, Action<PdfDocument> change)
    {
        using var doc = PdfDocument.Open(pdf);
        change(doc);
        return doc.ToArray(new PdfSaveOptions { Incremental = true });
    }

    private static void Fill(PdfDocument doc) => doc.GetForm()!.SetValue("name", "Alice");

    private static SignatureValidationResult First(byte[] pdf) =>
        PdfSignatureValidator.Validate(pdf).Single(r => r.FieldName == "Signature1");

    [Fact]
    public void NoChangesWhenTheSignatureCoversTheWholeDocument()
    {
        var result = First(Sign(CreateForm()));
        Assert.Empty(result.ChangesAfterSigning);
        Assert.False(result.HasDisallowedChanges);
    }

    [Fact]
    public void FormFillingAndSigningAfterAnApprovalSignatureArePermitted()
    {
        var signed = Sign(Update(Sign(CreateForm()), Fill), field: "Signature2");
        var result = First(signed);
        Assert.Contains(result.ChangesAfterSigning, c => c.Kind == DocumentChangeKind.FormFill && c.Description == "field 'name': value changed");
        Assert.Contains(result.ChangesAfterSigning, c => c.Kind == DocumentChangeKind.Signature && c.Description == "field 'Signature2' signed");
        Assert.All(result.ChangesAfterSigning, c => Assert.True(c.IsPermitted, c.ToString()));
        Assert.False(result.HasDisallowedChanges);
        Assert.True(result.IsIntegrityValid);
    }

    [Fact]
    public void PageContentChangedAfterSigningIsNotPermitted()
    {
        var changed = Update(Sign(CreateForm()), doc =>
        {
            using var canvas = doc.GetPage(1).GetCanvas();
            canvas.DrawText("Added later", PdfStandardFont.Helvetica, 12, 72, 500);
        });
        var result = First(changed);
        var change = Assert.Single(result.ChangesAfterSigning, c => c.Kind == DocumentChangeKind.PageContent);
        Assert.Equal("page 1: content changed", change.Description);
        Assert.False(change.IsPermitted);
        Assert.True(result.HasDisallowedChanges);
        Assert.True(result.IsIntegrityValid); // the signed bytes themselves are intact
    }

    [Fact]
    public void AddedPagesAreNotPermitted()
    {
        var result = First(Update(Sign(CreateForm()), doc => doc.AddPage()));
        Assert.Contains(result.ChangesAfterSigning, c => c.Kind == DocumentChangeKind.Pages && !c.IsPermitted);
    }

    [Fact]
    public void AnnotationsDependOnTheCertificationLevel()
    {
        static void Annotate(PdfDocument doc) => doc.GetPage(1).AddTextNote(100, 700, "a comment");

        var approval = First(Update(Sign(CreateForm()), Annotate));
        var formFilling = First(Update(Sign(CreateForm(), CertificationLevel.FormFilling), Annotate));
        var annotations = First(Update(Sign(CreateForm(), CertificationLevel.FormFillingAndAnnotations), Annotate));

        Assert.True(Assert.Single(approval.ChangesAfterSigning).IsPermitted);
        var notPermitted = Assert.Single(formFilling.ChangesAfterSigning);
        Assert.Equal(DocumentChangeKind.Annotation, notPermitted.Kind);
        Assert.Equal("page 1: Text annotation added", notPermitted.Description);
        Assert.False(notPermitted.IsPermitted);
        Assert.True(Assert.Single(annotations.ChangesAfterSigning).IsPermitted);
    }

    [Fact]
    public void FormFillingIsPermittedForCertificationLevelTwoOnly()
    {
        var levelTwo = First(Sign(Update(Sign(CreateForm(), CertificationLevel.FormFilling), Fill), field: "Signature2"));
        Assert.False(levelTwo.HasDisallowedChanges, string.Join("; ", levelTwo.ChangesAfterSigning));
        Assert.Equal(CertificationLevel.FormFilling, levelTwo.CertificationLevel);

        var levelOne = First(Update(Sign(CreateForm(), CertificationLevel.NoChanges), Fill));
        Assert.Contains(levelOne.ChangesAfterSigning, c => c.Kind == DocumentChangeKind.FormFill && !c.IsPermitted);
        Assert.True(levelOne.HasDisallowedChanges);
    }

    [Fact]
    public void DocumentTimestampsArePermittedEvenWithoutChangesAllowed()
    {
        var stamped = PdfSigner.AddDocumentTimestamp(Sign(CreateForm(), CertificationLevel.NoChanges), new FakeTsa(_pki));
        var result = First(stamped);
        var change = Assert.Single(result.ChangesAfterSigning);
        Assert.Equal(DocumentChangeKind.DocumentTimestamp, change.Kind);
        Assert.True(change.IsPermitted);
        Assert.False(result.HasDisallowedChanges);
    }

    [Fact]
    public void ChangedLockedFieldsAreNotPermitted()
    {
        var signed = Sign(CreateForm(), fieldLock: PdfFieldLock.Include("name"));
        var result = First(Update(signed, Fill));
        var change = Assert.Single(result.ChangesAfterSigning);
        Assert.Equal(DocumentChangeKind.FormFill, change.Kind);
        Assert.False(change.IsPermitted);
        Assert.Equal(new[] { "name" }, result.ModifiedLockedFields);
        Assert.True(result.HasDisallowedChanges);
    }

    [Fact]
    public void ChangedDocumentLevelEntriesAreReported()
    {
        var result = First(Update(Sign(CreateForm()), doc => doc.Catalog[PdfName.Get("OpenAction")] =
            new PdfDictionary { [PdfName.S] = PdfName.Get("JavaScript"), [PdfName.Get("JS")] = new PdfString("app.alert(1)") }));
        var change = Assert.Single(result.ChangesAfterSigning);
        Assert.Equal(DocumentChangeKind.Other, change.Kind);
        Assert.Equal("document catalog: /OpenAction added", change.Description);
        Assert.False(change.IsPermitted);
    }
}
