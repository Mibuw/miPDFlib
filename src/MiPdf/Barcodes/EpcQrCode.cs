using System.Globalization;
using System.Text;

namespace MiPdf.Barcodes;

/// <summary>
/// Payment data for an EPC QR code (EPC069-12 "SEPA credit transfer", known as GiroCode or
/// "Zahlen mit Code" in Austria).
/// </summary>
public sealed class EpcPayment
{
    /// <summary>Name of the beneficiary (max. 70 characters, required).</summary>
    public required string Name { get; init; }

    /// <summary>IBAN of the beneficiary (required; spaces are removed).</summary>
    public required string Iban { get; init; }

    /// <summary>BIC (optional within the EEA).</summary>
    public string? Bic { get; init; }

    /// <summary>Amount in EUR (0.01 - 999999999.99); null lets the payer enter the amount.</summary>
    public decimal? Amount { get; init; }

    /// <summary>Purpose code (4 characters, e.g. "GDDS"), optional.</summary>
    public string? Purpose { get; init; }

    /// <summary>Structured creditor reference (e.g. ISO 11649 "RF..." reference, max. 35). Mutually exclusive with <see cref="Text"/>.</summary>
    public string? Reference { get; init; }

    /// <summary>Unstructured remittance information (max. 140 characters).</summary>
    public string? Text { get; init; }

    /// <summary>Note to the payer shown by the banking app (max. 70 characters).</summary>
    public string? Information { get; init; }

    /// <summary>Builds the EPC payload (version 002, UTF-8).</summary>
    public string ToPayload()
    {
        static string Check(string? value, int max, string field)
        {
            value = value?.Trim() ?? "";
            if (value.Length > max) throw new ArgumentException($"{field} must not exceed {max} characters.");
            return value;
        }
        if (Reference != null && Text != null) throw new ArgumentException("Either Reference or Text can be used, not both.");
        string iban = Iban.Replace(" ", "").ToUpperInvariant();
        if (iban.Length is < 15 or > 34) throw new ArgumentException("Invalid IBAN.");
        if (Amount is { } a && (a < 0.01m || a > 999999999.99m)) throw new ArgumentException("Amount out of range.");
        string name = Check(Name, 70, "Name");
        if (name.Length == 0) throw new ArgumentException("Name is required.");
        var lines = new[]
        {
            "BCD", "002", "1", "SCT",
            Check(Bic?.Replace(" ", "").ToUpperInvariant(), 11, "BIC"),
            name,
            iban,
            Amount is { } amount ? "EUR" + amount.ToString("0.##", CultureInfo.InvariantCulture) : "",
            Check(Purpose, 4, "Purpose"),
            Check(Reference, 35, "Reference"),
            Check(Text, 140, "Text"),
            Check(Information, 70, "Information"),
        };
        string payload = string.Join("\n", lines).TrimEnd('\n');
        if (Encoding.UTF8.GetByteCount(payload) > 331) throw new ArgumentException("The EPC payload exceeds 331 bytes.");
        return payload;
    }
}

/// <summary>Creates EPC QR codes ("GiroCode", "Zahlen mit Code") for SEPA credit transfers.</summary>
public static class EpcQrCode
{
    /// <summary>Creates the QR Code (error correction level M as required by the EPC guideline).</summary>
    public static QrCode Create(EpcPayment payment) => QrCode.Create(payment.ToPayload(), new QrCodeOptions { ErrorCorrection = QrErrorCorrection.M, MaxVersion = 13 });
}
