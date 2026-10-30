using System.Text;
using MiPdf.Forms;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Tests.Forms;

public class WidgetAppearanceTests
{
    private static readonly double[] Rotate90 = { 0, 1, -1, 0, 0, 0 };

    private static string[] Tokens(PdfStream appearance) =>
        Encoding.Latin1.GetString(appearance.GetBytes()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static PdfStream Normal(PdfWidget widget, string? state = null)
    {
        var n = widget.Dictionary.GetDictionary(PdfName.AP)!.Get(PdfName.N)!.Resolve();
        return state == null ? (PdfStream)n : ((PdfDictionary)n).GetStream(PdfName.Get(state))!;
    }

    [Fact]
    public void FieldsCanBeCreatedWithoutBorder()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        var framed = form.AddTextField(page, "framed", new PdfRectangle(100, 700, 300, 720), "Max");
        var text = form.AddTextField(page, "text", new PdfRectangle(100, 670, 300, 690), "Max", borderWidth: 0);
        var box = form.AddCheckBox(page, "box", new PdfRectangle(100, 640, 114, 654), border: PdfColor.Rgb(1, 0, 0), borderWidth: 0);
        var thick = form.AddCheckBox(page, "thick", new PdfRectangle(130, 640, 144, 654), borderWidth: 2);

        Assert.Contains("S", Tokens(Normal(framed.Widgets[0])));
        foreach (var widget in new[] { text.Widgets[0], box.Widgets[0] })
        {
            Assert.Null(widget.BorderColor); // no /MK /BC: viewers that rebuild the appearance draw no border either
            Assert.Equal(0, widget.BorderWidth);
        }
        Assert.DoesNotContain("S", Tokens(Normal(text.Widgets[0])));
        Assert.DoesNotContain("S", Tokens(Normal(box.Widgets[0], "Off")));
        Assert.Equal(2, thick.Widgets[0].BorderWidth);
        Assert.Contains("2 w", Encoding.Latin1.GetString(Normal(thick.Widgets[0], "Off").GetBytes()));
        Assert.Throws<ArgumentOutOfRangeException>(() => form.AddTextField(page, "bad", new PdfRectangle(100, 600, 300, 620), borderWidth: -1));

        // removing the border of an existing field
        framed.Widgets[0].BorderColor = null;
        Assert.DoesNotContain("S", Tokens(Normal(framed.Widgets[0])));
        var reopened = PdfDocument.Open(doc.ToArray()).GetForm()!;
        Assert.Equal(0, reopened.GetField("text")!.Widgets[0].BorderWidth);
    }

    [Fact]
    public void CheckBoxHasNoBackgroundByDefault()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        var plain = form.AddCheckBox(page, "plain", new PdfRectangle(100, 600, 114, 614), borderWidth: 0);
        var white = form.AddCheckBox(page, "white", new PdfRectangle(130, 600, 144, 614), background: PdfColor.White);

        Assert.Null(plain.Widgets[0].BackgroundColor);
        Assert.Empty(Tokens(Normal(plain.Widgets[0], "Off"))); // nothing drawn: the page content below stays visible
        Assert.NotNull(white.Widgets[0].BackgroundColor);
        Assert.Contains("f", Tokens(Normal(white.Widgets[0], "Off")));

        var radio = form.AddRadioGroup(page, "size", new[] { ("S", new PdfRectangle(100, 560, 114, 574)) }, borderWidth: 0);
        var combo = form.AddComboBox(page, "country", new PdfRectangle(100, 520, 250, 538), new[] { "AT", "DE" });
        var list = form.AddListBox(page, "langs", new PdfRectangle(100, 440, 250, 500), new[] { "de", "en" }, background: PdfColor.White);
        Assert.Null(radio.Widgets[0].BackgroundColor);
        Assert.Empty(Tokens(Normal(radio.Widgets[0], "Off")));
        Assert.Null(combo.Widgets[0].BackgroundColor);
        Assert.NotNull(combo.Widgets[0].BorderColor);
        Assert.NotNull(list.Widgets[0].BackgroundColor);

        // an invalid border width is rejected before anything is added
        Assert.Throws<ArgumentOutOfRangeException>(() => form.AddComboBox(page, "bad", new PdfRectangle(100, 400, 250, 418), new[] { "x" }, borderWidth: -1));
        Assert.Null(form.GetField("bad"));
    }

    [Fact]
    public void CheckBoxAppearanceFollowsRotation()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var box = doc.GetOrCreateForm().AddCheckBox(page, "box", new PdfRectangle(100, 600, 140, 620), isChecked: true, style: CheckBoxStyle.Check);
        Assert.Null(Normal(box.Widgets[0], "Yes").GetArray(PdfName.Get("Matrix")));

        box.Widgets[0].Rotation = 90;
        Assert.Equal(90, box.Widgets[0].Dictionary.GetDictionary(PdfName.MK)!.GetInt(PdfName.Get("R")));
        foreach (var state in new[] { "Yes", "Off" })
        {
            var ap = Normal(box.Widgets[0], state);
            Assert.Equal(Rotate90, ap.GetArray(PdfName.Get("Matrix"))!.ToDoubleArray());
            Assert.Equal(new double[] { 0, 0, 20, 40 }, ap.GetArray(PdfName.BBox)!.ToDoubleArray()); // drawn upright: width and height swapped
        }
        Assert.True(box.IsChecked); // state is kept

        var reopened = PdfDocument.Open(doc.ToArray()).GetForm()!.GetField<PdfCheckBox>("box")!;
        Assert.True(reopened.IsChecked);
        Assert.Equal(90, reopened.Widgets[0].Rotation);
        reopened.Widgets[0].Rotation = -360;
        Assert.Equal(0, reopened.Widgets[0].Rotation);
        Assert.Null(Normal(reopened.Widgets[0], "Yes").GetArray(PdfName.Get("Matrix")));
        Assert.Throws<ArgumentException>(() => reopened.Widgets[0].Rotation = 45);
    }

    [Fact]
    public void ChangedColorsRebuildTheAppearance()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        var box = form.AddCheckBox(page, "box", new PdfRectangle(100, 600, 114, 614), style: CheckBoxStyle.Cross);
        var radio = form.AddRadioGroup(page, "size", new[] { ("S", new PdfRectangle(100, 560, 114, 574)), ("M", new PdfRectangle(130, 560, 144, 574)) }, "M");

        box.Widgets[0].BorderColor = PdfColor.Rgb(1, 0, 0);
        box.Widgets[0].BackgroundColor = null;
        var off = Encoding.Latin1.GetString(Normal(box.Widgets[0], "Off").GetBytes());
        Assert.Contains("1 0 0 RG", off);
        Assert.DoesNotContain(" f", off); // no background fill
        Assert.Equal('8', box.Widgets[0].Dictionary.GetDictionary(PdfName.MK)!.GetText(PdfName.Get("CA"))![0]);

        foreach (var widget in radio.Widgets) widget.Rotation = 270;
        Assert.Equal("M", radio.Value);
        Assert.Equal(new[] { "Off", "M" }, radio.Widgets.Select(w => w.AppearanceState));
        Assert.Equal(new double[] { 0, -1, 1, 0, 0, 0 }, Normal(radio.Widgets[1], "M").GetArray(PdfName.Get("Matrix"))!.ToDoubleArray());
    }

    [Fact]
    public void TextFieldRotationRebuildsTheAppearance()
    {
        var doc = PdfDocument.Create();
        var field = doc.GetOrCreateForm().AddTextField(doc.AddPage(), "name", new PdfRectangle(100, 500, 120, 700), "Vertical");
        field.Widgets[0].Rotation = 90;
        var ap = Normal(field.Widgets[0]);
        Assert.Equal(Rotate90, ap.GetArray(PdfName.Get("Matrix"))!.ToDoubleArray());
        Assert.Contains("(Vertical) Tj", Encoding.Latin1.GetString(ap.GetBytes()));
    }
}
