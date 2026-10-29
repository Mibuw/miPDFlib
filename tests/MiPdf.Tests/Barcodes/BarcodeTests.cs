using MiPdf.Barcodes;
using MiPdf.Layout;

namespace MiPdf.Tests.Barcodes;

// Module patterns were verified by decoding rendered output with zxing-cpp; these tests guard the structure.
public class BarcodeTests
{
    [Fact]
    public void Code128SelectsCodeSetsAndChecksum()
    {
        var abc = Code128.Create("ABC");
        Assert.Equal(new[] { 104, 33, 34, 35, 1, 106 }, abc.Values);
        Assert.Equal(11 * 5 + 13, abc.Modules.Count);

        var digits = Code128.Create("123456");
        Assert.Equal(new[] { 105, 12, 34, 56, (105 + 12 + 2 * 34 + 3 * 56) % 103, 106 }, digits.Values);

        var mixed = Code128.Create("AB12345678");
        Assert.Equal(104, mixed.Values[0]);
        Assert.Contains(99, mixed.Values); // switch to code set C for the digit run

        var control = Code128.Create("A\tB");
        Assert.Equal(103, control.Values[0]); // start A because of the control character
    }

    [Fact]
    public void Gs1128InsertsFnc1()
    {
        var code = Code128.CreateGs1("(01)09501101530003(10)ABC(21)1");
        Assert.Equal(105, code.Values[0]);
        Assert.Equal(102, code.Values[1]); // leading FNC1
        Assert.Equal(2, code.Values.Count(v => v == 102)); // separator after the variable-length (10) field only
        Assert.Equal("(01)09501101530003(10)ABC(21)1", code.HumanReadableText);
        Assert.Throws<FormatException>(() => Code128.CreateGs1("0109501101530003"));
    }

    [Fact]
    public void EanComputesAndVerifiesCheckDigits()
    {
        Assert.Equal(1, Ean.CheckDigit("400638133393"));
        var ean = Ean.CreateEan13("400638133393");
        Assert.Equal("4006381333931", ean.Text);
        Assert.Equal(95, ean.Modules.Count);
        Assert.Equal(67, Ean.CreateEan8("9638507").Modules.Count);
        Assert.Equal("036000291452", Ean.CreateUpcA("03600029145").Text);
        Assert.Throws<ArgumentException>(() => Ean.CreateEan13("4006381333932"));
        Assert.Throws<ArgumentException>(() => Ean.CreateEan13("40063813339X"));
    }

    [Fact]
    public void Code39AndItf()
    {
        var c39 = Code39.Create("AB-1", addCheckCharacter: true);
        Assert.Equal(7 * 16 - 1, c39.Modules.Count); // start, 4 chars, check, stop: 15 modules each + 1-module gaps
        Assert.Throws<ArgumentException>(() => Code39.Create("lower"));

        var itf = Interleaved2Of5.Create("1234567");
        Assert.Equal("01234567", itf.Text); // padded to an even number of digits
        Assert.Equal("15400141288763", Interleaved2Of5.Create("1540014128876", addCheckDigit: true).Text);
    }

    [Theory]
    [InlineData("HELLO WORLD", QrErrorCorrection.Q, 1)]
    [InlineData("01234567", QrErrorCorrection.M, 1)]
    [InlineData("https://github.com/Mibuw/miPDFlib", QrErrorCorrection.M, 3)]
    public void QrCodeChoosesSmallestVersion(string text, QrErrorCorrection ecc, int version)
    {
        var qr = QrCode.Create(text, ecc);
        Assert.Equal(version, qr.Version);
        Assert.Equal(17 + 4 * version, qr.Rows);
        Assert.Equal(qr.Rows + 8, qr.TotalWidthModules);
        // finder patterns: dark 7x7 outline with dark 3x3 center in three corners
        foreach (var (r, c) in new[] { (0, 0), (0, qr.Columns - 7), (qr.Rows - 7, 0) })
        {
            Assert.True(qr[r, c] && qr[r + 6, c + 6] && qr[r + 3, c + 3]);
            Assert.False(qr[r + 1, c + 1]);
        }
        Assert.True(qr[qr.Rows - 8, 8]); // dark module
    }

    [Fact]
    public void QrCodeCapacityLimits()
    {
        Assert.Equal(40, QrCode.Create(new string('1', 7089), QrErrorCorrection.L).Version);
        Assert.Throws<ArgumentException>(() => QrCode.Create(new string('1', 7090), QrErrorCorrection.L));
        Assert.Equal(5, QrCode.Create("x", new QrCodeOptions { MinVersion = 5, Mask = 3 }).Version);
        Assert.Equal(3, QrCode.Create("x", new QrCodeOptions { Mask = 3 }).Mask);
    }

    [Fact]
    public void DataMatrixSymbolSizesAndFinderPattern()
    {
        var small = DataMatrix.Create("123456");
        Assert.Equal(10, small.Rows);
        Assert.Equal(10, small.Columns);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(small[i, 0]); // solid left edge
            Assert.True(small[9, i]); // solid bottom edge
            Assert.Equal(i % 2 == 0, small[0, i]); // alternating top edge
        }
        var rect = DataMatrix.Create("ABCDEF", DataMatrixShape.Rectangle);
        Assert.Equal(8, rect.Rows);
        Assert.True(rect.Columns > rect.Rows);
        var large = DataMatrix.Create(new string('A', 400));
        Assert.True(large.Rows >= 52); // multiple data regions
        Assert.Throws<ArgumentException>(() => DataMatrix.Create(new string('A', 5000)));
        Assert.Equal(16, DataMatrix.CreateGs1("(01)09501101530003(10)AB").Rows);
    }

    [Fact]
    public void EpcPayloadFollowsTheGuideline()
    {
        var payment = new EpcPayment { Name = "Max Mustermann", Iban = "AT61 1904 3002 3457 3201", Bic = "BKAUATWW", Amount = 12.5m, Text = "Rechnung 42" };
        Assert.Equal("BCD\n002\n1\nSCT\nBKAUATWW\nMax Mustermann\nAT611904300234573201\nEUR12.5\n\n\nRechnung 42", payment.ToPayload());
        Assert.Equal(QrErrorCorrection.M, EpcQrCode.Create(payment).ErrorCorrection);
        Assert.Throws<ArgumentException>(() => new EpcPayment { Name = "x", Iban = "AT611904300234573201", Reference = "RF18", Text = "t" }.ToPayload());
        Assert.Throws<ArgumentException>(() => new EpcPayment { Name = "x", Iban = "AT611904300234573201", Amount = 0 }.ToPayload());
    }

    [Fact]
    public void BarcodesAreDrawnAsOneFilledPath()
    {
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        using (var canvas = page.GetCanvas())
        {
            canvas.DrawBarcode(QrCode.Create("test"), 50, 600, 100);
            canvas.DrawBarcode(Ean.CreateEan13("400638133393"), 200, 600, 120, 60);
        }
        using (var layout = new LayoutDocument(doc))
            layout.Add(new BarcodeElement(DataMatrix.Create("layout"), 50));
        var reopened = PdfDocument.Open(doc.ToArray());
        string content = System.Text.Encoding.Latin1.GetString(reopened.GetPage(1).GetContentBytes());
        Assert.Contains(" re\n", content);
        Assert.Contains("006381", reopened.GetPage(1).ExtractText());
        Assert.Equal(2, reopened.PageCount);
    }
}
