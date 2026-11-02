using System.Text;
using MiPdf.Forms;
using MiPdf.Objects;

namespace MiPdf.Tests.Forms;

public class FormDataTests
{
    /// <summary>A form with every value-bearing field type; filled if <paramref name="fill"/> is set.</summary>
    private static PdfDocument CreateForm(bool fill)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        form.AddTextField(page, "name", new PdfRectangle(72, 700, 300, 720), fill ? "Grüße & <Jürgen> \"Ä\"" : null);
        // "address.street": a terminal field below a non-terminal parent field, as in Acrobat forms
        var street = form.AddTextField(page, "street", new PdfRectangle(72, 670, 300, 690), fill ? "Hauptstraße 1" : null);
        var fields = form.Dictionary.GetArray(PdfName.Fields)!;
        var streetRef = fields.First(o => ReferenceEquals(o.Resolve(), street.Dictionary));
        var parent = new PdfDictionary();
        parent.SetText(PdfName.T, "address");
        parent[PdfName.Kids] = new PdfArray(streetRef);
        var parentRef = doc.AddObject(parent);
        street.Dictionary[PdfName.Parent] = parentRef;
        fields[fields.IndexOf(streetRef)] = parentRef;
        form.AddCheckBox(page, "agree", new PdfRectangle(72, 640, 86, 654), isChecked: fill);
        form.AddRadioGroup(page, "salutation", new[] { ("Mr", new PdfRectangle(72, 610, 86, 624)), ("Ms", new PdfRectangle(100, 610, 114, 624)) },
            selected: fill ? "Ms" : null);
        form.AddComboBox(page, "country", new PdfRectangle(72, 580, 200, 596), new[] { "Austria", "Germany" }, selected: fill ? "Germany" : null);
        var languages = form.AddListBox(page, "languages", new PdfRectangle(72, 500, 200, 570), new[] { "de", "en", "fr" }, multiSelect: true);
        if (fill) languages.SetSelection(new[] { "de", "fr" });
        form.AddSignatureField(page, "signature", new PdfRectangle(300, 500, 450, 550));
        return PdfDocument.Open(doc.ToArray());
    }

    private static void AssertFilled(PdfAcroForm form)
    {
        Assert.Equal("Grüße & <Jürgen> \"Ä\"", form.GetField("name")!.Value);
        Assert.Equal("Hauptstraße 1", form.GetField("address.street")!.Value);
        Assert.True(form.GetField<PdfCheckBox>("agree")!.IsChecked);
        Assert.Equal("Ms", form.GetField("salutation")!.Value);
        Assert.Equal(new[] { "Germany" }, form.GetField<PdfChoiceField>("country")!.SelectedValues);
        Assert.Equal(new[] { "de", "fr" }, form.GetField<PdfChoiceField>("languages")!.SelectedValues);
    }

    [Fact]
    public void XfdfRoundTrip()
    {
        string xfdf = CreateForm(fill: true).GetForm()!.ExportXfdf("antrag.pdf");
        Assert.Contains("xmlns=\"http://ns.adobe.com/xfdf/\"", xfdf);
        Assert.Contains("<f href=\"antrag.pdf\" />", xfdf);
        Assert.Contains("<field name=\"address\">", xfdf);     // hierarchical names are nested
        Assert.DoesNotContain("signature", xfdf);

        var target = CreateForm(fill: false);
        var result = target.GetForm()!.ImportXfdf(xfdf);
        Assert.Empty(result.Errors);
        Assert.Empty(result.UnknownFields);
        Assert.Equal(6, result.FieldsSet);
        AssertFilled(PdfDocument.Open(target.ToArray()).GetForm()!);
    }

    [Fact]
    public void FdfRoundTrip()
    {
        byte[] fdf = CreateForm(fill: true).GetForm()!.ExportFdf("antrag.pdf");
        string text = Encoding.Latin1.GetString(fdf);
        Assert.StartsWith("%FDF-1.2", text);
        Assert.Contains("/Kids", text);
        Assert.Contains("(antrag.pdf)", text);

        var target = CreateForm(fill: false);
        var result = target.GetForm()!.ImportFdf(fdf);
        Assert.Empty(result.Errors);
        Assert.Equal(6, result.FieldsSet);
        AssertFilled(PdfDocument.Open(target.ToArray()).GetForm()!);
    }

    [Fact]
    public void ImportsAcrobatStyleFdfWithIndirectObjects()
    {
        byte[] fdf = Encoding.Latin1.GetBytes("%FDF-1.2\n%\xE2\xE3\xCF\xD3\n" +
            "1 0 obj<</FDF<</Fields 2 0 R/F(antrag.pdf)>>>>endobj\n" +
            "2 0 obj[<</T(name)/V(Jane Doe)>> 3 0 R <</T(agree)/V/On>> <</T(languages)/V[(en)(fr)]>>]endobj\n" +
            "3 0 obj<</T(address)/Kids[<</T(street)/V<FEFF004D00FC0068006C0065>>>]>>endobj\n" +
            "trailer\n<</Root 1 0 R>>\n%%EOF\n");
        var doc = CreateForm(fill: false);
        var form = doc.GetForm()!;
        var result = form.ImportFdf(fdf);
        Assert.Empty(result.Errors);
        Assert.Equal(4, result.FieldsSet);
        Assert.Equal("Jane Doe", form.GetField("name")!.Value);
        Assert.Equal("Mühle", form.GetField("address.street")!.Value);   // UTF-16BE string
        Assert.True(form.GetField<PdfCheckBox>("agree")!.IsChecked);      // "On" for a box whose on-state is "Yes"
        Assert.Equal(new[] { "en", "fr" }, form.GetField<PdfChoiceField>("languages")!.SelectedValues);
    }

    [Fact]
    public void UnknownFieldsAndInvalidValuesAreReported()
    {
        const string xfdf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve">
              <fields>
                <field name="address.street"><value>flat name</value></field>
                <field name="missing"><value>x</value></field>
                <field name="country"><value>Narnia</value></field>
                <field name="signature"><value>forged</value></field>
              </fields>
            </xfdf>
            """;
        var form = CreateForm(fill: false).GetForm()!;
        var result = form.ImportXfdf(xfdf);
        Assert.Equal(1, result.FieldsSet);
        Assert.Equal("flat name", form.GetField("address.street")!.Value);
        Assert.Equal(new[] { "missing" }, result.UnknownFields);
        Assert.Equal(2, result.Errors.Count);
        Assert.StartsWith("country:", result.Errors[0]);
        Assert.StartsWith("signature:", result.Errors[1]);
    }

    [Fact]
    public void HostileOrForeignDataIsRejected()
    {
        var form = CreateForm(fill: false).GetForm()!;
        Assert.Throws<PdfException>(() => form.ImportXfdf(
            "<!DOCTYPE xfdf [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><xfdf><fields><field name=\"name\"><value>&e;</value></field></fields></xfdf>"));
        Assert.Throws<PdfException>(() => form.ImportXfdf("<html/>"));
        Assert.Throws<PdfException>(() => form.ImportXfdf("<xfdf"));
        Assert.Throws<PdfException>(() => form.ImportFdf(Encoding.ASCII.GetBytes("%PDF-1.7\n")));
        Assert.Throws<PdfException>(() => form.ImportFdf(Encoding.ASCII.GetBytes("%FDF-1.2\n1 0 obj<</Other 1>>endobj\n")));
        Assert.Null(form.GetField("name")!.Value);
    }
}
