using MiPdf.Forms;
using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.Signatures;

namespace MiPdf.Tests.Signatures;

public class FieldLockTests : IClassFixture<TestPki>
{
    private readonly TestPki _pki;

    public FieldLockTests(TestPki pki) => _pki = pki;

    private static byte[] CreateForm(Action<PdfSignatureField>? configure = null)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        form.AddTextField(page, "name", new PdfRectangle(72, 700, 300, 720), "Max Mustermann");
        form.AddTextField(page, "date", new PdfRectangle(72, 670, 300, 690), "2026-10-29");
        form.AddTextField(page, "comment", new PdfRectangle(72, 640, 300, 660));
        var signature = form.AddSignatureField(page, "Sig1", new PdfRectangle(72, 500, 272, 560));
        configure?.Invoke(signature);
        return doc.ToArray();
    }

    private static byte[] Fill(byte[] pdf, string field, string value)
    {
        var doc = PdfDocument.Open(pdf);
        doc.GetForm()!.GetField(field)!.Value = value;
        return doc.ToArray(new PdfSaveOptions { Incremental = true });
    }

    private static PdfDictionary FieldMdpReference(byte[] signed)
    {
        var sig = PdfDocument.Open(signed).GetForm()!.GetField<PdfSignatureField>("Sig1")!.SignatureDictionary!;
        return sig.GetArray(PdfName.Get("Reference"))!.Select(o => o.Resolve()).OfType<PdfDictionary>()
            .Single(r => r.GetName(PdfName.Get("TransformMethod"))?.Value == "FieldMDP");
    }

    [Fact]
    public void SignatureLocksTheIncludedFields()
    {
        var signed = PdfSigner.Sign(CreateForm(), _pki.Rsa,
            new PdfSignatureOptions { FieldName = "Sig1", FieldLock = PdfFieldLock.Include("name", "date") });

        // /Lock on the field (indirect) and the FieldMDP reference in the signature dictionary
        var field = PdfDocument.Open(signed).GetForm()!.GetField<PdfSignatureField>("Sig1")!;
        Assert.IsType<PdfReference>(field.Dictionary[PdfName.Get("Lock")]);
        Assert.True(field.Dictionary.GetDictionary(PdfName.Get("Lock"))!.IsType(PdfName.Get("SigFieldLock")));
        Assert.Equal(FieldLockAction.Include, field.Lock!.Action);
        var reference = FieldMdpReference(signed);
        Assert.IsType<PdfReference>(reference[PdfName.Get("Data")]);
        var transform = reference.GetDictionary(PdfName.Get("TransformParams"))!;
        Assert.Equal("Include", transform.GetName(PdfName.Get("Action"))!.Value);
        Assert.Equal("1.2", transform.GetName(PdfName.V)!.Value);
        Assert.Equal(new[] { "name", "date" }, transform.GetArray(PdfName.Get("Fields"))!.Select(o => ((PdfString)o.Resolve()).Text));

        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.Errors));
        Assert.Equal(new[] { "name", "date" }, result.FieldLock!.Fields);
        Assert.Empty(result.ModifiedLockedFields);

        // filling an unlocked field is fine, changing a locked one is reported
        var filled = Fill(signed, "comment", "approved");
        Assert.Empty(Assert.Single(PdfSignatureValidator.Validate(filled)).ModifiedLockedFields);
        var tampered = Fill(filled, "name", "Erika Musterfrau");
        var after = Assert.Single(PdfSignatureValidator.Validate(tampered));
        Assert.True(after.IsIntegrityValid);
        Assert.False(after.CoversWholeDocument);
        Assert.Equal(new[] { "name" }, after.ModifiedLockedFields);
    }

    [Fact]
    public void LockDefinedOnTheFieldIsAppliedWhenSigning()
    {
        var pdf = CreateForm(sig => sig.Lock = PdfFieldLock.Exclude("comment"));
        Assert.Equal(FieldLockAction.Exclude, PdfDocument.Open(pdf).GetForm()!.GetField<PdfSignatureField>("Sig1")!.Lock!.Action);

        var signed = PdfSigner.Sign(pdf, _pki.Rsa, new PdfSignatureOptions { FieldName = "Sig1" });
        Assert.Equal("Exclude", FieldMdpReference(signed).GetDictionary(PdfName.Get("TransformParams"))!.GetName(PdfName.Get("Action"))!.Value);

        Assert.Empty(Assert.Single(PdfSignatureValidator.Validate(Fill(signed, "comment", "ok"))).ModifiedLockedFields);
        Assert.Equal(new[] { "date" }, Assert.Single(PdfSignatureValidator.Validate(Fill(signed, "date", "2027-01-01"))).ModifiedLockedFields);
    }

    [Fact]
    public void FieldLockCanBeCombinedWithCertification()
    {
        var signed = PdfSigner.Sign(CreateForm(), _pki.Rsa, new PdfSignatureOptions
        {
            FieldName = "Sig1",
            Certification = CertificationLevel.FormFilling,
            FieldLock = PdfFieldLock.All(),
        });
        var sig = PdfDocument.Open(signed).GetForm()!.GetField<PdfSignatureField>("Sig1")!.SignatureDictionary!;
        var methods = sig.GetArray(PdfName.Get("Reference"))!.Select(o => ((PdfDictionary)o.Resolve()).GetName(PdfName.Get("TransformMethod"))!.Value);
        Assert.Equal(new[] { "DocMDP", "FieldMDP" }, methods);
        Assert.Null(FieldMdpReference(signed).GetDictionary(PdfName.Get("TransformParams"))!.GetArray(PdfName.Get("Fields")));

        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.Equal(CertificationLevel.FormFilling, result.CertificationLevel);
        Assert.Equal(FieldLockAction.All, result.FieldLock!.Action);
        Assert.Equal(new[] { "comment" }, Assert.Single(PdfSignatureValidator.Validate(Fill(signed, "comment", "x"))).ModifiedLockedFields);
    }

    [Fact]
    public void DeferredSigningWritesTheFieldLock()
    {
        var prepared = PdfSigner.PrepareExternalSignature(CreateForm(),
            new PdfSignatureOptions { FieldName = "Sig1", FieldLock = PdfFieldLock.Include("name") }, _pki.Rsa);
        Assert.Equal("Include", FieldMdpReference(prepared.PreparedPdf).GetDictionary(PdfName.Get("TransformParams"))!
            .GetName(PdfName.Get("Action"))!.Value);
    }

    [Fact]
    public void InvalidLocksAreRejected()
    {
        Assert.Throws<PdfException>(() => PdfSigner.Sign(CreateForm(), _pki.Rsa,
            new PdfSignatureOptions { FieldName = "Sig1", FieldLock = PdfFieldLock.Include("name", "missing") }));
        Assert.Throws<ArgumentException>(() => PdfFieldLock.Include());

        var signed = PdfSigner.Sign(CreateForm(), _pki.Rsa, new PdfSignatureOptions { FieldName = "Sig1" });
        var field = PdfDocument.Open(signed).GetForm()!.GetField<PdfSignatureField>("Sig1")!;
        Assert.Null(field.Lock);
        Assert.Throws<InvalidOperationException>(() => field.Lock = PdfFieldLock.All());
        Assert.Null(Assert.Single(PdfSignatureValidator.Validate(signed)).FieldLock);
    }

    [Fact]
    public void LockMatchesFieldNames()
    {
        Assert.True(PdfFieldLock.All().Locks("anything"));
        Assert.True(PdfFieldLock.Include("a.b").Locks("a.b"));
        Assert.False(PdfFieldLock.Include("a.b").Locks("a"));
        Assert.False(PdfFieldLock.Exclude("x").Locks("x"));
        Assert.True(PdfFieldLock.Exclude("x").Locks("y"));
    }
}
