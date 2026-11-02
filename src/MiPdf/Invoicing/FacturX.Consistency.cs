using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MiPdf.Text;

namespace MiPdf.Invoicing;

/// <summary>A value of the embedded invoice XML that was not found in the visible text of the PDF.</summary>
/// <param name="BusinessTerm">The EN 16931 business term, e.g. "BT-112".</param>
/// <param name="Name">The name of the business term.</param>
/// <param name="Value">The value as it appears in the XML.</param>
public sealed record FacturXFinding(string BusinessTerm, string Name, string Value)
{
    public override string ToString() => $"{Name} ({BusinessTerm}) \"{Value}\" was not found in the visible invoice.";
}

public static partial class FacturX
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly CultureInfo[] DateCultures =
        { CultureInfo.GetCultureInfo("de-DE"), CultureInfo.GetCultureInfo("de-AT"), CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("fr-FR") };

    private static readonly string[] DateFormats =
    {
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "M/d/yyyy", "dd-MM-yyyy",
        "d. MMMM yyyy", "d MMMM yyyy", "MMMM d, yyyy", "d MMM yyyy", "MMM d, yyyy",
    };

    /// <summary>
    /// Checks whether the key values of the embedded invoice XML appear in the visible text of the PDF: invoice number
    /// (BT-1), issue date (BT-2), seller and buyer name (BT-27, BT-44), seller VAT identifier (BT-31), IBAN (BT-84) and
    /// the totals (BT-109, BT-110, BT-112, BT-115). Returns the values that were not found; an empty list means the
    /// visible invoice is consistent with the XML as far as this check can tell.
    /// </summary>
    /// <remarks>
    /// This is a plausibility check, not a validation: a PDF has no semantic structure, so a value counts as found if it
    /// appears anywhere in the text. Common formats are recognised – grouped IBANs and VAT identifiers, amounts with two
    /// decimals in German, English and Swiss notation, numeric dates and German, English and French month names. For
    /// incoming invoices the XML is legally binding; a value that differs in the PDF (for example another IBAN) is a
    /// reason to look closer. Scanned invoices without a text layer report every value.
    /// </remarks>
    /// <exception cref="PdfException">The document contains no Factur-X/ZUGFeRD invoice, or its XML is not well-formed.</exception>
    public static IReadOnlyList<FacturXFinding> CheckConsistency(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var xml = GetInvoiceXml(document) ?? throw new PdfException("The document contains no Factur-X/ZUGFeRD invoice.");
        XDocument invoice;
        try
        {
            invoice = Parse(xml, nameof(document));
        }
        catch (ArgumentException ex)
        {
            throw new PdfException("The embedded invoice is not well-formed XML.", ex);
        }

        string text = PdfTextExtractor.ExtractText(document);
        var findings = new List<FacturXFinding>();
        var root = invoice.Root;
        var header = Element(root, "ExchangedDocument", "HeaderExchangedDocument");
        var seller = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "SellerTradeParty");
        var buyer = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "BuyerTradeParty");

        void CheckText(string term, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !ContainsIdentifier(text, value.Trim()))
                findings.Add(new FacturXFinding(term, name, value.Trim()));
        }

        CheckText("BT-1", "Invoice number", Element(header, "ID")?.Value);

        var issueDate = Element(Element(header, "IssueDateTime"), "DateTimeString")?.Value.Trim();
        if (!string.IsNullOrEmpty(issueDate) && !ContainsDate(text, issueDate))
            findings.Add(new FacturXFinding("BT-2", "Invoice issue date", issueDate));

        CheckText("BT-27", "Seller name", Element(seller, "Name")?.Value);
        CheckText("BT-44", "Buyer name", Element(buyer, "Name")?.Value);
        CheckText("BT-31", "Seller VAT identifier", seller?.Elements().Where(e => e.Name.LocalName == "SpecifiedTaxRegistration")
            .Select(r => Element(r, "ID")).FirstOrDefault(id => (string?)id?.Attribute("schemeID") == "VA")?.Value);
        foreach (var iban in root?.Descendants().Where(e => e.Name.LocalName == "IBANID").Select(e => e.Value.Trim()).Distinct() ?? [])
            CheckText("BT-84", "Payment account identifier (IBAN)", iban);

        string? currency = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "InvoiceCurrencyCode")?.Value.Trim();
        void CheckAmount(string term, string name, string localName)
        {
            var candidates = root?.Descendants().Where(e => e.Name.LocalName == localName).ToList() ?? [];
            // BT-111 (VAT in accounting currency) uses the same element; prefer the one in the invoice currency
            var element = candidates.FirstOrDefault(e => currency == null || (string?)e.Attribute("currencyID") is null || (string?)e.Attribute("currencyID") == currency)
                ?? candidates.FirstOrDefault();
            if (element == null) return;
            string value = element.Value.Trim();
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || !ContainsAmount(text, amount))
                findings.Add(new FacturXFinding(term, name, value));
        }

        CheckAmount("BT-109", "Invoice total amount without VAT", "TaxBasisTotalAmount");
        CheckAmount("BT-110", "Invoice total VAT amount", "TaxTotalAmount");
        CheckAmount("BT-112", "Invoice total amount with VAT", "GrandTotalAmount");
        CheckAmount("BT-115", "Amount due for payment", "DuePayableAmount");
        return findings;
    }

    /// <summary>Finds a value case-insensitively, ignoring whitespace inside it (grouped IBANs, "DE 123 456 789").</summary>
    private static bool ContainsIdentifier(string text, string value)
    {
        var chars = value.Where(c => !char.IsWhiteSpace(c)).Select(c => Regex.Escape(c.ToString()));
        string pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"\s*", chars) + @"(?![\p{L}\p{N}])";
        return IsMatch(text, pattern);
    }

    /// <summary>Finds a CII date (format 102, yyyyMMdd) in common numeric and written forms.</summary>
    private static bool ContainsDate(string text, string value)
    {
        if (!DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return ContainsIdentifier(text, value);
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value };
        foreach (var culture in DateCultures)
            foreach (var format in DateFormats)
                forms.Add(date.ToString(format, culture));
        return forms.Any(form => IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(form).Replace(@"\ ", @"\s*") + @"(?![\p{L}\p{N}])"));
    }

    /// <summary>
    /// Finds an amount with two (or more) decimals, with or without thousands separators: 1190.00 matches
    /// "1.190,00", "1,190.00", "1 190,00", "1'190.00" and "1190,00", but not "11.190,00".
    /// </summary>
    private static bool ContainsAmount(string text, decimal amount)
    {
        string digits = Math.Abs(amount).ToString(Math.Round(amount, 2) == amount ? "0.00" : "0.00########", CultureInfo.InvariantCulture);
        int dot = digits.IndexOf('.');
        string integer = digits[..dot], fraction = digits[(dot + 1)..];
        var groups = new List<string>();
        for (int end = integer.Length; end > 0; end -= 3) groups.Insert(0, integer[Math.Max(0, end - 3)..end]);
        var sb = new StringBuilder(@"(?<![\d.,'’])");
        sb.Append(string.Join(@"[.,'’\s]?", groups)).Append("[.,]").Append(fraction).Append(@"(?!\d)");
        return IsMatch(text, sb.ToString());
    }

    private static bool IsMatch(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
