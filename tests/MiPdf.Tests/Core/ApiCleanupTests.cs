using System.Text;
using MiPdf.Barcodes;
using MiPdf.Content;
using MiPdf.Fonts;
using MiPdf.Forms;
using MiPdf.Graphics;
using MiPdf.IO;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.Redaction;
using MiPdf.Tagging;
using MiPdf.Text;

namespace MiPdf.Tests.Core;

/// <summary>The API changes of issue #13 (cleanup before 1.0).</summary>
public class ApiCleanupTests
{
    [Fact]
    public void ChoiceFieldValueRoundTrips()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        var list = form.AddListBox(page, "colors", new PdfRectangle(72, 600, 200, 680), new[] { "red", "green", "blue" }, multiSelect: true);
        list.SetSelection(new[] { "green", "blue" });
        Assert.Equal("green", list.Value);
        Assert.Equal(new[] { "green", "blue" }, list.SelectedValues);
        list.Value = list.Value;
        Assert.Equal(new[] { "green" }, list.SelectedValues);
        list.Value = null;
        Assert.Null(list.Value);
    }

    [Fact]
    public void FormFieldsRejectJustifyAndWidgetsCanBeHidden()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var field = doc.GetOrCreateForm().AddTextField(page, "name", new PdfRectangle(72, 700, 300, 720));
        Assert.Throws<ArgumentException>(() => field.Alignment = TextAlignment.Justify);
        field.Alignment = TextAlignment.Right;
        Assert.Equal(TextAlignment.Right, field.Alignment);

        var widget = field.Widgets[0];
        int flags = widget.Dictionary.GetInt(PdfName.F);
        widget.IsHidden = true;
        Assert.True(PdfDocument.Open(doc.ToArray()).GetForm()!.GetField("name")!.Widgets[0].IsHidden);
        widget.IsHidden = false;
        Assert.Equal(flags, widget.Dictionary.GetInt(PdfName.F));

        field.ToolTip = "Your name";
        Assert.Equal("Your name", field.ToolTip);
    }

    [Fact]
    public void RestoreStateRestoresTheFont()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        using (var canvas = page.GetCanvas())
        {
            canvas.BeginText().SetFont(PdfStandardFont.Helvetica, 12).SetTextPosition(72, 700).ShowText("before").EndText();
            canvas.SaveState().BeginText().SetFont(PdfStandardFont.Courier, 8).EndText().RestoreState();
            canvas.BeginText().SetTextPosition(72, 680).ShowText("after").EndText(); // Helvetica again, no SetFont needed
            canvas.DrawText("drawn", PdfStandardFont.TimesRoman, 10, 72, 660);
            canvas.BeginText().SetTextPosition(72, 640).ShowText("still").EndText();
        }
        var text = PdfTextExtractor.ExtractText(PdfDocument.Open(doc.ToArray()));
        Assert.Contains("after", text);
        Assert.Contains("still", text);
    }

    [Fact]
    public void DrawTextBoxJustifiesAllButTheLastLineOfAParagraph()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        string text = string.Join(' ', Enumerable.Repeat("justified text with several words", 6)) + "\nshort last";
        var box = new PdfRectangle(72, 400, 272, 700);
        using (var canvas = page.GetCanvas())
            canvas.DrawTextBox(text, PdfStandardFont.Helvetica, 10, box, TextAlignment.Justify);
        var lines = PdfTextExtractor.ExtractLines(PdfDocument.Open(doc.ToArray()).GetPage(1));
        Assert.True(lines.Count >= 3);
        foreach (var line in lines.Take(lines.Count - 2)) Assert.Equal(box.Right, line.Bounds.Right, 0);
        Assert.True(lines[^1].Bounds.Right < box.Right - 20); // "short last" is not stretched
    }

    [Fact]
    public void FormXObjectMatrixIsTyped()
    {
        var doc = PdfDocument.Create();
        var xobject = new PdfFormXObject(doc, new PdfRectangle(0, 0, 10, 20));
        Assert.Null(xobject.Matrix);
        xobject.Matrix = new Graphics.Matrix(0, 1, -1, 0, 5, 6);
        Assert.Equal(new Graphics.Matrix(0, 1, -1, 0, 5, 6), xobject.Matrix);
        xobject.Matrix = null;
        Assert.False(xobject.Stream.ContainsKey(PdfName.Get("Matrix")));
    }

    [Fact]
    public void ColorsAndPageBoxes()
    {
        Assert.Equal(PdfColor.Rgb(0, 1, 0), PdfColor.Lime);
        Assert.Equal(PdfColor.FromRgb(31, 78, 121), PdfColor.FromHex("#1F4E79"));

        var page = PdfDocument.Create().AddPage(PageSize.A4);
        page.CropBox = new PdfRectangle(10, 10, 500, 800);
        Assert.Equal(page.CropBox, page.TrimBox);
        Assert.Equal(page.CropBox, page.BleedBox);
        Assert.Equal(page.CropBox, page.ArtBox);
        page.TrimBox = new PdfRectangle(20, 20, 400, 700);
        Assert.Equal(new PdfRectangle(20, 20, 400, 700), page.TrimBox);
        Assert.True(page.Dictionary.ContainsKey(PdfName.TrimBox));
        Assert.False(page.Dictionary.ContainsKey(PdfName.BleedBox));
    }

    [Fact]
    public void NumbersRoundLikeTheConstructor()
    {
        Assert.Equal(3, new PdfNumber(2.7).IntValue);
        Assert.Equal(-3, new PdfNumber(-2.7).LongValue);
        Assert.Equal(new PdfNumber(2.7).LongValue, new PdfNumber(3).LongValue);
        var parsed = (PdfNumber)new PdfObjectParser(new PdfLexer(Encoding.ASCII.GetBytes("1.9"))).ParseObject()!;
        Assert.Equal(2, parsed.IntValue);
        Assert.Equal(9e18, new PdfNumber(1e30).LongValue); // clamped, no overflow
    }

    [Fact]
    public void ContentParserReportsSyntaxErrorsWhenStrict()
    {
        var content = Encoding.ASCII.GetBytes("1 0 0 1 0 0 cm " + new string('[', 10_000));
        Assert.Single(ContentParser.Parse(content));
        Assert.Throws<PdfParseException>(() => ContentParser.Parse(content, strict: true).ToList());
    }

    [Fact]
    public void LayoutAndBarcodeCleanups()
    {
        var spacer = new Spacer(30);
        Assert.Equal(30, spacer.Height);
        Assert.Equal(0, spacer.SpacingBefore);
        Assert.Equal("a.", new ListElement(ListStyle.LowerAlpha).AddItems("x").GetLabel(0));
        Assert.Equal("A.", new ListElement(ListStyle.UpperAlpha).AddItems("x").GetLabel(0));

        var qr = QrCode.Create("hello");
        Assert.Equal(qr.TotalWidthModules, qr.TotalHeightModules);
        Assert.Contains(StructureType.Article, StructureType.All);
        Assert.Contains(StructureType.Code, StructureType.All);
        Assert.Equal("H", StructureType.H);
    }

    [Fact]
    public void SpacerHeightIsLaidOut()
    {
        double Bottom(double spacer)
        {
            var doc = PdfDocument.Create();
            using (var layout = new LayoutDocument(doc, PageSize.A4, new Thickness(50)))
            {
                layout.Add(new Paragraph("first", PdfStandardFont.Helvetica, 10));
                layout.Add(new Spacer(spacer));
                layout.Add(new Paragraph("second", PdfStandardFont.Helvetica, 10));
            }
            return PdfTextExtractor.ExtractLines(PdfDocument.Open(doc.ToArray()).GetPage(1))[^1].Bounds.Bottom;
        }
        Assert.Equal(100, Bottom(0) - Bottom(100), 1);
    }

    [Fact]
    public void RedactionWarningsAreReadOnlyAndOptionsSettable()
    {
        var options = new RedactionOptions();
        options.OverlayText = "removed";
        Assert.Equal("removed", options.OverlayText);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(new RedactionResult().Warnings);
        Assert.False(typeof(RedactionResult).GetProperty(nameof(RedactionResult.Warnings))!.PropertyType.IsAssignableTo(typeof(List<string>)));
    }
}
