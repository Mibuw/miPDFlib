using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.PdfA;
using MiPdf.Text;

namespace MiPdf.Tests.Manipulation;

public class LayerTests
{
    private static byte[] CreateWithLayers()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var notes = doc.AddLayer("Notes");
        var draft = doc.AddLayer("Draft", visible: false);
        using (var canvas = page.GetCanvas())
        {
            canvas.BeginText().SetFont(PdfStandardFont.Helvetica, 12).MoveText(72, 700);
            canvas.ShowText("Visible ");
            canvas.Layer(draft, c => c.ShowText("HIDDEN "));   // inside the same text object
            canvas.ShowText("end").EndText();
            canvas.Layer(notes, c => c.DrawText("A note", PdfStandardFont.Helvetica, 12, 72, 650));
        }
        return doc.ToArray();
    }

    [Fact]
    public void LayersAreCreatedWithTheirState()
    {
        var bytes = CreateWithLayers();
        var doc = PdfDocument.Open(bytes);
        Assert.Equal(new[] { "Notes", "Draft" }, doc.Layers.Select(l => l.Name));
        Assert.True(doc.Layers[0].IsVisible);
        Assert.False(doc.Layers[1].IsVisible);
        Assert.False(doc.Layers[0].IsLocked);
        Assert.Contains("/OC /OC", Encoding.Latin1.GetString(doc.GetPage(1).GetContentBytes()));
        var configuration = doc.Catalog.GetDictionary(PdfName.Get("OCProperties"))!.GetDictionary(PdfName.Get("D"))!;
        Assert.Equal(2, configuration.GetArray(PdfName.Get("Order"))!.Count);
        Assert.Equal("Default", configuration.GetText(PdfName.Name));
    }

    [Fact]
    public void LayersCanBeSwitchedAndLocked()
    {
        var doc = PdfDocument.Open(CreateWithLayers());
        doc.Layers[0].IsVisible = false;
        doc.Layers[1].IsVisible = true;
        doc.Layers[1].IsLocked = true;
        doc.Layers[1].Name = "Final";
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.False(reopened.Layers[0].IsVisible);
        Assert.True(reopened.Layers[1].IsVisible);
        Assert.True(reopened.Layers[1].IsLocked);
        Assert.Equal("Final", reopened.Layers[1].Name);

        // base state OFF: only groups listed in /ON are visible
        var configuration = reopened.Catalog.GetDictionary(PdfName.Get("OCProperties"))!.GetDictionary(PdfName.Get("D"))!;
        configuration[PdfName.Get("BaseState")] = PdfName.Get("OFF");
        configuration.Remove(PdfName.Get("OFF"));
        Assert.False(reopened.Layers[1].IsVisible);
        reopened.Layers[1].IsVisible = true;
        Assert.True(reopened.Layers[1].IsVisible);
        Assert.False(reopened.Layers[0].IsVisible);
    }

    [Fact]
    public void TextOfHiddenLayersCanBeLeftOut()
    {
        var page = PdfDocument.Open(CreateWithLayers()).GetPage(1);
        var all = page.ExtractText();
        Assert.Contains("HIDDEN", all);
        Assert.Contains("A note", all);
        var visible = page.ExtractText(new TextExtractionOptions { IncludeHiddenLayers = false });
        Assert.DoesNotContain("HIDDEN", visible);
        Assert.Contains("Visible", visible);
        Assert.Contains("A note", visible);
        // the hidden text still advances the position: "end" stays where it is drawn
        var end = page.FindText("end").Single();
        var withoutHidden = PdfTextExtractor.ExtractWords(page, new TextExtractionOptions { IncludeHiddenLayers = false }).Single(w => w.Text == "end");
        Assert.Equal(end.Left, withoutHidden.Bounds.Left, 3);
    }

    [Fact]
    public void AnnotationsCanBelongToALayer()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var layer = doc.AddLayer("Comments", visible: false);
        var stamp = page.AddStamp(new PdfRectangle(100, 600, 300, 650), "Approved");
        stamp.Layer = layer;
        var reopened = PdfDocument.Open(doc.ToArray()).GetPage(1);
        var annotation = Assert.Single(reopened.Annotations);
        Assert.Equal("Comments", annotation.Layer!.Name);
        Assert.False(annotation.Layer.IsVisible);
        // also on the appearance stream (viewers such as Ghostscript only evaluate it there; kept when flattening)
        var appearance = annotation.Dictionary.GetDictionary(PdfName.AP)!.GetStream(PdfName.Get("N"))!;
        Assert.Equal(annotation.Layer.Reference.ObjectNumber, ((PdfReference)appearance[PdfName.Get("OC")]!).ObjectNumber);
        var options = new TextExtractionOptions { IncludeAnnotations = true };
        Assert.Contains("Approved", reopened.ExtractText(options));
        options.IncludeHiddenLayers = false;
        Assert.DoesNotContain("Approved", reopened.ExtractText(options));
        annotation.Layer = null;
        Assert.Null(annotation.Layer);
        Assert.False(appearance.ContainsKey(PdfName.Get("OC")));
    }

    [Fact]
    public void MembershipDictionariesFollowTheirPolicy()
    {
        var doc = PdfDocument.Create();
        var on = doc.AddLayer("On");
        var off = doc.AddLayer("Off", visible: false);
        PdfObject Membership(string policy) => doc.AddObject(new PdfDictionary(PdfName.Get("OCMD"))
        {
            [PdfName.Get("OCGs")] = new PdfArray(on.Reference, off.Reference),
            [PdfName.P] = PdfName.Get(policy),
        });
        Assert.False(doc.IsHiddenContent(Membership("AnyOn")));
        Assert.True(doc.IsHiddenContent(Membership("AllOn")));
        Assert.False(doc.IsHiddenContent(Membership("AnyOff")));
        Assert.True(doc.IsHiddenContent(Membership("AllOff")));
        Assert.True(doc.IsHiddenContent(off.Reference));
        Assert.False(doc.IsHiddenContent(null));
    }

    [Fact]
    public void LayersInPdfA()
    {
        var doc = PdfDocument.Create();
        doc.AddLayer("Layer");
        Assert.DoesNotContain(doc.CheckPdfA(PdfAConformance.PdfA2B), i => i.Contains("ptional content"));
        Assert.Contains(doc.CheckPdfA(PdfAConformance.PdfA1B), i => i.Contains("not allowed in PDF/A-1"));
        doc.Catalog.GetDictionary(PdfName.Get("OCProperties"))!.GetDictionary(PdfName.Get("D"))!.Remove(PdfName.Name);
        Assert.Contains(doc.CheckPdfA(PdfAConformance.PdfA2B), i => i.Contains("without /Name"));
    }
}
