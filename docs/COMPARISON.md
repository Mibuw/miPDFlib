# miPDFlib compared with iText and Syncfusion PDF

A short, practical comparison for the tasks that matter in form and signing projects. It is not a benchmark. A
detailed version with background and examples is the article
[iText or Syncfusion PDF? A practical comparison for forms and digital signatures in .NET](https://www.mitterbucher.com/en/articles/itext-vs-syncfusion-pdf/)
([German](https://www.mitterbucher.com/artikel/itext-vs-syncfusion-pdf/)). For switching code over, see the
[migration guide](MIGRATION.md).

iText and Syncfusion PDF are mature, widely used libraries with a much larger feature set and commercial support.
miPDFlib is young; its strengths are the licence, signatures without BouncyCastle and archiving formats.

## Licence

| | iText | Syncfusion PDF | miPDFlib |
|---|---|---|---|
| Licence | AGPLv3 or commercial | commercial; free Community License for small companies (revenue, team and capital limits) | Apache-2.0 |
| Closed commercial product | commercial licence required | commercial licence, or Community License within its limits | allowed, free of charge |
| Obligations | AGPL: disclose the source of your application; producer line in every PDF | licence key registration, otherwise a trial watermark | keep LICENSE and NOTICE when redistributing |
| Commercial support | yes | yes | no |

## PDF handling

| | iText | Syncfusion PDF | miPDFlib |
|---|---|---|---|
| Incremental saving | `StampingProperties.UseAppendMode()` | `PdfFileStructure.IncrementalUpdate = true` | `new PdfSaveOptions { Incremental = true }`; signing is always incremental |
| Office documents to PDF | – | in the same suite | – (recommended: LibreOffice via Gotenberg, see README) |

## Forms (AcroForms)

| Task | iText | Syncfusion PDF | miPDFlib |
|---|---|---|---|
| Load the form | `PdfAcroForm.GetAcroForm(doc, …)` | `PdfLoadedDocument.Form` | `doc.GetForm()` |
| Read fields | `GetAllFormFields()` | typed classes such as `PdfLoadedTextBoxField` | `form.Fields`, typed via `GetField<PdfTextField>("name")` |
| Create fields | builder classes, e.g. `TextFormFieldBuilder` | a class per field type, e.g. `PdfTextBoxField` | `form.AddTextField(..)`, `AddCheckBox(..)`, `AddSignatureField(..)` |
| Flatten | via the AcroForm object | `Flatten = true` / `FlattenFields()` | `form.Flatten()` |
| Data exchange | field API | additionally FDF import/export | `form.GetValues()`, FDF and XFDF import/export, XFA data |

## Digital signatures

| Topic | iText | Syncfusion PDF | miPDFlib |
|---|---|---|---|
| Signing | `PdfSigner`, CMS or CAdES | `PdfSignature`, CMS or CAdES | `PdfSigner.Sign`, CMS or CAdES (default: PAdES) |
| PAdES levels | `PdfPadesSigner`: B, T, LT, LTA | timestamps, LTV, archive timestamps | B-B, B-T; B-LT and B-LTA via `PdfLtv` |
| External signing (HSM, cloud) | `IExternalSignature`, `IExternalSignatureContainer` | `ComputeHash` event, `IPdfExternalSigner` | `IExternalSigner`; two-step `PrepareExternalSignature` / `CompleteExternalSignature` |
| Custom signed attributes | via your own CMS container | via `ComputeHash` and your own CMS | `AdditionalSignedAttributes` |
| Certification and field locks | DocMDP, FieldMDP | DocMDP | DocMDP (`Certification`), FieldMDP (`FieldLock`) |
| Validation | validation module (since 9) | built in | `PdfSignatureValidator` (integrity, chain, coverage, timestamp, PAdES level, field locks, revocation status from the embedded OCSP responses and CRLs) |
| Cryptography | BouncyCastle (mandatory adapter) | own implementation; BouncyCastle or .NET for custom CMS | .NET (`System.Security.Cryptography.Pkcs`), no BouncyCastle |

## When to choose what

- **Open-source project under the AGPL:** iText – mature, with very comprehensive signature support.
- **Closed product of a small company within the Community License limits:** Syncfusion PDF.
- **Office conversion in the same workflow:** Syncfusion.
- **Commercial support, FIPS or the widest feature set:** iText or Syncfusion with a commercial licence.
- **Closed product without licence costs and without revenue or size limits, focused on forms, PAdES signatures,
  PDF/A, PDF/UA or e-invoices:** miPDFlib – test it with your own documents before going live.

Information on iText and Syncfusion as of October 2026, from the vendors' documentation; the binding licence terms
are those of the vendors.
