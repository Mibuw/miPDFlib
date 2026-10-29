using System.Text;
using MiPdf.Fonts;
using MiPdf.Forms;
using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.Objects;
using MiPdf.PdfA;
using MiPdf.Security;
using MiPdf.Signatures;

namespace MiPdf.Tests.Manipulation;

/// <summary>Signature dictionary entries, XFA, image extraction, free space, push buttons, PDF/A identification, permissions.</summary>
public class AdditionalFeatureTests : IClassFixture<Signatures.TestPki>
{
    private readonly Signatures.TestPki _pki;

    public AdditionalFeatureTests(Signatures.TestPki pki) => _pki = pki;

    private static byte[] Png(int w, int h, bool alpha)
    {
        var pixels = new byte[w * h * (alpha ? 4 : 3)];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * (alpha ? 4 : 3);
            pixels[o] = (byte)(i * 7);
            pixels[o + 1] = 80;
            pixels[o + 2] = 200;
            if (alpha) pixels[o + 3] = (byte)(i % 2 == 0 ? 255 : 0);
        }
        return TestHelpers.CreatePng(w, h, pixels, alpha);
    }

    [Fact]
    public void CustomSignatureDictionaryEntries()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        var options = new PdfSignatureOptions { SignatureCreator = "MyApp" };
        options.SignatureDictionaryEntries["Biometrics"] = new PdfString("ZW5jcnlwdGVk");
        options.SignatureDictionaryEntries["MyAppData"] = new PdfString("100.5;200");
        var signed = PdfSigner.Sign(doc.ToArray(), _pki.Rsa, options);
        var result = Assert.Single(PdfSignatureValidator.Validate(signed));
        Assert.True(result.IsIntegrityValid);
        Assert.Equal("ZW5jcnlwdGVk", result.GetSignatureDictionaryText("Biometrics"));
        Assert.Equal("100.5;200", result.GetSignatureDictionaryText("MyAppData"));
        Assert.Equal("MyApp", result.SignatureDictionary!.GetDictionary(PdfName.Get("Prop_Build"))!.GetDictionary(PdfName.Get("App"))!.GetName(PdfName.Name)!.Value);
        options.SignatureDictionaryEntries["ByteRange"] = new PdfArray();
        Assert.Throws<PdfException>(() => PdfSigner.Sign(doc.ToArray(), _pki.Rsa, options));
    }

    [Fact]
    public void XfaDataCanBeReadAndReplaced()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        form.AddTextField(page, "name", new PdfRectangle(50, 700, 250, 720));
        PdfReference Packet(string xml) => doc.AddObject(new PdfStream(Encoding.UTF8.GetBytes(xml)));
        form.Dictionary[PdfName.Get("XFA")] = new PdfArray(
            new PdfString("preamble"), Packet("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">"),
            new PdfString("template"), Packet("<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"/>"),
            new PdfString("postamble"), Packet("</xdp:xdp>"));
        var reopened = PdfDocument.Open(doc.ToArray());
        var xfa = reopened.GetForm()!.Xfa;
        Assert.True(xfa.Exists);
        Assert.Null(xfa.GetData());

        xfa.FillData("<form1><Name>Erika Müller</Name><Amount>12.50</Amount></form1>");
        var third = PdfDocument.Open(reopened.ToArray()).GetForm()!.Xfa;
        Assert.Contains("<Name>Erika Müller</Name>", third.GetData());
        var packets = third.GetPackets();
        Assert.Equal(new[] { "preamble", "datasets", "template", "postamble" }, packets.Keys);

        third.FillData("<xfa:data xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><form1><Name>Neu</Name></form1></xfa:data>");
        Assert.Equal("<form1><Name>Neu</Name></form1>", third.GetData());
        Assert.Throws<PdfException>(() => third.FillData("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x>&e;</x>"));
        third.Remove();
        Assert.False(third.Exists);
    }

    [Fact]
    public void ImagesAreExtractedFromPagesAndSignatureAppearances()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var png = Png(6, 4, alpha: true);
        var jpeg = TestHelpers.CreateJpegHeader(30, 20, 3);
        using (var c = page.GetCanvas())
        {
            c.DrawImage(PdfImage.FromBytes(png), 100, 600, 60, 40);
            c.DrawImage(PdfImage.FromBytes(jpeg), 300, 600, 30, 20);
            c.WriteRaw("q 20 0 0 10 50 50 cm BI /W 2 /H 1 /BPC 8 /CS /G ID \u0010\u00F0\nEI Q");
        }
        var signed = PdfSigner.Sign(doc.ToArray(), _pki.Rsa, new PdfSignatureOptions
        {
            Rectangle = new PdfRectangle(350, 100, 500, 160),
            Appearance = new PdfSignatureAppearance { Image = PdfImage.FromBytes(Png(8, 8, alpha: true)), ImagePosition = SignatureImagePosition.ImageOnly },
        });
        var reopened = PdfDocument.Open(signed).GetPage(1);
        var images = reopened.ExtractImages();
        Assert.Equal(3, images.Count);
        Assert.Equal(new PdfRectangle(100, 600, 160, 640), images[0].Bounds);
        Assert.True(images[0].HasTransparency);
        var decoded = images[0].ToPng()!;
        Assert.Equal(0x89, decoded[0]);
        Assert.True(images[1].IsJpeg);
        Assert.Equal(jpeg, images[1].ToFile(out var ext));
        Assert.Equal(".jpg", ext);
        Assert.Null(images[1].ToPng());
        Assert.Equal(2, images[2].Width);       // inline image
        Assert.NotNull(images[2].ToPng(PdfColor.White));

        var widget = reopened.Annotations.Single(a => a.Subtype == "Widget");
        var signatureImage = Assert.Single(widget.ExtractImages());
        Assert.Equal(8, signatureImage.Width);
        // square image centered in the 150 x 60 widget (page coordinates)
        Assert.Equal(425, (signatureImage.Bounds.Left + signatureImage.Bounds.Right) / 2, 1);
        Assert.Equal(130, (signatureImage.Bounds.Bottom + signatureImage.Bounds.Top) / 2, 1);
        var opaque = signatureImage.ToPng(PdfColor.Rgb(1, 0, 0))!;
        Assert.Equal(2, opaque[25]); // IHDR color type 2 (RGB, alpha composited)
    }

    [Fact]
    public void FreeSpacesAvoidContent()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 600, 800));
        using (var c = page.GetCanvas())
        {
            c.DrawText("Text oben links", PdfStandardFont.Helvetica, 12, 50, 750);
            c.SetFillColor(PdfColor.Red).Rectangle(300, 300, 200, 200).Fill();
        }
        page.AddTextNote(560, 790, "x");
        var free = page.FindFreeSpaces(100, 100);
        Assert.NotEmpty(free);
        var text = page.FindText("Text")[0];
        foreach (var r in free)
        {
            Assert.False(r.Left < 500 && r.Right > 300 && r.Bottom < 500 && r.Top > 300, $"{r} overlaps the rectangle");
            Assert.False(r.Left < text.Right && r.Right > text.Left && r.Bottom < text.Top && r.Top > text.Bottom, $"{r} overlaps the text");
            Assert.True(r.Width >= 100 && r.Height >= 100);
        }
        Assert.Contains(free, r => r.Left == 0 && r.Bottom == 0 && r.Right == 600 && r.Top >= 299); // below the rectangle
    }

    [Fact]
    public void PushButtonsWithImagesAndCheckBoxStyles()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        var button = form.AddPushButton(page, "sig1", new PdfRectangle(50, 600, 250, 660), PdfImage.FromBytes(Png(10, 5, true)));
        button.ReadOnly = true;
        form.AddPushButton(page, "ok", new PdfRectangle(300, 600, 380, 630), caption: "OK", border: PdfColor.Black);
        form.AddCheckBox(page, "cross", new PdfRectangle(50, 500, 64, 514), true, style: CheckBoxStyle.Cross);
        var reopened = PdfDocument.Open(doc.ToArray());
        var f = reopened.GetForm()!;
        var b = f.GetField<PdfPushButton>("sig1")!;
        Assert.True(b.ReadOnly);
        Assert.Single(b.Widgets[0].ExtractImages());
        Assert.Equal(1, b.Widgets[0].Dictionary.GetDictionary(PdfName.MK)!.GetInt(PdfName.Get("TP")));
        b.SetImage(PdfImage.FromBytes(Png(20, 10, false)));
        Assert.Equal(20, b.Widgets[0].ExtractImages()[0].Width);
        Assert.Equal("OK", f.GetField<PdfPushButton>("ok")!.Caption);
        var cross = f.GetField<PdfCheckBox>("cross")!;
        Assert.True(cross.IsChecked);
        Assert.Equal("8", cross.Widgets[0].Dictionary.GetDictionary(PdfName.MK)!.GetText(PdfName.Get("CA")));
        f.Flatten();
        Assert.Single(PdfDocument.Open(reopened.ToArray()).GetPage(1).ExtractImages());
    }

    [Fact]
    public void PdfAIdentificationIsRead()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        doc.Conformance = PdfAConformance.PdfA2U;
        var id = PdfDocument.Open(doc.ToArray()).GetPdfAIdentification()!;
        Assert.Equal(2, id.Part);
        Assert.Equal("U", id.Conformance);
        Assert.Equal(PdfAConformance.PdfA2U, id.ToConformance());
        Assert.Equal("PDF/A-2u", id.ToString());

        var plain = PdfDocument.Create();
        plain.AddPage();
        Assert.Null(PdfDocument.Open(plain.ToArray()).GetPdfAIdentification());
        plain.SetXmpMetadata(Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"1\" pdfaid:conformance=\"A\"/></rdf:RDF></x:xmpmeta>"));
        var attr = PdfDocument.Open(plain.ToArray()).GetPdfAIdentification()!;
        Assert.Equal(new PdfAIdentification(1, "A"), attr);
        Assert.Null(attr.ToConformance());
    }

    [Fact]
    public void PermissionsCanBeIgnoredDeliberately()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionOptions { UserPassword = "", OwnerPassword = "geheim", Permissions = PdfPermissions.Print });
        var encrypted = doc.ToArray();
        var opened = PdfDocument.Open(encrypted);
        Assert.False(opened.IsOwnerAccess);
        Assert.Throws<PdfException>(() => opened.RemoveEncryption());
        opened.IgnorePermissions = true;
        opened.RemoveEncryption();
        var decrypted = PdfDocument.Open(opened.ToArray());
        Assert.False(decrypted.IsEncrypted);

        var again = PdfDocument.Open(encrypted);
        again.IgnorePermissions = true;
        again.Encrypt(new PdfEncryptionOptions { UserPassword = "", OwnerPassword = "neu", Permissions = PdfPermissions.All });
        Assert.Equal(PdfPermissions.All, PdfDocument.Open(again.ToArray()).Permissions);
    }
}
