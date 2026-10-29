using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using MiPdf.Forms;
using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf.Signatures;

/// <summary>Options for <see cref="PdfLtv"/>.</summary>
public sealed class LtvOptions
{
    /// <summary>Source of issuer certificates, OCSP responses and CRLs (default: <see cref="HttpRevocationSource"/>).</summary>
    public IRevocationSource RevocationSource { get; set; } = new HttpRevocationSource();

    /// <summary>Also embed CRLs when an OCSP response was obtained (default false: CRLs only as fallback).</summary>
    public bool IncludeCrlsWithOcsp { get; set; }

    /// <summary>Use CRLs at all (default true).</summary>
    public bool UseCrls { get; set; } = true;

    /// <summary>Use OCSP (default true).</summary>
    public bool UseOcsp { get; set; } = true;

    /// <summary>Write per-signature /VRI entries (optional in PAdES; default false).</summary>
    public bool IncludeVri { get; set; }

    /// <summary>Additional certificates, e.g. the trust anchor or intermediates not reachable via AIA.</summary>
    public IList<X509Certificate2> AdditionalCertificates { get; } = new List<X509Certificate2>();

    /// <summary>Restrict to these signature field names (default: all signatures and document time-stamps).</summary>
    public IReadOnlyCollection<string>? FieldNames { get; set; }

    /// <summary>Password for encrypted documents.</summary>
    public string? Password { get; set; }

    /// <summary>Maximum number of certificates processed (limits network requests for hostile documents; default 100).</summary>
    public int MaxCertificates { get; set; } = 100;
}

/// <summary>Result of <see cref="PdfLtv"/> operations.</summary>
public sealed class LtvResult
{
    internal LtvResult(byte[] pdf, int certificates, int ocsps, int crls, bool complete, IReadOnlyList<string> warnings, PadesLevel? level)
    {
        Pdf = pdf;
        CertificatesAdded = certificates;
        OcspResponsesAdded = ocsps;
        CrlsAdded = crls;
        IsComplete = complete;
        Warnings = warnings;
        Level = level;
    }

    /// <summary>The document with the added validation data (unchanged input if nothing could be added).</summary>
    public byte[] Pdf { get; }

    public int CertificatesAdded { get; }

    public int OcspResponsesAdded { get; }

    public int CrlsAdded { get; }

    /// <summary>True if revocation data was found for every certificate that needs it (all except roots and OCSP responders with no-check).</summary>
    public bool IsComplete { get; }

    /// <summary>Problems such as unreachable responders, missing issuers or revoked certificates.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The PAdES level reached by <see cref="PdfLtv.MakeLta"/> (null for <see cref="PdfLtv.AddValidationData"/>).</summary>
    public PadesLevel? Level { get; }
}

/// <summary>
/// Long-term validation (PAdES B-LT and B-LTA): adds certificates, OCSP responses and CRLs for existing signatures
/// to the Document Security Store (/DSS) in an incremental update. Works for signatures created by any software.
/// </summary>
/// <example>
/// <code>
/// var signed = PdfSigner.Sign(pdf, signer, new PdfSignatureOptions { TimestampClient = tsa });   // B-T
/// var lt = PdfLtv.AddValidationData(signed);                                                     // B-LT
/// var lta = PdfLtv.MakeLta(signed, tsa);                                                         // B-LTA
/// </code>
/// </example>
public static class PdfLtv
{
    /// <summary>Adds validation data for all (or the selected) signatures and document time-stamps (B-LT).</summary>
    public static LtvResult AddValidationData(byte[] pdf, LtvOptions? options = null) => AddValidationDataCore(pdf, options ?? new LtvOptions());

    /// <summary>
    /// Makes the signatures B-LTA: adds validation data, a document time-stamp and validation data for the
    /// time-stamp's certificates. If the time-stamp fails, the result is B-LT (see <see cref="LtvResult.Level"/>).
    /// </summary>
    public static LtvResult MakeLta(byte[] pdf, ITimestampClient tsa, LtvOptions? options = null)
    {
        options ??= new LtvOptions();
        var lt = AddValidationData(pdf, options);
        var warnings = lt.Warnings.ToList();
        byte[] timestamped;
        string field;
        try
        {
            var doc = PdfDocument.Open(lt.Pdf, options.Password);
            var form = doc.GetForm();
            field = "DocTimeStamp" + (1 + (form?.Fields.Count(f => f.FullName.StartsWith("DocTimeStamp", StringComparison.Ordinal)) ?? 0));
            timestamped = PdfSigner.AddDocumentTimestamp(lt.Pdf, tsa, field, options.Password);
        }
        catch (Exception ex) when (ex is PdfException or HttpRequestException or CryptographicException or TaskCanceledException or IOException)
        {
            warnings.Add("Document time-stamp failed: " + ex.Message);
            return new LtvResult(lt.Pdf, lt.CertificatesAdded, lt.OcspResponsesAdded, lt.CrlsAdded, lt.IsComplete, warnings, PadesLevel.BLT);
        }
        // validation data for the document time-stamp itself, so that it can be extended later
        var tsOptions = new LtvOptions
        {
            RevocationSource = options.RevocationSource,
            IncludeCrlsWithOcsp = options.IncludeCrlsWithOcsp,
            UseCrls = options.UseCrls,
            UseOcsp = options.UseOcsp,
            IncludeVri = options.IncludeVri,
            FieldNames = new[] { field },
            Password = options.Password,
        };
        foreach (var c in options.AdditionalCertificates) tsOptions.AdditionalCertificates.Add(c);
        var final = AddValidationData(timestamped, tsOptions);
        warnings.AddRange(final.Warnings);
        return new LtvResult(final.Pdf, lt.CertificatesAdded + final.CertificatesAdded, lt.OcspResponsesAdded + final.OcspResponsesAdded,
            lt.CrlsAdded + final.CrlsAdded, lt.IsComplete && final.IsComplete, warnings, PadesLevel.BLTA);
    }

    private static LtvResult AddValidationDataCore(byte[] pdf, LtvOptions options)
    {
        var source = options.RevocationSource;
        var warnings = new List<string>();
        var doc = PdfDocument.Open(pdf, options.Password);
        var signatures = new List<(string Field, byte[] Contents, SignedCms Cms)>();
        foreach (var field in doc.GetForm()?.Fields.OfType<PdfSignatureField>() ?? Enumerable.Empty<PdfSignatureField>())
        {
            if (field.SignatureDictionary is not { } sig || options.FieldNames?.Contains(field.FullName) == false) continue;
            var contents = sig.GetString(PdfName.Contents)?.Bytes;
            if (contents == null) continue;
            try
            {
                var cms = new SignedCms();
                cms.Decode(CmsBuilder.TrimDer(contents));
                signatures.Add((field.FullName, contents, cms));
            }
            catch (CryptographicException ex)
            {
                warnings.Add($"{field.FullName}: signature container cannot be decoded ({ex.Message}).");
            }
        }
        if (signatures.Count == 0)
        {
            warnings.Add("No signatures found.");
            return new LtvResult(pdf, 0, 0, 0, false, warnings, null);
        }

        var pool = new CertificatePool { Limit = options.MaxCertificates };
        foreach (var c in options.AdditionalCertificates) pool.Add(c);
        var perSignature = new List<(byte[] Contents, List<X509Certificate2> Certs)>();
        foreach (var (field, contents, cms) in signatures)
        {
            var certs = new List<X509Certificate2>();
            foreach (var c in cms.Certificates) certs.Add(c);
            if (cms.SignerInfos.Count > 0 && cms.SignerInfos[0].Certificate is { } signerCert) certs.Insert(0, signerCert);
            foreach (var info in cms.SignerInfos.Cast<SignerInfo>())
            foreach (var attr in info.UnsignedAttributes)
            {
                if (attr.Oid.Value != CmsBuilder.OidTimestampToken) continue;
                foreach (var value in attr.Values)
                {
                    try
                    {
                        var token = new SignedCms();
                        token.Decode(value.RawData);
                        foreach (var c in token.Certificates) certs.Add(c);
                    }
                    catch (CryptographicException ex)
                    {
                        warnings.Add($"{field}: signature time-stamp cannot be decoded ({ex.Message}).");
                    }
                }
            }
            foreach (var c in certs) pool.Add(c);
            perSignature.Add((contents, certs));
        }

        // complete the chains (AIA caIssuers) and collect revocation data until nothing new is found
        var ocsps = new List<byte[]>();
        var crls = new List<byte[]>();
        var revocationFor = new Dictionary<string, (List<byte[]> Ocsp, List<byte[]> Crl)>();
        bool complete = true;
        var processed = new HashSet<string>();
        for (int round = 0; round < 10; round++)
        {
            var pending = pool.All.Where(c => !processed.Contains(c.Thumbprint)).ToList();
            if (pending.Count == 0) break;
            foreach (var cert in pending)
            {
                processed.Add(cert.Thumbprint);
                if (RevocationInfo.IsSelfSigned(cert)) continue;
                var issuer = pool.FindIssuer(cert);
                if (issuer == null)
                {
                    try
                    {
                        foreach (var fetched in source.GetIssuers(cert)) pool.Add(fetched);
                    }
                    catch (Exception ex) when (IsNetworkError(ex))
                    {
                        warnings.Add($"Issuer of '{cert.Subject}' could not be downloaded: {ex.Message}");
                    }
                    issuer = pool.FindIssuer(cert);
                }
                if (issuer == null)
                {
                    warnings.Add($"Issuer certificate of '{cert.Subject}' not found.");
                    complete = false;
                    continue;
                }
                if (RevocationInfo.HasOcspNoCheck(cert)) continue;

                var entry = (Ocsp: new List<byte[]>(), Crl: new List<byte[]>());
                if (options.UseOcsp)
                {
                    try
                    {
                        if (source.GetOcspResponse(cert, issuer) is { } response)
                        {
                            var parsed = OcspResponse.Parse(response);
                            if (parsed.CertStatus == 1) warnings.Add($"Certificate '{cert.Subject}' is REVOKED according to OCSP.");
                            foreach (var responder in parsed.Certificates) pool.Add(responder);
                            entry.Ocsp.Add(response);
                        }
                    }
                    catch (Exception ex) when (IsNetworkError(ex))
                    {
                        warnings.Add($"OCSP for '{cert.Subject}' failed: {ex.Message}");
                    }
                }
                if (options.UseCrls && (entry.Ocsp.Count == 0 || options.IncludeCrlsWithOcsp))
                {
                    try
                    {
                        entry.Crl.AddRange(source.GetCrls(cert));
                    }
                    catch (Exception ex) when (IsNetworkError(ex))
                    {
                        warnings.Add($"CRL for '{cert.Subject}' failed: {ex.Message}");
                    }
                }
                if (entry.Ocsp.Count == 0 && entry.Crl.Count == 0)
                {
                    warnings.Add($"No revocation information for '{cert.Subject}'.");
                    complete = false;
                }
                ocsps.AddRange(entry.Ocsp);
                crls.AddRange(entry.Crl);
                revocationFor[cert.Thumbprint] = entry;
            }
        }

        if (pool.LimitReached)
        {
            warnings.Add($"More than {options.MaxCertificates} certificates; the rest was ignored (LtvOptions.MaxCertificates).");
            complete = false;
        }
        var dss = new DssWriter(doc);
        int certsAdded = 0, ocspAdded = 0, crlAdded = 0;
        foreach (var c in pool.All) certsAdded += dss.AddCertificate(c.RawData) ? 1 : 0;
        foreach (var o in ocsps) ocspAdded += dss.AddOcsp(o) ? 1 : 0;
        foreach (var c in crls) crlAdded += dss.AddCrl(c) ? 1 : 0;
        if (options.IncludeVri)
        {
            foreach (var (contents, certs) in perSignature)
            {
                var chain = pool.CompleteChain(certs);
                dss.AddVri(contents, chain.Select(c => c.RawData),
                    chain.SelectMany(c => revocationFor.TryGetValue(c.Thumbprint, out var r) ? r.Ocsp : new List<byte[]>()),
                    chain.SelectMany(c => revocationFor.TryGetValue(c.Thumbprint, out var r) ? r.Crl : new List<byte[]>()));
            }
        }
        if (!dss.Changed) return new LtvResult(pdf, 0, 0, 0, complete, warnings, null);
        dss.Commit();
        var result = doc.ToArray(new PdfSaveOptions { Incremental = true });
        return new LtvResult(result, certsAdded, ocspAdded, crlAdded, complete, warnings, null);
    }

    // validation data is collected on a best-effort basis: any failure of a source is reported as a warning
    private static bool IsNetworkError(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or ThreadAbortException);

    /// <summary>Certificates indexed for issuer lookup.</summary>
    private sealed class CertificatePool
    {
        private readonly Dictionary<string, X509Certificate2> _byThumbprint = new();

        public IEnumerable<X509Certificate2> All => _byThumbprint.Values.ToList();

        public int Limit { get; init; } = int.MaxValue;

        public bool LimitReached { get; private set; }

        public void Add(X509Certificate2 certificate)
        {
            if (_byThumbprint.Count >= Limit && !_byThumbprint.ContainsKey(certificate.Thumbprint))
            {
                LimitReached = true;
                return;
            }
            _byThumbprint.TryAdd(certificate.Thumbprint, certificate);
        }

        public X509Certificate2? FindIssuer(X509Certificate2 certificate)
        {
            foreach (var candidate in _byThumbprint.Values)
            {
                if (candidate.Thumbprint == certificate.Thumbprint) continue;
                if (!candidate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)) continue;
                if (IsSignedBy(certificate, candidate)) return candidate;
            }
            return null;
        }

        public List<X509Certificate2> CompleteChain(IEnumerable<X509Certificate2> start)
        {
            var result = new Dictionary<string, X509Certificate2>();
            var queue = new Queue<X509Certificate2>(start);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                if (!result.TryAdd(c.Thumbprint, c)) continue;
                if (FindIssuer(c) is { } issuer) queue.Enqueue(issuer);
                foreach (var other in _byThumbprint.Values) // OCSP responder certificates issued by chain members
                    if (RevocationInfo.HasOcspNoCheck(other) && other.IssuerName.RawData.AsSpan().SequenceEqual(c.SubjectName.RawData)) queue.Enqueue(other);
            }
            return result.Values.ToList();
        }

        private static bool IsSignedBy(X509Certificate2 certificate, X509Certificate2 issuer) => CertificateSignature.Verify(certificate, issuer) != false;
    }

    /// <summary>Reads and extends the /DSS dictionary of the catalog.</summary>
    private sealed class DssWriter
    {
        private readonly PdfDocument _doc;
        private readonly PdfDictionary _dss;
        private readonly Dictionary<string, PdfReference> _known = new();

        public DssWriter(PdfDocument doc)
        {
            _doc = doc;
            _dss = doc.Catalog.GetDictionary(PdfName.Get("DSS")) ?? new PdfDictionary(PdfName.Get("DSS"));
            foreach (var key in new[] { "Certs", "OCSPs", "CRLs" })
            {
                if (_dss.GetArray(PdfName.Get(key)) is not { } array) continue;
                foreach (var item in array)
                    if (item is PdfReference r && r.Resolve() is PdfStream s) _known.TryAdd(key + Key(s.GetBytes()), r);
            }
        }

        public bool Changed { get; private set; }

        private static string Key(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        private PdfReference? Add(string arrayKey, byte[] data, out bool added)
        {
            added = false;
            string key = arrayKey + Key(data);
            if (_known.TryGetValue(key, out var existing)) return existing;
            var stream = new PdfStream(data, compress: true);
            var reference = _doc.AddObject(stream);
            var array = _dss.GetArray(PdfName.Get(arrayKey));
            if (array == null) _dss[PdfName.Get(arrayKey)] = array = new PdfArray();
            array.Add(reference);
            _known[key] = reference;
            Changed = added = true;
            return reference;
        }

        public bool AddCertificate(byte[] der)
        {
            Add("Certs", der, out bool added);
            return added;
        }

        public bool AddOcsp(byte[] der)
        {
            Add("OCSPs", der, out bool added);
            return added;
        }

        public bool AddCrl(byte[] der)
        {
            Add("CRLs", der, out bool added);
            return added;
        }

        public void AddVri(byte[] signatureContents, IEnumerable<byte[]> certs, IEnumerable<byte[]> ocsps, IEnumerable<byte[]> crls)
        {
            var vri = _dss.GetDictionary(PdfName.Get("VRI"));
            if (vri == null) _dss[PdfName.Get("VRI")] = vri = new PdfDictionary();
            var entry = new PdfDictionary();
            void Fill(string key, string arrayKey, IEnumerable<byte[]> items)
            {
                var array = new PdfArray();
                foreach (var item in items) array.Add(Add(arrayKey, item, out _)!);
                if (array.Count > 0) entry[PdfName.Get(key)] = array;
            }
            Fill("Cert", "Certs", certs);
            Fill("OCSP", "OCSPs", ocsps);
            Fill("CRL", "CRLs", crls);
            vri[PdfName.Get(Convert.ToHexString(SHA1.HashData(signatureContents)))] = entry;
            Changed = true;
        }

        public void Commit()
        {
            if (_doc.Catalog[PdfName.Get("DSS")] is not PdfReference) _doc.Catalog[PdfName.Get("DSS")] = _doc.AddObject(_dss);
            // declare the PAdES extension of ISO 32000-1 (as Adobe Acrobat does)
            var extensions = _doc.Catalog.GetDictionary(PdfName.Get("Extensions"));
            if (extensions == null) _doc.Catalog[PdfName.Get("Extensions")] = extensions = new PdfDictionary();
            if (!extensions.ContainsKey(PdfName.Get("ESIC")))
            {
                var esic = new PdfDictionary();
                esic[PdfName.Get("BaseVersion")] = PdfName.Get("1.7");
                esic.Set(PdfName.Get("ExtensionLevel"), 5);
                extensions[PdfName.Get("ESIC")] = esic;
            }
        }
    }
}
