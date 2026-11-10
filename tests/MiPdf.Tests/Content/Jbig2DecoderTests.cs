using System.Buffers.Binary;
using System.Security.Cryptography;
using MiPdf.Filters;
using MiPdf.Objects;

namespace MiPdf.Tests.Content;

/// <summary>
/// JBIG2 decoding (Resources/jbig2-fixtures.txt). Every "bitmap*" stream encodes the same test image with another
/// JBIG2 feature; the expected hash is that of the image, also produced by Ghostscript (jbig2dec) for the streams it
/// decodes correctly. In addition the decoder was compared pixel by pixel with Ghostscript on 221 images of real PDFs.
/// </summary>
public class Jbig2DecoderTests
{
    private static readonly Dictionary<string, byte[]> Fixtures =
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Resources", "jbig2-fixtures.txt"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t'))
            .ToDictionary(p => p[0], p => Convert.FromBase64String(p[1]));

    private const string BitmapHash = "F195624B2FC5505FDBBD3473C58EAF6B22B4F376B57CED779A4357D8BD12BC4F"; // 399 x 400
    private const string AnnexHHash = "D806F2E73907D03B1DEFBB7AFADCEE5962D80A009A844D3170D8B3F8F446A2FD"; // 64 x 56

    public static IEnumerable<object[]> Names() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Resources", "jbig2-fixtures.txt"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => new object[] { l.Split('\t')[0] });

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodesTheTestImage(string name)
    {
        var data = Fixtures[name];
        var page = Jbig2Decoder.DecodePage(data, null);
        var packed = Jbig2Decoder.Decode(data, null, page.Width, page.Height);
        if (name == "annex-h")
        {
            Assert.Equal((64, 56), (page.Width, page.Height));
            Assert.Equal(AnnexHHash, Hash(packed));
        }
        else
        {
            Assert.Equal((399, 400), (page.Width, page.Height));
            Assert.Equal(BitmapHash, Hash(packed));
        }
    }

    /// <summary>Splits a stand-alone JBIG2 file into the embedded stream of page 1 and the global segments (page 0).</summary>
    private static (byte[] Page, byte[] Globals) Embed(byte[] file)
    {
        int p = 9 + ((file[8] & 2) == 0 ? 4 : 0);
        var page = new MemoryStream();
        var globals = new MemoryStream();
        while (p < file.Length)
        {
            int start = p;
            uint number = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p));
            int flags = file[p + 4], type = flags & 0x3F;
            p += 5;
            int count = file[p] >> 5;
            p += 1 + count * (number <= 256 ? 1 : number <= 65536 ? 2 : 4);
            int association = (flags & 0x40) != 0 ? (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p)) : file[p];
            p += (flags & 0x40) != 0 ? 4 : 1;
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p));
            p += 4 + length;
            if (type == 51) break; // end of file
            (association == 0 ? globals : page).Write(file, start, p - start);
        }
        return (page.ToArray(), globals.ToArray());
    }

    [Fact]
    public void JBIG2ImagesInPdfAreDecodedWithTheirGlobals()
    {
        var (data, globals) = Embed(Fixtures["bitmap-symbol-global"]);
        Assert.NotEmpty(globals);
        var doc = PdfDocument.Create();
        var page = doc.AddPage(new PdfRectangle(0, 0, 399, 400));
        var globalsStream = new PdfStream(globals, compress: false);
        var image = new PdfStream(data, compress: false);
        image[PdfName.Type] = PdfName.XObject;
        image[PdfName.Subtype] = PdfName.Get("Image");
        image.Set(PdfName.Get("Width"), 399);
        image.Set(PdfName.Get("Height"), 400);
        image.Set(PdfName.BitsPerComponent, 1);
        image[PdfName.ColorSpace] = PdfName.Get("DeviceGray");
        image[PdfName.Filter] = PdfName.JBIG2Decode;
        image[PdfName.DecodeParms] = new PdfDictionary { [PdfName.Get("JBIG2Globals")] = doc.AddObject(globalsStream) };
        var resources = new PdfDictionary { [PdfName.XObject] = new PdfDictionary { [PdfName.Get("Im0")] = doc.AddObject(image) } };
        page.Dictionary[PdfName.Resources] = resources;
        using (var canvas = page.GetCanvas()) canvas.WriteRaw("q 399 0 0 400 0 0 cm /Im0 Do Q");

        using var reopened = PdfDocument.Open(doc.ToArray());
        var extracted = Assert.Single(reopened.GetPage(1).ExtractImages());
        var decoded = StreamFilters.DecodeImage(extracted.Stream.RawBytes, extracted.Stream, out var remaining);
        Assert.Null(remaining);
        Assert.Equal(BitmapHash, Hash(decoded));
        Assert.NotNull(Images.ImageDecoder.Decode(extracted.Stream));
        Assert.NotNull(extracted.ToPng());
    }

    [Fact]
    public void DecodingWorkIsLimited()
    {
        // all bitmaps of a stream count against PdfLimits.MaxDecodedStreamSize together: every single one is below the
        // limit here (the page is 399 x 400 = 159,600 pixels), the region and its placement on the page are not
        foreach (var name in new[] { "bitmap-symbol", "bitmap-halftone" })
        {
            using (PdfLimits.Use(new PdfLimits { MaxDecodedStreamSize = 200_000 }))
                Assert.ThrowsAny<PdfException>(() => Jbig2Decoder.DecodePage(Fixtures[name], null));
            Assert.Equal(BitmapHash, Hash(Jbig2Decoder.Decode(Fixtures[name], null, 399, 400)));
        }
    }

    [Fact]
    public void DamagedDataRaisesPdfException()
    {
        var data = Fixtures["bitmap-symbol"];
        // truncated in the middle of a segment header and with a bogus segment length
        Assert.ThrowsAny<PdfException>(() => Jbig2Decoder.Decode(new byte[] { 0, 0, 0, 1, 48, 0, 1 }, null, 10, 10));
        var broken = (byte[])data.Clone();
        for (int i = 60; i < broken.Length; i += 7) broken[i] ^= 0x5A;
        try
        {
            Jbig2Decoder.Decode(broken, null, 399, 400); // garbage pixels are fine, crashes are not
        }
        catch (PdfException)
        {
        }
    }
}
