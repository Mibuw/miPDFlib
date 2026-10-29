using MiPdf.Images;

namespace MiPdf.Tests.Content;

/// <summary>BMP and GIF decoding compared with reference pixels produced by Pillow (Resources/bitmap-fixtures.txt).</summary>
public class BitmapDecoderTests
{
    public static IEnumerable<object[]> Cases() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Resources", "bitmap-fixtures.txt"))
            .Select(l => l.Split('\t')).Select(p => new object[] { p[0], p[1], p[2] });

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodesLikeReference(string name, string data, string expectedRgba)
    {
        var bytes = Convert.FromBase64String(data);
        var expected = Convert.FromBase64String(expectedRgba);
        var bitmap = name.StartsWith("bmp", StringComparison.Ordinal) ? BmpDecoder.Decode(bytes) : GifDecoder.Decode(bytes);
        Assert.Equal(expected.Length / 4, bitmap.Width * bitmap.Height);
        for (int i = 0; i < bitmap.Width * bitmap.Height; i++)
        {
            byte alpha = bitmap.Alpha?[i] ?? 255;
            Assert.True(expected[i * 4 + 3] == alpha, $"{name}: alpha at {i}: expected {expected[i * 4 + 3]}, got {alpha}");
            if (alpha == 0) continue;
            for (int c = 0; c < 3; c++)
                Assert.True(Math.Abs(expected[i * 4 + c] - bitmap.Rgb[i * 3 + c]) <= 1, $"{name}: pixel {i} channel {c}: expected {expected[i * 4 + c]}, got {bitmap.Rgb[i * 3 + c]}");
        }
        // usable as PdfImage
        var image = PdfImage.FromBytes(bytes);
        Assert.Equal(bitmap.Width, image.Width);
        var doc = PdfDocument.Create();
        using (var c = doc.AddPage().GetCanvas()) c.DrawImage(image, 10, 10, 70, 50);
        Assert.NotEmpty(doc.ToArray());
    }
}
