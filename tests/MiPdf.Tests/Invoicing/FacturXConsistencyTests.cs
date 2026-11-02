using System.Text;
using MiPdf.Fonts;
using MiPdf.Invoicing;

namespace MiPdf.Tests.Invoicing;

public class FacturXConsistencyTests
{
    private const string En16931 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"
            xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100"
            xmlns:udt="urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100">
          <rsm:ExchangedDocumentContext>
            <ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>{0}</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter>
          </rsm:ExchangedDocumentContext>
          <rsm:ExchangedDocument>
            <ram:ID>2026-0042</ram:ID>
            <ram:TypeCode>380</ram:TypeCode>
            <ram:IssueDateTime><udt:DateTimeString format="102">20261102</udt:DateTimeString></ram:IssueDateTime>
          </rsm:ExchangedDocument>
          <rsm:SupplyChainTradeTransaction>
            <ram:ApplicableHeaderTradeAgreement>
              <ram:SellerTradeParty>
                <ram:Name>Example Seller GmbH</ram:Name>
                <ram:SpecifiedTaxRegistration><ram:ID schemeID="FC">201/113/40209</ram:ID></ram:SpecifiedTaxRegistration>
                <ram:SpecifiedTaxRegistration><ram:ID schemeID="VA">DE123456789</ram:ID></ram:SpecifiedTaxRegistration>
              </ram:SellerTradeParty>
              <ram:BuyerTradeParty><ram:Name>Example Buyer AG</ram:Name></ram:BuyerTradeParty>
            </ram:ApplicableHeaderTradeAgreement>
            <ram:ApplicableHeaderTradeDelivery/>
            <ram:ApplicableHeaderTradeSettlement>
              <ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>
              <ram:SpecifiedTradeSettlementPaymentMeans>
                <ram:TypeCode>58</ram:TypeCode>
                <ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02120300000000202051</ram:IBANID></ram:PayeePartyCreditorFinancialAccount>
              </ram:SpecifiedTradeSettlementPaymentMeans>
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
        """;

    private static byte[] Xml(string guideline = "urn:cen.eu:en16931:2017") => Encoding.UTF8.GetBytes(En16931.Replace("{0}", guideline));

    /// <summary>A visible invoice with the given lines, the XML embedded, saved and reopened.</summary>
    private static PdfDocument Invoice(params string[] lines)
    {
        var doc = PdfDocument.Create();
        using (var canvas = doc.AddPage().GetCanvas())
            for (int i = 0; i < lines.Length; i++)
                canvas.DrawText(lines[i], PdfStandardFont.Helvetica, 10, 72, 760 - i * 16);
        FacturX.Attach(doc, Xml(), FacturXProfile.EN16931);
        return PdfDocument.Open(doc.ToArray());
    }

    [Fact]
    public void ConsistentGermanInvoiceHasNoFindings()
    {
        var doc = Invoice(
            "Example Seller GmbH - USt-IdNr. DE 123 456 789",
            "Example Buyer AG",
            "Rechnung Nr. 2026-0042 vom 02.11.2026",
            "Netto 1.000,00 EUR",
            "USt 19 % 190,00 EUR",
            "Gesamtbetrag 1.190,00 EUR",
            "Zahlbar: 1.190,00 EUR auf IBAN DE02 1203 0000 0000 2020 51");
        Assert.Empty(FacturX.CheckConsistency(doc));
    }

    [Fact]
    public void ConsistentEnglishInvoiceHasNoFindings()
    {
        var doc = Invoice(
            "EXAMPLE SELLER GMBH, VAT ID DE123456789",
            "Bill to: Example Buyer AG",
            "Invoice 2026-0042, November 2, 2026",
            "Subtotal 1,000.00 VAT 190.00 Total 1,190.00",
            "Amount due 1190.00 - IBAN DE02120300000000202051");
        Assert.Empty(FacturX.CheckConsistency(doc));
    }

    [Fact]
    public void DifferingValuesAreReported()
    {
        var doc = Invoice(
            "Example Seller GmbH - USt-IdNr. DE 123 456 789",
            "Example Buyer AG",
            "Rechnung Nr. 2026-00421 vom 3. November 2026",
            "Netto 1.000,00 EUR",
            "Gesamtbetrag 1.190,00 EUR",
            "IBAN DE89 3704 0044 0532 0130 00");
        var findings = FacturX.CheckConsistency(doc);
        // the VAT amount 190,00 must not be found inside 1.190,00, and 2026-0042 not inside 2026-00421
        Assert.Equal(new[] { "BT-1", "BT-2", "BT-84", "BT-110" }, findings.Select(f => f.BusinessTerm));
        Assert.Contains("DE02120300000000202051", findings.Single(f => f.BusinessTerm == "BT-84").ToString());
    }

    [Fact]
    public void WrittenDatesAreRecognised()
    {
        Assert.DoesNotContain(FacturX.CheckConsistency(Invoice("2. November 2026")), f => f.BusinessTerm == "BT-2");
        Assert.DoesNotContain(FacturX.CheckConsistency(Invoice("2 nov. 2026")), f => f.BusinessTerm == "BT-2");
        Assert.DoesNotContain(FacturX.CheckConsistency(Invoice("2026-11-02")), f => f.BusinessTerm == "BT-2");
        Assert.Contains(FacturX.CheckConsistency(Invoice("12.11.2026")), f => f.BusinessTerm == "BT-2");
    }

    [Fact]
    public void DocumentWithoutInvoiceThrows()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        Assert.Throws<PdfException>(() => FacturX.CheckConsistency(doc));
    }

    [Theory]
    [InlineData("urn:factur-x.eu:1p0:minimum", FacturXProfile.Minimum)]
    [InlineData("urn:factur-x.eu:1p0:basicwl", FacturXProfile.BasicWL)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic", FacturXProfile.Basic)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:zugferd.de:2p0:basic", FacturXProfile.Basic)]
    [InlineData("urn:cen.eu:en16931:2017", FacturXProfile.EN16931)]
    [InlineData("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended", FacturXProfile.Extended)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", FacturXProfile.XRechnung)]
    public void ProfileIsDetectedFromTheGuidelineId(string guideline, FacturXProfile expected)
    {
        Assert.Equal(expected, FacturX.DetectProfile(Xml(guideline)));
        Assert.Null(FacturX.DetectProfile(Xml("urn:example:unknown")));
    }

    [Fact]
    public void AttachRejectsAProfileThatContradictsTheXml()
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        var ex = Assert.Throws<ArgumentException>(() => FacturX.Attach(doc, Xml("urn:factur-x.eu:1p0:minimum"), FacturXProfile.EN16931));
        Assert.Contains("MINIMUM", ex.Message);
        Assert.Null(doc.GetAttachment("factur-x.xml"));
        FacturX.Attach(doc, Xml("urn:factur-x.eu:1p0:minimum"), FacturXProfile.Minimum);
        FacturX.Attach(doc, Xml("urn:example:unknown"), FacturXProfile.EN16931);   // unknown IDs are not judged
        Assert.NotNull(doc.GetAttachment("factur-x.xml"));
    }
}
