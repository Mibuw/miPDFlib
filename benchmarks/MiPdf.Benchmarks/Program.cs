using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MiPdf;
using MiPdf.Fonts;
using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.Signatures;
using MiPdf.Text;

// Memory and time measurements for large documents.
//   generate <dir>              creates the test files
//   run <scenario> <file> <out> runs one scenario in this process and prints one result line
//   all <dir>                   runs every scenario on every test file, each in its own process
//   huge <dir>                  creates a 2.5 GB file and runs the scenarios that read from the file
// Each scenario runs in a fresh process so that the peak working set belongs to that scenario alone.

string command = args.Length > 0 ? args[0] : "all";
string dir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "mipdf-benchmarks");

switch (command)
{
    case "generate":
        Generate(dir);
        break;
    case "run":
        RunScenario(args[1], args[2], args[3]);
        break;
    case "all":
        Generate(dir);
        RunAll(dir);
        break;
    case "huge":
        GenerateHuge(dir);
        RunHuge(dir);
        break;
    default:
        Console.Error.WriteLine("Usage: generate <dir> | run <scenario> <file> <out> | all <dir> | huge <dir>");
        return 1;
}
return 0;

static void Generate(string dir)
{
    Directory.CreateDirectory(dir);
    var images = Path.Combine(dir, "images.pdf");
    if (!File.Exists(images))
    {
        // 100 pages with one 3 MB image each: large streams, few objects
        var doc = PdfDocument.Create();
        var random = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            var page = doc.AddPage();
            var data = new byte[3 * 1024 * 1024];
            random.NextBytes(data);
            var image = new PdfStream();
            image.SetRawBytes(data);
            image[PdfName.Type] = PdfName.XObject;
            image[PdfName.Subtype] = PdfName.Get("Image");
            image.Set(PdfName.Get("Width"), 2000);
            image.Set(PdfName.Get("Height"), 1500);
            image[PdfName.Get("ColorSpace")] = PdfName.Get("DeviceRGB");
            image.Set(PdfName.Get("BitsPerComponent"), 8);
            image[PdfName.Filter] = PdfName.Get("DCTDecode");
            var xobjects = new PdfDictionary { [PdfName.Get("Im0")] = doc.AddObject(image) };
            page.Dictionary.GetDictionary(PdfName.Resources)![PdfName.XObject] = xobjects;
            page.Dictionary[PdfName.Contents] = doc.AddObject(new PdfStream("q 500 0 0 375 50 400 cm /Im0 Do Q"u8.ToArray()));
        }
        doc.Save(images, new PdfSaveOptions { CompressUncompressedStreams = false });
    }

    foreach (var (name, objectStreams) in new[] { ("pages.pdf", false), ("pages-objstm.pdf", true) })
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) continue;
        // 20,000 pages of text: many small objects
        var doc = PdfDocument.Create();
        for (int i = 0; i < 20_000; i++)
        {
            var page = doc.AddPage();
            using var canvas = page.GetCanvas();
            for (int line = 0; line < 20; line++)
                canvas.DrawText($"Page {i + 1}, line {line + 1}: The quick brown fox jumps over the lazy dog.",
                    PdfStandardFont.Helvetica, 11, 72, 770 - line * 16);
        }
        doc.Save(path, new PdfSaveOptions { UseObjectStreams = objectStreams });
    }
}

static void GenerateHuge(string dir)
{
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "huge.pdf");
    if (File.Exists(path)) return;
    // 850 pages with one 3 MB image each (2.5 GB); the image objects share one array, so creating the file needs
    // little memory, but every image is written separately
    var data = new byte[3 * 1024 * 1024];
    new Random(42).NextBytes(data);
    var doc = PdfDocument.Create();
    for (int i = 0; i < 850; i++)
    {
        var page = doc.AddPage();
        using (var canvas = page.GetCanvas())
            canvas.DrawText($"Page {i + 1}", PdfStandardFont.Helvetica, 11, 72, 770);
        var image = new PdfStream();
        image.SetRawBytes(data);
        image[PdfName.Type] = PdfName.XObject;
        image[PdfName.Subtype] = PdfName.Get("Image");
        image.Set(PdfName.Get("Width"), 2000);
        image.Set(PdfName.Get("Height"), 1500);
        image[PdfName.Get("ColorSpace")] = PdfName.Get("DeviceRGB");
        image.Set(PdfName.Get("BitsPerComponent"), 8);
        image[PdfName.Filter] = PdfName.Get("DCTDecode");
        page.Dictionary.GetDictionary(PdfName.Resources)![PdfName.XObject] = new PdfDictionary { [PdfName.Get("Im0")] = doc.AddObject(image) };
    }
    doc.Save(path, new PdfSaveOptions { CompressUncompressedStreams = false });
}

static void RunHuge(string dir)
{
    var file = Path.Combine(dir, "huge.pdf");
    Console.WriteLine($"{"file",-18} {"MB",6} {"scenario",-12} {"ms",8} {"peak WS MB",11} {"alloc MB",9}");
    foreach (var scenario in new[] { "open", "text-first", "incremental", "full", "sign-file", "validate-file" })
        RunInProcess(scenario, file, Path.Combine(dir, "out-" + scenario + "-huge.pdf"), keep: scenario == "sign-file");
}

static void RunInProcess(string scenario, string file, string output, bool keep)
{
    var exe = Environment.ProcessPath!;
    var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(exe) == "dotnet") start.ArgumentList.Add(typeof(Program).Assembly.Location);
    foreach (var a in new[] { "run", scenario, file, output }) start.ArgumentList.Add(a);
    using var process = Process.Start(start)!;
    string line = process.StandardOutput.ReadToEnd().Trim();
    string error = process.StandardError.ReadToEnd().Trim();
    process.WaitForExit();
    Console.WriteLine($"{Path.GetFileName(file),-18} {new FileInfo(file).Length / 1048576.0,6:F0} {scenario,-12} {(process.ExitCode == 0 ? line : "failed: " + error.Split('\n')[0])}");
    if (!keep && File.Exists(output)) File.Delete(output);
}

static void RunAll(string dir)
{
    var scenarios = new[] { "open", "text-first", "text-all", "incremental", "full", "sign", "validate", "merge" };
    var exe = Environment.ProcessPath!;
    var self = typeof(Program).Assembly.Location;
    Console.WriteLine($"{"file",-18} {"MB",6} {"scenario",-12} {"ms",8} {"peak WS MB",11} {"alloc MB",9}");
    foreach (var name in new[] { "images.pdf", "pages.pdf", "pages-objstm.pdf" })
    {
        var file = Path.Combine(dir, name);
        foreach (var scenario in scenarios)
        {
            if (scenario == "text-all" && name == "images.pdf") continue;
            var output = Path.Combine(dir, "out-" + scenario + "-" + name);
            var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(exe) == "dotnet") start.ArgumentList.Add(self);
            foreach (var a in new[] { "run", scenario, file, output }) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            string line = process.StandardOutput.ReadToEnd().Trim();
            string error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            Console.WriteLine($"{name,-18} {new FileInfo(file).Length / 1048576.0,6:F0} {scenario,-12} {(process.ExitCode == 0 ? line : "failed: " + error.Split('\n')[0])}");
            if (File.Exists(output) && scenario != "sign") File.Delete(output);
        }
    }
}

static void RunScenario(string scenario, string file, string output)
{
    // warm up the JIT on a tiny document so that only the work on the large file is measured
    using (var warm = PdfDocument.Create()) { warm.AddPage(); warm.ToArray(); }
    long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var stopwatch = Stopwatch.StartNew();
    switch (scenario)
    {
        case "open":
        {
            using var doc = PdfDocument.Open(file);
            _ = doc.PageCount;
            break;
        }
        case "text-first":
        {
            using var doc = PdfDocument.Open(file);
            _ = doc.GetPage(1).ExtractText();
            break;
        }
        case "text-all":
        {
            using var doc = PdfDocument.Open(file);
            _ = PdfTextExtractor.ExtractText(doc);
            break;
        }
        case "incremental":
        {
            using var doc = PdfDocument.Open(file);
            doc.Info.Title = "Changed";
            doc.Save(output, new PdfSaveOptions { Incremental = true });
            break;
        }
        case "full":
        {
            using var doc = PdfDocument.Open(file);
            doc.Save(output);
            break;
        }
        case "sign":
        {
            using var certificate = CreateCertificate();
            var signed = PdfSigner.Sign(File.ReadAllBytes(file), certificate, new PdfSignatureOptions());
            File.WriteAllBytes(output, signed);
            break;
        }
        case "sign-file":
        {
            using var certificate = CreateCertificate();
            PdfSigner.SignFile(file, output, certificate);
            break;
        }
        case "validate-file":
        {
            var signedFile = Path.Combine(Path.GetDirectoryName(output)!, "out-sign-file-" + Path.GetFileName(file).Replace(".pdf", "") + ".pdf");
            var results = PdfSignatureValidator.Validate(signedFile);
            if (results.Count != 1 || !results[0].IsIntegrityValid) throw new InvalidOperationException("The signature is not valid.");
            break;
        }
        case "validate":
        {
            var signedFile = Path.Combine(Path.GetDirectoryName(output)!, "out-sign-" + Path.GetFileName(file));
            var results = PdfSignatureValidator.Validate(File.ReadAllBytes(signedFile));
            if (results.Count != 1 || !results[0].IsIntegrityValid) throw new InvalidOperationException("The signature is not valid.");
            break;
        }
        case "merge":
        {
            using var merged = PdfDocument.Merge(file, file);
            merged.Save(output);
            break;
        }
        default:
            throw new ArgumentException("Unknown scenario " + scenario);
    }
    stopwatch.Stop();
    long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    Console.WriteLine($"{stopwatch.ElapsedMilliseconds,8} {process.PeakWorkingSet64 / 1048576.0,11:F0} {allocated / 1048576.0,9:F0}");
}

static X509Certificate2 CreateCertificate()
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=Benchmark", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var now = DateTimeOffset.UtcNow;
    return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
}
