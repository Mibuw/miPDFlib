# Accessibility: tagged PDF and PDF/UA

miPDFlib creates tagged PDF (ISO 32000 logical structure), PDF/UA-1 (ISO 14289-1) and PDF/UA-2 (ISO 14289-2)
documents. Namespace `MiPdf.Tagging`.

## PDF/UA with the layout engine

```csharp
var font = PdfTrueTypeFont.FromFile("arial.ttf");          // PDF/UA requires embedded fonts
var bold = PdfTrueTypeFont.FromFile("arialbd.ttf");

var doc = PdfDocument.Create();
doc.Info.Title = "Invoice 2026-0042";                     // required, shown in the window title
doc.Language = "en-US";                                    // required
doc.UAConformance = PdfUAConformance.PdfUA1;               // enables tagging
// doc.Conformance = PdfAConformance.PdfA2U;               // can be combined with PDF/A

using (var layout = new LayoutDocument(doc))
{
    layout.Footer = ctx => ctx.Canvas.DrawText($"Page {ctx.PageNumber}", font, 8, ctx.ContentArea.Right, 30,
        alignment: TextAlignment.Right);                   // header/footer become artifacts
    layout.Add(new Paragraph("Invoice", bold, 20).AsHeading(1));
    layout.Add(new Paragraph("Thank you ...", font, 10));
    var table = new Table(1, 4, 2) { Font = font, HeaderFont = bold };
    // ... header cells become TH (Scope=Column), body cells TD; repeated headers are artifacts
    layout.Add(table);
    layout.Add(new ImageElement(logo, 80) { AlternateText = "Logo of Example Ltd." });
    layout.Add(new ImageElement(ornament, 20) { IsDecorative = true });   // artifact
}

var issues = doc.CheckPdfUA();   // empty list = all machine-checkable rules satisfied
doc.Save("accessible.pdf");
```

What the layout engine tags automatically:

| Element | Structure | Notes |
|---|---|---|
| `Paragraph` | `P` | `AsHeading(n)` or `StructureType = "H2"`, any standard type possible; headings also get bookmarks (`HeadingBookmarks`) |
| links in a `Paragraph` (`AddLink`, `TextRun.Uri`) | `Link` inside the `P` | linked text and link annotations (one per line) with the text as `/Contents` |
| `Table` | `Table` → `TR` → `TH`/`TD` | `Scope` and `ColSpan` attributes, one table element across pages |
| `ListElement` | `L` → `LI` → `Lbl`/`LBody` | `ListNumbering` attribute, nested lists inside `LBody` |
| `ImageElement`, `BarcodeElement` | `Figure` with `/Alt` and `BBox` | `IsDecorative` → artifact |
| `HorizontalLine`, cell borders/backgrounds | artifact (Layout) | |
| `Header`, `Footer` | artifact (Pagination) | |
| `PageStarted` drawing | artifact (Background) | |

`LayoutDocument.StructureParent` can be set to a `Sect`/`Div` element to build deeper structures.

When saving with `UAConformance` set, miPDFlib also writes the XMP identification (`pdfuaid:part`, with the PDF/A extension
schema when combined with PDF/A), sets `DisplayDocTitle`, sets `/Tabs /S` on pages with annotations and adds link,
form field and other annotations to the structure tree (`Link`/`Form`/`Annot` with object references). Links without
description get their URI as `/Contents`; form fields should get a description via `field.ToolTip`.

## PDF/UA-2 (PDF 2.0)

```csharp
doc.UAConformance = PdfUAConformance.PdfUA2;
doc.Conformance = PdfAConformance.PdfA4;    // optional; PDF/UA-2 can only be combined with PDF/A-4
```

The same code produces PDF/UA-2. In addition, saving

- writes a PDF 2.0 file with `pdfuaid:part` 2 and `pdfuaid:rev` 2024,
- puts the structure elements of PDF 2.0 types into the PDF 2.0 structure namespace (`http://iso.org/pdf2/ssn`,
  `/Namespaces` in the structure tree root); types that exist only in PDF 1.7 (`Art`, `BlockQuote`, `Code`, ...) and
  role-mapped custom types stay in the PDF 1.7 namespace,
- gives form fields their description as `/Contents` as well.

The layout engine adds the `ListNumbering` attribute to lists, and the bookmarks it creates for headings point to the
heading's structure element (PDF/UA-2 requires structure destinations for bookmarks and links within the document;
bookmarks added with `doc.Outlines` have page destinations only). PDF 2.0 introduces new structure types
(`StructureType.Title`, `Aside`, `FENote`, `Sub`, `Emphasis`, `Strong`, `DocumentFragment`); use `FENote` instead of
`Note`, which PDF/UA-2 does not allow. Skipped heading levels are allowed in PDF/UA-2. The containment rules of
PDF 2.0 (which element may contain which, ISO 32000-2 Annex L) are not checked by `CheckPdfUA`: for example, inline
elements such as `Quote` or `Sub` belong in a `P`, not directly in a `Sect`.

## Manual tagging on the canvas

```csharp
var tree = doc.EnableTagging();
var section = tree.Root.AddChild(StructureType.Sect);
var heading = section.AddChild(StructureType.H1);
using (var canvas = page.GetCanvas())
{
    canvas.Tag(heading, c => c.DrawText("Title", font, 18, 72, 760));
    canvas.BeginArtifact(ArtifactType.Layout).DrawLine(72, 750, 520, 750).EndArtifact();
    var figure = section.AddChild(StructureType.Figure, alternateText: "Company logo");
    canvas.BeginTag(figure).DrawImage(logo, 72, 650, 80, 80).EndTag();
}
tree.MapRole("Invoice", StructureType.Sect);   // custom types need a role mapping
```

An element can receive content from several places and pages (call `Tag`/`BeginTag` repeatedly). Existing tagged
documents can be extended: `EnableTagging()` continues the existing structure tree and parent tree.

## Checker

`PdfUAChecker.Check(doc)` (or `doc.CheckPdfUA()`) reports:

- missing tagging (`MarkInfo`, `StructTreeRoot`), language, title, `DisplayDocTitle`, XMP identification
- content that is neither tagged nor marked as artifact
- fonts that are not embedded (also in annotation appearances)
- figures/formulas without alternate text, skipped heading levels (PDF/UA-1), non-standard types without role mapping
- PDF/UA-2: the `Document` element in the PDF 2.0 namespace, `ListNumbering` on lists with labels, `Note`, widgets
  without `/Contents`, bookmarks and links within the document without structure destination
- annotations outside the structure tree, missing `/Tabs /S`, links without `/Contents`, form fields without `/TU`
- encryption that blocks accessibility tools

It covers the machine-checkable part of the Matterhorn protocol. Reading order and the quality of alternate texts
need human review; use veraPDF (PDF/UA profile) or PAC for formal validation.

Validated with veraPDF 1.30 (`--flavour ua1` and `--flavour 2u`): layout documents with headings, tables across pages,
figures, barcodes, links, text fields and check boxes pass PDF/UA-1 and PDF/A-2u – also after signing with a visible
signature and extending to PAdES B-LTA. The same documents pass PDF/UA-2 and PDF/A-4 / PDF/A-4f (`--flavour ua2`,
`4`, `4f`), also after signing.

## Forms and signatures in tagged documents

- Set `form.AppearanceFont` to an embedded `PdfTrueTypeFont` so that field appearances do not use Helvetica.
- Check boxes and radio buttons draw their symbol (check, cross, circle, square, diamond) as vector graphics, so no
  ZapfDingbats font is needed.
- Give every field a description: `field.ToolTip = "..."`.
- `PdfSigner` keeps tagged documents accessible: the signature field gets a description, the page `/Tabs /S`, and a
  visible signature widget is added to the structure tree as `Form` element. Use an embedded font for the signature
  text (`options.Appearance.Font`).

## Limitations

- The standard 14 fonts are not embedded and therefore not allowed in PDF/UA; use `PdfTrueTypeFont`.
- Table header/body groups (`THead`/`TBody`) can be created manually but are not produced by the layout engine yet.
