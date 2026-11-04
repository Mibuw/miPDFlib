using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Images;
using MiPdf.IO;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.PdfA;
using MiPdf.Tagging;

namespace MiPdf.Tests.PdfA;

/// <summary>PDF/A-4 (ISO 19005-4) and PDF/UA-2 (ISO 14289-2), both based on PDF 2.0.</summary>
public class PdfA4AndUA2Tests
{
    private static string Xmp(PdfDocument doc) => Encoding.UTF8.GetString(doc.GetXmpMetadata()!);

    private static PdfDocument WithPage(PdfAConformance? conformance)
    {
        var doc = PdfDocument.Create();
        doc.Info.Title = "Archive";
        doc.Info.Author = "Author";
        doc.Conformance = conformance;
        using (var canvas = doc.AddPage().GetCanvas())
            canvas.SetFillColor(PdfColor.Rgb(0.2, 0.4, 0.6)).Rectangle(72, 600, 200, 100).Fill();
        return doc;
    }

    [Fact]
    public void PdfA4IsWrittenAsPdf20WithTheInformationInXmpOnly()
    {
        var bytes = WithPage(PdfAConformance.PdfA4).ToArray();
        Assert.StartsWith("%PDF-2.0", Encoding.ASCII.GetString(bytes, 0, 8));

        var reopened = PdfDocument.Open(bytes);
        Assert.False(reopened.Trailer.ContainsKey(PdfName.Info)); // PDF/A-4 allows no document information dictionary
        Assert.Null(reopened.Info.Dictionary);
        var xmp = Xmp(reopened);
        Assert.Contains("<pdfaid:part>4</pdfaid:part>", xmp);
        Assert.Contains("<pdfaid:rev>2020</pdfaid:rev>", xmp);
        Assert.DoesNotContain("pdfaid:conformance", xmp);
        Assert.DoesNotContain("pdfaExtension", xmp); // PDF/A-4 needs no extension schema descriptions

        // the information is read from the XMP metadata
        Assert.Equal("Archive", reopened.Info.Title);
        Assert.Equal("Author", reopened.Info.Author);
        Assert.Equal("miPDFlib", reopened.Info.Producer);
        Assert.NotNull(reopened.Info.CreationDate);

        var identification = reopened.GetPdfAIdentification()!;
        Assert.Equal("PDF/A-4", identification.ToString());
        Assert.Equal(PdfAConformance.PdfA4, identification.ToConformance());
        Assert.Empty(reopened.CheckPdfA(PdfAConformance.PdfA4));
    }

    [Fact]
    public void PdfA4FRequiresAnEmbeddedFile()
    {
        var doc = WithPage(PdfAConformance.PdfA4F);
        Assert.Contains(doc.CheckPdfA(PdfAConformance.PdfA4F), i => i.Contains("at least one embedded file"));
        doc.AddAttachment("data.xml", "<data/>"u8.ToArray(), "text/xml", "Data", AttachmentRelationship.Data);
        Assert.Empty(doc.CheckPdfA(PdfAConformance.PdfA4F));

        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Contains("<pdfaid:conformance>F</pdfaid:conformance>", Xmp(reopened));
        Assert.Equal(PdfAConformance.PdfA4F, reopened.GetPdfAIdentification()!.ToConformance());
        Assert.Equal("PDF/A-4f", reopened.GetPdfAIdentification()!.ToString());
        Assert.Contains(reopened.CheckPdfA(PdfAConformance.PdfA4), i => i.Contains("only embedded PDF/A files"));
    }

    [Fact]
    public void SavingAnOpenedPdfA4KeepsItWithoutInformationDictionary()
    {
        var original = WithPage(PdfAConformance.PdfA4).ToArray();
        foreach (bool incremental in new[] { false, true })
        {
            var doc = PdfDocument.Open(original);
            doc.Info.Title = "Changed";
            doc.Info.Subject = "New subject";
            var saved = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { Incremental = incremental }));
            Assert.False(saved.Trailer.ContainsKey(PdfName.Info));
            Assert.Equal("Changed", saved.Info.Title); // changes go into the XMP metadata
            Assert.Equal("New subject", saved.Info.Subject);
            Assert.Equal("Author", saved.Info.Author);
            Assert.Equal("2.0", saved.Version);
            Assert.Equal(PdfAConformance.PdfA4, saved.GetPdfAIdentification()!.ToConformance());
        }
    }

    [Fact]
    public void SavingAnOpenedPdfA2KeepsInformationAndXmpEqual()
    {
        var doc = PdfDocument.Open(WithPage(PdfAConformance.PdfA2B).ToArray());
        doc.Info.Title = "New title";
        doc.Info.Keywords = "a, b";
        var saved = PdfDocument.Open(doc.ToArray());
        Assert.Equal("New title", saved.Info.Dictionary!.GetText(PdfName.Title));
        var xmp = Xmp(saved);
        Assert.Contains("<rdf:li xml:lang=\"x-default\">New title</rdf:li>", xmp);
        Assert.Contains("<pdf:Keywords>a, b</pdf:Keywords>", xmp);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xmp, "<dc:title>"));
    }

    [Fact]
    public void InformationIsReadFromXmpOnlyWithoutDictionary()
    {
        var doc = WithPage(null);
        doc.SetXmpMetadata(XmpMetadata.Create(doc.Info, null));
        doc.Trailer.Remove(PdfName.Info);
        var reopened = PdfDocument.Open(doc.ToArray(new PdfSaveOptions { UpdateModificationDate = false }));
        Assert.Null(reopened.Info.Dictionary);
        Assert.Equal("Archive", reopened.Info.Title);

        reopened.Info.Remove(PdfName.Author); // the first change creates the dictionary from the XMP values
        Assert.Equal("Archive", reopened.Info.Dictionary!.GetText(PdfName.Title));
        Assert.Null(reopened.Info.Author);
    }

    [Fact]
    public void PdfUA2IsCombinedWithPdfA4Only()
    {
        var withA2 = WithPage(PdfAConformance.PdfA2B);
        withA2.UAConformance = PdfUAConformance.PdfUA2;
        Assert.Contains("only be combined with PDF/A-4", Assert.Throws<PdfException>(() => withA2.ToArray()).Message);

        var withUA1 = WithPage(PdfAConformance.PdfA4);
        withUA1.UAConformance = PdfUAConformance.PdfUA1;
        Assert.Contains("only be combined with PDF/UA-2", Assert.Throws<PdfException>(() => withUA1.ToArray()).Message);
    }

    [Fact]
    public void PdfUA2WritesThePdf20StructureNamespace()
    {
        var fontPath = TestHelpers.FindSystemFont();
        if (fontPath == null) return;
        var font = PdfTrueTypeFont.FromFile(fontPath);
        var doc = PdfDocument.Create();
        doc.Info.Title = "Accessible";
        doc.Language = "en";
        doc.Conformance = PdfAConformance.PdfA4;
        doc.UAConformance = PdfUAConformance.PdfUA2;
        using (var layout = new LayoutDocument(doc))
        {
            layout.Add(new Paragraph("Heading", font, 16).AsHeading(1));
            layout.Add(new Paragraph("Skipped level", font, 12).AsHeading(3)); // allowed in PDF/UA-2
            layout.Add(new ListElement(ListStyle.LowerRoman) { Font = font }.AddItems("one", "two"));
            layout.Add(new ListElement(ListStyle.Dash) { Font = font }.AddItems("x"));
            layout.Add(new ImageElement(PdfImage.FromPixels(1, 1, new byte[] { 255, 0, 0 }, 3), 20, 20) { AlternateText = "Logo" });
        }
        var form = doc.GetOrCreateForm();
        form.AppearanceFont = font;
        form.AddTextField(doc.GetPage(1), "name", new PdfRectangle(300, 100, 500, 120)).ToolTip = "Your name";
        Assert.Empty(doc.CheckPdfUA());

        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Empty(reopened.CheckPdfUA());
        Assert.Equal("2.0", reopened.Version);
        var xmp = Xmp(reopened);
        Assert.Contains("<pdfuaid:part>2</pdfuaid:part>", xmp);
        Assert.Contains("<pdfuaid:rev>2024</pdfuaid:rev>", xmp);

        var root = reopened.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var ns = root.GetArray(PdfName.Get("Namespaces"))!.GetDictionary(0)!;
        Assert.Equal("http://iso.org/pdf2/ssn", ns.GetText(PdfName.Get("NS")));
        var document = (PdfDictionary)root.Get(PdfName.K)!.Resolve()!;
        Assert.Same(ns, document.GetDictionary(PdfName.Get("NS")));

        var lists = new List<PdfDictionary>();
        void Walk(PdfObject? node)
        {
            switch (node?.Resolve())
            {
                case PdfArray array:
                    foreach (var item in array) Walk(item);
                    break;
                case PdfDictionary d when d.ContainsKey(PdfName.S):
                    Assert.Same(ns, d.GetDictionary(PdfName.Get("NS"))); // all types used are PDF 2.0 types
                    if (d.GetName(PdfName.S)!.Value == "L") lists.Add(d);
                    Walk(d.Get(PdfName.K));
                    break;
            }
        }
        Walk(document);
        Assert.Equal(new[] { "LowerRoman", "Unordered" },
            lists.Select(l => l.GetDictionary(PdfName.Get("A"))!.GetName(PdfName.Get("ListNumbering"))!.Value));
        var widget = reopened.GetPage(1).Annotations.Single(a => a.Subtype == "Widget");
        Assert.Equal("Your name", widget.Contents);
    }

    [Fact]
    public void CheckerKnowsTheStructureTypesOfEachPart()
    {
        IReadOnlyList<string> Check(PdfUAConformance ua, params string[] types)
        {
            var doc = PdfDocument.Create();
            doc.Info.Title = "Types";
            doc.Language = "en";
            if (ua == PdfUAConformance.PdfUA2) doc.Conformance = PdfAConformance.PdfA4;
            doc.UAConformance = ua;
            var tree = doc.EnableTagging();
            var page = doc.AddPage();
            using (var canvas = page.GetCanvas())
                foreach (var type in types)
                    canvas.BeginTag(tree.Root.AddChild(type, "x")).Rectangle(10, 10, 5, 5).Fill().EndTag();
            return PdfDocument.Open(doc.ToArray()).CheckPdfUA();
        }

        Assert.Empty(Check(PdfUAConformance.PdfUA2, StructureType.Strong, StructureType.FENote, StructureType.Title, StructureType.Aside, StructureType.Code, "H7"));
        Assert.Contains(Check(PdfUAConformance.PdfUA2, StructureType.Note), i => i.Contains("use FENote"));
        Assert.Contains(Check(PdfUAConformance.PdfUA1, StructureType.Strong), i => i.Contains("'Strong' is neither standard"));
        Assert.Empty(Check(PdfUAConformance.PdfUA1, StructureType.Note, StructureType.Code));
        Assert.Contains(Check(PdfUAConformance.PdfUA1, StructureType.H1, StructureType.H3), i => i.Contains("Heading level skipped"));
        Assert.Empty(Check(PdfUAConformance.PdfUA2, StructureType.H1, StructureType.H3));
    }

    [Fact]
    public void CheckerRequiresListNumberingInPdfUA2()
    {
        var doc = PdfDocument.Create();
        doc.Info.Title = "List";
        doc.Language = "en";
        doc.UAConformance = PdfUAConformance.PdfUA2;
        var list = doc.EnableTagging().Root.AddChild(StructureType.List);
        var item = list.AddChild(StructureType.ListItem);
        using (var canvas = doc.AddPage().GetCanvas())
        {
            canvas.BeginTag(item.AddChild(StructureType.Label)).Rectangle(10, 10, 5, 5).Fill().EndTag();
            canvas.BeginTag(item.AddChild(StructureType.ListBody)).Rectangle(20, 10, 5, 5).Fill().EndTag();
        }
        Assert.Contains(doc.CheckPdfUA(), i => i.Contains("ListNumbering"));
        list.SetAttribute("List", "ListNumbering", PdfName.Get("Disc"));
        Assert.Empty(doc.CheckPdfUA());
    }
}

public class PdfA4SignatureTests : IClassFixture<MiPdf.Tests.Signatures.TestPki>
{
    private readonly MiPdf.Tests.Signatures.TestPki _pki;

    public PdfA4SignatureTests(MiPdf.Tests.Signatures.TestPki pki) => _pki = pki;

    [Fact]
    public void SigningKeepsPdfA4AndTheMetadata()
    {
        var doc = PdfDocument.Create();
        doc.Info.Title = "Signed archive";
        doc.Language = "en";
        doc.Conformance = PdfAConformance.PdfA4;
        doc.UAConformance = PdfUAConformance.PdfUA2;
        using (var canvas = doc.AddPage().GetCanvas())
            canvas.BeginArtifact().Rectangle(72, 600, 200, 100).Fill().EndArtifact();
        var pdf = doc.ToArray();
        var xmp = PdfDocument.Open(pdf).GetXmpMetadata();

        var signed = MiPdf.Signatures.PdfSigner.Sign(pdf, _pki.Rsa, new MiPdf.Signatures.PdfSignatureOptions { Rectangle = new PdfRectangle(0, 0, 0, 0) });
        var reopened = PdfDocument.Open(signed);
        Assert.False(reopened.Trailer.ContainsKey(PdfName.Info));
        Assert.Equal(xmp, reopened.GetXmpMetadata()); // signing does not rewrite the metadata
        Assert.Empty(reopened.CheckPdfUA());
        Assert.True(MiPdf.Signatures.PdfSignatureValidator.Validate(signed).Single().IsIntegrityValid);
    }
}
