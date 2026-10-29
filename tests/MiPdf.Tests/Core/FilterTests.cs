using System.Text;
using MiPdf.Filters;
using MiPdf.Objects;

namespace MiPdf.Tests.Core;

public class FilterTests
{
    [Fact]
    public void FlateRoundTrip()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Hello PDF ", 500)));
        var encoded = FlateFilter.Encode(data);
        Assert.True(encoded.Length < data.Length);
        Assert.Equal(data, FlateFilter.Decode(encoded));
    }

    [Fact]
    public void FlateToleratesTruncatedData()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => i.ToString())));
        var encoded = FlateFilter.Encode(data);
        var truncated = encoded[..(encoded.Length / 2)];
        var decoded = FlateFilter.Decode(truncated);
        Assert.True(decoded.Length > 0);
        Assert.Equal(data[..decoded.Length], decoded);
    }

    [Fact]
    public void Ascii85Decode() =>
        Assert.Equal("Man is distinguished", Encoding.ASCII.GetString(Ascii85Filter.Decode(Encoding.ASCII.GetBytes("9jqo^BlbD-BleB1DJ+*+F(f,q~>"))));

    [Fact]
    public void Ascii85DecodesZAndPartialGroups()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Ascii85Filter.Decode(Encoding.ASCII.GetBytes("z~>")));
        Assert.Equal("Man", Encoding.ASCII.GetString(Ascii85Filter.Decode(Encoding.ASCII.GetBytes("9jqo~>"))));
    }

    [Fact]
    public void AsciiHexDecode() =>
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0xE0 }, AsciiHexFilter.Decode(Encoding.ASCII.GetBytes("ab c\nD e>")));

    [Fact]
    public void LzwDecodeSpecExample()
    {
        // ISO 32000-2, 7.4.4.2 example: "-----A---B"
        var encoded = new byte[] { 0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01 };
        Assert.Equal("-----A---B", Encoding.ASCII.GetString(LzwFilter.Decode(encoded)));
    }

    [Fact]
    public void RunLengthDecode()
    {
        var encoded = new byte[] { 2, (byte)'a', (byte)'b', (byte)'c', 254, (byte)'x', 128 };
        Assert.Equal("abcxxx", Encoding.ASCII.GetString(RunLengthFilter.Decode(encoded)));
    }

    [Fact]
    public void PngUpPredictor()
    {
        // two rows of 3 bytes; second row uses "Up" filter
        var raw = new byte[] { 0, 1, 2, 3, 2, 1, 1, 1 };
        var parms = new PdfDictionary();
        parms.Set(PdfName.Predictor, 12);
        parms.Set(PdfName.Columns, 3);
        Assert.Equal(new byte[] { 1, 2, 3, 2, 3, 4 }, Predictor.Apply(raw, parms));
    }

    [Fact]
    public void StreamWithFilterChain()
    {
        var data = Encoding.ASCII.GetBytes("filter chain test");
        var flate = FlateFilter.Encode(data);
        var hex = AsciiHexFilter.Encode(flate);
        var stream = new PdfStream();
        stream[PdfName.Filter] = new PdfArray(PdfName.ASCIIHexDecode, PdfName.FlateDecode);
        stream.SetRawBytes(hex);
        Assert.Equal(data, stream.GetBytes());
    }
}
