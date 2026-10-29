using System.Text;
using MiPdf.PdfA;

namespace MiPdf.Invoicing;

/// <summary>Factur-X / ZUGFeRD 2.x profiles (conformance levels of the embedded CII invoice).</summary>
public enum FacturXProfile
{
    Minimum,
    BasicWL,
    Basic,
    EN16931,
    Extended,

    /// <summary>XRechnung (CII syntax) embedded as ZUGFeRD; file name "xrechnung.xml".</summary>
    XRechnung,
}

/// <summary>
/// Hybrid e-invoices according to Factur-X 1.0 / ZUGFeRD 2.x: a PDF/A-3 document with the structured invoice
/// (UN/CEFACT Cross Industry Invoice XML) as associated file and the Factur-X XMP metadata.
/// </summary>
/// <example>
/// <code>
/// using var pdf = PdfDocument.Create();
/// // ... render the human-readable invoice with embedded fonts ...
/// FacturX.Attach(pdf, File.ReadAllBytes("factur-x.xml"), FacturXProfile.EN16931);
/// pdf.Save("invoice.pdf");    // PDF/A-3b with factur-x.xml
/// </code>
/// </example>
public static class FacturX
{
    /// <summary>XMP namespace of the Factur-X extension schema.</summary>
    public const string NamespaceUri = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#";

    private static readonly string[] KnownFileNames = { "factur-x.xml", "zugferd-invoice.xml", "ZUGFeRD-invoice.xml", "xrechnung.xml" };

    /// <summary>
    /// Embeds the invoice XML and prepares the document for PDF/A-3 (sets <see cref="PdfDocument.Conformance"/> to
    /// PDF/A-3b unless a PDF/A-3 level is already set). Fonts must be embedded for a valid PDF/A document.
    /// </summary>
    /// <param name="document">The visual invoice.</param>
    /// <param name="xml">The CII invoice XML (UTF-8).</param>
    /// <param name="profile">Profile of the XML.</param>
    /// <param name="fileName">Attachment name (default "factur-x.xml", or "xrechnung.xml" for <see cref="FacturXProfile.XRechnung"/>).</param>
    public static PdfAttachment Attach(PdfDocument document, byte[] xml, FacturXProfile profile, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(xml);
        CheckXml(xml);
        fileName ??= profile == FacturXProfile.XRechnung ? "xrechnung.xml" : "factur-x.xml";
        if (document.Conformance is not (PdfAConformance.PdfA3B or PdfAConformance.PdfA3U)) document.Conformance = PdfAConformance.PdfA3B;

        foreach (var known in KnownFileNames) document.RemoveAttachment(known);
        var relationship = profile is FacturXProfile.Minimum or FacturXProfile.BasicWL ? AttachmentRelationship.Data : AttachmentRelationship.Alternative;
        var attachment = document.AddAttachment(fileName, xml, "text/xml", "Factur-X/ZUGFeRD invoice", relationship);

        for (int i = document.XmpExtensions.Count - 1; i >= 0; i--)
            if (document.XmpExtensions[i].NamespaceUri == NamespaceUri) document.XmpExtensions.RemoveAt(i);
        document.XmpExtensions.Add(new XmpExtensionSchema("fx", NamespaceUri, "Factur-X PDFA Extension Schema", new[]
        {
            new XmpProperty("DocumentFileName", fileName, "Text", "external", "The name of the embedded XML document"),
            new XmpProperty("DocumentType", "INVOICE", "Text", "external", "The type of the hybrid document in capital letters, e.g. INVOICE or ORDER"),
            new XmpProperty("Version", "1.0", "Text", "external", "The actual version of the standard applying to the embedded XML document"),
            new XmpProperty("ConformanceLevel", ConformanceLevel(profile), "Text", "external", "The conformance level of the embedded XML document"),
        }));
        return attachment;
    }

    /// <summary>The value of fx:ConformanceLevel for a profile.</summary>
    public static string ConformanceLevel(FacturXProfile profile) => profile switch
    {
        FacturXProfile.Minimum => "MINIMUM",
        FacturXProfile.BasicWL => "BASIC WL",
        FacturXProfile.Basic => "BASIC",
        FacturXProfile.EN16931 => "EN 16931",
        FacturXProfile.Extended => "EXTENDED",
        _ => "XRECHNUNG",
    };

    /// <summary>Returns the embedded invoice XML of a Factur-X/ZUGFeRD document, or null.</summary>
    public static byte[]? GetInvoiceXml(PdfDocument document)
    {
        foreach (var name in KnownFileNames)
            if (document.GetAttachment(name) is { } attachment) return attachment.GetData();
        return null;
    }

    /// <summary>Returns the profile declared in the XMP metadata (fx:ConformanceLevel), or null.</summary>
    public static FacturXProfile? GetProfile(PdfDocument document)
    {
        if (document.GetXmpMetadata() is not { } xmp) return null;
        var text = Encoding.UTF8.GetString(xmp);
        int start = text.IndexOf(":ConformanceLevel>", StringComparison.Ordinal);
        if (start < 0 || !text.Contains(NamespaceUri, StringComparison.Ordinal)) return null;
        int end = text.IndexOf('<', start);
        string level = text[(start + 18)..end].Trim();
        foreach (var profile in Enum.GetValues<FacturXProfile>())
            if (ConformanceLevel(profile) == level) return profile;
        return null;
    }

    private static void CheckXml(byte[] xml)
    {
        try
        {
            using var reader = System.Xml.XmlReader.Create(new MemoryStream(xml),
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read()) { }
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ArgumentException("The invoice is not well-formed XML: " + ex.Message, nameof(xml), ex);
        }
    }
}
