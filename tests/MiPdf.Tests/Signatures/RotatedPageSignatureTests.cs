using MiPdf.Forms;
using MiPdf.Objects;
using MiPdf.Signatures;

namespace MiPdf.Tests.Signatures;

public class RotatedPageSignatureTests : IClassFixture<TestPki>
{
    private readonly TestPki _pki;

    public RotatedPageSignatureTests(TestPki pki) => _pki = pki;

    private static byte[] CreatePage(int rotation)
    {
        var doc = PdfDocument.Create();
        doc.AddPage().Rotation = rotation;
        return doc.ToArray();
    }

    private PdfStream SignAndGetAppearance(byte[] pdf, bool keepUpright = true)
    {
        // tall in page coordinates: shown 200 wide and 60 high on a page rotated by 90 degrees
        var signed = PdfSigner.Sign(pdf, _pki.Rsa, new PdfSignatureOptions
        {
            FieldName = "Sig1",
            Rectangle = new PdfRectangle(100, 100, 160, 300),
            Appearance = { KeepUpright = keepUpright },
        });
        Assert.True(Assert.Single(PdfSignatureValidator.Validate(signed)).IsIntegrityValid);
        var widget = PdfDocument.Open(signed).GetForm()!.GetField<PdfSignatureField>("Sig1")!.Widgets[0];
        return widget.Dictionary.GetDictionary(PdfName.AP)!.GetStream(PdfName.N)!;
    }

    [Theory]
    [InlineData(90, new double[] { 0, 1, -1, 0, 0, 0 }, 200, 60)]
    [InlineData(180, new double[] { -1, 0, 0, -1, 0, 0 }, 60, 200)]
    [InlineData(270, new double[] { 0, -1, 1, 0, 0, 0 }, 200, 60)]
    public void AppearanceIsUprightOnRotatedPages(int rotation, double[] matrix, double width, double height)
    {
        var ap = SignAndGetAppearance(CreatePage(rotation));
        Assert.Equal(matrix, ap.GetArray(PdfName.Get("Matrix"))!.ToDoubleArray());
        Assert.Equal(new double[] { 0, 0, width, height }, ap.GetArray(PdfName.BBox)!.ToDoubleArray());
    }

    [Fact]
    public void UnrotatedPagesAndOptOutKeepThePageOrientation()
    {
        foreach (var ap in new[] { SignAndGetAppearance(CreatePage(0)), SignAndGetAppearance(CreatePage(90), keepUpright: false) })
        {
            Assert.Null(ap.GetArray(PdfName.Get("Matrix")));
            Assert.Equal(new double[] { 0, 0, 60, 200 }, ap.GetArray(PdfName.BBox)!.ToDoubleArray());
        }
    }

    [Fact]
    public void RotationOfAnExistingFieldIsHonored()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        doc.GetOrCreateForm().AddSignatureField(page, "Sig1", new PdfRectangle(100, 100, 160, 300)).Widgets[0].Rotation = 270;
        var ap = SignAndGetAppearance(doc.ToArray());
        Assert.Equal(new double[] { 0, -1, 1, 0, 0, 0 }, ap.GetArray(PdfName.Get("Matrix"))!.ToDoubleArray());
    }
}
