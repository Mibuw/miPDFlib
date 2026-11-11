using System.Text;
using MiPdf.Objects;
using MiPdf.PostScript;
using MiPdf.PostScript.Interpreter;
using MiPdf.Rendering;

namespace MiPdf.Tests.PostScript;

/// <summary>
/// PostScript images written as PDF (stage 4). The expected colors were taken from Ghostscript rendering the same
/// programs. During development these and about 140 random image programs (all color spaces, 1 to 16 bits, data from
/// strings, procedures and filtered files, ImageType 1, 3 and 4) were compared with Ghostscript, also through
/// Ghostscript's ps2write and eps2write output.
/// </summary>
public class ImageTests
{
    private const string Page = "<< /PageSize [200 200] >> setpagedevice\n";

    private static PdfDocument Convert(string program, out PsJobResult result) =>
        PsConverter.Convert(Encoding.Latin1.GetBytes(program), out result);

    private static RenderedPage Render(string program)
    {
        using var doc = Convert(program, out var result);
        Assert.Null(result.ErrorName);
        using var reopened = PdfDocument.Open(doc.ToArray());
        return PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 });
    }

    private static (int R, int G, int B) At(RenderedPage page, double x, double y)
    {
        int px = (int)x, py = page.Height - 1 - (int)y;
        int i = (py * page.Width + px) * 4;
        return (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
    }

    private static void Near((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance = 3) =>
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected}, got {actual}");

    /// <summary>The image XObjects of page 1.</summary>
    private static List<PdfStream> Images(PdfDocument doc) =>
        doc.GetPage(1).Resources.GetDictionary(PdfName.XObject)?.Select(kv => kv.Value.Resolve()).OfType<PdfStream>().ToList() ?? new List<PdfStream>();

    [Fact]
    public void OperandFormsAndDataSources()
    {
        var page = Render(Page + "1 0 0 setrgbcolor 0 0 200 200 rectfill\n" +
            // a string is used again when the image needs more data
            "gsave 10 150 translate 80 40 scale 8 4 8 [8 0 0 -4 0 4] <00FF00FF00FF00FF> image grestore\n" +
            // a procedure that ends the data after two rows: the other rows are not painted
            "/n 0 def gsave 110 150 translate 80 40 scale 8 4 8 [8 0 0 -4 0 4] { /n n 1 add def n 2 le { <00FF00FF00FF00FF> } { () } ifelse } image grestore\n" +
            // strings shorter than a row: the data runs on across rows
            "gsave 10 90 translate 80 40 scale 8 4 8 [8 0 0 -4 0 4] { <0080FF> } image grestore\n" +
            "0 0 1 setrgbcolor gsave 110 90 translate 80 40 scale 8 4 true [8 0 0 -4 0 4] <F0F0F0F0> imagemask grestore\n" +
            // multiple data sources with 4 bits, and 12 bits with an inverted decode array
            "gsave 10 30 translate 80 40 scale 2 2 4 [2 0 0 -2 0 2] <F0F0> <0F00> <00F0> true 3 colorimage grestore\n" +
            "/DeviceRGB setcolorspace gsave 110 30 translate 80 40 scale << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 12 " +
            "/Decode [1 0 1 0 1 0] /ImageMatrix [2 0 0 -1 0 1] /DataSource <FFF000000 000FFF000> >> image grestore showpage");
        Near((0, 0, 0), At(page, 15, 185));
        Near((255, 255, 255), At(page, 25, 185));
        Near((0, 0, 0), At(page, 15, 155));
        Near((255, 255, 255), At(page, 25, 155));
        Near((0, 0, 0), At(page, 115, 185));
        Near((0, 0, 0), At(page, 115, 175));
        Near((255, 0, 0), At(page, 115, 165));
        Near((255, 0, 0), At(page, 115, 155));
        Near((0, 0, 0), At(page, 15, 125));
        Near((128, 128, 128), At(page, 25, 125));
        Near((255, 255, 255), At(page, 35, 125));
        Near((255, 255, 255), At(page, 15, 115));
        Near((0, 0, 0), At(page, 25, 115));
        Near((0, 0, 255), At(page, 115, 125));
        Near((255, 0, 0), At(page, 155, 125));
        Near((255, 0, 0), At(page, 30, 60));
        Near((0, 255, 0), At(page, 70, 60));
        Near((255, 0, 255), At(page, 30, 40));
        Near((0, 0, 0), At(page, 70, 40));
        Near((0, 255, 255), At(page, 130, 50));
        Near((255, 0, 255), At(page, 170, 50));
    }

    [Fact]
    public void DataIsReadFromTheProgramThroughFilters()
    {
        // the filters consume their end-of-data marker, so the program goes on after the data
        var page = Render(Page +
            "gsave 10 100 translate 80 80 scale 2 2 8 [2 0 0 -2 0 2] currentfile /ASCIIHexDecode filter image\n00FF FF00>\ngrestore\n" +
            "gsave 110 100 translate 80 80 scale 2 2 8 [2 0 0 -2 0 2] currentfile /ASCII85Decode filter image\n!<<'!~>\ngrestore\n" +
            "/s 2 string def gsave 10 10 translate 80 80 scale 2 2 8 [2 0 0 -2 0 2] { currentfile s readhexstring pop } image\nFF00\n00FF\ngrestore\n" +
            "1 0 0 setrgbcolor 140 30 30 30 rectfill showpage");
        foreach (int x in new[] { 10, 110 })
        {
            Near((0, 0, 0), At(page, x + 20, 160));
            Near((255, 255, 255), At(page, x + 60, 160));
            Near((255, 255, 255), At(page, x + 20, 120));
            Near((0, 0, 0), At(page, x + 60, 120));
        }
        Near((255, 255, 255), At(page, 30, 70));
        Near((0, 0, 0), At(page, 70, 70));
        Near((0, 0, 0), At(page, 30, 30));
        Near((255, 0, 0), At(page, 155, 45));
    }

    [Fact]
    public void ColorSpacesAreKeptOrConverted()
    {
        const string program = Page + "0 1 0 setrgbcolor 0 0 200 200 rectfill\n" +
            "gsave 10 110 translate 80 80 scale [/Indexed /DeviceRGB 1 <FF0000 0000FF>] setcolorspace " +
            "<< /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 1 /Decode [0 1] /ImageMatrix [2 0 0 -1 0 1] /DataSource <40> >> image grestore\n" +
            // Separation: through the tint transform (t -> t 0 t)
            "gsave 110 110 translate 80 80 scale [/Separation /Spot /DeviceRGB { dup 0 exch }] setcolorspace " +
            "<< /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /Decode [0 1] /ImageMatrix [2 0 0 -1 0 1] /DataSource <00FF> >> image grestore\n" +
            // DeviceN (a b -> a b 0) is converted per sample
            "gsave 10 10 translate 80 80 scale [/DeviceN [/A /B] /DeviceRGB { 0 }] setcolorspace " +
            "<< /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1] /ImageMatrix [2 0 0 -1 0 1] /DataSource <FF0000FF> >> image grestore\n" +
            // Separation /None paints nothing
            "gsave 110 10 translate 80 80 scale [/Separation /None /DeviceGray {}] setcolorspace " +
            "<< /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /Decode [0 1] /ImageMatrix [2 0 0 -1 0 1] /DataSource <00FF> >> image grestore showpage";
        var page = Render(program);
        Near((255, 0, 0), At(page, 30, 150));
        Near((0, 0, 255), At(page, 70, 150));
        Near((0, 0, 0), At(page, 130, 150));
        Near((255, 0, 255), At(page, 170, 150));
        Near((255, 0, 0), At(page, 30, 50));
        Near((0, 255, 0), At(page, 70, 50));
        Near((0, 255, 0), At(page, 150, 50));

        // small images are written inline: the Indexed image keeps its lookup table, the single-component Separation
        // becomes a table of its colors, DeviceN is converted to RGB samples
        using var doc = Convert(program, out _);
        string content = Encoding.Latin1.GetString(doc.GetPage(1).GetContentBytes());
        Assert.Contains("/CS [/Indexed /DeviceRGB 1 <FF00000000FF>] /BPC 1", content);
        Assert.Contains("/CS [/Indexed /DeviceRGB 255 <000000", content);
        Assert.Contains("/CS /DeviceRGB /BPC 8", content);
        Assert.Equal(3, content.Split("BI ").Length - 1);
    }

    [Fact]
    public void ColorKeyAndExplicitMasks()
    {
        var page = Render(Page + "0 1 0 setrgbcolor 0 0 200 200 rectfill /DeviceRGB setcolorspace\n" +
            // ImageType 4: red is not painted
            "gsave 10 110 translate 80 80 scale << /ImageType 4 /Width 2 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] " +
            "/ImageMatrix [2 0 0 -1 0 1] /MaskColor [255 255 0 0 0 0] /DataSource <FF00000000FF> >> image grestore\n" +
            // ImageType 3 interleaved by sample: a mask sample of 1 is not painted
            "gsave 110 110 translate 80 80 scale << /ImageType 3 /InterleaveType 1 " +
            "/DataDict << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] /ImageMatrix [2 0 0 -1 0 1] /DataSource <FFFF0000000000FF> >> " +
            "/MaskDict << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 8 /Decode [0 1] /ImageMatrix [2 0 0 -1 0 1] >> >> image grestore\n" +
            // ImageType 3 with separate data sources, the mask finer than the image
            "gsave 10 10 translate 80 80 scale << /ImageType 3 /InterleaveType 3 " +
            "/DataDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] /ImageMatrix [2 0 0 -2 0 2] /DataSource <0000FF0000FF0000FF0000FF> >> " +
            "/MaskDict << /ImageType 1 /Width 4 /Height 4 /BitsPerComponent 1 /Decode [0 1] /ImageMatrix [4 0 0 -4 0 4] /DataSource <80803030> >> >> image grestore\n" +
            // ImageType 3 interleaved by row, the image twice as high as the mask: a mask row, then two image rows
            "gsave 110 10 translate 80 80 scale << /ImageType 3 /InterleaveType 2 " +
            "/DataDict << /ImageType 1 /Width 2 /Height 2 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] /ImageMatrix [2 0 0 -2 0 2] /DataSource <80 FF0000FF0000 0000FF0000FF> >> " +
            "/MaskDict << /ImageType 1 /Width 2 /Height 1 /BitsPerComponent 1 /Decode [0 1] /ImageMatrix [2 0 0 -1 0 1] >> >> image grestore showpage");
        Near((0, 255, 0), At(page, 30, 150));
        Near((0, 0, 255), At(page, 70, 150));
        Near((0, 255, 0), At(page, 130, 150));
        Near((0, 0, 255), At(page, 170, 150));
        Near((0, 255, 0), At(page, 15, 70)); // mask bits set: the left quarter of the top half
        Near((0, 0, 255), At(page, 40, 70));
        Near((0, 0, 255), At(page, 70, 70));
        Near((0, 0, 255), At(page, 30, 30));
        Near((0, 255, 0), At(page, 70, 30)); // the right half of the bottom half
        Near((0, 255, 0), At(page, 130, 70)); // the left mask column is set
        Near((0, 255, 0), At(page, 130, 30));
        Near((255, 0, 0), At(page, 170, 70));
        Near((0, 0, 255), At(page, 170, 30));
    }

    private const string Jpeg =
        "FFD8FFDB0043000201010101010201010102020202020403020202020504040304060506060605060606070908060709" +
        "070606080B08090A0A0A0A0A06080B0C0B0A0C090A0A0AFFDB004301020202020202050303050A0706070A0A0A0A0A0A" +
        "0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0A0AFFC00011" +
        "080008001003011100021101031101FFC4001F0000010501010101010100000000000000000102030405060708090A0B" +
        "FFC400B5100002010303020403050504040000017D01020300041105122131410613516107227114328191A1082342B1" +
        "C11552D1F02433627282090A161718191A25262728292A3435363738393A434445464748494A535455565758595A6364" +
        "65666768696A737475767778797A838485868788898A92939495969798999AA2A3A4A5A6A7A8A9AAB2B3B4B5B6B7B8B9" +
        "BAC2C3C4C5C6C7C8C9CAD2D3D4D5D6D7D8D9DAE1E2E3E4E5E6E7E8E9EAF1F2F3F4F5F6F7F8F9FAFFC4001F0100030101" +
        "010101010101010000000000000102030405060708090A0BFFC400B51100020102040403040705040400010277000102" +
        "031104052131061241510761711322328108144291A1B1C109233352F0156272D10A162434E125F11718191A26272829" +
        "2A35363738393A434445464748494A535455565758595A636465666768696A737475767778797A82838485868788898A" +
        "92939495969798999AA2A3A4A5A6A7A8A9AAB2B3B4B5B6B7B8B9BAC2C3C4C5C6C7C8C9CAD2D3D4D5D6D7D8D9DAE2E3E4" +
        "E5E6E7E8E9EAF2F3F4F5F6F7F8F9FAFFDA000C03010002110311003F00F8BEBF94CFF7F0F90EBFEAA0FF009AF3FFD9";

    [Fact]
    public void JpegDataIsWrittenUnchanged()
    {
        // a 16 x 8 JPEG: red left, blue right
        string program = Page + "gsave 20 20 translate 160 80 scale 16 8 8 [16 0 0 -8 0 8] currentfile /ASCIIHexDecode filter /DCTDecode filter false 3 colorimage\n" +
                         Jpeg + ">\ngrestore 0 1 0 setrgbcolor 20 150 20 20 rectfill showpage";
        using var doc = Convert(program, out var result);
        Assert.Null(result.ErrorName);
        using var reopened = PdfDocument.Open(doc.ToArray());
        var image = Assert.Single(Images(reopened));
        Assert.Equal("DCTDecode", Assert.Single(image.GetFilters()).Value);
        Assert.Equal(System.Convert.FromHexString(Jpeg), image.RawBytes);
        var page = PdfRenderer.Render(reopened.GetPage(1), new PdfRenderOptions { Dpi = 72 });
        Near((255, 0, 0), At(page, 40, 60), 12);
        Near((0, 0, 255), At(page, 160, 60), 12);
        Near((0, 255, 0), At(page, 30, 160)); // the program went on after the JPEG data
    }

    [Fact]
    public void IdenticalImagesAreWrittenOnce()
    {
        // larger than the inline limit: an XObject, used twice
        string data = string.Concat(Enumerable.Range(0, 64 * 64).Select(i => (i * 7 % 256).ToString("X2")));
        string program = Page + $"/img {{ 64 64 8 [64 0 0 -64 0 64] <{data}> image }} def " +
                         "gsave 10 10 translate 50 50 scale img grestore gsave 100 100 translate 30 rotate 50 50 scale img grestore showpage";
        using var doc = Convert(program, out var result);
        Assert.Null(result.ErrorName);
        using var reopened = PdfDocument.Open(doc.ToArray());
        var image = Assert.Single(Images(reopened));
        Assert.Equal(64, image.GetInt(PdfName.Get("Width")));
        Assert.Equal(System.Convert.FromHexString(data), image.GetBytes());
    }

    [Fact]
    public void BitmapFontsPaintTheirGlyphsWithImagemask()
    {
        // a Type 3 font as written by dvips: the glyphs are imagemask bitmaps
        var page = Render(Page +
            "/BitFont 10 dict dup begin /FontType 3 def /FontMatrix [0.0625 0 0 0.0625 0 0] def /FontBBox [0 0 16 16] def\n" +
            "/Encoding 256 array def 0 1 255 { Encoding exch /.notdef put } for Encoding 65 /A put\n" +
            "/BuildChar { exch begin Encoding exch get /A eq { 16 0 0 0 16 16 setcachedevice 16 16 true [1 0 0 -1 0 16] " +
            "<FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00000000000000000000000000000000> imagemask } if end } def end definefont pop\n" +
            "/BitFont findfont 80 scalefont setfont 1 0 0 setrgbcolor 20 20 moveto (AA) show showpage");
        Near((255, 0, 0), At(page, 40, 90)); // the top half of the glyph is set
        Near((255, 255, 255), At(page, 40, 40));
        Near((255, 0, 0), At(page, 120, 90));
    }

    [Theory]
    [InlineData("1 1 3 [1 0 0 1 0 0] (a) image", "rangecheck")]
    [InlineData("1 1 8 [0 0 0 0 0 0] (a) image", "undefinedresult")]
    [InlineData("1 1 8 [1 0 0 1 0 0] 5 image", "typecheck")]
    [InlineData("1 1 8 [1 0 0 1 0 0] (a) true 2 colorimage", "rangecheck")]
    [InlineData("<< /ImageType 1 /Width 1 /Height 1 /ImageMatrix [1 0 0 1 0 0] /DataSource (a) >> image", "undefined")]
    [InlineData("100000 100000 8 [1 0 0 1 0 0] (a) image", "limitcheck")]
    [InlineData("/DeviceRGB setcolorspace << /ImageType 4 /Width 1 /Height 1 /BitsPerComponent 8 /Decode [0 1 0 1 0 1] /ImageMatrix [1 0 0 1 0 0] /DataSource (abc) /MaskColor [300 0 0] >> image", "rangecheck")]
    public void InvalidOperandsAreErrors(string call, string error)
    {
        using var doc = Convert(call, out var result);
        Assert.Equal(error, result.ErrorName);
    }

    [Fact]
    public void ImagesOnTheNullDeviceReadTheirData()
    {
        var page = Render(Page + "gsave nulldevice 2 2 8 [2 0 0 -2 0 2] currentfile /ASCIIHexDecode filter image\n00FF FF00>\ngrestore\n" +
                          "0 0 1 setrgbcolor 20 20 50 50 rectfill showpage");
        Near((0, 0, 255), At(page, 40, 40));
        Near((255, 255, 255), At(page, 150, 150));
    }
}
