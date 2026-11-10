# Error handling and diagnostics

miPDFlib distinguishes two kinds of problems:

- **Errors** stop an operation and are thrown as `PdfException` – a file that cannot be parsed at all
  (`PdfParseException`, with the byte offset), a wrong password (`PdfPasswordException`), limits exceeded
  (`PdfLimits`) or a call that is not possible. Damaged input never escapes as `IndexOutOfRangeException`,
  `InvalidCastException` or similar: catching `PdfException` is enough.
- **Tolerated problems** do not stop anything: real-world files are often slightly broken, and miPDFlib repairs or
  skips what it can, like PDF viewers do. These problems are reported as **diagnostics**, so that you can log them
  or decide whether a result is trustworthy.

```csharp
using MiPdf;

try
{
    using var doc = PdfDocument.Open("input.pdf");
    string text = doc.GetPage(1).ExtractText();

    foreach (var diagnostic in doc.Diagnostics)
        Console.WriteLine(diagnostic); // e.g. "Warning ObjectUnreadable (object 12): Object 12 is not where ..."
}
catch (PdfPasswordException)
{
    // encrypted, wrong or missing password
}
catch (PdfException ex)
{
    // the file cannot be processed
}
```

## Diagnostics

`PdfDocument.Diagnostics` collects what was tolerated so far – while opening and while the document is used: text and
image extraction, rendering. Each problem is listed once, at most 1,000 entries. To receive them as they occur, set
a callback when opening:

```csharp
var options = new PdfOpenOptions
{
    OnDiagnostic = d => Console.WriteLine($"{d.Severity}: {d.Message}"),
};
using var doc = PdfDocument.Open("input.pdf", options);
```

The callback runs synchronously on the thread that does the work.

A `PdfDiagnostic` has:

| Property | Meaning |
|---|---|
| `Severity` | `Warning`: damaged or unsupported input, the result may differ from what the author intended; `Information`: worth knowing, the result is as intended |
| `Code` | a stable identifier, see below |
| `Message` | a description in English |
| `PageNumber` | the page (1-based) the problem occurred on, if page-specific |
| `ObjectNumber` | the indirect object concerned, if known |

| Code | Severity | When |
|---|---|---|
| `XrefRepaired` | Warning | the cross-reference table is damaged; the objects were located by scanning the file (also `PdfDocument.WasRepaired`) |
| `ObjectUnreadable` | Warning | an object is not where the cross-reference table says or cannot be read; it is treated as null |
| `StreamDamaged` | Warning | compressed stream data is damaged; the data decoded before the error is used |
| `ContentDamaged` | Warning | a content stream cannot be parsed further; the rest of it is ignored |
| `ToUnicodeDamaged` | Warning | the ToUnicode map of a font cannot be read; extracted text of that font may be wrong |
| `ImageNotDecoded` | Warning | an image cannot be decoded (damaged data, unsupported color space) and is not shown |
| `FontSubstituted` | Information | rendering: a font is not embedded and is drawn with an installed substitute |
| `FontMissing` | Warning | rendering: a font is not embedded and no substitute is installed; its text is not drawn |
| `FontDamaged` | Warning | rendering: an embedded font program cannot be read; a substitute is used |
| `PaintNotSupported` | Warning | rendering: a shading or pattern is invalid or unsupported and is not painted |
| `NestingTooDeep` | Warning | rendering: content is nested too deeply or uses itself; the inner part is not drawn |

New codes may be added in later versions; treat unknown codes by their `Severity`.

Signing, LTV and redaction report their own results in `Warnings` lists on their result objects (for example
`PdfSigner` and `PdfRedactor`); signature validation reports in `SignatureValidationResult`.

## Logging with Microsoft.Extensions.Logging

miPDFlib has no dependency on a logging framework. Connecting it to an `ILogger` takes one line:

```csharp
using Microsoft.Extensions.Logging;

var options = new PdfOpenOptions
{
    OnDiagnostic = d => logger.Log(
        d.Severity == PdfDiagnosticSeverity.Warning ? LogLevel.Warning : LogLevel.Information,
        "PDF {Code} (page {Page}, object {Object}): {Message}", d.Code, d.PageNumber, d.ObjectNumber, d.Message),
};
```

The same works with Serilog, NLog or any other logger.
