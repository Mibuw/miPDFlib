# miPDFlib web API sample

A minimal ASP.NET Core API that creates, signs and validates PDF documents with miPDFlib.

| Endpoint | Input | Output |
|---|---|---|
| `POST /invoices` | invoice as JSON | invoice PDF |
| `POST /sign` | PDF upload (`file`), optional `?reason=` | signed PDF (PAdES, with time-stamp if configured) |
| `POST /validate` | PDF upload (`file`) | signature report as JSON |

## Run

```bash
dotnet run --project samples/MiPdf.WebApi --urls http://localhost:8080
```

or with Docker (from the repository root):

```bash
docker build -f samples/MiPdf.WebApi/Dockerfile -t mipdflib-webapi .
docker run --rm -p 8080:8080 mipdflib-webapi
```

## Try it

```bash
curl -X POST http://localhost:8080/invoices -H "Content-Type: application/json" -o invoice.pdf \
  -d '{"number":"2026-0042","customer":"Example Buyer AG","items":[{"description":"Consulting","quantity":10,"unitPrice":100}]}'

curl -X POST "http://localhost:8080/sign?reason=Approved" -F "file=@invoice.pdf" -o invoice-signed.pdf

curl -X POST http://localhost:8080/validate -F "file=@invoice-signed.pdf"
```

## Configuration

Without configuration the API signs with a self-signed demo certificate (logged as a warning). For real signatures
set the certificate and, for PAdES B-T, a time-stamp authority – as environment variables, for example:

```bash
docker run --rm -p 8080:8080 -v /path/to/certs:/certs:ro \
  -e Signing__PfxPath=/certs/signer.pfx -e Signing__PfxPassword=... \
  -e Signing__TimestampUrl=http://timestamp.digicert.com \
  mipdflib-webapi
```

Keep the password in a secret store in production. Uploads are limited to 20 MB; miPDFlib's resource limits
(`PdfLimits`) protect against decompression bombs and similar crafted files. For keys in an HSM or a cloud signing
service, replace `CertificateSigner` with your own `IExternalSigner`
(see [How to sign a PDF with PAdES in .NET](../../docs/howto/sign-pdf-pades-dotnet.md)).
