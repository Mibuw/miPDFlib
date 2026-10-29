# Migration guide: iText 7/8 and Syncfusion PDF → miPDFlib

This guide maps the most frequently used APIs. Namespaces: `MiPdf`, `MiPdf.Fonts`, `MiPdf.Graphics`,
`MiPdf.Images`, `MiPdf.Layout`, `MiPdf.Forms`, `MiPdf.Text`, `MiPdf.Security`, `MiPdf.Signatures`,
`MiPdf.PdfA`, `MiPdf.IO` (save options), `MiPdf.Objects` (low level).

## General differences

| Topic | iText | Syncfusion | miPDFlib |
|---|---|---|---|
| Coordinates | bottom-left origin, points | **top-left** origin, points | bottom-left origin, points (PDF native) |
| Reading vs. writing | `PdfReader` + `PdfWriter` | `PdfDocument` vs. `PdfLoadedDocument` | one class: `PdfDocument.Create()` / `PdfDocument.Open()` |
| Saving | on `Close()` | `Save(stream)` + `Close(true)` | `Save(path/stream)` or `ToArray()`; any number of times |
| Font size | part of `SetFont(font, size)` | part of the font object | passed per call (`DrawText(text, font, size, ...)`), font objects are size-independent |
| Append mode | `StampingProperties.UseAppendMode()` | `PdfLoadedDocument.FileStructure.IncrementalUpdate` | `new PdfSaveOptions { Incremental = true }` |

Converting Syncfusion's top-left coordinates: `yPdf = page.Height - ySyncfusion - height` (for rectangles) or
`yPdf = page.Height - ySyncfusion - font.GetAscent(size)` (text baseline from the top of the text box).

## Documents and pages

| Task | iText | Syncfusion | miPDFlib |
|---|---|---|---|
| New document | `new PdfDocument(new PdfWriter(path))` | `new PdfDocument()` | `PdfDocument.Create()` |
| Open | `new PdfDocument(new PdfReader(path))` | `new PdfLoadedDocument(path)` | `PdfDocument.Open(path)` |
| Open with password | `new ReaderProperties().SetPassword(pw)` | `new PdfLoadedDocument(path, pw)` | `PdfDocument.Open(path, pw)` |
| Add page | `pdf.AddNewPage(PageSize.A4)` | `doc.Pages.Add()` | `doc.AddPage(PageSize.A4)` |
| Page count | `pdf.GetNumberOfPages()` | `doc.Pages.Count` | `doc.PageCount` |
| Get page | `pdf.GetPage(n)` (1-based) | `doc.Pages[i]` (0-based) | `doc.GetPage(n)` (1-based) / `doc.Pages[i]` |
| Remove/move pages | `pdf.RemovePage(n)`, `MovePage` | `doc.Pages.RemoveAt(i)` | `doc.RemovePage(i)`, `doc.MovePage(from, to)` |
| Rotate | `page.SetRotation(90)` | `page.Rotation = PdfPageRotateAngle.RotateAngle90` | `page.Rotation = 90` |
| Metadata | `pdf.GetDocumentInfo().SetTitle(..)` | `doc.DocumentInformation.Title` | `doc.Info.Title` |
| Compression | `WriterProperties.SetFullCompressionMode(true)` | `doc.Compression = PdfCompressionLevel.Best` | `new PdfSaveOptions { UseObjectStreams = true }` |

## Drawing

| Task | iText | Syncfusion | miPDFlib |
|---|---|---|---|
| Canvas | `new PdfCanvas(page)` | `page.Graphics` | `using var c = page.GetCanvas();` (`background: true` = under content) |
| Text | `canvas.BeginText().SetFontAndSize(f, 12).MoveText(x, y).ShowText(s).EndText()` | `g.DrawString(s, font, brush, point)` | `c.DrawText(s, font, 12, x, y, color, alignment)` |
| Standard font | `PdfFontFactory.CreateFont(StandardFonts.HELVETICA)` | `new PdfStandardFont(PdfFontFamily.Helvetica, 12)` | `PdfStandardFont.Helvetica` |
| TrueType (Unicode) | `PdfFontFactory.CreateFont(path, PdfEncodings.IDENTITY_H)` | `new PdfTrueTypeFont(stream, 12)` | `PdfTrueTypeFont.FromFile(path)` |
| Measure text | `font.GetWidth(s, size)` | `font.MeasureString(s)` | `font.GetTextWidth(s, size)` |
| Colors | `ColorConstants.RED`, `new DeviceRgb(...)` | `PdfBrushes.Red`, `new PdfColor(...)` | `PdfColor.Red`, `PdfColor.Rgb(...)`, `PdfColor.FromHex("#..")` |
| Lines/rects | `canvas.Rectangle(..).Stroke()` | `g.DrawRectangle(pen, brush, rect)` | `c.DrawRectangle(x, y, w, h, stroke, fill)` or `c.Rectangle(..).Stroke()` |
| Transparency | `PdfExtGState.SetFillOpacity` | `g.SetTransparency(0.5f)` | `c.SetOpacity(0.5)` |
| Images | `ImageDataFactory.Create(path)` + `canvas.AddImageAt` | `new PdfBitmap(stream)` + `g.DrawImage` | `PdfImage.FromFile(path)` + `c.DrawImage(img, x, y, w, h)` |
| Templates | `PdfFormXObject` | `PdfTemplate` | `PdfFormXObject` + `c.DrawXObject` |
| Page as template | `page.CopyAsFormXObject(pdf)` | `loadedPage.CreateTemplate()` | `doc.ImportPageAsTemplate(page)` |

## Layout

| Task | iText (`Document`) | Syncfusion | miPDFlib (`LayoutDocument`) |
|---|---|---|---|
| Flow document | `new Document(pdf, PageSize.A4)` | `PdfLayoutFormat` / `PdfTextElement.Draw` | `new LayoutDocument(pdf, PageSize.A4, new Thickness(50))` |
| Paragraph | `new Paragraph("..").SetFont(f).SetFontSize(10)` | `new PdfTextElement(text, font)` | `new Paragraph("..", font, 10)` |
| Styled runs | `new Text("..").SetBold()` | `PdfHTMLTextElement` | `paragraph.Add(new TextRun("..", bold) { Underline = true })` |
| Alignment | `SetTextAlignment(TextAlignment.JUSTIFIED)` | `PdfStringFormat.Alignment` | `Alignment = TextAlignment.Justify` |
| Table | `new Table(UnitValue.CreatePercentArray(..))` | `PdfGrid` | `new Table(1, 3, 1)` |
| Header rows | `table.AddHeaderCell(..)` | `grid.Headers.Add(1)` | `table.AddHeaderCell(..)` (repeated per page) |
| Cell span | `new Cell(1, 2)` | `cell.ColumnSpan = 2` | `new Cell("..", colSpan: 2)` |
| Image | `new Image(ImageDataFactory.Create(..))` | `PdfBitmap.Draw` | `new ImageElement(PdfImage.FromFile(..), width: 200)` |
| Page break | `new AreaBreak()` | `PdfLayoutResult` / new page | `new PageBreak()` |
| Header/footer | event handler `PdfDocumentEvent.END_PAGE` | `PdfPageTemplateElement` | `layout.Header`/`layout.Footer` callbacks (with `TotalPages`) |

## Merge, split, bookmarks, links

| Task | iText | Syncfusion | miPDFlib |
|---|---|---|---|
| Merge | `new PdfMerger(pdf).Merge(src, 1, n)` | `PdfDocumentBase.Merge(doc, sources)` | `PdfDocument.Merge(paths)` or `target.ImportPages(src)` |
| Import pages | `src.CopyPagesTo(1, 3, dest)` | `doc.ImportPageRange(src, 0, 2)` | `dest.ImportPages(src, new[] { 1, 2, 3 })` |
| Split | `PdfSplitter` | `doc.Split(pattern)` | `doc.Split(pagesPerDocument)` / `doc.ExtractPages(..)` |
| Bookmarks | `pdf.GetOutlines(false).AddOutline(..)` | `doc.Bookmarks.Add(..)` | `doc.Outlines.AddChild(title, page)` |
| Links | `new PdfLinkAnnotation(rect).SetAction(PdfAction.CreateURI(..))` | `PdfUriAnnotation` | `page.AddLink(rect, "https://..")` |
| Watermark | canvas + `PdfExtGState` | `g.SetTransparency` + `DrawString` | `page.AddTextWatermark("ENTWURF")` |

## Forms

| Task | iText | Syncfusion | miPDFlib |
|---|---|---|---|
| Get form | `PdfAcroForm.GetAcroForm(pdf, true)` | `loadedDoc.Form` | `doc.GetForm()` / `doc.GetOrCreateForm()` |
| Set value | `form.GetField("x").SetValue("v")` | `(form.Fields["x"] as PdfLoadedTextBoxField).Text = "v"` | `form.SetValue("x", "v")` |
| Check box | `field.SetValue("Yes")` | `checkBox.Checked = true` | `form.GetField<PdfCheckBox>("x").IsChecked = true` |
| All fields | `form.GetAllFormFields()` | `form.Fields` | `form.Fields` / `form.GetValues()` |
| Flatten | `form.FlattenFields()` | `form.Flatten = true` | `form.Flatten()` |
| Create field | `new TextFormFieldBuilder(pdf, "x").SetWidgetRectangle(..).CreateText()` | `new PdfTextBoxField(page, "x")` | `form.AddTextField(page, "x", rect)` |
| Unicode font | `field.SetFont(font)` | `field.Font = new PdfTrueTypeFont(..)` | `form.AppearanceFont = PdfTrueTypeFont.FromFile(..)` |

## Text extraction

| Task | iText | Syncfusion | miPDFlib |
|---|---|---|---|
| Page text | `PdfTextExtractor.GetTextFromPage(page)` | `loadedPage.ExtractText()` | `page.ExtractText()` |
| With positions | `LocationTextExtractionStrategy` / `IEventListener` | `ExtractText(out TextLineCollection)` | `PdfTextExtractor.ExtractLines(page)` / `ExtractWords(page)` |
| Search | `RegexBasedLocationExtractionStrategy` | `loadedDoc.FindText(..)` | `page.FindText("..")` |

## Security

| Task | iText | Syncfusion | miPDFlib |
|---|---|---|---|
| Encrypt | `WriterProperties.SetStandardEncryption(user, owner, perms, ENCRYPTION_AES_256)` | `doc.Security.UserPassword = ..; Algorithm = PdfEncryptionAlgorithm.AES` | `doc.Encrypt(new PdfEncryptionOptions { UserPassword, OwnerPassword, Permissions })` |
| Permissions | `EncryptionConstants.ALLOW_PRINTING` | `PdfPermissionsFlags.Print` | `PdfPermissions.Print` |
| Remove | open with owner password, write without encryption | `doc.Security.UserPassword = ""` … | `doc.RemoveEncryption()` |

## Digital signatures

| Task | iText | Syncfusion | miPDFlib |
|---|---|---|---|
| Sign | `PdfSigner.SignDetached(digest, pks, chain, crl, ocsp, tsa, size, CryptoStandard.CADES)` | `new PdfSignature(doc, page, cert, "Sig")` | `PdfSigner.Sign(pdf, signer, options)` |
| Key from PFX | `Pkcs12Store` (BouncyCastle) | `new PdfCertificate(path, pw)` | `CertificateSigner.FromPfx(path, pw)` or `X509Certificate2` |
| HSM / external | `IExternalSignature` | `signature.ComputeHash += ..` / `IPdfExternalSigner` | `IExternalSigner.SignHash(hash, alg)` |
| Deferred signing | `IExternalSignatureContainer` + `SignDeferred` | `PdfSignature.ReplaceEmptySignature` | `PrepareExternalSignature` + `CompleteExternalSignature` |
| CAdES/PAdES | `CryptoStandard.CADES` | `CryptographicStandard.CADES` | `Format = PdfSignatureFormat.PAdES` (default) |
| Time-stamp | `new TSAClientBouncyCastle(url)` | `signature.TimeStampServer = new TimeStampServer(uri)` | `TimestampClient = new HttpTimestampClient(url)` |
| Visible signature | `signer.GetSignatureAppearance().SetPageRect(..)` | `signature.Bounds`, `Appearance.Normal.Graphics` | `Rectangle = new PdfRectangle(..)`, `Appearance = new PdfSignatureAppearance { .. }` |
| Certify | `signer.SetCertificationLevel(..)` | `signature.Certificated = true` | `Certification = CertificationLevel.FormFilling` |
| Lock fields (FieldMDP) | `signer.SetFieldLockDict(new PdfSigFieldLock()..)` | – | `FieldLock = PdfFieldLock.Include("name", "date")` |
| Doc time-stamp | `signer.Timestamp(tsa, name)` | `PdfSignature.AddTimeStamp` | `PdfSigner.AddDocumentTimestamp(pdf, tsa)` |
| Verify | `SignatureUtil` + `PdfPKCS7.VerifySignatureIntegrityAndAuthenticity()` | `loadedDoc.Form.Fields[..] as PdfLoadedSignatureField` + `ValidateSignature()` | `PdfSignatureValidator.Validate(pdf, options)` |

## PDF/A

| Task | iText | Syncfusion | miPDFlib |
|---|---|---|---|
| Create PDF/A | `new PdfADocument(writer, PdfAConformanceLevel.PDF_A_2B, outputIntent)` | `new PdfDocument(PdfConformanceLevel.Pdf_A2B)` | `doc.Conformance = PdfAConformance.PdfA2B` (embedded fonts required) |
| Check | built-in checker (throws) | – | `doc.CheckPdfA(level)` (basic) + veraPDF for full validation |

## Not (yet) covered by miPDFlib

| Feature | Alternative |
|---|---|
| HTML → PDF (iText pdfHTML, Syncfusion HtmlConverter) | headless browser (e.g. Playwright/Chromium `page.PdfAsync`) |
| PDF → image rendering | PDFium-based libraries, Ghostscript |
| Office → PDF | LibreOffice headless, OpenXML-based converters |
| Barcodes / QR codes (iText `BarcodeQRCode`, `Barcode128`, `BarcodeEAN`, `BarcodeDataMatrix`; Syncfusion `PdfQRBarcode`, `PdfCode128Barcode`, `PdfEan13Barcode`, `PdfDataMatrixBarcode`) | `QrCode`, `Code128`, `Ean`, `DataMatrix`, `Code39`, `Interleaved2Of5`, `EpcQrCode` + `canvas.DrawBarcode(...)` / `BarcodeElement` – see [guide/barcodes.md](guide/barcodes.md); PDF417 is not implemented |
| OCR | Tesseract |
| PAdES B-LT/B-LTA validation data (DSS) | `PdfLtv.AddValidationData` / `PdfLtv.MakeLta` (iText `LtvVerification`/`PdfPadesSigner`, Syncfusion `EnableLtv`) |
| XFA forms | not planned |
