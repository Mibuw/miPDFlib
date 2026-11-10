# Barcodes

Namespace `MiPdf.Barcodes`. All barcodes are drawn as vector graphics (one filled path), so they stay sharp in print.

| Class | Symbology | Notes |
|---|---|---|
| `QrCode` | QR Code model 2, versions 1–40, levels L/M/Q/H | numeric/alphanumeric/byte (UTF-8) mode chosen automatically, optional UTF-8 ECI |
| `EpcQrCode` | EPC QR ("GiroCode", "Zahlen mit Code") | SEPA credit transfer payload per EPC069-12 |
| `DataMatrix` | Data Matrix ECC 200, square and rectangular | ASCII (digit pairs) / Base 256, GS1 Data Matrix via `CreateGs1` |
| `Pdf417` | PDF417 (ISO/IEC 15438), error correction levels 0–8 | text/numeric/byte compaction chosen automatically, UTF-8 with ECI for non-ASCII text |
| `Code128` | Code 128 (sets A/B/C automatic) | GS1-128 via `CreateGs1("(01)...(10)...")` |
| `Ean` | EAN-13, EAN-8, UPC-A | check digit calculated or verified, guard bars and digit groups |
| `Code39` | Code 39 | optional modulo 43 check character |
| `Interleaved2Of5` | ITF / ITF-14 | optional GS1 check digit |

All encoders were verified by rendering the output with Ghostscript and decoding it with zxing-cpp
(all QR versions/levels/masks, all Data Matrix sizes, GS1, every 1D symbology; PDF417 with about 500 symbols of
text, digits, control characters, binary data and Unicode at all error correction levels and column counts).

## Drawing

```csharp
using MiPdf.Barcodes;

using (var canvas = page.GetCanvas())
{
    // 2D codes: square of 80 pt including the quiet zone
    canvas.DrawBarcode(QrCode.Create("https://example.com"), 450, 700, 80);

    // linear codes: rectangle including quiet zone and human-readable text
    canvas.DrawBarcode(Ean.CreateEan13("400638133393"), 72, 700, 150, 70);

    // fixed module size (0.5 pt per module), height 40 pt
    Code128.CreateGs1("(01)09501101530003(10)ABC").DrawWithModuleSize(canvas, 72, 600, 0.5, 40);
}
```

Options on every barcode: `Color`, `Background`, `QuietZone` (modules). Linear barcodes: `ShowText`, `Font`, `FontSize`.

## PDF417

PDF417 is the stacked code on driver's licences and ID cards (AAMVA), boarding passes and shipping labels. Its rows
are three modules high by default; `DrawBarcode` keeps that proportion:

```csharp
var code = Pdf417.Create("@\n\u001e\rANSI 636000090002DL00410278ZV03190008DLDAQT64235789\nDCSSAMPLE\n...");
canvas.DrawBarcode(code, 72, 600, 300, 100);   // as large as fits into 300 × 100 pt, centered

var options = new Pdf417Options { ErrorCorrectionLevel = 5, Columns = 6, RowHeight = 4 };
Pdf417.Create("Shipment 4711, 3 parcels", options).DrawWithModuleSize(canvas, 72, 450, 0.8);   // 0.8 pt per module
```

Without options the error correction level follows the recommendation of ISO/IEC 15438 for the amount of data (2 for
up to 40 codewords, up to 5 for large symbols), and the number of columns is chosen so that the symbol is about
twice as wide as high. Runs of 13 or more digits use numeric compaction, text uses text compaction, everything else
(control characters, binary data) byte compaction. `Pdf417.Create(byte[])` encodes raw bytes; text with characters
outside ASCII is encoded as UTF-8 with an ECI designator, which current scanners understand.

## Layout

```csharp
layout.Add(new BarcodeElement(DataMatrix.Create("ORDER-4711"), width: 60) { Alignment = TextAlignment.Right });
```

In tagged documents (PDF/UA) a `BarcodeElement` becomes a `Figure` whose alternate text defaults to the encoded
text; set `AlternateText` for a better description.

## Payment QR code (EPC / GiroCode)

```csharp
var qr = EpcQrCode.Create(new EpcPayment
{
    Name = "Example Ltd.",
    Iban = "AT61 1904 3002 3457 3201",
    Bic = "BKAUATWW",          // optional within the EEA
    Amount = 123.45m,
    Text = "Invoice 2026-0042", // or Reference = "RF18..." (structured)
});
canvas.DrawBarcode(qr, 450, 60, 90);
```

## Inspecting modules

`MatrixBarcode` exposes `Rows`, `Columns` and an indexer `[row, column]`; `LinearBarcode.Modules` returns the bar/space
sequence. Both can be used to render barcodes elsewhere (e.g. into a bitmap).
