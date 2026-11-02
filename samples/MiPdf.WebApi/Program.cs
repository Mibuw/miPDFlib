// miPDFlib web API sample: create an invoice PDF, sign uploaded PDFs (PAdES) and validate signatures.
// Run: dotnet run --project samples/MiPdf.WebApi   (or with Docker, see README.md)
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MiPdf;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Layout;
using MiPdf.Signatures;

var builder = WebApplication.CreateBuilder(args);
const long MaxUploadSize = 20 * 1024 * 1024;
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MaxUploadSize);

var app = builder.Build();
var signer = CreateSigner(app.Configuration, app.Logger);
var timestampUrl = app.Configuration["Signing:TimestampUrl"];
ITimestampClient? tsa = string.IsNullOrEmpty(timestampUrl) ? null : new HttpTimestampClient(timestampUrl);

app.MapGet("/", () => Results.Text(
    "miPDFlib web API sample\n" +
    "POST /invoices  JSON invoice -> PDF\n" +
    "POST /sign      multipart file -> signed PDF (PAdES)\n" +
    "POST /validate  multipart file -> signature report (JSON)\n"));

// JSON -> invoice PDF
app.MapPost("/invoices", (InvoiceRequest invoice) =>
{
    if (invoice.Items is not { Count: > 0 }) return Results.BadRequest("The invoice needs at least one item.");
    return Results.File(CreateInvoice(invoice), "application/pdf", $"invoice-{invoice.Number}.pdf");
});

// uploaded PDF -> signed PDF
app.MapPost("/sign", async (IFormFile file, string? reason) =>
{
    var pdf = await ReadUpload(file);
    try
    {
        var result = PdfSigner.SignDetailed(pdf, signer, new PdfSignatureOptions
        {
            Reason = reason ?? "Approved",
            TimestampClient = tsa,
            TimestampOptional = true,             // sign without time-stamp (B-B) if the TSA is unreachable
        });
        return Results.File(result.Pdf, "application/pdf", "signed-" + Path.GetFileName(file.FileName));
    }
    catch (PdfException ex)
    {
        return Results.BadRequest(ex.Message);    // damaged or encrypted document, already signed field, ...
    }
}).DisableAntiforgery();

// uploaded PDF -> signature report
app.MapPost("/validate", async (IFormFile file) =>
{
    var pdf = await ReadUpload(file);
    try
    {
        return Results.Ok(PdfSignatureValidator.Validate(pdf).Select(r => new
        {
            r.FieldName,
            r.SignerName,
            r.IsIntegrityValid,
            r.CoversWholeDocument,
            Level = r.Level?.ToString(),
            r.SigningTime,
            r.TimestampTime,
            r.Errors,
        }));
    }
    catch (PdfException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).DisableAntiforgery();

app.Run();

static async Task<byte[]> ReadUpload(IFormFile file)
{
    using var buffer = new MemoryStream();
    await file.CopyToAsync(buffer);
    return buffer.ToArray();
}

static byte[] CreateInvoice(InvoiceRequest invoice)
{
    using var pdf = PdfDocument.Create();
    pdf.Info.Title = "Invoice " + invoice.Number;
    var font = PdfStandardFont.Helvetica;
    var bold = PdfStandardFont.HelveticaBold;
    using (var layout = new LayoutDocument(pdf, PageSize.A4, new Thickness(50)))
    {
        layout.Footer = page => page.Canvas.DrawText($"Page {page.PageNumber} of {page.TotalPages}", font, 8,
            page.ContentArea.Right, 30, alignment: TextAlignment.Right);
        layout.Add(new Paragraph(invoice.Customer ?? "", font, 10) { SpacingAfter = 20 });
        layout.Add(new Paragraph("Invoice " + invoice.Number, bold, 16) { SpacingAfter = 12 });
        var table = new Table(4, 1, 1.4, 1.4) { HeaderFont = bold, SpacingAfter = 12 };
        foreach (var header in new[] { "Description", "Quantity", "Unit price", "Amount" }) table.AddHeaderCell(header);
        decimal total = 0;
        foreach (var item in invoice.Items!)
        {
            decimal amount = item.Quantity * item.UnitPrice;
            total += amount;
            table.AddCell(item.Description);
            table.AddCell(new Cell(item.Quantity.ToString("0.##")) { TextAlignment = TextAlignment.Right });
            table.AddCell(new Cell(item.UnitPrice.ToString("N2")) { TextAlignment = TextAlignment.Right });
            table.AddCell(new Cell(amount.ToString("N2")) { TextAlignment = TextAlignment.Right });
        }
        table.AddCell(new Cell("Total", colSpan: 3) { Font = bold, TextAlignment = TextAlignment.Right });
        table.AddCell(new Cell(total.ToString("N2")) { Font = bold, TextAlignment = TextAlignment.Right });
        layout.Add(table);
    }
    return pdf.ToArray();
}

// Signing certificate from configuration (Signing:PfxPath, Signing:PfxPassword), otherwise a self-signed demo certificate.
static IExternalSigner CreateSigner(IConfiguration configuration, ILogger logger)
{
    var path = configuration["Signing:PfxPath"];
    if (!string.IsNullOrEmpty(path)) return CertificateSigner.FromPfx(path, configuration["Signing:PfxPassword"]);
    logger.LogWarning("Signing:PfxPath is not set – signing with a self-signed demo certificate.");
    using var key = RSA.Create(3072);
    var request = new CertificateRequest("CN=miPDFlib Web API Demo", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    // round trip through PKCS#12 so that the key is usable on every platform
    return new CertificateSigner(X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null));
}

record InvoiceItem(string Description, decimal Quantity, decimal UnitPrice);

record InvoiceRequest(string Number, string? Customer, List<InvoiceItem>? Items);
