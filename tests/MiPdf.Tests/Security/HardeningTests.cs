using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.IO;
using MiPdf.Signatures;

namespace MiPdf.Tests.Security;

/// <summary>Malicious and broken input must fail with <see cref="PdfException"/> within the configured limits.</summary>
public class HardeningTests
{
    /// <summary>Builds a minimal PDF from object bodies (object 1 = catalog, 2 = pages, 3 = page, 4 = contents, 5 = resources).</summary>
    internal static byte[] BuildPdf(IDictionary<int, byte[]> objects, string trailerExtra = "")
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        var offsets = new Dictionary<int, long>();
        foreach (var (num, body) in objects.OrderBy(kv => kv.Key))
        {
            offsets[num] = ms.Position;
            W($"{num} 0 obj\n");
            ms.Write(body);
            W("\nendobj\n");
        }
        int size = objects.Keys.Max() + 1;
        long xref = ms.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int n = 1; n < size; n++) W(offsets.TryGetValue(n, out var o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R {trailerExtra.Replace("XREF", xref.ToString())}>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Stream(string dict, byte[] data) => [.. B($"<< {dict} /Length {data.Length} >>\nstream\n"), .. data, .. B("\nendstream")];

    private static Dictionary<int, byte[]> Basic(byte[] contents, string resources = "<< >>") => new()
    {
        [1] = B("<< /Type /Catalog /Pages 2 0 R >>"),
        [2] = B("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        [3] = B("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources 5 0 R >>"),
        [4] = contents,
        [5] = B(resources),
    };

    /// <summary>Runs the typical read operations; only PdfException may escape.</summary>
    internal static void Exercise(byte[] data)
    {
        try
        {
            using var doc = PdfDocument.Open(data);
            for (int p = 1; p <= Math.Min(doc.PageCount, 3); p++)
            {
                doc.GetPage(p).ExtractText();
                _ = doc.GetPage(p).Annotations;
            }
            doc.GetForm()?.GetValues();
            _ = doc.Outlines.Children.Count;
            PdfSignatureValidator.Validate(data);
            doc.CheckPdfUA();
            doc.GetForm()?.Flatten();
            doc.ToArray();
        }
        catch (PdfException)
        {
            // expected for broken input
        }
    }

    [Fact]
    public void DecompressionBombIsStoppedByTheLimit()
    {
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            for (int i = 0; i < 8; i++) z.Write(new byte[1 << 20]);
        var pdf = BuildPdf(Basic(Stream("/Filter /FlateDecode", compressed.ToArray())));
        using (PdfLimits.Use(PdfLimits.Default with { MaxDecodedStreamSize = 1 << 20 }))
        {
            var doc = PdfDocument.Open(pdf);
            var ex = Assert.Throws<PdfException>(() => doc.GetPage(1).ExtractText());
            Assert.Contains("MaxDecodedStreamSize", ex.ToString());
        }
        Assert.Equal("", PdfDocument.Open(pdf).GetPage(1).ExtractText()); // default limit: decodes fine
    }

    [Fact]
    public void ScopedLimitsAreRestored()
    {
        var before = PdfLimits.Current;
        using (PdfLimits.Use(before with { MaxImagePixels = 10 })) Assert.Equal(10, PdfLimits.Current.MaxImagePixels);
        Assert.Same(before, PdfLimits.Current);
    }

    [Fact]
    public void StructuralAttacksTerminate()
    {
        var cases = new List<byte[]>
        {
            // reference cycle
            BuildPdf(new Dictionary<int, byte[]>(Basic(Stream("", B("BT ET")))) { [3] = B("<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources 6 0 R >>"), [6] = B("7 0 R"), [7] = B("6 0 R") }),
            // xref /Prev pointing to itself
            BuildPdf(Basic(Stream("", B("BT ET"))), "/Prev XREF "),
            // form XObject drawing itself
            BuildPdf(new Dictionary<int, byte[]>(Basic(Stream("", B("/X Do")), "<< /XObject << /X 6 0 R >> >>"))
                { [6] = Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /X 6 0 R >> >>", B("/X Do")) }),
            // page tree cycle with absurd /Count
            BuildPdf(new Dictionary<int, byte[]>(Basic(Stream("", B("BT ET")))) { [2] = B("<< /Type /Pages /Kids [3 0 R 2 0 R] /Count 2147483647 >>") }),
            // deep nesting
            BuildPdf(Basic(Stream("", B("BT ET")), "<< /A " + new string('[', 100000) + new string(']', 100000) + " >>")),
            // absurd predictor parameters
            BuildPdf(Basic(Stream("/Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 2147483647 /Colors 4 /BitsPerComponent 16 >>",
                FlateCompress(new byte[101])))),
            // thousands of filters
            BuildPdf(Basic(Stream("/Filter [" + string.Concat(Enumerable.Repeat("/AHx ", 5000)) + "]", B("4>")))),
            // object stream with absurd /N
            BuildPdf(new Dictionary<int, byte[]>(Basic(Stream("", B("BT ET")))) { [6] = Stream("/Type /ObjStm /N 2000000000 /First 10", B("5 0 6 3 << >> 6 0 R")) }),
            // number overflow in the lexer
            BuildPdf(Basic(Stream("", B("99999999999999999999999.5.5 99999999999999999999 Tw")))),
        };
        foreach (var pdf in cases) Exercise(pdf);
    }

    [Fact]
    public void CMapRangesAreLimited()
    {
        var range = "begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0000> <FFFF> <0041> endbfrange endcmap\n";
        var cmap = Stream("", B(string.Concat(Enumerable.Repeat(range, 500))));
        var objects = new Dictionary<int, byte[]>(Basic(Stream("", B("BT /F1 12 Tf (abc) Tj ET")), "<< /Font << /F1 6 0 R >> >>"))
        {
            [6] = B("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 7 0 R >>"),
            [7] = cmap,
        };
        var pdf = BuildPdf(objects);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (PdfLimits.Use(PdfLimits.Default with { MaxCMapEntries = 200_000 }))
            Assert.NotNull(PdfDocument.Open(pdf).GetPage(1).ExtractText());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void HugeImagesAreRejected()
    {
        // PNG header claiming 100000 x 100000 pixels
        var png = TestHelpers.CreatePng(1, 1, new byte[3], alpha: false);
        png[16] = 0; png[17] = 1; png[18] = 0x86; png[19] = 0xA0;   // width 100000
        png[20] = 0; png[21] = 1; png[22] = 0x86; png[23] = 0xA0;   // height 100000
        var doc = PdfDocument.Create();
        var page = doc.AddPage();
        Assert.Throws<PdfException>(() =>
        {
            using var c = page.GetCanvas();
            c.DrawImage(MiPdf.Images.PdfImage.FromBytes(png), 0, 0, 10, 10);
        });
        Assert.Throws<ArgumentException>(() => MiPdf.Images.PdfImage.FromPixels(65536, 65536, new byte[3], 3));
    }

    private static byte[] FlateCompress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2001:4860:4860::8888", true)]
    public void PrivateAddressesAreNotPublic(string address, bool expected) =>
        Assert.Equal(expected, SafeHttp.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void RevocationSourceBlocksInternalUrls()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=SSRF", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(new[] { "http://127.0.0.1:9/ocsp" }, new[] { "http://169.254.169.254/latest/meta-data" }));
        req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { "http://localhost:9/crl" }));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var source = new HttpRevocationSource();
        var ex = Assert.ThrowsAny<HttpRequestException>(() => source.GetOcspResponse(cert, cert));
        Assert.Contains("private", ex.ToString());
        Assert.ThrowsAny<HttpRequestException>(() => source.GetIssuers(cert));
        Assert.ThrowsAny<HttpRequestException>(() => source.GetCrls(cert));
        var restricted = new HttpRevocationSource { AllowedHosts = new[] { "ocsp.example.com" } };
        Assert.Contains("allowed hosts", Assert.ThrowsAny<HttpRequestException>(() => restricted.GetOcspResponse(cert, cert)).ToString());
    }

    [Fact]
    public void MutatedDocumentsOnlyRaisePdfExceptions()
    {
        var doc = PdfDocument.Create();
        for (int p = 0; p < 2; p++)
        {
            var page = doc.AddPage();
            using (var c = page.GetCanvas())
            {
                c.DrawText($"Seite {p + 1} – Grüße", PdfStandardFont.Helvetica, 12, 72, 700);
                c.SetFillColor(PdfColor.Red).Rectangle(72, 600, 100, 50).Fill();
                c.DrawImage(MiPdf.Images.PdfImage.FromPixels(2, 2, new byte[12], 3), 200, 600, 20, 20);
            }
            page.AddLink(new PdfRectangle(72, 690, 200, 710), "https://example.com");
        }
        var form = doc.GetOrCreateForm();
        form.AddTextField(doc.GetPage(1), "name", new PdfRectangle(72, 500, 300, 520), "Wert");
        form.AddCheckBox(doc.GetPage(1), "ok", new PdfRectangle(72, 470, 86, 484), true);
        doc.Outlines.AddChild("Start", doc.GetPage(1));
        var seeds = new[] { doc.ToArray(), doc.ToArray(new PdfSaveOptions { UseObjectStreams = true }) };

        var rnd = new Random(4711);
        for (int i = 0; i < 400; i++)
        {
            var data = (byte[])seeds[i % 2].Clone();
            switch (i % 4)
            {
                case 0:
                    for (int k = 0; k < 1 + rnd.Next(20); k++) data[rnd.Next(data.Length)] = (byte)rnd.Next(256);
                    break;
                case 1:
                    data = data[..rnd.Next(1, data.Length)];
                    break;
                case 2:
                    for (int k = 0; k < 1 + rnd.Next(10); k++)
                    {
                        int pos = rnd.Next(data.Length);
                        if (data[pos] is >= (byte)'0' and <= (byte)'9') data[pos] = (byte)('0' + rnd.Next(10));
                    }
                    break;
                default:
                    int a = rnd.Next(data.Length), len = Math.Min(rnd.Next(1, 500), data.Length - a);
                    data = [.. data[..a], .. data[(a + len)..]];
                    break;
            }
            Exercise(data);
        }
    }
}
