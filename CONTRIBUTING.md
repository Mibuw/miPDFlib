# Contributing to miPDFlib

Thank you for helping to improve miPDFlib! Bug reports with a reproducible example are the most valuable
contribution – a PDF library gets robust through the unusual files of its users.

## Reporting bugs and requesting features

- Use the issue templates (*Issues → New issue*). Please state the miPDFlib version, the .NET version and the
  operating system, and add a minimal code example.
- **Sample PDFs:** attach a file only if it contains no confidential or personal data. If the problem only
  occurs with a confidential document, try to reproduce it with a document you create yourself, or describe the
  producing software (`/Producer`) and the error.
- **Security problems** are reported privately, never in a public issue – see [SECURITY.md](SECURITY.md).
- Questions about using the library are welcome as issues as well.

There is no guaranteed response time; the project is maintained alongside other work.

## Pull requests

1. For larger changes, open an issue first so that the approach can be agreed on before you invest time.
2. Fork the repository and create a branch from `main`.
3. Make your change, add tests and update the documentation (see below).
4. Run `dotnet build -c Release` and `dotnet test -c Release` – both must pass without errors.
5. Open the pull request and fill in the checklist.
6. On your first pull request the CLA bot asks you to accept the [Contributor License Agreement](CLA.md) by
   posting a comment. This is needed once.

### Why a CLA?

You keep the copyright in your contribution. The CLA grants the maintainer the rights to distribute it as part
of miPDFlib – under the Apache License 2.0 and, if ever needed, under other license terms – and confirms that the
code is yours to contribute. This keeps the licensing of the project clear for all users.

## Rules for code

- **Clean room:** implement from the public specifications (ISO 32000, ISO 19005, ISO 14289, ETSI EN 319 142, the
  RFCs). Do not copy or translate code from iText, Syncfusion, PDFBox, pdf.js or any other project, and do not
  read their sources while implementing the same feature.
- **No new dependencies** without prior discussion. The library deliberately depends only on
  `System.Security.Cryptography.Pkcs`. No native code, no `unsafe`.
- **Targets:** net8.0 and net10.0. Everything must work on Windows, Linux and macOS.
- **Untrusted input:** parsing code must stay within `PdfLimits`, detect cycles and raise `PdfException` for
  malformed input (see [SECURITY.md](SECURITY.md) and [ARCHITECTURE.md](docs/ARCHITECTURE.md)).
- **Style:** follow the style of the surrounding code (nullable reference types enabled, file-scoped namespaces,
  XML documentation for public APIs). Comments, documentation and commit messages are written in English.
- **Public API:** keep breaking changes to a minimum and describe them in the pull request.

## Tests

- Every bug fix comes with a regression test, every feature with tests (xUnit, `tests/MiPdf.Tests`).
- Prefer round-trip tests: create → save → open with the library's own parser → check.
- Test documents are generated in the test code. Small binary fixtures may be added to `tests/MiPdf.Tests/Resources`
  if they are synthetic and free of confidential or personal data.
- Signature tests use the test PKI in the test project; never commit real certificates or private keys.

## Documentation

- Update the guides in `docs/guide` and the XML documentation for public API changes.
- Add an entry to [CHANGELOG.md](CHANGELOG.md) under "Unreleased".
- `README.md` is shown on GitHub, `src/MiPdf/NUGET-README.md` on nuget.org (Markdown only, no HTML, absolute links).
  Keep both in sync when features or links change.

## License

By contributing you agree that your contributions are licensed under the [Apache License 2.0](LICENSE) and
the terms of the [CLA](CLA.md).
