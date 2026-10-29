using System.Text;
using MiPdf.Fonts;
using MiPdf.Forms;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Tests.Forms;

public class FormTests
{
    private static PdfDocument CreateForm()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        form.AddTextField(page, "name", new PdfRectangle(100, 700, 300, 720), "Max");
        form.AddTextField(page, "notes", new PdfRectangle(100, 600, 300, 680), multiline: true);
        form.AddCheckBox(page, "agree", new PdfRectangle(100, 560, 114, 574));
        form.AddRadioGroup(page, "size", new[] { ("S", new PdfRectangle(100, 520, 114, 534)), ("M", new PdfRectangle(130, 520, 144, 534)), ("L", new PdfRectangle(160, 520, 174, 534)) }, "M");
        form.AddComboBox(page, "country", new PdfRectangle(100, 480, 250, 498), new[] { "AT", "DE", "CH" }, "DE");
        form.AddListBox(page, "langs", new PdfRectangle(100, 400, 250, 470), new[] { "de", "en", "fr" });
        form.AddSignatureField(page, "sig", new PdfRectangle(300, 100, 500, 150));
        return PdfDocument.Open(doc.ToArray());
    }

    [Fact]
    public void ReadsCreatedFields()
    {
        var form = CreateForm().GetForm()!;
        Assert.Equal(new[] { "name", "notes", "agree", "size", "country", "langs", "sig" }, form.Fields.Select(f => f.FullName));
        Assert.IsType<PdfTextField>(form.GetField("name"));
        Assert.True(form.GetField<PdfTextField>("notes")!.Multiline);
        Assert.IsType<PdfCheckBox>(form.GetField("agree"));
        Assert.Equal(new[] { "S", "M", "L" }, form.GetField<PdfRadioButtonGroup>("size")!.Options);
        Assert.True(form.GetField<PdfChoiceField>("country")!.IsComboBox);
        Assert.False(form.GetField<PdfSignatureField>("sig")!.IsSigned);

        var values = form.GetValues();
        Assert.Equal("Max", values["name"]);
        Assert.Equal("Off", values["agree"]);
        Assert.Equal("M", values["size"]);
        Assert.Equal("DE", values["country"]);
    }

    [Fact]
    public void FillAndRoundTrip()
    {
        var doc = CreateForm();
        var form = doc.GetForm()!;
        form.SetValue("name", "Grüße (Test)");
        form.SetValue("notes", "line 1\nline 2");
        form.GetField<PdfCheckBox>("agree")!.IsChecked = true;
        form.SetValue("size", "L");
        form.SetValue("country", "CH");
        form.GetField<PdfChoiceField>("langs")!.SetSelection(new[] { "fr" });

        var reopened = PdfDocument.Open(doc.ToArray()).GetForm()!;
        Assert.Equal("Grüße (Test)", reopened.GetField("name")!.Value);
        Assert.Equal("line 1\nline 2", reopened.GetField("notes")!.Value);
        Assert.True(reopened.GetField<PdfCheckBox>("agree")!.IsChecked);
        var size = reopened.GetField<PdfRadioButtonGroup>("size")!;
        Assert.Equal("L", size.Value);
        Assert.Equal(new[] { "Off", "Off", "L" }, size.Widgets.Select(w => w.AppearanceState));
        Assert.Equal(new[] { "CH" }, reopened.GetField<PdfChoiceField>("country")!.SelectedValues);
        Assert.Equal(2, reopened.GetField("langs")!.Dictionary.GetArray("I")!.GetInt(0));
    }

    [Fact]
    public void AppearanceContainsValue()
    {
        var doc = CreateForm();
        var field = doc.GetForm()!.GetField<PdfTextField>("name")!;
        field.Value = "Appearance";
        var ap = field.Widgets[0].Dictionary.GetDictionary(PdfName.AP)!.GetStream(PdfName.N)!;
        var content = Encoding.Latin1.GetString(ap.GetBytes());
        Assert.Contains("(Appearance) Tj", content);
        Assert.Contains("/Tx BMC", content);
        Assert.Equal("Appearance", PdfDocument.Open(doc.ToArray()).GetPage(1).ExtractText(new MiPdf.Text.TextExtractionOptions { IncludeAnnotations = true }).Split('\n')[0]);
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        var form = CreateForm().GetForm()!;
        Assert.Throws<ArgumentException>(() => form.SetValue("size", "XL"));
        Assert.Throws<ArgumentException>(() => form.SetValue("country", "FR"));
        Assert.Throws<KeyNotFoundException>(() => form.SetValue("unknown", "x"));
    }

    [Fact]
    public void UnicodeValuesWithEmbeddedFont()
    {
        var path = TestHelpers.FindSystemFont();
        if (path == null) return;
        var doc = CreateForm();
        var form = doc.GetForm()!;
        form.AppearanceFont = PdfTrueTypeFont.FromFile(path);
        form.SetValue("name", "Łódź Ελλάδα");
        form.Flatten();
        var text = PdfDocument.Open(doc.ToArray()).GetPage(1).ExtractText();
        Assert.Contains("Łódź Ελλάδα", text);
    }

    [Fact]
    public void FlattenMovesAppearanceIntoContent()
    {
        var doc = CreateForm();
        var form = doc.GetForm()!;
        form.SetValue("name", "Flattened value");
        form.GetField<PdfCheckBox>("agree")!.IsChecked = true;
        form.Flatten();
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Null(reopened.GetForm());
        Assert.Empty(reopened.GetPage(1).Annotations);
        var text = reopened.GetPage(1).ExtractText();
        Assert.Contains("Flattened value", text);
        // the check mark is vector graphics (no ZapfDingbats font, PDF/A compatible), drawn via the appearance XObject
        Assert.Contains(" Do", System.Text.Encoding.Latin1.GetString(reopened.GetPage(1).GetContentBytes()));
    }

    [Fact]
    public void FlattenSingleField()
    {
        var doc = CreateForm();
        var form = doc.GetForm()!;
        form.Flatten(new[] { form.GetField("name")! });
        var reopened = PdfDocument.Open(doc.ToArray()).GetForm()!;
        Assert.Null(reopened.GetField("name"));
        Assert.NotNull(reopened.GetField("notes"));
    }

    [Fact]
    public void RemoveField()
    {
        var doc = CreateForm();
        var form = doc.GetForm()!;
        form.RemoveField(form.GetField("country")!);
        var reopened = PdfDocument.Open(doc.ToArray());
        Assert.Null(reopened.GetForm()!.GetField("country"));
        Assert.Equal(8, reopened.GetPage(1).Annotations.Count); // 9 widgets (radio group has 3) minus 1
    }

    [Fact]
    public void HierarchicalNamesAndInheritance()
    {
        const string pdf = "%PDF-1.7\n1 0 obj<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[4 0 R]/DA(/Helv 0 Tf 0 g)>>>>endobj\n" +
            "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]/Annots[5 0 R 6 0 R]>>endobj\n" +
            "4 0 obj<</T(address)/FT/Tx/Kids[5 0 R 6 0 R]>>endobj\n" +
            "5 0 obj<</T(street)/Parent 4 0 R/Subtype/Widget/Rect[10 10 100 30]/V(Main St)>>endobj\n" +
            "6 0 obj<</T(city)/Parent 4 0 R/Subtype/Widget/Rect[10 40 100 60]/Ff 1>>endobj\n" +
            "trailer<</Root 1 0 R>>\n%%EOF";
        var form = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf)).GetForm()!;
        Assert.Equal(new[] { "address.street", "address.city" }, form.Fields.Select(f => f.FullName));
        var street = Assert.IsType<PdfTextField>(form.GetField("address.street")); // FT inherited from parent
        Assert.Equal("Main St", street.Value);
        Assert.True(form.GetField("address.city")!.ReadOnly);
        Assert.Same(street.Widgets[0].Page!.Dictionary, form.Document.GetPage(1).Dictionary);
    }

    [Fact]
    public void MergeCombinesFormFields()
    {
        var a = CreateForm();
        var b = PdfDocument.Create();
        b.GetOrCreateForm().AddTextField(b.AddPage(), "other", new PdfRectangle(10, 10, 100, 30), "B");
        var merged = PdfDocument.Merge(new[] { a, PdfDocument.Open(b.ToArray()) });
        var form = PdfDocument.Open(merged.ToArray()).GetForm()!;
        Assert.Equal(8, form.Fields.Count);
        Assert.Equal("B", form.GetField("other")!.Value);
        Assert.Equal("Max", form.GetField("name")!.Value);
    }
}
