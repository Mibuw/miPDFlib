// miPDFlib samples. Usage: dotnet run --project samples/MiPdf.Samples [output directory]
// Every sample writes a PDF into the output directory (default: ./samples-output).
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MiPdf;
using MiPdf.Barcodes;
using MiPdf.Fonts;
using MiPdf.Forms;
using MiPdf.Graphics;
using MiPdf.IO;
using MiPdf.Layout;
using MiPdf.Security;
using MiPdf.Signatures;

string output = args.Length > 0 ? args[0] : "samples-output";
Directory.CreateDirectory(output);
string Out(string name) => Path.Combine(output, name);

Invoice(Out("01-invoice.pdf"));
MergeAndStamp(Out("01-invoice.pdf"), Out("02-merged-stamped.pdf"));
FormFillAndFlatten(Out("03-form.pdf"), Out("03-form-flattened.pdf"));
ExtractText(Out("01-invoice.pdf"));
Encrypt(Out("01-invoice.pdf"), Out("05-encrypted.pdf"));
Sign(Out("01-invoice.pdf"), Out("06-signed.pdf"));
Accessible(Out("07-accessible.pdf"));
Console.WriteLine($"Samples written to {Path.GetFullPath(output)}");

// 1. Generate a multi-page invoice with the flow layout
static void Invoice(string path)
{
    using var pdf = PdfDocument.Create();
    pdf.Info.Title = "Invoice 2026-0042";
    pdf.Info.Author = "miPDFlib Samples";
    var font = PdfStandardFont.Helvetica;
    var bold = PdfStandardFont.HelveticaBold;
    using (var layout = new LayoutDocument(pdf, PageSize.A4, new Thickness(50, 60, 50, 60)))
    {
        layout.Footer = ctx => ctx.Canvas.DrawText($"Page {ctx.PageNumber} of {ctx.TotalPages}", font, 8,
            ctx.ContentArea.Right, 35, alignment: TextAlignment.Right);
        layout.Add(new Paragraph("Invoice No. 2026-0042", bold, 20) { SpacingAfter = 12, Color = PdfColor.FromHex("#1F4E79") });
        layout.Add(new Paragraph("Thank you for your order. We invoice the following services:") { SpacingAfter = 8 });
        var table = new Table(0.6, 4, 1, 1.4) { AlternateRowBackground = PdfColor.Gray(0.96), SpacingAfter = 10 };
        foreach (var h in new[] { "Item", "Description", "Quantity", "Amount" }) table.AddHeaderCell(h);
        decimal sum = 0;
        for (int i = 1; i <= 40; i++)
        {
            decimal amount = i * 12.5m;
            sum += amount;
            table.AddCell(i.ToString());
            table.AddCell($"Service {i}");
            table.AddCell(new Cell("1") { TextAlignment = TextAlignment.Right });
            table.AddCell(new Cell($"{amount:N2} €") { TextAlignment = TextAlignment.Right });
        }
        table.AddCell(new Cell("Total", colSpan: 3) { Font = bold, TextAlignment = TextAlignment.Right });
        table.AddCell(new Cell($"{sum:N2} €") { Font = bold, TextAlignment = TextAlignment.Right });
        layout.Add(table);
        layout.Add(new Paragraph("Payable within 14 days without deduction.") { Alignment = TextAlignment.Justify, SpacingAfter = 8 });
        // payment QR code ("Zahlen mit Code" / GiroCode)
        layout.Add(new BarcodeElement(EpcQrCode.Create(new EpcPayment
        {
            Name = "miPDFlib Samples Ltd.",
            Iban = "AT61 1904 3002 3457 3201",
            Amount = sum,
            Text = "Invoice 2026-0042",
        }), 80) { AlternateText = "QR code for the bank transfer" });
    }
    pdf.Save(path, new PdfSaveOptions { UseObjectStreams = true });
}

// 2. Merge documents, add a watermark and bookmarks
static void MergeAndStamp(string source, string path)
{
    using var merged = PdfDocument.Merge(source, source);
    merged.Outlines.AddChild("Original", merged.GetPage(1));
    merged.Outlines.AddChild("Kopie", merged.GetPage(merged.PageCount / 2 + 1));
    for (int i = merged.PageCount / 2 + 1; i <= merged.PageCount; i++)
        merged.GetPage(i).AddTextWatermark("KOPIE", opacity: 0.2);
    merged.Save(path);
}

// 3. Create a form, fill it and flatten a copy
static void FormFillAndFlatten(string path, string flattenedPath)
{
    using (var doc = PdfDocument.Create())
    {
        var page = doc.AddPage();
        var form = doc.GetOrCreateForm();
        using (var c = page.GetCanvas())
        {
            c.DrawText("Name:", PdfStandardFont.Helvetica, 11, 50, 755);
            c.DrawText("Agreed:", PdfStandardFont.Helvetica, 11, 50, 715);
            c.DrawText("Country:", PdfStandardFont.Helvetica, 11, 50, 675);
        }
        form.AddTextField(page, "name", new PdfRectangle(150, 750, 400, 770));
        form.AddCheckBox(page, "agree", new PdfRectangle(150, 710, 164, 724));
        form.AddComboBox(page, "country", new PdfRectangle(150, 670, 300, 688), new[] { "Austria", "Germany", "Switzerland" });
        doc.Save(path);
    }
    using var filled = PdfDocument.Open(path);
    var f = filled.GetForm()!;
    f.SetValue("name", "Jane Doe");
    f.GetField<PdfCheckBox>("agree")!.IsChecked = true;
    f.SetValue("country", "Austria");
    f.Flatten();
    filled.Save(flattenedPath);
}

// 4. Extract text with positions
static void ExtractText(string path)
{
    using var doc = PdfDocument.Open(path);
    var first = doc.GetPage(1).ExtractText().Split('\n').First();
    var hit = doc.GetPage(1).FindText("Total").FirstOrDefault();
    Console.WriteLine($"First line: {first}; 'Total' at {hit}");
}

// 5. Encrypt with AES-256
static void Encrypt(string source, string path)
{
    using var doc = PdfDocument.Open(source);
    doc.Encrypt(new PdfEncryptionOptions
    {
        UserPassword = "user",
        OwnerPassword = "owner",
        Permissions = PdfPermissions.Print | PdfPermissions.CopyContents,
    });
    doc.Save(path);
}

// 6. Sign with a (self-signed demo) certificate, visible signature
static void Sign(string source, string path)
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=Demo Signer, O=miPDFlib Samples", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    var signed = PdfSigner.Sign(File.ReadAllBytes(source), certificate, new PdfSignatureOptions
    {
        Reason = "Approval",
        Location = "Vienna",
        Rectangle = new PdfRectangle(350, 60, 545, 115),
        Appearance = new PdfSignatureAppearance { Border = PdfColor.Blue },
        // TimestampClient = new HttpTimestampClient("http://timestamp.digicert.com"),
    });
    File.WriteAllBytes(path, signed);
    foreach (var result in PdfSignatureValidator.Validate(signed)) Console.WriteLine(result);
}

// 7. Accessible (tagged) document conforming to PDF/UA-1 and PDF/A-2u
static void Accessible(string path)
{
    string? fontFile = new[] { @"C:\Windows\Fonts\arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" }.FirstOrDefault(File.Exists);
    if (fontFile == null) return; // PDF/UA requires an embeddable font
    var font = PdfTrueTypeFont.FromFile(fontFile);
    using var pdf = PdfDocument.Create();
    pdf.Info.Title = "Accessible document";
    pdf.Language = "en-US";
    pdf.UAConformance = PdfUAConformance.PdfUA1;
    pdf.Conformance = MiPdf.PdfA.PdfAConformance.PdfA2U;
    using (var layout = new LayoutDocument(pdf))
    {
        layout.Footer = ctx => ctx.Canvas.DrawText($"Page {ctx.PageNumber} of {ctx.TotalPages}", font, 8,
            ctx.ContentArea.Right, 30, alignment: TextAlignment.Right);
        layout.Add(new Paragraph("Accessible PDF", font, 20).AsHeading(1));
        layout.Add(new Paragraph("Headings, paragraphs, tables and images are tagged automatically.", font, 10) { SpacingAfter = 8 });
        var table = new Table(1, 3) { Font = font, HeaderFont = font };
        table.AddHeaderCell("No.");
        table.AddHeaderCell("Name");
        for (int i = 1; i <= 5; i++)
        {
            table.AddCell(i.ToString());
            table.AddCell($"Entry {i}");
        }
        layout.Add(table);
    }
    Console.WriteLine($"PDF/UA check: {(pdf.CheckPdfUA().Count == 0 ? "ok" : string.Join("; ", pdf.CheckPdfUA()))}");
    pdf.Save(path);
}
