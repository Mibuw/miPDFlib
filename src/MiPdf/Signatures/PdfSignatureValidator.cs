using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using MiPdf.Forms;
using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf.Signatures;

/// <summary>Result of verifying one signature.</summary>
public sealed class SignatureValidationResult
{
    /// <summary>Fully qualified name of the signature field.</summary>
    public required string FieldName { get; init; }

    /// <summary>"ETSI.CAdES.detached", "adbe.pkcs7.detached", "ETSI.RFC3161", ...</summary>
    public required string SubFilter { get; init; }

    /// <summary>True for a document time-stamp (/SubFilter ETSI.RFC3161) rather than a signature.</summary>
    public bool IsDocumentTimestamp => SubFilter == "ETSI.RFC3161";

    /// <summary>The signed bytes are unchanged and the cryptographic signature is valid.</summary>
    public bool IsIntegrityValid { get; init; }

    /// <summary>The signature covers the complete file (no later incremental updates).</summary>
    public bool CoversWholeDocument { get; init; }

    /// <summary>Number of bytes covered (= length of the signed revision).</summary>
    public long SignedRevisionLength { get; init; }

    /// <summary>The certificate of the signer (for document time-stamps: of the time-stamping authority); null if it could not be determined.</summary>
    public X509Certificate2? SignerCertificate { get; init; }

    /// <summary>The /Name of the signature dictionary, otherwise the common name of the <see cref="SignerCertificate"/>.</summary>
    public string? SignerName { get; init; }

    /// <summary>Claimed signing time (/M or CMS signing-time).</summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>Time from a verified signature or document time-stamp.</summary>
    public DateTimeOffset? TimestampTime { get; init; }

    /// <summary>True if a verified time-stamp is present (<see cref="TimestampTime"/> is set).</summary>
    public bool HasTimestamp => TimestampTime.HasValue;

    /// <summary>Result of building the certificate chain (null if not checked).</summary>
    public bool? IsCertificateTrusted { get; init; }

    /// <summary>The /Reason entry of the signature dictionary, or null.</summary>
    public string? Reason { get; init; }

    /// <summary>The /Location entry of the signature dictionary, or null.</summary>
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

    /// <summary>
    /// The changes made in the incremental updates after this signature (empty if it covers the whole document), each
    /// with its kind and whether it is permitted by the certification level (DocMDP) – see
    /// <see cref="DocumentChange.IsPermitted"/>. The signed bytes stay intact (<see cref="IsIntegrityValid"/>); this
    /// tells whether the later changes are acceptable.
    /// </summary>
    public IReadOnlyList<DocumentChange> ChangesAfterSigning { get; internal set; } = Array.Empty<DocumentChange>();

    /// <summary>
    /// True if a change after signing is not permitted (<see cref="ChangesAfterSigning"/>) or a locked field was
    /// modified (<see cref="ModifiedLockedFields"/>). Validators such as Adobe Acrobat and EU DSS then report the
    /// signature as invalid or the document as modified.
    /// </summary>
    public bool HasDisallowedChanges => ModifiedLockedFields.Count > 0 || ChangesAfterSigning.Any(c => !c.IsPermitted);

    /// <summary>Problems found during verification (invalid byte range, digest mismatch, invalid time-stamp, untrusted chain, ...).</summary>
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

    /// <summary>
    /// Revocation status of the signer's certificate chain at the time of signing, evaluated from the OCSP responses
    /// and CRLs stored in the document (DSS) or in the signature (adbe-revocationInfoArchival); null if the document
    /// contains no revocation data. For document time-stamps this is the chain of the time-stamping authority. The
    /// time of signing is the verified signature time-stamp, otherwise the earliest later document time-stamp,
    /// otherwise the time of validation – the claimed signing time is not trusted. Details per certificate:
    /// <see cref="RevocationDetails"/>.
    /// </summary>
    public RevocationStatus? Revocation { get; internal set; }

    /// <summary>Revocation status per certificate of the chain (signer first; roots are not checked).</summary>
    public IReadOnlyList<CertificateRevocation> RevocationDetails { get; internal set; } = Array.Empty<CertificateRevocation>();

    /// <summary>Returns the DER value of a signed CMS attribute (e.g. biometric data), or null.</summary>
    public byte[]? GetSignedAttribute(string oid) => Cms == null || IsDocumentTimestamp ? null : CmsUtil.GetSignedAttribute(Cms, oid);

    /// <summary>Returns the DER value of an unsigned CMS attribute, or null.</summary>
    public byte[]? GetUnsignedAttribute(string oid) => Cms == null || IsDocumentTimestamp ? null : CmsUtil.GetUnsignedAttribute(Cms, oid);

    /// <summary>Returns a one-line summary (field, validity, signer, coverage, time-stamp and errors), for logging.</summary>
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

    /// <summary>
    /// Trusted certificates (custom trust store; the system store is then not used). Besides root certificates, these
    /// can be CA certificates that are not self-signed, such as the trust anchors of EU trusted lists.
    /// </summary>
    public X509Certificate2Collection TrustedRoots { get; } = new();
}

/// <summary>Verifies the digital signatures of a document.</summary>
public static class PdfSignatureValidator
{
    /// <summary>
    /// Verifies all signatures and document time-stamps of a PDF file. Returns one result per signed signature field
    /// (unsigned fields are skipped); the list is empty if the document has no signatures.
    /// </summary>
    /// <param name="pdf">The complete PDF file.</param>
    /// <param name="options">Validation options (default: no certificate chain check).</param>
    /// <param name="password">Password for encrypted documents.</param>
    public static IReadOnlyList<SignatureValidationResult> Validate(byte[] pdf, SignatureValidationOptions? options = null, string? password = null)
    {
        using var document = PdfDocument.Open(pdf, password);
        return Validate(document, options, password);
    }

    /// <summary>
    /// Verifies all signatures and document time-stamps of a PDF file. The file is read on demand and the signed byte
    /// ranges are hashed from it, so files of any size (also larger than 2 GB) are validated with little memory.
    /// </summary>
    /// <param name="path">The PDF file.</param>
    /// <param name="options">Validation options (default: no certificate chain check).</param>
    /// <param name="password">Password for encrypted documents.</param>
    public static IReadOnlyList<SignatureValidationResult> Validate(string path, SignatureValidationOptions? options = null, string? password = null)
    {
        using var document = PdfDocument.Open(path, new PdfOpenOptions { Password = password, ReadFromFile = true });
        return Validate(document, options, password);
    }

    /// <summary>
    /// Verifies all signatures and document time-stamps of an opened document against the file it was opened from
    /// (unsaved changes are not considered). Returns one result per signed signature field.
    /// </summary>
    public static IReadOnlyList<SignatureValidationResult> Validate(PdfDocument document, SignatureValidationOptions? options = null) =>
        Validate(document, options, document.OpenPassword);

    private static IReadOnlyList<SignatureValidationResult> Validate(PdfDocument document, SignatureValidationOptions? options, string? password)
    {
        options ??= new SignatureValidationOptions();
        var results = new List<SignatureValidationResult>();
        var form = document.GetForm();
        if (form == null) return results;
        var file = document.Store.File;
        foreach (var field in form.Fields.OfType<PdfSignatureField>())
        {
            if (field.SignatureDictionary is { } sig) results.Add(Verify(field.FullName, sig, file, options));
        }
        AnalyzeChanges(results, file, password);
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
        EvaluateRevocation(dss, results, options);
        return results;
    }

    /// <summary>Sets <see cref="SignatureValidationResult.Revocation"/> from the stored validation data.</summary>
    private static void EvaluateRevocation(PdfDictionary? dss, List<SignatureValidationResult> results, SignatureValidationOptions options)
    {
        List<byte[]> Streams(string key) => dss?.GetArray(PdfName.Get(key))?.Select(o => o.Resolve()).OfType<PdfStream>()
            .Take(EmbeddedRevocation.MaxItems).Select(s => s.GetBytes()).ToList() ?? new List<byte[]>();
        var dssOcsps = Streams("OCSPs").Select(EmbeddedRevocation.ParseOcsp).OfType<EmbeddedRevocation.Ocsp>().ToList();
        var dssCrls = Streams("CRLs").Select(EmbeddedRevocation.ParseCrl).OfType<EmbeddedRevocation.Crl>().ToList();
        var dssCerts = new List<X509Certificate2>();
        foreach (var der in Streams("Certs"))
        {
            try
            {
                dssCerts.Add(CmsBuilder.LoadCertificate(der));
            }
            catch (CryptographicException)
            {
                // unreadable certificates are ignored
            }
        }

        foreach (var result in results.Where(r => r.SignerCertificate != null))
        {
            var ocsps = new List<EmbeddedRevocation.Ocsp>(dssOcsps);
            var crls = new List<EmbeddedRevocation.Crl>(dssCrls);
            if (result.GetSignedAttribute(EmbeddedRevocation.OidRevocationInfoArchival) is { } archival)
            {
                var crlData = new List<byte[]>();
                var ocspData = new List<byte[]>();
                EmbeddedRevocation.ParseRevocationInfoArchival(archival, crlData, ocspData);
                ocsps.AddRange(ocspData.Select(EmbeddedRevocation.ParseOcsp).OfType<EmbeddedRevocation.Ocsp>());
                crls.AddRange(crlData.Select(EmbeddedRevocation.ParseCrl).OfType<EmbeddedRevocation.Crl>());
            }
            if (ocsps.Count == 0 && crls.Count == 0) continue;
            var pool = result.Certificates.Concat(dssCerts).Concat(ocsps.SelectMany(o => o.Certificates))
                .Concat(options.TrustedRoots.Cast<X509Certificate2>()).ToList();
            var time = result.TimestampTime
                ?? results.Where(r => r.IsDocumentTimestamp && r.IsIntegrityValid && r.SignedRevisionLength > result.SignedRevisionLength)
                    .Select(r => r.TimestampTime).Min()
                ?? DateTimeOffset.UtcNow;
            var (status, details) = EmbeddedRevocation.Evaluate(result.SignerCertificate!, pool, options.TrustedRoots.Cast<X509Certificate2>().ToList(), ocsps, crls, time);
            result.Revocation = status;
            result.RevocationDetails = details;
        }
    }

    private static SignatureValidationResult Verify(string fieldName, PdfDictionary sig, PdfFileData file, SignatureValidationOptions options)
    {
        var errors = new List<string>();
        var chainErrors = new List<string>(); // reported in Errors, but not part of IsIntegrityValid
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
        // the signed data are the two ranges; they are hashed in place, without copying them
        // the signed data are the two ranges; they are hashed in place (or read in blocks), without copying them
        byte[] Digest(HashAlgorithmName algorithm)
        {
            using var hash = IncrementalHash.CreateHash(algorithm);
            file.AppendTo(hash, 0, br[1]);
            file.AppendTo(hash, br[2], br[3]);
            return hash.GetHashAndReset();
        }
        // the excluded range must be exactly the /Contents hex string
        if (br[2] - br[1] != sig.GetString(PdfName.Contents)!.Bytes.Length * 2 + 2 || file.ReadByte(br[1]) != '<' || file.ReadByte(br[2] - 1) != '>')
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
                var hashAlgorithm = CmsVerifier.DigestName(token.TokenInfo.HashAlgorithmId.Value)
                    ?? throw new CryptographicException($"Unsupported digest algorithm {token.TokenInfo.HashAlgorithmId.Value}.");
                integrity = token.VerifySignatureForHash(Digest(hashAlgorithm), token.TokenInfo.HashAlgorithmId, out signer);
                embedded.AddRange(token.AsSignedCms().Certificates);
                timestamp = token.TokenInfo.Timestamp;
                if (!integrity) errors.Add("The time-stamp does not match the document.");
            }
            else
            {
                var cms = new SignedCms();
                cms.Decode(contents);
                var signerInfo = cms.SignerInfos[0];
                signer = signerInfo.Certificate;
                embedded.AddRange(cms.Certificates);
                if (subFilter == "adbe.pkcs7.sha1")
                {
                    // the SHA-1 digest of the document is the signed content
                    if (!cms.ContentInfo.Content.AsSpan().SequenceEqual(Digest(HashAlgorithmName.SHA1))) errors.Add("Document digest mismatch.");
                    signerInfo.CheckSignature(verifySignatureOnly: true);
                }
                else CmsVerifier.Verify(cms, contents, Digest); // message digest and signature value
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
                    trusted = CheckChain(signer, cms.Certificates, options, timestamp ?? signingTime, chainErrors);
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
            Errors = errors.Concat(chainErrors).ToList(),
            Cms = contents,
            Certificates = embedded,
            SignatureDictionary = sig,
        };
    }

    /// <summary>
    /// Compares the signed revision of each signature that does not cover the whole file with the file as saved:
    /// changes after signing (<see cref="SignatureValidationResult.ChangesAfterSigning"/>) and modified locked fields.
    /// </summary>
    private static void AnalyzeChanges(List<SignatureValidationResult> results, PdfFileData file, string? password)
    {
        var certification = results.FirstOrDefault(r => r.CertificationLevel != null && r.IsIntegrityValid)?.CertificationLevel;
        PdfDocument? saved = null;
        foreach (var result in results.Where(r => r.IsIntegrityValid && !r.CoversWholeDocument))
        {
            try
            {
                // views of the file, not copies; the file as saved, without unsaved changes of the opened document
                saved ??= PdfDocument.Open(file.Slice(0, file.Length), password);
                var signed = PdfDocument.Open(file.Slice(0, result.SignedRevisionLength), password);
                var changes = ChangeAnalyzer.Analyze(signed, saved, certification);
                if (result.FieldLock != null && saved.GetForm() is { } form)
                {
                    result.ModifiedLockedFields = FindModifiedLockedFields(form, signed.GetForm(), result.FieldLock);
                    // changes of locked fields are not permitted, whatever the certification level allows
                    var locked = result.ModifiedLockedFields.Select(name => $"field '{name}':").ToList();
                    changes = changes.Select(c => c.IsPermitted && locked.Any(prefix => c.Description.StartsWith(prefix, StringComparison.Ordinal))
                        ? new DocumentChange(c.Kind, c.Description + " (locked)", isPermitted: false) : c).ToList();
                }
                result.ChangesAfterSigning = changes;
            }
            catch (PdfException)
            {
                result.ChangesAfterSigning = new[]
                {
                    new DocumentChange(DocumentChangeKind.Other, "the signed revision cannot be read", isPermitted: false),
                };
                if (result.FieldLock != null) result.ModifiedLockedFields = new[] { "(the signed revision cannot be read)" };
            }
        }
    }

    /// <summary>Compares the locked fields of the signed revision with the current document.</summary>
    private static IReadOnlyList<string> FindModifiedLockedFields(PdfAcroForm current, PdfAcroForm? signed, PdfFieldLock fieldLock)
    {
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

    private static bool IsOnlyTrailingWhitespace(PdfFileData file, long from)
    {
        if (file.Length - from > 4096) return false;
        foreach (byte b in file.ReadBytes(from, (int)(file.Length - from)))
            if (!PdfLexer.IsWhitespace(b)) return false;
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
        bool ok = chain.Build(certificate) || EndsAtTrustAnchor(chain, options.TrustedRoots);
        if (!ok) errors.Add("Certificate chain: " + string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim())));
        return ok;
    }

    /// <summary>
    /// Trust anchors need not be self-signed: EU trusted lists name the issuing CA certificates. The chain is trusted if
    /// it reaches one of <paramref name="anchors"/> and the certificates up to the anchor have no errors (the part
    /// above the anchor, which may be missing or untrusted, is not evaluated).
    /// </summary>
    private static bool EndsAtTrustAnchor(X509Chain chain, X509Certificate2Collection anchors)
    {
        foreach (var element in chain.ChainElements)
        {
            if (element.ChainElementStatus.Any(s => s.Status is not (X509ChainStatusFlags.NoError or X509ChainStatusFlags.PartialChain
                    or X509ChainStatusFlags.UntrustedRoot)))
                return false;
            if (anchors.Cast<X509Certificate2>().Any(anchor => anchor.RawData.AsSpan().SequenceEqual(element.Certificate.RawData)))
                return true;
        }
        return false;
    }

    /// <summary>Returns the document as it was when the given signature was applied (the signed revision).</summary>
    public static byte[] GetSignedRevision(byte[] pdf, SignatureValidationResult signature) =>
        pdf.AsSpan(0, (int)signature.SignedRevisionLength).ToArray();
}
