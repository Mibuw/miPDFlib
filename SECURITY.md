# Security

## Reporting a vulnerability

Please report security problems privately via a GitHub security advisory of this repository
(*Security → Report a vulnerability*) instead of a public issue.

## Threat model

miPDFlib is designed to process **untrusted PDF files** (uploads, e-mail attachments) in server applications.
A malicious document must not be able to crash the process, exhaust memory or CPU without bounds, reach internal
network services, or make an invalid signature appear valid.

### What the library does

| Risk | Protection |
|---|---|
| Decompression bombs | `PdfLimits.MaxDecodedStreamSize` (default 256 MB per stream, checked while inflating Flate/LZW/RunLength) |
| Huge images | `PdfLimits.MaxImagePixels` (default 100 megapixels) for decoded images (PNG input) |
| CPU exhaustion via CMaps / filter chains | `PdfLimits.MaxCMapEntries`, `PdfLimits.MaxFiltersPerStream` |
| Deep nesting, reference cycles, xref `/Prev` loops, page tree cycles, self-referencing XObjects | depth limits and cycle detection in parser, page tree, forms, outlines, text extraction |
| Unexpected exceptions on malformed input | public entry points (`Open`, text extraction) raise `PdfException`; covered by regression and fuzz tests |
| SSRF through certificate URLs (OCSP, CRL, CA issuers) when adding LTV data | `HttpRevocationSource` connects only to public internet addresses (checked per connection, so redirects and DNS rebinding are covered), at most 3 redirects, size limits (CRL 5 MB, others 1 MB), bounded cache, `LtvOptions.MaxCertificates` |
| Oversized time-stamp responses | `HttpTimestampClient.MaxResponseSize` (1 MB) |
| Signature wrapping / byte range manipulation | the validator requires the byte range to start at 0, to exclude exactly the `/Contents` string and reports whether a signature covers the whole file |
| Active content | JavaScript and other actions are never executed; there is no rendering engine and no XML entity processing |

Limits apply process-wide (`PdfLimits.Default`) or per request (`using (PdfLimits.Use(...))`).

### What the application must do

- **Limit the upload size** before calling `PdfDocument.Open` – documents are held in memory. Peak memory for a
  document can reach about twice `MaxDecodedStreamSize` plus the file size.
- **Bound the processing time** (there is no built-in time limit or cancellation): process untrusted documents in a
  worker with a timeout, and limit parallelism.
- `PdfDocument` instances are **not thread-safe**; use one instance per thread/request.
- **Signature validation:** `PdfSignatureValidator` checks integrity, the certificate chain (optional) and time-stamps.
  It does **not** evaluate whether changes after a signature are permitted (DocMDP/FieldMDP), does not check the
  revocation data stored in the document and does not apply EU trusted lists or qualification rules. Treat
  `CoversWholeDocument == false` as "modified after signing" and use EU DSS for legally relevant validation.
- **LTV for internal CAs:** set `HttpRevocationSource.AllowPrivateNetworks` or `AllowedHosts` deliberately; a
  caller-supplied `HttpClient` is used without SSRF checks.
- **Encryption:** use AES-256 (default). RC4 and AES-128 are supported for compatibility only.
- Keep private keys in an HSM or key vault and use `IExternalSigner`; protect PFX passwords.

## Verification

- Regression tests with crafted attack files (`tests/MiPdf.Tests/Security/HardeningTests.cs`) and a deterministic
  mutation fuzz test run in CI.
- Additional local fuzzing: about 6,000 mutated real-world documents without crashes or hangs.
