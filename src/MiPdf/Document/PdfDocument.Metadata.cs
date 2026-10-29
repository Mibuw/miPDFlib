using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.PdfA;

namespace MiPdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// PDF/A conformance to produce. When set, saving adds XMP metadata with PDF/A identification,
    /// an sRGB output intent and synchronizes the document information. Use <see cref="PdfAChecker"/>
    /// to find content that violates the standard (e.g. non-embedded fonts).
    /// </summary>
    public PdfAConformance? Conformance { get; set; }

    /// <summary>Custom XMP schemas written with the metadata when <see cref="Conformance"/> or PDF/UA is set (e.g. Factur-X).</summary>
    public IList<XmpExtensionSchema> XmpExtensions { get; } = new List<XmpExtensionSchema>();

    /// <summary>
    /// Reads the PDF/A identification (pdfaid:part / pdfaid:conformance) from the XMP metadata, or null if the
    /// document does not claim PDF/A conformance. Note: this is the claim of the document, not a validation.
    /// </summary>
    public PdfAIdentification? GetPdfAIdentification()
    {
        if (GetXmpMetadata() is not { } xmp) return null;
        var text = System.Text.Encoding.UTF8.GetString(xmp);
        string? Value(string property)
        {
            // element form <pdfaid:part>2</pdfaid:part> or attribute form pdfaid:part="2"
            var element = System.Text.RegularExpressions.Regex.Match(text, $@"<pdfaid:{property}>\s*([^<\s]+)\s*</pdfaid:{property}>");
            if (element.Success) return element.Groups[1].Value;
            var attribute = System.Text.RegularExpressions.Regex.Match(text, $@"pdfaid:{property}\s*=\s*[""']([^""']+)[""']");
            return attribute.Success ? attribute.Groups[1].Value : null;
        }
        if (!int.TryParse(Value("part"), out int part) || part is < 1 or > 4) return null;
        return new PdfAIdentification(part, Value("conformance")?.ToUpperInvariant() ?? "");
    }

    /// <summary>Returns the XMP metadata of the document, or null.</summary>
    public byte[]? GetXmpMetadata() => Catalog.GetStream(PdfName.Metadata)?.GetBytes();

    /// <summary>Sets (or with null removes) the XMP metadata stream.</summary>
    public void SetXmpMetadata(byte[]? xmp)
    {
        if (xmp == null)
        {
            Catalog.Remove(PdfName.Metadata);
            return;
        }
        var stream = new PdfStream(xmp, compress: false);
        stream[PdfName.Type] = PdfName.Metadata;
        stream[PdfName.Subtype] = PdfName.XML;
        if (Catalog[PdfName.Metadata] is PdfReference existing) ReplaceObject(existing, stream);
        else Catalog[PdfName.Metadata] = AddObject(stream);
    }

    /// <summary>Checks the document against basic PDF/A rules (see <see cref="PdfAChecker"/>).</summary>
    /// <remarks>If <see cref="Conformance"/> is set, the metadata and output intent written on saving are created first.</remarks>
    public IReadOnlyList<string> CheckPdfA(PdfAConformance conformance)
    {
        if (Conformance != null && !IsEncrypted) ApplyConformance(new PdfSaveOptions());
        return PdfAChecker.Check(this, conformance);
    }

    private void ApplyConformance(PdfSaveOptions options)
    {
        if (Conformance == null && _uaConformance == null && XmpExtensions.Count == 0) return;
        if (Conformance is { } conformance)
        {
            if (IsEncrypted) throw new PdfException("PDF/A documents must not be encrypted.");
            if (conformance == PdfAConformance.PdfA1B)
            {
                if (options.UseObjectStreams) throw new PdfException("PDF/A-1 does not allow object streams; set UseObjectStreams = false.");
                Version = "1.4";
            }
            else if (string.CompareOrdinal(Version, "1.7") != 0 && !options.Incremental) Version = "1.7";
        }
        else if (string.CompareOrdinal(Version, "1.7") < 0 && !options.Incremental) Version = "1.7";

        Info.ModificationDate = DateTimeOffset.Now;
        Info.CreationDate ??= Info.ModificationDate;
        Info.Producer ??= "miPDFlib";
        Info.Remove(PdfName.Trapped);
        SetXmpMetadata(XmpMetadata.Create(Info, Conformance, _uaConformance, Language, XmpExtensions));
        if (Conformance == null) return;

        var intents = Catalog.GetArray(PdfName.Get("OutputIntents"));
        if (intents is not { Count: > 0 })
        {
            var icc = new PdfStream(SrgbProfile.Bytes);
            icc.Set(PdfName.N, 3);
            var intent = new PdfDictionary(PdfName.Get("OutputIntent"));
            intent[PdfName.S] = PdfName.Get("GTS_PDFA1");
            intent.SetText(PdfName.Get("OutputConditionIdentifier"), "sRGB IEC61966-2.1");
            intent.SetText(PdfName.Get("Info"), "sRGB IEC61966-2.1");
            intent.SetText(PdfName.Get("RegistryName"), "http://www.color.org");
            intent[PdfName.Get("DestOutputProfile")] = AddObject(icc);
            Catalog[PdfName.Get("OutputIntents")] = new PdfArray(AddObject(intent));
        }
    }
}
