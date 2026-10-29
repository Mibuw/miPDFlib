using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using MiPdf.Forms;
using MiPdf.Objects;

namespace MiPdf.Signatures;

/// <summary>Result of verifying one signature.</summary>
public sealed class SignatureValidationResult
{
    public required string FieldName { get; init; }

    /// <summary>"ETSI.CAdES.detached", "adbe.pkcs7.detached", "ETSI.RFC3161", ...</summary>
    public required string SubFilter { get; init; }

    public bool IsDocumentTimestamp => SubFilter == "ETSI.RFC3161";

    /// <summary>The signed bytes are unchanged and the cryptographic signature is valid.</summary>
    public bool IsIntegrityValid { get; init; }

    /// <summary>The signature covers the complete file (no later incremental updates).</summary>
    public bool CoversWholeDocument { get; init; }

    /// <summary>Number of bytes covered (= length of the signed revision).</summary>
    public long SignedRevisionLength { get; init; }

    public X509Certificate2? SignerCertificate { get; init; }

    public string? SignerName { get; init; }

    /// <summary>Claimed signing time (/M or CMS signing-time).</summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>Time from a verified signature or document time-stamp.</summary>
    public DateTimeOffset? TimestampTime { get; init; }

    public bool HasTimestamp => TimestampTime.HasValue;

    /// <summary>Result of building the certificate chain (null if not checked).</summary>
    public bool? IsCertificateTrusted { get; init; }

    public string? Reason { get; init; }

    public string? Location { get; init; }

    /// <summary>DocMDP level of a certification signature, null for approval signatures.</summary>
    public CertificationLevel? CertificationLevel { get; init; }

    /// <summary>Form fields locked by this signature (FieldMDP), null if it locks none.</summary>
    public PdfFieldLock? FieldLock { get; init; }

    /// <summary>
    /// Locked fields (<see cref="FieldLock"/>) whose value was changed or that were removed after signing. Validators
    /// such as Adobe Acrobat treat such a signature as invalid even though <see cref="IsIntegrityValid"/> is true.
    /// </summary>
    public IReadOnlyList<string> ModifiedLockedFields { get; internal set; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>The signature dictionary (/V), e.g. to read application-specific entries.</summary>
    public PdfDictionary? SignatureDictionary { get; init; }

    /// <summary>Returns a text entry of the signature dictionary (e.g. a custom "/Biometrics" entry), or null.</summary>
    public string? GetSignatureDictionaryText(string key) => SignatureDictionary?.GetText(PdfName.Get(key));

    /// <summary>The DER-encoded signature container (CMS SignedData or time-stamp token).</summary>
    public byte[]? Cms { get; init; }

    /// <summary>Certificates embedded in the signature container.</summary>
    public IReadOnlyList<X509Certificate2> Certificates { get; init; } = Array.Empty<X509Certificate2>();

    /// <summary>
    /// PAdES level, derived from the time-stamp, the Document Security Store and later document time-stamps
    /// (null for document time-stamps).
    /// </summary>
    public PadesLevel? Level { get; internal set; }

    /// <summary>Returns the DER value of a signed CMS attribute (e.g. biometric data), or null.</summary>
    public byte[]? GetSignedAttribute(string oid) => Cms == null || IsDocumentTimestamp ? null : CmsUtil.GetSignedAttribute(Cms, oid);

    /// <summary>Returns the DER value of an unsigned CMS attribute, or null.</summary>
    public byte[]? GetUnsignedAttribute(string oid) => Cms == null || IsDocumentTimestamp ? null : CmsUtil.GetUnsignedAttribute(Cms, oid);

    public override string ToString() =>
        $"{FieldName}: {(IsIntegrityValid ? "valid" : "INVALID")}, signer={SignerName}, whole document={CoversWholeDocument}" +
        (HasTimestamp ? $", timestamp={TimestampTime:O}" : "") + (Errors.Count > 0 ? " – " + string.Join("; ", Errors) : "");
}

/// <summary>Options for <see cref="PdfSignatureValidator"/>.</summary>
public sealed class SignatureValidationOptions
{
    /// <summary>Build and check the certificate chain against the system trust store.</summary>
    public bool CheckCertificateChain { get; set; }

    /// <summary>Revocation checking used when building chains (default: none, offline).</summary>
    public X509RevocationMode RevocationMode { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>Additional trusted root certificates (custom trust store).</summary>
    public X509Certificate2Collection TrustedRoots { get; } = new();
}

/// <summary>Verifies the digital signatures of a document.</summary>
public static class PdfSignatureValidator
{
    public static IReadOnlyList<SignatureValidationResult> Validate(byte[] pdf, SignatureValidationOptions? options = null, string? password = null) =>
        Validate(PdfDocument.Open(pdf, password), options, password);

    public static IReadOnlyList<SignatureValidationResult> Validate(PdfDocument document, SignatureValidationOptions? options = null) =>
        Validate(document, options, null);

    private static IReadOnlyList<SignatureValidationResult> Validate(PdfDocument document, SignatureValidationOptions? options, string? password)
    {
        options ??= new SignatureValidationOptions();
        var results = new List<SignatureValidationResult>();
        var form = document.GetForm();
        if (form == null) return results;
        byte[] file = document.Store.OriginalData;
        foreach (var field in form.Fields.OfType<PdfSignatureField>())
        {
            if (field.SignatureDictionary is { } sig) results.Add(Verify(field.FullName, sig, file, options));
        }
        foreach (var result in results.Where(r => r.FieldLock != null && r.IsIntegrityValid && !r.CoversWholeDocument))
            result.ModifiedLockedFields = FindModifiedLockedFields(form, file, result, password);
        var dss = document.Catalog.GetDictionary(PdfName.Get("DSS"));
        bool validationData = dss?.GetArray(PdfName.Get("OCSPs")) is { Count: > 0 } || dss?.GetArray(PdfName.Get("CRLs")) is { Count: > 0 };
        foreach (var result in results.Where(r => !r.IsDocumentTimestamp))
        {
            bool laterTimestamp = results.Any(r => r.IsDocumentTimestamp && r.IsIntegrityValid && r.SignedRevisionLength > result.SignedRevisionLength);
            result.Level = validationData && laterTimestamp ? PadesLevel.BLTA
                : validationData ? PadesLevel.BLT
                : result.HasTimestamp || laterTimestamp ? PadesLevel.BT
                : PadesLevel.BB;
        }
        return results;
    }

    private static SignatureValidationResult Verify(string fieldName, PdfDictionary sig, byte[] file, SignatureValidationOptions options)
    {
        var errors = new List<string>();
        string subFilter = sig.GetName(PdfName.SubFilter)?.Value ?? "";
        var br = sig.GetArray(PdfName.ByteRange)?.Select(o => (o.Resolve() as PdfNumber)?.LongValue ?? -1).ToArray();
        var contents = CmsBuilder.TrimDer(sig.GetString(PdfName.Contents)?.Bytes ?? Array.Empty<byte>());
        DateTimeOffset? claimed = PdfDate.Parse(sig.GetText(PdfName.M));
        CertificationLevel? certification = null;
        PdfFieldLock? fieldLock = null;
        if (sig.GetArray(PdfName.Get("Reference")) is { } refs)
            foreach (var r in refs.Select(o => o.Resolve()).OfType<PdfDictionary>())
                switch (r.GetName(PdfName.Get("TransformMethod"))?.Value)
                {
                    case "DocMDP":
                        certification = (CertificationLevel)(r.GetDictionary(PdfName.Get("TransformParams"))?.GetInt(PdfName.P, 2) ?? 2);
                        break;
                    case "FieldMDP":
                        fieldLock = PdfFieldLock.FromDictionary(r.GetDictionary(PdfName.Get("TransformParams")));
                        break;
                }

        if (br == null || br.Length != 4 || br.Any(v => v < 0) || br[0] != 0 || br[1] > br[2] || br[2] + br[3] > file.Length)
        {
            errors.Add("Invalid /ByteRange.");
            return new SignatureValidationResult { FieldName = fieldName, SubFilter = subFilter, Errors = errors, SigningTime = claimed };
        }
        var data = new byte[br[1] + br[3]];
        file.AsSpan(0, (int)br[1]).CopyTo(data);
        file.AsSpan((int)br[2], (int)br[3]).CopyTo(data.AsSpan((int)br[1]));
        // the excluded range must be exactly the /Contents hex string
        if (br[2] - br[1] != sig.GetString(PdfName.Contents)!.Bytes.Length * 2 + 2 || file[br[1]] != '<' || file[br[2] - 1] != '>')
            errors.Add("The byte range does not exclude exactly the signature contents.");
        bool coversWhole = br[2] + br[3] == file.Length || IsOnlyTrailingWhitespace(file, br[2] + br[3]);

        bool integrity = false;
        X509Certificate2? signer = null;
        DateTimeOffset? signingTime = claimed, timestamp = null;
        bool? trusted = null;
        var embedded = new List<X509Certificate2>();
        try
        {
            if (subFilter == "ETSI.RFC3161")
            {
                if (!Rfc3161TimestampToken.TryDecode(contents, out var token, out _)) throw new CryptographicException("Invalid time-stamp token.");
                integrity = token.VerifySignatureForData(data, out signer);
                embedded.AddRange(token.AsSignedCms().Certificates);
                timestamp = token.TokenInfo.Timestamp;
                if (!integrity) errors.Add("The time-stamp does not match the document.");
            }
            else
            {
                SignedCms cms;
                if (subFilter == "adbe.pkcs7.sha1")
                {
                    cms = new SignedCms();
                    cms.Decode(contents);
                    if (!cms.ContentInfo.Content.AsSpan().SequenceEqual(SHA1.HashData(data))) errors.Add("Document digest mismatch.");
                }
                else
                {
                    cms = new SignedCms(new ContentInfo(data), detached: true);
                    cms.Decode(contents);
                }
                var signerInfo = cms.SignerInfos[0];
                signer = signerInfo.Certificate;
                embedded.AddRange(cms.Certificates);
                signerInfo.CheckSignature(verifySignatureOnly: true); // checks message digest and signature value
                integrity = errors.Count == 0;
                foreach (var attr in signerInfo.SignedAttributes)
                    if (attr.Oid.Value == "1.2.840.113549.1.9.5" && attr.Values.Count > 0)
                        signingTime ??= new Pkcs9SigningTime(attr.Values[0].RawData).SigningTime;
                foreach (var attr in signerInfo.UnsignedAttributes)
                {
                    if (attr.Oid.Value != CmsBuilder.OidTimestampToken || attr.Values.Count == 0) continue;
                    if (Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var token, out _) &&
                        token.VerifySignatureForSignerInfo(signerInfo, out _))
                        timestamp = token.TokenInfo.Timestamp;
                    else errors.Add("The signature time-stamp is invalid.");
                }
                if (options.CheckCertificateChain && signer != null)
                    trusted = CheckChain(signer, cms.Certificates, options, timestamp ?? signingTime, errors);
            }
        }
        catch (CryptographicException ex)
        {
            integrity = false;
            errors.Add("Signature verification failed: " + ex.Message);
        }

        return new SignatureValidationResult
        {
            FieldName = fieldName,
            SubFilter = subFilter,
            IsIntegrityValid = integrity && errors.Count == 0,
            CoversWholeDocument = coversWhole,
            SignedRevisionLength = br[2] + br[3],
            SignerCertificate = signer,
            SignerName = sig.GetText(PdfName.Name) ?? signer?.GetNameInfo(X509NameType.SimpleName, false),
            SigningTime = signingTime,
            TimestampTime = timestamp,
            IsCertificateTrusted = trusted,
            Reason = sig.GetText(PdfName.Get("Reason")),
            Location = sig.GetText(PdfName.Get("Location")),
            CertificationLevel = certification,
            FieldLock = fieldLock,
            Errors = errors,
            Cms = contents,
            Certificates = embedded,
            SignatureDictionary = sig,
        };
    }

    /// <summary>Compares the locked fields of the signed revision with the current document.</summary>
    private static IReadOnlyList<string> FindModifiedLockedFields(PdfAcroForm current, byte[] file, SignatureValidationResult result, string? password)
    {
        PdfAcroForm? signed;
        try
        {
            signed = PdfDocument.Open(file.AsSpan(0, (int)result.SignedRevisionLength).ToArray(), password).GetForm();
        }
        catch (PdfException)
        {
            return new[] { "(the signed revision cannot be read)" };
        }
        var fieldLock = result.FieldLock!;
        var currentFields = current.Fields.Where(f => f is not PdfSignatureField)
            .GroupBy(f => f.FullName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var modified = new List<string>();
        foreach (var before in signed?.Fields ?? Enumerable.Empty<PdfFormField>())
        {
            // signature fields are protected by their own signatures
            if (before is PdfSignatureField || !fieldLock.Locks(before.FullName)) continue;
            if (!currentFields.TryGetValue(before.FullName, out var after) || after.Value != before.Value)
                modified.Add(before.FullName);
        }
        return modified;
    }

    private static bool IsOnlyTrailingWhitespace(byte[] file, long from)
    {
        for (long i = from; i < file.Length; i++)
            if (!IO.PdfLexer.IsWhitespace(file[i])) return false;
        return true;
    }

    private static bool CheckChain(X509Certificate2 certificate, X509Certificate2Collection extra, SignatureValidationOptions options,
        DateTimeOffset? time, List<string> errors)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = options.RevocationMode;
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        if (options.TrustedRoots.Count > 0)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(options.TrustedRoots);
        }
        if (time.HasValue) chain.ChainPolicy.VerificationTime = time.Value.LocalDateTime;
        bool ok = chain.Build(certificate);
        if (!ok) errors.Add("Certificate chain: " + string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim())));
        return ok;
    }

    /// <summary>Returns the document as it was when the given signature was applied (the signed revision).</summary>
    public static byte[] GetSignedRevision(byte[] pdf, SignatureValidationResult signature) =>
        pdf.AsSpan(0, (int)signature.SignedRevisionLength).ToArray();
}
