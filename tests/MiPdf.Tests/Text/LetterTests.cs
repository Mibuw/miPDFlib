using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.Text;

namespace MiPdf.Tests.Text;

/// <summary><see cref="PdfTextExtractor.ExtractLetters"/>: every character with position, font, colors and rendering mode.</summary>
public class LetterTests
{
    private static PdfPage RawPage(string content)
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage(PageSize.A4);
        var fonts = new PdfDictionary
        {
            [PdfName.Get("F1")] = new PdfDictionary
            {
                [PdfName.Type] = PdfName.Font, [PdfName.Subtype] = PdfName.Get("Type1"),
                [PdfName.BaseFont] = PdfName.Get("Helvetica-BoldOblique"), [PdfName.Get("Encoding")] = PdfName.Get("WinAnsiEncoding"),
            },
            // a font that is not embedded, with its style only in the descriptor and a subset prefix
            [PdfName.Get("F2")] = new PdfDictionary
            {
                [PdfName.Type] = PdfName.Font, [PdfName.Subtype] = PdfName.Get("Type1"), [PdfName.BaseFont] = PdfName.Get("ABCDEF+Garamond"),
                [PdfName.Get("FirstChar")] = new PdfNumber(65), [PdfName.Get("LastChar")] = new PdfNumber(66),
                [PdfName.Get("Widths")] = PdfArray.FromNumbers(600, 600),
                [PdfName.Get("FontDescriptor")] = new PdfDictionary
                {
                    [PdfName.Type] = PdfName.Get("FontDescriptor"), [PdfName.Get("FontName")] = PdfName.Get("ABCDEF+Garamond"),
                    [PdfName.Get("Flags")] = new PdfNumber(34), [PdfName.Get("FontWeight")] = new PdfNumber(700),
                    [PdfName.Get("ItalicAngle")] = new PdfNumber(-12),
                },
            },
            [PdfName.Get("F3")] = new PdfDictionary
            {
                [PdfName.Type] = PdfName.Font, [PdfName.Subtype] = PdfName.Get("Type1"),
                [PdfName.BaseFont] = PdfName.Get("Helvetica"), [PdfName.Get("Encoding")] = PdfName.Get("WinAnsiEncoding"),
            },
        };
        var spot = new PdfArray(PdfName.Get("Separation"), PdfName.Get("Gold"), PdfName.Get("DeviceRGB"), new PdfDictionary
        {
            [PdfName.Get("FunctionType")] = new PdfNumber(2), [PdfName.Get("Domain")] = PdfArray.FromNumbers(0, 1),
            [PdfName.Get("C0")] = PdfArray.FromNumbers(1, 1, 1), [PdfName.Get("C1")] = PdfArray.FromNumbers(1, 0.8, 0), [PdfName.Get("N")] = new PdfNumber(1),
        });
        page.Dictionary[PdfName.Resources] = new PdfDictionary
        {
            [PdfName.Font] = fonts,
            [PdfName.ColorSpace] = new PdfDictionary { [PdfName.Get("Spot")] = spot, [PdfName.Get("P")] = PdfName.Get("Pattern") },
        };
        page.SetContent(Encoding.ASCII.GetBytes(content));
        return PdfDocument.Open(doc.ToArray()).GetPage(1);
    }

    [Fact]
    public void LettersCarryPositionFontColorsAndRenderingMode()
    {
        var letters = PdfTextExtractor.ExtractLetters(RawPage(
            "BT /F1 10 Tf 1 0 0 rg 72 700 Td (Ab) Tj ET\n" +
            "BT /F2 10 Tf 0.1 0.2 0.3 0.4 k 72 680 Td (AB) Tj ET\n" +
            "BT /F3 10 Tf /Spot cs 1 scn 72 660 Td (C) Tj ET\n" +
            "BT /F3 10 Tf 3 Tr 72 640 Td (h) Tj ET\n" +
            "BT /F3 20 Tf 0 0 1 RG 1 Tr 0 1 -1 0 300 300 Tm (U) Tj ET\n" +
            "q 2 0 0 2 0 0 cm BT /F3 10 Tf 0.5 g 50 200 Td (S) Tj ET Q\n" +
            "BT /F3 10 Tf /P cs /Pat1 scn 72 100 Td (X) Tj ET"));
        TextLetter L(string text) => letters.Single(l => l.Text == text);

        var a = letters.Single(l => l.Text == "A" && l.FontName.StartsWith("Helvetica"));
        Assert.Equal("Helvetica-BoldOblique", a.FontName);
        Assert.True(a.IsBold);
        Assert.True(a.IsItalic);
        Assert.Equal(PdfColor.Rgb(1, 0, 0), a.FillColor);
        Assert.Equal(PdfColor.Black, a.StrokeColor);
        Assert.Equal(0, a.RenderingMode);
        Assert.True(a.IsVisible);
        Assert.Equal((72.0, 700.0), a.BaselineStart);
        Assert.Equal(0, a.Angle);
        Assert.Equal(10, a.FontSize);

        // style from the descriptor, subset prefix removed, CMYK kept
        var b = letters.Single(l => l.Text == "B");
        Assert.Equal("Garamond", b.FontName);
        Assert.True(b.IsBold);
        Assert.True(b.IsItalic);
        Assert.Equal(PdfColor.Cmyk(0.1, 0.2, 0.3, 0.4), b.FillColor);
        Assert.Equal(78, b.BaselineStart.X, 6);  // after the 600 units wide A at 10 pt

        // a spot color converted through its tint transform; a pattern has no single color
        var c = L("C");
        Assert.Equal(PdfColor.FromRgb(255, 204, 0), c.FillColor);
        Assert.False(c.IsBold);
        Assert.Null(L("X").FillColor);

        // invisible text (OCR layers), stroked text turned upwards
        Assert.False(L("h").IsVisible);
        var u = L("U");
        Assert.Equal(1, u.RenderingMode);
        Assert.Equal(PdfColor.Rgb(0, 0, 1), u.StrokeColor);
        Assert.Equal(90, u.Angle);
        Assert.Equal(20, u.FontSize);
        Assert.Equal((300.0, 300.0), u.BaselineStart);
        Assert.Equal(300, u.BaselineEnd.X, 6);
        Assert.True(u.BaselineEnd.Y > 300);

        // the transformation matrix scales size and position
        var s = L("S");
        Assert.Equal(20, s.FontSize);
        Assert.Equal((100.0, 400.0), s.BaselineStart);
        Assert.Equal(100 + 0.667 * 20, s.BaselineEnd.X, 6);
        Assert.Equal(PdfColor.Gray(0.5), s.FillColor);
        Assert.True(s.Bounds.Left <= 100 && s.Bounds.Right >= 113 && s.Bounds.Bottom < 400 && s.Bounds.Top > 410);
    }

    [Fact]
    public void LettersFollowTheReadingOrderOfTheText()
    {
        var doc = PdfDocument.Create();
        using (var layout = new LayoutDocument(doc, PageSize.A4, new Thickness(50)))
        {
            layout.Add(new Paragraph("Zweite Spalte? Nein: ein Absatz mit mehreren Zeilen, der umbricht, damit die Reihenfolge " +
                                     "der Buchstaben über Zeilen hinweg geprüft wird.", PdfStandardFont.TimesRoman, 14));
            layout.Add(new Paragraph("Fett gedruckt", PdfStandardFont.HelveticaBold, 12));
        }
        var page = PdfDocument.Open(doc.ToArray()).GetPage(1);
        var letters = PdfTextExtractor.ExtractLetters(page);
        static string Squeeze(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal(Squeeze(page.ExtractText()), Squeeze(string.Concat(letters.Select(l => l.Text))));
        Assert.All(letters.Where(l => l.FontName == "Helvetica-Bold"), l => Assert.True(l.IsBold));
        Assert.All(letters.Where(l => l.FontName == "Times-Roman"), l => Assert.False(l.IsBold || l.IsItalic));
        Assert.All(letters, l => Assert.Equal(PdfColor.Black, l.FillColor));
    }

    [Fact]
    public void DamagedColorSpacesCostOnlyTheColor()
    {
        var letters = PdfTextExtractor.ExtractLetters(RawPage(
            "BT /F3 10 Tf /Missing cs 1 2 3 scn 72 700 Td (A) Tj ET BT /F3 10 Tf [/Separation /X /DeviceRGB 5] cs 1 scn 72 680 Td (B) Tj ET"));
        Assert.Equal("AB", string.Concat(letters.Select(l => l.Text)));
    }
}
