using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MiPdf.Forms;
using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf.Signatures;

/// <summary>
/// A document prepared for an external signature: the signature dictionary is in place, the byte range is
/// fixed and only the signature container (/Contents) is missing.
/// </summary>
public sealed class PreparedSignature
{
    internal PreparedSignature(byte[] pdf, string fieldName, long[] byteRange, HashAlgorithmName digestAlgorithm)
    {
        PreparedPdf = pdf;
        FieldName = fieldName;
        ByteRange = byteRange;
        DigestAlgorithm = digestAlgorithm;
    }

    /// <summary>The prepared PDF (store it if the signature is completed in another process).</summary>
    public byte[] PreparedPdf { get; }

    public string FieldName { get; }

    /// <summary>The /ByteRange [offset1 length1 offset2 length2].</summary>
    public IReadOnlyList<long> ByteRange { get; }

    public HashAlgorithmName DigestAlgorithm { get; }

    /// <summary>Space available for the DER-encoded signature container in bytes.</summary>
    public int ContainerSize => (int)((ByteRange[2] - ByteRange[1] - 2) / 2);

    /// <summary>The signed bytes (both byte ranges concatenated).</summary>
    public byte[] GetSignedData()
    {
        var data = new byte[ByteRange[1] + ByteRange[3]];
        PreparedPdf.AsSpan(0, (int)ByteRange[1]).CopyTo(data);
        PreparedPdf.AsSpan((int)ByteRange[2], (int)ByteRange[3]).CopyTo(data.AsSpan((int)ByteRange[1]));
        return data;
    }

    /// <summary>The digest of the signed bytes (to be signed by an external CMS service).</summary>
    public byte[] GetDocumentDigest() => CmsBuilder.Hash(DigestAlgorithm, GetSignedData());
}

/// <summary>Result of <see cref="PdfSigner.SignDetailed(byte[], IExternalSigner, PdfSignatureOptions?)"/>.</summary>
public sealed class PdfSigningResult
{
    internal PdfSigningResult(byte[] pdf, string fieldName, byte[] cms, PadesLevel level, IReadOnlyList<string> warnings)
    {
        Pdf = pdf;
        FieldName = fieldName;
        Cms = cms;
        Level = level;
        Warnings = warnings;
    }

    /// <summary>The signed document.</summary>
    public byte[] Pdf { get; }

    public string FieldName { get; }

    /// <summary>The embedded CMS signature container.</summary>
    public byte[] Cms { get; }

    /// <summary>The PAdES level reached (B-B if an optional time-stamp could not be obtained).</summary>
    public PadesLevel Level { get; }

    /// <summary>Non-fatal problems, e.g. a failed optional time-stamp.</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// Signs PDF documents. Signatures are always added as incremental update, so existing signatures stay valid.
/// </summary>
/// <example>
/// <code>
/// var signer = CertificateSigner.FromPfx("me.pfx", "password");
/// byte[] signed = PdfSigner.Sign(File.ReadAllBytes("in.pdf"), signer, new PdfSignatureOptions
/// {
///     Reason = "Approval", Location = "Vienna",
///     Rectangle = new PdfRectangle(350, 50, 550, 110),
///     TimestampClient = new HttpTimestampClient("http://timestamp.digicert.com"),
/// });
/// </code>
/// </example>
public static class PdfSigner
{
    private const long ByteRangePlaceholder = 9_999_999_999;
    private static readonly PdfName AdobePpkLite = PdfName.Get("Adobe.PPKLite");

    /// <summary>Signs with a certificate that has a private key (e.g. loaded from a .pfx file).</summary>
    public static byte[] Sign(byte[] pdf, X509Certificate2 certificate, PdfSignatureOptions? options = null, IEnumerable<X509Certificate2>? chain = null) =>
        Sign(pdf, new CertificateSigner(certificate, chain), options);

    /// <summary>Signs a document with any <see cref="IExternalSigner"/> (local key, HSM, smart card, remote service).</summary>
    public static byte[] Sign(byte[] pdf, IExternalSigner signer, PdfSignatureOptions? options = null) => SignDetailed(pdf, signer, options).Pdf;

    /// <summary>Signs like <see cref="Sign(byte[], IExternalSigner, PdfSignatureOptions?)"/> and reports the reached level and the CMS.</summary>
    public static PdfSigningResult SignDetailed(byte[] pdf, IExternalSigner signer, PdfSignatureOptions? options = null)
    {
        options ??= new PdfSignatureOptions();
        var signingTime = options.SigningTime ?? DateTimeOffset.Now;
        int size = options.SignatureSize > 0 ? options.SignatureSize : EstimateSize(signer, options);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var subFilter = options.Format == PdfSignatureFormat.PAdES ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached";
            var prepared = Prepare(pdf, options, PdfName.Sig, subFilter, size, signer.Certificate, signingTime);
            var cms = CmsBuilder.Sign(prepared.GetSignedData(), signer, options, signingTime, out bool timestamped, out string? tsError);
            if (cms.Length <= prepared.ContainerSize)
            {
                var warnings = tsError != null ? new[] { "Time-stamp failed, signed without time-stamp: " + tsError } : Array.Empty<string>();
                return new PdfSigningResult(Embed(prepared, cms), prepared.FieldName, cms, timestamped ? PadesLevel.BT : PadesLevel.BB, warnings);
            }
            if (options.SignatureSize > 0)
                throw new PdfException($"The signature ({cms.Length} bytes) does not fit into the reserved space ({prepared.ContainerSize} bytes).");
            size = cms.Length + 4096;
        }
        throw new PdfException("Could not determine the signature size.");
    }

    /// <summary>Signs a <see cref="PdfDocument"/> (it is saved first) and returns the signed bytes.</summary>
    public static byte[] Sign(PdfDocument document, IExternalSigner signer, PdfSignatureOptions? options = null) =>
        Sign(document.ToArray(), signer, options);

    /// <summary>
    /// Step 1 of a deferred signature: prepares the document and returns the data to sign. Send
    /// <see cref="PreparedSignature.GetDocumentDigest"/> (or the signed data) to the signing service, which returns a
    /// detached CMS/PKCS#7 container, then call <see cref="CompleteExternalSignature(PreparedSignature, byte[])"/>.
    /// </summary>
    /// <param name="pdf">The document.</param>
    /// <param name="options">Signature options (time-stamps must be added by the signing service).</param>
    /// <param name="certificate">Optional signer certificate, used for the visible appearance and /Name.</param>
    /// <param name="containerSize">Space reserved for the signature container in bytes.</param>
    public static PreparedSignature PrepareExternalSignature(byte[] pdf, PdfSignatureOptions options, X509Certificate2? certificate = null, int containerSize = 16384)
    {
        var subFilter = options.Format == PdfSignatureFormat.PAdES ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached";
        return Prepare(pdf, options, PdfName.Sig, subFilter, containerSize, certificate, options.SigningTime ?? DateTimeOffset.Now);
    }

    /// <summary>Step 2 of a deferred signature: embeds the CMS container returned by the signing service.</summary>
    public static byte[] CompleteExternalSignature(PreparedSignature prepared, byte[] cmsContainer) => Embed(prepared, cmsContainer);

    /// <summary>Embeds a CMS container into a prepared PDF given only its bytes and the field name.</summary>
    public static byte[] CompleteExternalSignature(byte[] preparedPdf, string fieldName, byte[] cmsContainer, string? password = null)
    {
        var doc = PdfDocument.Open(preparedPdf, password);
        var field = doc.GetForm()?.GetField<PdfSignatureField>(fieldName) ?? throw new PdfException($"Signature field '{fieldName}' not found.");
        var br = field.SignatureDictionary?.GetArray(PdfName.ByteRange) ?? throw new PdfException("The field has no prepared signature.");
        var prepared = new PreparedSignature(preparedPdf, fieldName, br.Select(o => ((PdfNumber)o.Resolve()).LongValue).ToArray(), HashAlgorithmName.SHA256);
        return Embed(prepared, cmsContainer);
    }

    /// <summary>
    /// Adds a document time-stamp (PAdES, /SubFilter ETSI.RFC3161) as incremental update, e.g. to extend
    /// the validity of existing signatures (B-LTA).
    /// </summary>
    public static byte[] AddDocumentTimestamp(byte[] pdf, ITimestampClient tsa, string? fieldName = null, string? password = null,
        HashAlgorithmName? digestAlgorithm = null)
    {
        var options = new PdfSignatureOptions { FieldName = fieldName, Password = password, DigestAlgorithm = digestAlgorithm ?? HashAlgorithmName.SHA256 };
        int size = 12000;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var prepared = Prepare(pdf, options, PdfName.Get("DocTimeStamp"), "ETSI.RFC3161", size, null, DateTimeOffset.Now);
            var token = tsa.GetTimestampToken(prepared.GetDocumentDigest(), options.DigestAlgorithm);
            if (token.Length <= prepared.ContainerSize) return Embed(prepared, token);
            size = token.Length + 2048;
        }
        throw new PdfException("Could not embed the time-stamp token.");
    }

    // ------------------------------------------------------------------ internals

    private static int EstimateSize(IExternalSigner signer, PdfSignatureOptions options)
    {
        int size = signer.Certificate.RawData.Length + signer.CertificateChain.Sum(c => c.RawData.Length) + 2048;
        size += options.AdditionalSignedAttributes.Concat(options.AdditionalUnsignedAttributes).Sum(a => a.RawData.Length + a.Oid!.Value!.Length + 16);
        if (options.TimestampClient != null) size += 10000;
        return size;
    }

    private static PreparedSignature Prepare(byte[] pdf, PdfSignatureOptions options, PdfName type, string subFilter, int containerSize,
        X509Certificate2? certificate, DateTimeOffset signingTime)
    {
        var doc = PdfDocument.Open(pdf, options.Password);
        if (doc.WasRepaired)
        {
            if (!options.RepairDamagedDocument)
                throw new PdfException("The document is damaged; save it (full save) before signing or set RepairDamagedDocument.");
            if (doc.GetForm()?.Fields.OfType<PdfSignatureField>().Any(f => f.IsSigned) == true)
                throw new PdfException("The document is damaged and contains signatures; repairing would invalidate them.");
            pdf = doc.ToArray();
            doc = PdfDocument.Open(pdf, options.Password);
        }
        var form = doc.GetOrCreateForm();
        form.Dictionary.Set(PdfName.SigFlags, 3); // signatures exist, append only

        string fieldName = options.FieldName ?? NextFieldName(form, type == PdfName.Sig ? "Signature" : "Timestamp");
        var field = form.GetField(fieldName) as PdfSignatureField;
        if (form.GetField(fieldName) is { } other && field == null) throw new PdfException($"Field '{fieldName}' is not a signature field.");
        if (field?.IsSigned == true) throw new PdfException($"Signature field '{fieldName}' is already signed.");
        if (field == null)
        {
            var rect = type == PdfName.Sig ? options.Rectangle ?? default : default;
            field = form.AddSignatureField(doc.GetPage(Math.Clamp(options.PageNumber, 1, doc.PageCount)), fieldName, rect);
        }

        var sig = new PdfDictionary(type);
        sig[PdfName.Filter] = AdobePpkLite;
        sig[PdfName.SubFilter] = PdfName.Get(subFilter);
        sig[PdfName.ByteRange] = new PdfArray(new PdfNumber(0), new PdfNumber(ByteRangePlaceholder), new PdfNumber(ByteRangePlaceholder), new PdfNumber(ByteRangePlaceholder));
        sig[PdfName.Contents] = new PdfString(new byte[containerSize], hex: true);
        if (type == PdfName.Sig)
        {
            sig[PdfName.M] = new PdfString(PdfDate.Format(signingTime));
            string? name = options.SignerName ?? certificate?.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.IsNullOrEmpty(name)) sig.SetText(PdfName.Name, name);
            if (options.Reason != null) sig.SetText(PdfName.Get("Reason"), options.Reason);
            if (options.Location != null) sig.SetText(PdfName.Get("Location"), options.Location);
            if (options.ContactInfo != null) sig.SetText(PdfName.Get("ContactInfo"), options.ContactInfo);
            var app = new PdfDictionary { [PdfName.Name] = PdfName.Get(string.IsNullOrEmpty(options.SignatureCreator) ? "miPDFlib" : options.SignatureCreator) };
            sig[PdfName.Get("Prop_Build")] = new PdfDictionary { [PdfName.Get("App")] = app };
            foreach (var (key, value) in options.SignatureDictionaryEntries)
            {
                if (key is "Type" or "Filter" or "SubFilter" or "ByteRange" or "Contents" or "Reference")
                    throw new PdfException($"The signature dictionary entry /{key} cannot be overridden.");
                sig[PdfName.Get(key)] = value.Clone();
            }
        }
        var references = new PdfArray();
        if (type == PdfName.Sig && (options.FieldLock ?? field.Lock) is { } fieldLock)
        {
            var unknown = fieldLock.Fields.Where(f => form.GetField(f) == null).ToList();
            if (unknown.Count > 0) throw new PdfException($"Field lock: unknown field(s) {string.Join(", ", unknown)}.");
            if (options.FieldLock != null) field.Lock = fieldLock;
            var lockRef = new PdfDictionary(PdfName.Get("SigRef"));
            lockRef[PdfName.Get("TransformMethod")] = PdfName.Get("FieldMDP");
            lockRef[PdfName.Get("TransformParams")] = fieldLock.ToDictionary(PdfFieldLock.TransformParams);
            if (doc.Trailer[PdfName.Root] is PdfReference catalog)
                lockRef[PdfName.Get("Data")] = catalog; // required for FieldMDP: indirect reference to the catalog
            references.Add(lockRef);
        }

        var sigRef = doc.AddObject(sig);
        field.Dictionary[PdfName.V] = sigRef;

        if (options.Certification is { } level && type == PdfName.Sig)
        {
            if (doc.Catalog.GetDictionary(PdfName.Perms)?.ContainsKey(PdfName.Get("DocMDP")) == true)
                throw new PdfException("The document is already certified.");
            if (doc.GetForm()!.Fields.OfType<PdfSignatureField>().Any(f => f.IsSigned && !ReferenceEquals(f.Dictionary, field.Dictionary)))
                throw new PdfException("A certification signature must be the first signature of a document.");
            var transform = new PdfDictionary(PdfName.Get("TransformParams"));
            transform.Set(PdfName.P, (int)level);
            transform[PdfName.V] = PdfName.Get("1.2");
            var sigRefDict = new PdfDictionary(PdfName.Get("SigRef"));
            sigRefDict[PdfName.Get("TransformMethod")] = PdfName.Get("DocMDP");
            sigRefDict[PdfName.Get("TransformParams")] = transform;
            references.Insert(0, sigRefDict);
            var perms = doc.Catalog.GetDictionary(PdfName.Perms) ?? new PdfDictionary();
            perms[PdfName.Get("DocMDP")] = sigRef;
            doc.Catalog[PdfName.Perms] = perms;
        }
        if (references.Count > 0) sig[PdfName.Get("Reference")] = references;

        var widget = field.Widgets.FirstOrDefault();
        if (widget != null && type == PdfName.Sig && widget.Rect.Width > 0 && widget.Rect.Height > 0)
            SignatureAppearanceBuilder.Apply(doc, widget, options, certificate, signingTime);
        if (widget != null && doc.IsTagged)
        {
            string? signerName = options.SignerName ?? certificate?.GetNameInfo(X509NameType.SimpleName, false);
            TagSignatureField(doc, field, widget, type != PdfName.Sig ? "Document time-stamp"
                : string.IsNullOrEmpty(signerName) ? "Digital signature" : "Digital signature of " + signerName);
        }

        // the update is appended to the PDF data (without data before the %PDF- header, if any)
        var bytes = doc.ToArray(new PdfSaveOptions { Incremental = true });
        return LocatePlaceholders(bytes, doc.Store.OriginalData.Length, containerSize, fieldName, options.DigestAlgorithm);
    }

    /// <summary>Keeps tagged (PDF/UA) documents accessible: description, tab order and a Form structure element.</summary>
    private static void TagSignatureField(PdfDocument doc, PdfSignatureField field, PdfWidget widget, string description)
    {
        field.ToolTip ??= description;
        if (widget.Page is not { } page) return;
        page.Dictionary[PdfName.Get("Tabs")] = PdfName.S;
        if (widget.Rect.Width <= 0 || widget.Rect.Height <= 0 || widget.Dictionary.ContainsKey(PdfName.Get("StructParent"))) return;
        var annotation = page.Annotations.FirstOrDefault(a => ReferenceEquals(a.Dictionary, widget.Dictionary));
        if (annotation?.Reference == null) return;
        var element = doc.EnableTagging().Root.AddChild(Tagging.StructureType.Form, field.ToolTip);
        element.AddAnnotation(annotation, page);
    }

    private static string NextFieldName(PdfAcroForm form, string prefix)
    {
        for (int i = 1; ; i++)
            if (form.GetField(prefix + i) == null) return prefix + i;
    }

    private static PreparedSignature LocatePlaceholders(byte[] bytes, int searchFrom, int containerSize, string fieldName, HashAlgorithmName digest)
    {
        var text = Encoding.Latin1.GetString(bytes, searchFrom, bytes.Length - searchFrom);
        string contentsPattern = "<" + new string('0', containerSize * 2) + ">";
        int contents = text.IndexOf(contentsPattern, StringComparison.Ordinal);
        string rangePattern = $"[0 {ByteRangePlaceholder} {ByteRangePlaceholder} {ByteRangePlaceholder}]";
        int range = text.IndexOf(rangePattern, StringComparison.Ordinal);
        if (contents < 0 || range < 0) throw new PdfException("Signature placeholders not found in the prepared document.");
        long start = searchFrom + contents;
        long end = start + contentsPattern.Length;
        var byteRange = new[] { 0, start, end, bytes.Length - end };
        var replacement = $"[0 {byteRange[1]} {byteRange[2]} {byteRange[3]}";
        replacement = replacement.PadRight(rangePattern.Length - 1) + "]";
        Encoding.ASCII.GetBytes(replacement).CopyTo(bytes, searchFrom + range);
        return new PreparedSignature(bytes, fieldName, byteRange, digest);
    }

    private static byte[] Embed(PreparedSignature prepared, byte[] container)
    {
        if (container.Length > prepared.ContainerSize)
            throw new PdfException($"The signature container ({container.Length} bytes) exceeds the reserved space ({prepared.ContainerSize} bytes).");
        var result = (byte[])prepared.PreparedPdf.Clone();
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(container));
        hex.CopyTo(result, (int)prepared.ByteRange[1] + 1);
        return result;
    }
}
