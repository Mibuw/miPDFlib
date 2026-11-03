# Benchmarks

`MiPdf.Benchmarks` measures time and memory for large documents. It creates three test files and runs every
scenario in its own process, so the peak working set belongs to that scenario alone:

| File | Content |
|---|---|
| `images.pdf` | 100 pages with one 3 MB image each (300 MB; large streams, few objects) |
| `pages.pdf` | 20,000 pages of text (9 MB; many small objects) |
| `pages-objstm.pdf` | the same with object streams and a cross-reference stream (6 MB) |

```
dotnet run -c Release --project benchmarks/MiPdf.Benchmarks -- all <directory>
```

The test files are created in `<directory>` (default: a folder in the temp directory) and reused by later runs.
`run <scenario> <file> <output>` runs a single scenario.

## Results

Peak working set in MB, .NET 8, Windows 11. "Before" is release 0.5.0.

| Scenario | images.pdf before | images.pdf now | pages.pdf before | pages.pdf now |
|---|---:|---:|---:|---:|
| open + page count | 328 | 328 | 91 | 91 |
| text of page 1 | 332 | 329 | 93 | 93 |
| text of all pages | – | – | 275 | 278 |
| incremental save | 1,228 | 328 | 71 | 43 |
| full save | 1,602 | 329 | 135 | 107 |
| sign (`byte[]` in and out) | 1,536 | 635 | 131 | 110 |
| validate the signature | 934 | 332 | 114 | 95 |
| merge the file with itself | 3,350 | 631 | 316 | 246 |

Opening reads the whole file into memory; objects are loaded lazily, so only what is used is parsed. Signing a
`byte[]` needs the input and the signed output; signatures are created and verified from the digest of the signed
byte ranges, without copying them.

## Files larger than 2 GB

```
dotnet run -c Release --project benchmarks/MiPdf.Benchmarks -- huge <directory>
```

creates `huge.pdf` (850 pages with a 3 MB image each, 2.5 GB) and runs the scenarios that read from the file
(`PdfOpenOptions.ReadFromFile`, automatic above 2 GB). Peak working set in MB; before, such files could not be
opened at all:

| Scenario | huge.pdf (2,550 MB) |
|---|---:|
| open + page count | 35 |
| text of page 1 | 37 |
| incremental save to a file | 30 |
| full save to a file | 47 |
| `PdfSigner.SignFile` | 48 |
| `PdfSignatureValidator.Validate(path)` | 41 |
