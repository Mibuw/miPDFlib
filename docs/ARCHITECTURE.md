# Architecture

This document describes the internal structure of miPDFlib for contributors.

## Layers

```
 High-level API     PdfDocument · PdfPage · PdfCanvas · Forms · Signatures · TextExtractor
 ───────────────────────────────────────────────────────────────────────────────────────
 Object model       PdfObject and subclasses (MiPdf.Objects)
 ───────────────────────────────────────────────────────────────────────────────────────
 File I/O           PdfLexer · PdfObjectParser · PdfObjectStore · PdfFileWriter (MiPdf.IO)
                    Stream filters (MiPdf.Filters) · Security handlers (MiPdf.Security)
```

## Object model (`MiPdf.Objects`)

| Class | PDF type | Notes |
|---|---|---|
| `PdfNull`, `PdfBoolean` | null, boolean | singletons |
| `PdfNumber` | integer / real | keeps the integer/real distinction; reals are written without exponent |
| `PdfString` | string | raw bytes; `Text` decodes PDFDocEncoding / UTF-16BE / UTF-8 |
| `PdfName` | name | interned; implicit conversion from `string` (`dict["Type"]`) |
| `PdfArray` | array | `IList<PdfObject>`; typed getters resolve references |
| `PdfDictionary` | dictionary | indexer returns raw values, typed getters (`GetInt`, `GetDictionary`, ...) resolve references; assigning `null` removes a key |
| `PdfStream` | stream | derives from `PdfDictionary`; `RawBytes` (encoded) vs. `GetBytes()` (decoded) |
| `PdfReference` | indirect reference | bound to the owning document; `Resolve()` loads the target lazily |

Objects are mutable. A dictionary or array can contain direct objects or references. Streams must be
indirect; the writer automatically allocates object numbers for streams that were stored directly.

## Reading (`PdfObjectStore`)

1. The whole file is held in memory (`byte[]`).
2. `startxref` is located at the end of the file; xref sections are read following `/Prev`.
   Supported: classic xref tables, xref streams (PDF 1.5), hybrid files (`/XRefStm`), object streams.
3. Objects are loaded **lazily** on first access and cached.
4. **Repair**: if the xref information is missing or inconsistent (wrong offsets, missing trailer,
   broken `/Prev` chain), the file is scanned for `n g obj` headers, trailers, xref streams and object
   streams, and the cross-reference table is rebuilt. `PdfDocument.WasRepaired` reports this.
5. Stream data is read using `/Length`; if the length is wrong, the data is delimited by `endstream`.
6. Encrypted files: strings and streams are decrypted while loading (see Security).

## Writing (`PdfFileWriter`)

- **Full save**: objects reachable from the trailer (`/Root`, `/Info`, `/Encrypt`) are collected
  breadth-first and renumbered compactly. Unreachable objects are dropped (garbage collection).
  Options: classic xref table, or object streams + xref stream (`PdfSaveOptions.UseObjectStreams`).
- **Incremental save** (`PdfSaveOptions.Incremental`): the original bytes are copied unchanged and
  only modified/new objects are appended with a new xref section pointing to the previous one.
  This is required to keep existing digital signatures valid.

### Change detection for incremental updates

Objects do not track their own modifications. Instead, every *loaded* object is compared with a
fresh copy parsed from the original file (by serializing both). Unloaded objects cannot have been
modified. This makes change tracking reliable even when users manipulate dictionaries directly.

## Pages

The page tree is flattened into a list on first access. Inheritable attributes (`/Resources`,
`/MediaBox`, `/CropBox`, `/Rotate`) are resolved through the `/Parent` chain. When pages are
added, removed or reordered, the tree is rewritten as a single flat `/Pages` node before saving
and inherited attributes are copied into the page dictionaries.

## Validation

- Unit tests: `tests/MiPdf.Tests`.
- Real-world corpus: during development, a corpus of third-party PDFs is opened, every stream is
  decoded and the file is re-written in all three modes (full, object streams, incremental); the
  results are re-opened and checked with Ghostscript.
