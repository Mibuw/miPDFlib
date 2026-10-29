using System.Text;
using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf.Tests.Core;

public class LexerParserTests
{
    private static PdfObject? Parse(string text) =>
        new PdfObjectParser(new PdfLexer(Encoding.Latin1.GetBytes(text))).ParseObject();

    [Theory]
    [InlineData("123", 123d, true)]
    [InlineData("-17", -17d, true)]
    [InlineData("+5", 5d, true)]
    [InlineData("3.25", 3.25, false)]
    [InlineData("-.5", -0.5, false)]
    [InlineData("4.", 4d, false)]
    [InlineData("--5", -5d, true)]
    [InlineData("0.00-5", 0d, false)]
    public void ParsesNumbers(string text, double expected, bool isInteger)
    {
        var n = Assert.IsType<PdfNumber>(Parse(text));
        Assert.Equal(expected, n.Value, 6);
        Assert.Equal(isInteger, n.IsInteger);
    }

    [Fact]
    public void ParsesLiteralStringWithEscapes()
    {
        var s = Assert.IsType<PdfString>(Parse(@"(a\(b\)c\\d\n\101\0533 (nested) x\
y)"));
        Assert.Equal("a(b)c\\d\nA+3 (nested) xy", Encoding.Latin1.GetString(s.Bytes));
    }

    [Fact]
    public void ParsesHexString()
    {
        var s = Assert.IsType<PdfString>(Parse("<48 65 6C6C 6F7>"));
        Assert.Equal(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x70 }, s.Bytes);
        Assert.True(s.IsHex);
    }

    [Fact]
    public void ParsesNamesWithHexEscapes()
    {
        var n = Assert.IsType<PdfName>(Parse("/A#20B#23"));
        Assert.Equal("A B#", n.Value);
    }

    [Fact]
    public void ParsesNestedDictionaryWithReferences()
    {
        var d = Assert.IsType<PdfDictionary>(Parse("<< /Type /Page /Kids [1 0 R 2 0 R] /Count 2 /Sub << /X true /Y null >> /N -3.5 >>"));
        Assert.Equal(PdfName.Page, d.GetName(PdfName.Type));
        var kids = Assert.IsType<PdfArray>(d[PdfName.Kids]);
        Assert.Equal(2, kids.Count);
        var r = Assert.IsType<PdfReference>(kids[1]);
        Assert.Equal(2, r.ObjectNumber);
        Assert.Equal(2, d.GetInt(PdfName.Count));
        var sub = d.GetDictionary("Sub")!;
        Assert.True(sub.GetBool("X"));
        Assert.False(sub.ContainsKey("Y")); // null values are treated as absent
        Assert.Equal(-3.5, d.GetDouble(PdfName.N));
    }

    [Fact]
    public void ArrayOfIntegersIsNotMistakenForReferences()
    {
        var a = Assert.IsType<PdfArray>(Parse("[1 2 3 4 R 5]"));
        Assert.Equal(4, a.Count);
        Assert.IsType<PdfNumber>(a[0]);
        Assert.IsType<PdfNumber>(a[1]);
        Assert.IsType<PdfReference>(a[2]);
    }

    [Fact]
    public void SkipsComments()
    {
        var a = Assert.IsType<PdfArray>(Parse("[1 % comment ]\n 2]"));
        Assert.Equal(2, a.Count);
    }

    [Fact]
    public void SerializerRoundTrip()
    {
        var dict = new PdfDictionary();
        dict["Name"] = PdfName.Get("With Space");
        dict["Text"] = new PdfString("Grüße (äöü)");
        dict["Unicode"] = new PdfString("日本語");
        dict["Real"] = new PdfNumber(1.0 / 3);
        dict["Arr"] = new PdfArray(new PdfNumber(1), PdfBoolean.True, PdfNull.Instance);
        var text = dict.ToString();
        var parsed = Assert.IsType<PdfDictionary>(Parse(text));
        Assert.Equal("With Space", parsed.GetName("Name")!.Value);
        Assert.Equal("Grüße (äöü)", parsed.GetText("Text"));
        Assert.Equal("日本語", parsed.GetText("Unicode"));
        Assert.Equal(0.333333, parsed.GetDouble("Real"), 6);
    }

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(1e-9, "0")]
    [InlineData(12345678.0, "12345678")]
    [InlineData(1.23456789, "1.234568")]
    public void FormatsRealsWithoutExponent(double value, string expected) =>
        Assert.Equal(expected, PdfNumber.FormatReal(value));

    [Fact]
    public void PdfDateRoundTrip()
    {
        var date = new DateTimeOffset(2026, 10, 28, 14, 5, 9, TimeSpan.FromHours(1));
        var s = PdfDate.Format(date);
        Assert.Equal("D:20261028140509+01'00'", s);
        Assert.Equal(date, PdfDate.Parse(s));
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), PdfDate.Parse("D:2020"));
    }
}
