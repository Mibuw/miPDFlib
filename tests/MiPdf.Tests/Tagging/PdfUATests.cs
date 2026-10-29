using System.Text;
using MiPdf.Barcodes;
using MiPdf.Fonts;
using MiPdf.Images;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.PdfA;
using MiPdf.Tagging;

namespace MiPdf.Tests.Tagging;

public class PdfUATests
{
    private static List<PdfDictionary> Elements(PdfDocument doc)
    {
        var result = new List<PdfDictionary>();
        void Walk(PdfObject? node)
        {
            switch (node?.Resolve())
            {
                case PdfArray array:
                    foreach (var item in array) Walk(item);
                    break;
                case PdfDictionary d when d.ContainsKey(PdfName.S):
                    result.Add(d);
                    Walk(d.Get(PdfName.K));
                    break;
            }
        }
        Walk(doc.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.K));
        return result;
    }

    [Fact]
    public void LayoutDocumentProducesConformingTaggedPdf()
    {
        var fontPath = TestHelpers.FindSystemFont();
        if (fontPath == null) return;
        var font = PdfTrueTypeFont.FromFile(fontPath);
        var doc = PdfDocument.Create();
        doc.Info.Title = "Tagged";
        doc.Language = "de-AT";
        doc.UAConformance = PdfUAConformance.PdfUA1;
        using (var layout = new LayoutDocument(doc))
        {
            layout.Footer = ctx => ctx.Canvas.DrawText($"Seite {ctx.PageNumber}", font, 8, ctx.ContentArea.Right, 30, alignment: MiPdf.Graphics.TextAlignment.Right);
            layout.Add(new Paragraph("Überschrift", font, 18).AsHeading(1));
            layout.Add(new Paragraph("Text", font, 10));
            var table = new Table(1, 2) { Font = font, HeaderFont = font };
            table.AddHeaderCell("A");
            table.AddHeaderCell("B");
            for (int i = 0; i < 80; i++)
            {
                table.AddCell(i.ToString());
                table.AddCell("x");
            }
            layout.Add(table);
            var image = PdfImage.FromPixels(1, 1, new byte[] { 255, 0, 0 }, 3);
            layout.Add(new ImageElement(image, 20, 20) { AlternateText = "Logo" });
            layout.Add(new ImageElement(image, 5, 5) { IsDecorative = true });
            layout.Add(new BarcodeElement(QrCode.Create("https://example.com"), 50));
            layout.Add(new HorizontalLine());
        }
        doc.GetPage(1).AddLink(new PdfRectangle(10, 10, 50, 20), "https://example.com");

        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Empty(reopened.CheckPdfUA());
        Assert.True(reopened.IsTagged);
        Assert.True(reopened.PageCount >= 2);

        var elements = Elements(reopened);
        string Type(PdfDictionary d) => d.GetName(PdfName.S)!.Value;
        Assert.Equal("Document", Type(elements[0]));
        Assert.Equal("H1", Type(elements[1]));
        Assert.Single(elements, e => Type(e) == "Table"); // one table element across pages
        Assert.Equal(2, elements.Count(e => Type(e) == "TH")); // repeated header rows are artifacts
        Assert.Equal(160, elements.Count(e => Type(e) == "TD"));
        Assert.All(elements.Where(e => Type(e) == "TH"), th => Assert.Equal("Column", th.GetDictionary(PdfName.Get("A"))!.GetName(PdfName.Get("Scope"))!.Value));
        var figures = elements.Where(e => Type(e) == "Figure").ToList();
        Assert.Equal(new[] { "Logo", "https://example.com" }, figures.Select(f => f.GetText(PdfName.Get("Alt"))));
        var link = Assert.Single(elements, e => Type(e) == "Link");
        Assert.Equal("OBJR", link.GetArray(PdfName.K)![0].Resolve() is PdfDictionary objr ? objr.GetName(PdfName.Type)!.Value : null);
        Assert.Equal("https://example.com", reopened.GetPage(1).Annotations[0].Contents);
        Assert.Equal("S", reopened.GetPage(1).Dictionary.GetName(PdfName.Get("Tabs"))!.Value);

        var xmp = Encoding.UTF8.GetString(reopened.GetXmpMetadata()!);
        Assert.Contains("<pdfuaid:part>1</pdfuaid:part>", xmp);
        Assert.Contains("<dc:title>", xmp);
        Assert.True(reopened.Catalog.GetDictionary(PdfName.Get("ViewerPreferences"))!.GetBool(PdfName.Get("DisplayDocTitle")));

        // the parent tree maps every page's MCIDs back to structure elements
        var parentTree = reopened.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("ParentTree"))!;
        var nums = parentTree.GetArray(PdfName.Get("Nums"))!;
        int key = reopened.GetPage(1).Dictionary.GetInt(PdfName.Get("StructParents"), -1);
        int index = Enumerable.Range(0, nums.Count / 2).First(i => nums.GetInt(2 * i) == key);
        Assert.True(nums.Get(2 * index + 1) is PdfArray { Count: > 3 });
    }

    [Fact]
    public void CheckerFindsViolations()
    {
        var doc = PdfDocument.Create();
        doc.UAConformance = PdfUAConformance.PdfUA1;
        var tree = doc.StructureTree!;
        var page = doc.AddPage();
        using (var canvas = page.GetCanvas())
        {
            canvas.DrawText("untagged", PdfStandardFont.Helvetica, 12, 72, 700);
            canvas.BeginTag(tree.Root.AddChild(StructureType.Figure)).Rectangle(0, 0, 10, 10).Fill().EndTag();
            canvas.BeginTag(tree.Root.AddChild("H3")).EndTag();
            canvas.BeginTag(tree.Root.AddChild("Fancy")).EndTag();
            canvas.BeginArtifact().Rectangle(0, 0, 5, 5).Fill().EndArtifact();
        }
        var issues = PdfDocument.Open(doc.ToArray()).CheckPdfUA();
        Assert.Contains(issues, i => i.Contains("1 content operation(s) are neither tagged"));
        Assert.Contains(issues, i => i.Contains("Helvetica is not embedded"));
        Assert.Contains(issues, i => i.Contains("Figure element without alternate text"));
        Assert.Contains(issues, i => i.Contains("Heading level skipped"));
        Assert.Contains(issues, i => i.Contains("'Fancy'"));
        Assert.Contains(issues, i => i.Contains("title is missing"));
        Assert.Contains(issues, i => i.Contains("/Lang"));

        tree.MapRole("Fancy", "P");
        var mapped = PdfDocument.Open(doc.ToArray()).CheckPdfUA();
        Assert.DoesNotContain(mapped, i => i.Contains("'Fancy'"));
    }

    [Fact]
    public void ManualTaggingAndContinuationAfterReopen()
    {
        var doc = PdfDocument.Create();
        var tree = doc.EnableTagging();
        var page = doc.AddPage();
        var p = tree.Root.AddChild(StructureType.Paragraph);
        using (var canvas = page.GetCanvas())
            canvas.Tag(p, c => c.DrawText("one", PdfStandardFont.Helvetica, 12, 72, 700))
                  .Tag(p, c => c.DrawText("two", PdfStandardFont.Helvetica, 12, 72, 680));
        Assert.Contains("/P <</MCID 1>> BDC", Encoding.Latin1.GetString(page.GetContentBytes()));

        var reopened = PdfDocument.Open(doc.ToArray());
        var tree2 = reopened.EnableTagging();
        Assert.Equal("Document", tree2.Root.Type);
        using (var canvas = reopened.GetPage(1).GetCanvas())
            canvas.Tag(tree2.Root.AddChild(StructureType.Paragraph), c => c.DrawText("three", PdfStandardFont.Helvetica, 12, 72, 660));
        Assert.Contains("/P <</MCID 2>> BDC", Encoding.Latin1.GetString(reopened.GetPage(1).GetContentBytes()));
        var third = PdfDocument.Open(reopened.ToArray());
        Assert.Equal(3, Elements(third).Sum(e => e.GetArray(PdfName.K)?.Count(k => k is PdfNumber) ?? 0));
        Assert.Equal(0, PdfUAChecker.CountUntaggedContent(third.GetPage(1).GetContentBytes(), out _));
    }

    [Fact]
    public void PdfUAWithPdfADeclaresExtensionSchema()
    {
        var doc = PdfDocument.Create();
        doc.Info.Title = "Both";
        doc.UAConformance = PdfUAConformance.PdfUA1;
        doc.Conformance = PdfAConformance.PdfA2U;
        doc.AddPage();
        var xmp = Encoding.UTF8.GetString(PdfDocument.Open(doc.ToArray()).GetXmpMetadata()!);
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp);
        Assert.Contains("<pdfaSchema:prefix>pdfuaid</pdfaSchema:prefix>", xmp);
        System.Xml.Linq.XDocument.Parse(xmp[xmp.IndexOf("<x:xmpmeta", StringComparison.Ordinal)..(xmp.IndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12)]);
    }
}

public class PdfUASignatureTests : IClassFixture<MiPdf.Tests.Signatures.TestPki>
{
    private readonly MiPdf.Tests.Signatures.TestPki _pki;

    public PdfUASignatureTests(MiPdf.Tests.Signatures.TestPki pki) => _pki = pki;

    [Fact]
    public void SigningKeepsTaggedDocumentsAccessible()
    {
        var fontPath = TestHelpers.FindSystemFont();
        if (fontPath == null) return;
        var font = PdfTrueTypeFont.FromFile(fontPath);
        var doc = PdfDocument.Create();
        doc.Info.Title = "Signed";
        doc.Language = "de";
        doc.UAConformance = PdfUAConformance.PdfUA1;
        using (var layout = new LayoutDocument(doc)) layout.Add(new Paragraph("Vertrag", font, 12));
        var form = doc.GetOrCreateForm();
        form.AppearanceFont = font;
        var check = form.AddCheckBox(doc.GetPage(1), "ok", new PdfRectangle(50, 50, 64, 64), true);
        check.ToolTip = "Einverstanden";
        var pdf = doc.ToArray();
        Assert.Empty(PdfDocument.Open(pdf).CheckPdfUA());

        // check box appearance is vector graphics, no ZapfDingbats needed
        var widget = PdfDocument.Open(pdf).GetPage(1).Annotations.Single(a => a.Subtype == "Widget");
        var on = (PdfStream)widget.Dictionary.GetDictionary(PdfName.AP)!.GetDictionary(PdfName.N)!.Get(PdfName.Get("Yes"))!;
        Assert.DoesNotContain("Tf", Encoding.Latin1.GetString(on.GetBytes()));

        var options = new MiPdf.Signatures.PdfSignatureOptions { Rectangle = new PdfRectangle(300, 50, 500, 100) };
        options.Appearance.Font = font;
        var signed = MiPdf.Signatures.PdfSigner.Sign(pdf, _pki.Rsa, options);
        var reopened = PdfDocument.Open(signed);
        Assert.Empty(reopened.CheckPdfUA());
        var sigField = reopened.GetForm()!.GetField("Signature1")!;
        Assert.Equal("Digital signature of Max Mustermann", sigField.ToolTip);
        Assert.True(MiPdf.Signatures.PdfSignatureValidator.Validate(signed)[0].IsIntegrityValid);
    }
}
