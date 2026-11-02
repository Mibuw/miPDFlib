# How to create a ZUGFeRD / Factur-X / XRechnung invoice in C#

A ZUGFeRD (Germany) or Factur-X (France) invoice is a hybrid e-invoice: a PDF/A-3 document that people can read,
with the structured invoice data embedded as XML (UN/CEFACT Cross Industry Invoice, CII) that accounting software
processes automatically. In Germany, businesses must be able to receive e-invoices since 2025, and issuing them
becomes mandatory step by step until 2028; ZUGFeRD from profile EN 16931 meets the requirements.

This how-to creates such an invoice in C# with [miPDFlib](https://github.com/Mibuw/miPDFlib) (Apache-2.0).

```bash
dotnet add package miPDFlib
```

## 1. The invoice data as CII XML

miPDFlib embeds the XML; producing it is up to your invoicing code (a template, `XDocument` or a dedicated CII
library). A minimal invoice in profile EN 16931 – one line item, 19 % VAT – looks like this:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"
    xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100"
    xmlns:udt="urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100">
  <rsm:ExchangedDocumentContext>
    <ram:GuidelineSpecifiedDocumentContextParameter>
      <ram:ID>urn:cen.eu:en16931:2017</ram:ID>
    </ram:GuidelineSpecifiedDocumentContextParameter>
  </rsm:ExchangedDocumentContext>
  <rsm:ExchangedDocument>
    <ram:ID>2026-0042</ram:ID>
    <ram:TypeCode>380</ram:TypeCode>
    <ram:IssueDateTime><udt:DateTimeString format="102">20261102</udt:DateTimeString></ram:IssueDateTime>
  </rsm:ExchangedDocument>
  <rsm:SupplyChainTradeTransaction>
    <ram:IncludedSupplyChainTradeLineItem>
      <ram:AssociatedDocumentLineDocument><ram:LineID>1</ram:LineID></ram:AssociatedDocumentLineDocument>
      <ram:SpecifiedTradeProduct><ram:Name>Consulting</ram:Name></ram:SpecifiedTradeProduct>
      <ram:SpecifiedLineTradeAgreement>
        <ram:NetPriceProductTradePrice><ram:ChargeAmount>100.00</ram:ChargeAmount></ram:NetPriceProductTradePrice>
      </ram:SpecifiedLineTradeAgreement>
      <ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode="HUR">10</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>
      <ram:SpecifiedLineTradeSettlement>
        <ram:ApplicableTradeTax>
          <ram:TypeCode>VAT</ram:TypeCode>
          <ram:CategoryCode>S</ram:CategoryCode>
          <ram:RateApplicablePercent>19</ram:RateApplicablePercent>
        </ram:ApplicableTradeTax>
        <ram:SpecifiedTradeSettlementLineMonetarySummation>
          <ram:LineTotalAmount>1000.00</ram:LineTotalAmount>
        </ram:SpecifiedTradeSettlementLineMonetarySummation>
      </ram:SpecifiedLineTradeSettlement>
    </ram:IncludedSupplyChainTradeLineItem>
    <ram:ApplicableHeaderTradeAgreement>
      <ram:SellerTradeParty>
        <ram:Name>Example Seller GmbH</ram:Name>
        <ram:PostalTradeAddress>
          <ram:PostcodeCode>10115</ram:PostcodeCode>
          <ram:LineOne>Example Street 1</ram:LineOne>
          <ram:CityName>Berlin</ram:CityName>
          <ram:CountryID>DE</ram:CountryID>
        </ram:PostalTradeAddress>
        <ram:SpecifiedTaxRegistration><ram:ID schemeID="VA">DE123456789</ram:ID></ram:SpecifiedTaxRegistration>
      </ram:SellerTradeParty>
      <ram:BuyerTradeParty>
        <ram:Name>Example Buyer AG</ram:Name>
        <ram:PostalTradeAddress>
          <ram:PostcodeCode>80331</ram:PostcodeCode>
          <ram:LineOne>Sample Road 2</ram:LineOne>
          <ram:CityName>Munich</ram:CityName>
          <ram:CountryID>DE</ram:CountryID>
        </ram:PostalTradeAddress>
      </ram:BuyerTradeParty>
    </ram:ApplicableHeaderTradeAgreement>
    <ram:ApplicableHeaderTradeDelivery>
      <ram:ActualDeliverySupplyChainEvent>
        <ram:OccurrenceDateTime><udt:DateTimeString format="102">20261031</udt:DateTimeString></ram:OccurrenceDateTime>
      </ram:ActualDeliverySupplyChainEvent>
    </ram:ApplicableHeaderTradeDelivery>
    <ram:ApplicableHeaderTradeSettlement>
      <ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>
      <ram:SpecifiedTradeSettlementPaymentMeans>
        <ram:TypeCode>58</ram:TypeCode>
        <ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02120300000000202051</ram:IBANID></ram:PayeePartyCreditorFinancialAccount>
      </ram:SpecifiedTradeSettlementPaymentMeans>
      <ram:ApplicableTradeTax>
        <ram:CalculatedAmount>190.00</ram:CalculatedAmount>
        <ram:TypeCode>VAT</ram:TypeCode>
        <ram:BasisAmount>1000.00</ram:BasisAmount>
        <ram:CategoryCode>S</ram:CategoryCode>
        <ram:RateApplicablePercent>19</ram:RateApplicablePercent>
      </ram:ApplicableTradeTax>
      <ram:SpecifiedTradePaymentTerms>
        <ram:DueDateDateTime><udt:DateTimeString format="102">20261116</udt:DateTimeString></ram:DueDateDateTime>
      </ram:SpecifiedTradePaymentTerms>
      <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        <ram:LineTotalAmount>1000.00</ram:LineTotalAmount>
        <ram:TaxBasisTotalAmount>1000.00</ram:TaxBasisTotalAmount>
        <ram:TaxTotalAmount currencyID="EUR">190.00</ram:TaxTotalAmount>
        <ram:GrandTotalAmount>1190.00</ram:GrandTotalAmount>
        <ram:DuePayableAmount>1190.00</ram:DuePayableAmount>
      </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
    </ram:ApplicableHeaderTradeSettlement>
  </rsm:SupplyChainTradeTransaction>
</rsm:CrossIndustryInvoice>
```

The element order is fixed by the CII schema. Validate the XML of your real invoices before going live, for example
with [Mustang](https://www.mustangproject.org/) or the [KoSIT validator](https://github.com/itplr-kosit/validator)
(XRechnung).

## 2. The visible invoice and the embedded XML

PDF/A requires embedded fonts, so the invoice is rendered with a TrueType font instead of the standard 14 fonts:

```csharp
using MiPdf;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Invoicing;
using MiPdf.Layout;

var font = PdfTrueTypeFont.FromFile("fonts/DejaVuSans.ttf");     // any embeddable font
var bold = PdfTrueTypeFont.FromFile("fonts/DejaVuSans-Bold.ttf");

using var pdf = PdfDocument.Create();
pdf.Info.Title = "Invoice 2026-0042";
using (var layout = new LayoutDocument(pdf, PageSize.A4, new Thickness(50)))
{
    layout.Add(new Paragraph("Example Seller GmbH · Example Street 1 · 10115 Berlin", font, 8) { SpacingAfter = 20 });
    layout.Add(new Paragraph("Example Buyer AG\nSample Road 2\n80331 Munich", font, 10) { SpacingAfter = 20 });
    layout.Add(new Paragraph("Invoice 2026-0042", bold, 16) { SpacingAfter = 4 });
    layout.Add(new Paragraph("Date: 2026-11-02 · Delivery: 2026-10-31 · VAT ID: DE123456789", font, 9) { SpacingAfter = 12 });

    var table = new Table(3, 1, 1.2, 1.2) { Font = font, HeaderFont = bold, SpacingAfter = 12 };
    foreach (var header in new[] { "Description", "Hours", "Price", "Amount" }) table.AddHeaderCell(header);
    table.AddCell("Consulting");
    table.AddCell(new Cell("10") { TextAlignment = TextAlignment.Right });
    table.AddCell(new Cell("100.00 €") { TextAlignment = TextAlignment.Right });
    table.AddCell(new Cell("1,000.00 €") { TextAlignment = TextAlignment.Right });
    table.AddCell(new Cell("VAT 19 %", colSpan: 3) { TextAlignment = TextAlignment.Right });
    table.AddCell(new Cell("190.00 €") { TextAlignment = TextAlignment.Right });
    table.AddCell(new Cell("Total", colSpan: 3) { Font = bold, TextAlignment = TextAlignment.Right });
    table.AddCell(new Cell("1,190.00 €") { Font = bold, TextAlignment = TextAlignment.Right });
    layout.Add(table);
    layout.Add(new Paragraph("Payable by 2026-11-16 to IBAN DE02 1203 0000 0000 2020 51.", font, 10));
}

FacturX.Attach(pdf, File.ReadAllBytes("factur-x.xml"), FacturXProfile.EN16931);
pdf.Save("invoice-2026-0042.pdf");
```

`FacturX.Attach`

- switches the document to PDF/A-3b,
- embeds the XML as `factur-x.xml` (`xrechnung.xml` for `FacturXProfile.XRechnung`) with the relationship
  `/Alternative`, as required by Factur-X 1.0 / ZUGFeRD 2.x,
- writes the Factur-X XMP metadata (document type, file name, version and conformance level),
- rejects XML that is not well-formed.

Check the PDF/A part before saving with `pdf.CheckPdfA(MiPdf.PdfA.PdfAConformance.PdfA3B)` (empty list = no
problems found), and with [veraPDF](https://verapdf.org) for a full validation.

## 3. XRechnung

XRechnung is the German CIUS of EN 16931 for invoices to public authorities. In CII syntax it can be embedded the
same way with `FacturXProfile.XRechnung`. The XML then needs the XRechnung guideline ID
(`urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0`) and the additional XRechnung fields, such
as the buyer reference (Leitweg-ID) and the seller's contact details. Many public portals expect the pure XML file
instead of a hybrid PDF – check the recipient's requirements.

## 4. Reading an e-invoice

```csharp
using var doc = PdfDocument.Open("incoming.pdf");
byte[]? xml = FacturX.GetInvoiceXml(doc);          // also finds zugferd-invoice.xml (ZUGFeRD 2.0)
FacturXProfile? profile = FacturX.GetProfile(doc);

// does the visible invoice show the same invoice number, date, IBAN and totals as the XML?
foreach (var finding in FacturX.CheckConsistency(doc))
    Console.WriteLine(finding);
```

The XML is the legally binding part of the invoice. `CheckConsistency` reports values of the XML that do not appear
in the visible PDF – a different IBAN in the PDF, for example, is a reason to look closer before paying.

## 5. Signing the invoice (optional)

A signed invoice proves its origin and integrity. Sign after `FacturX.Attach` – the signature is added as an
incremental update and keeps the PDF/A-3 conformance:

```csharp
byte[] signed = PdfSigner.Sign(File.ReadAllBytes("invoice-2026-0042.pdf"), CertificateSigner.FromPfx("company.pfx", "password"));
```

See [How to sign a PDF with PAdES in .NET](sign-pdf-pades-dotnet.md).

## More

- [Attachments and e-invoices guide](../guide/attachments.md)
- [Creating documents](../guide/creating-documents.md): fonts, tables, layout
