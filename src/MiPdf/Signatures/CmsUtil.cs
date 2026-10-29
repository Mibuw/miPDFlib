using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MiPdf.Signatures;

/// <summary>
/// Low-level helpers for CMS signature containers produced elsewhere (signing services, smart cards, remote signing APIs):
/// adding a signature time-stamp without re-encoding the signed parts, and assembling a CMS from a raw signature value
/// (deferred hash signing, e.g. when the signature value arrives in a later HTTP round trip).
/// </summary>
public static class CmsUtil
{
    private const string OidData = "1.2.840.113549.1.7.1";
    private const string OidSignedData = "1.2.840.113549.1.7.2";
    private const string OidContentType = "1.2.840.113549.1.9.3";
    private const string OidMessageDigest = "1.2.840.113549.1.9.4";
    private const string OidSigningTime = "1.2.840.113549.1.9.5";
    private const string OidRsaEncryption = "1.2.840.113549.1.1.1";
    internal const string OidRsaPss = "1.2.840.113549.1.1.10";
    private const string OidMgf1 = "1.2.840.113549.1.1.8";

    private static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0, isConstructed: true);
    private static readonly Asn1Tag Context1 = new(TagClass.ContextSpecific, 1, isConstructed: true);

    /// <summary>
    /// Adds an RFC 3161 signature time-stamp (unsigned attribute) to the first SignerInfo of <paramref name="cms"/>
    /// (PAdES B-T). The signed parts are copied byte by byte.
    /// </summary>
    public static byte[] AddSignatureTimestamp(byte[] cms, ITimestampClient tsa, HashAlgorithmName? hashAlgorithm = null)
    {
        var hash = hashAlgorithm ?? HashAlgorithmName.SHA256;
        var token = tsa.GetTimestampToken(CmsBuilder.Hash(hash, GetSignatureValue(cms)), hash);
        return AddUnsignedAttribute(cms, CmsBuilder.OidTimestampToken, token);
    }

    /// <summary>Returns the signature value (the encrypted digest) of the first SignerInfo.</summary>
    public static byte[] GetSignatureValue(byte[] cms)
    {
        var signerInfo = new AsnReader(ReadFirstSignerInfo(cms, out _, out _), AsnEncodingRules.BER).ReadSequence();
        while (signerInfo.HasData)
        {
            if (signerInfo.PeekTag().HasSameClassAndValue(Asn1Tag.PrimitiveOctetString)) return signerInfo.ReadOctetString();
            signerInfo.ReadEncodedValue();
        }
        throw new PdfException("The CMS contains no signature value.");
    }

    /// <summary>Adds an unsigned attribute (DER encoded value) to the first SignerInfo.</summary>
    public static byte[] AddUnsignedAttribute(byte[] cms, string oid, byte[] valueDer)
    {
        var signerInfo = ReadFirstSignerInfo(cms, out var signedDataElements, out var otherSignerInfos);
        var elements = new List<ReadOnlyMemory<byte>>();
        var reader = new AsnReader(signerInfo, AsnEncodingRules.BER).ReadSequence();
        ReadOnlyMemory<byte>? unsigned = null;
        while (reader.HasData)
        {
            if (reader.PeekTag().HasSameClassAndValue(Context1)) unsigned = reader.ReadEncodedValue();
            else elements.Add(reader.ReadEncodedValue());
        }

        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(OidSignedData);
            using (writer.PushSequence(Context0))
            using (writer.PushSequence())
            {
                for (int i = 0; i < signedDataElements.Count - 1; i++) writer.WriteEncodedValue(signedDataElements[i].Span);
                using (writer.PushSetOf())
                {
                    using (writer.PushSequence())
                    {
                        foreach (var e in elements) writer.WriteEncodedValue(e.Span);
                        using (writer.PushSetOf(Context1))
                        {
                            if (unsigned is { } existing)
                            {
                                var attrs = new AsnReader(existing, AsnEncodingRules.BER).ReadSetOf(skipSortOrderValidation: true, expectedTag: Context1);
                                while (attrs.HasData) writer.WriteEncodedValue(attrs.ReadEncodedValue().Span);
                            }
                            using (writer.PushSequence())
                            {
                                writer.WriteObjectIdentifier(oid);
                                using (writer.PushSetOf()) writer.WriteEncodedValue(valueDer);
                            }
                        }
                    }
                    foreach (var other in otherSignerInfos) writer.WriteEncodedValue(other.Span);
                }
            }
        }
        return writer.Encode();
    }

    /// <summary>Returns the value of a signed attribute of the first SignerInfo (DER of the first value), or null.</summary>
    public static byte[]? GetSignedAttribute(byte[] cms, string oid) => GetAttribute(cms, oid, Context0);

    /// <summary>Returns the value of an unsigned attribute of the first SignerInfo (DER of the first value), or null.</summary>
    public static byte[]? GetUnsignedAttribute(byte[] cms, string oid) => GetAttribute(cms, oid, Context1);

    private static byte[]? GetAttribute(byte[] cms, string oid, Asn1Tag tag)
    {
        var reader = new AsnReader(ReadFirstSignerInfo(cms, out _, out _), AsnEncodingRules.BER).ReadSequence();
        while (reader.HasData)
        {
            if (!reader.PeekTag().HasSameClassAndValue(tag))
            {
                reader.ReadEncodedValue();
                continue;
            }
            var attrs = reader.ReadSetOf(skipSortOrderValidation: true, expectedTag: tag);
            while (attrs.HasData)
            {
                var attr = attrs.ReadSequence();
                if (attr.ReadObjectIdentifier() != oid) continue;
                var values = attr.ReadSetOf(skipSortOrderValidation: true);
                return values.HasData ? values.ReadEncodedValue().ToArray() : null;
            }
        }
        return null;
    }

    private static ReadOnlyMemory<byte> ReadFirstSignerInfo(byte[] cms, out List<ReadOnlyMemory<byte>> signedDataElements, out List<ReadOnlyMemory<byte>> otherSignerInfos)
    {
        try
        {
            var contentInfo = new AsnReader(CmsBuilder.TrimDer(cms), AsnEncodingRules.BER).ReadSequence();
            if (contentInfo.ReadObjectIdentifier() != OidSignedData) throw new PdfException("The CMS is not a SignedData structure.");
            var signedData = contentInfo.ReadSequence(Context0).ReadSequence();
            signedDataElements = new List<ReadOnlyMemory<byte>>();
            while (signedData.HasData) signedDataElements.Add(signedData.ReadEncodedValue());
            var signerInfos = new AsnReader(signedDataElements[^1], AsnEncodingRules.BER).ReadSetOf(skipSortOrderValidation: true);
            var first = signerInfos.ReadEncodedValue();
            otherSignerInfos = new List<ReadOnlyMemory<byte>>();
            while (signerInfos.HasData) otherSignerInfos.Add(signerInfos.ReadEncodedValue());
            return first;
        }
        catch (Exception ex) when (ex is AsnContentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw new PdfException("Invalid CMS structure: " + ex.Message, ex);
        }
    }

    // ------------------------------------------------------------------ deferred signing with a raw signature value

    /// <summary>
    /// Builds the DER-encoded signed attributes (SET OF Attribute) for a PAdES/CAdES signature over a document digest.
    /// Hash these bytes and have the hash signed (e.g. by a remote service); then call <see cref="AssembleSignedData"/>
    /// with the same attributes and the signature value. Store the returned bytes between the two steps.
    /// </summary>
    /// <param name="documentDigest">Digest of the signed byte ranges (<see cref="PreparedSignature.GetDocumentDigest"/>).</param>
    /// <param name="signerCertificate">Signer certificate (for the signing-certificate-v2 attribute).</param>
    /// <param name="format">PAdES (signing-certificate-v2) or PKCS#7 (signing-certificate-v2 and signing-time).</param>
    /// <param name="signingTime">Signing time for PKCS#7 signatures.</param>
    /// <param name="additionalAttributes">Further signed attributes (e.g. biometric data).</param>
    public static byte[] CreateSignedAttributes(byte[] documentDigest, X509Certificate2 signerCertificate,
        PdfSignatureFormat format = PdfSignatureFormat.PAdES, DateTimeOffset? signingTime = null, IEnumerable<AsnEncodedData>? additionalAttributes = null)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSetOf())
        {
            WriteAttribute(writer, OidContentType, w => w.WriteObjectIdentifier(OidData));
            WriteAttribute(writer, OidMessageDigest, w => w.WriteOctetString(documentDigest));
            WriteAttribute(writer, CmsBuilder.OidSigningCertificateV2, w => w.WriteEncodedValue(CmsBuilder.SigningCertificateV2(signerCertificate)));
            if (format == PdfSignatureFormat.Pkcs7Detached)
                WriteAttribute(writer, OidSigningTime, w => w.WriteUtcTime(signingTime ?? DateTimeOffset.UtcNow));
            foreach (var attribute in additionalAttributes ?? Array.Empty<AsnEncodedData>())
                WriteAttribute(writer, attribute.Oid!.Value!, w => w.WriteEncodedValue(attribute.RawData));
        }
        return writer.Encode();
    }

    /// <summary>
    /// Assembles a detached CMS SignedData from signed attributes (as returned by <see cref="CreateSignedAttributes"/>)
    /// and the signature value over them.
    /// </summary>
    /// <param name="signedAttributes">DER SET OF Attribute that was hashed and signed.</param>
    /// <param name="signatureValue">RSA: PKCS#1 v1.5 or PSS signature. ECDSA: IEEE P1363 (r || s) or DER-encoded.</param>
    /// <param name="signerCertificate">Signer certificate.</param>
    /// <param name="chain">Further certificates to embed (intermediates).</param>
    /// <param name="digestAlgorithm">Digest algorithm used for the document digest and the attributes.</param>
    /// <param name="rsaPadding">
    /// RSA only: <see cref="RSASignaturePadding.Pkcs1"/> (default) or <see cref="RSASignaturePadding.Pss"/> when the
    /// signature value is RSASSA-PSS (MGF1 with <paramref name="digestAlgorithm"/>, salt as long as the digest).
    /// </param>
    public static byte[] AssembleSignedData(byte[] signedAttributes, byte[] signatureValue, X509Certificate2 signerCertificate,
        IEnumerable<X509Certificate2>? chain = null, HashAlgorithmName? digestAlgorithm = null, RSASignaturePadding? rsaPadding = null)
    {
        var digest = digestAlgorithm ?? HashAlgorithmName.SHA256;
        string digestOid = CmsBuilder.DigestOid(digest);
        Action<AsnWriter> writeSignatureAlgorithm;
        using var rsaKey = signerCertificate.GetRSAPublicKey();
        using var ecKey = rsaKey == null ? signerCertificate.GetECDsaPublicKey() : null;
        if (rsaKey != null)
        {
            writeSignatureAlgorithm = rsaPadding == RSASignaturePadding.Pss
                ? w => WritePssAlgorithm(w, digest)
                : w => WriteAlgorithm(w, OidRsaEncryption, withNull: true);
        }
        else if (ecKey != null)
        {
            string signatureOid = digest.Name switch
            {
                "SHA256" => "1.2.840.10045.4.3.2",
                "SHA384" => "1.2.840.10045.4.3.3",
                "SHA512" => "1.2.840.10045.4.3.4",
                _ => "1.2.840.10045.4.1",
            };
            writeSignatureAlgorithm = w => WriteAlgorithm(w, signatureOid, withNull: false);
            int fieldBytes = (ecKey.KeySize + 7) / 8;
            if (signatureValue.Length == 2 * fieldBytes && !IsDerEcdsaSignature(signatureValue)) signatureValue = P1363ToDer(signatureValue);
        }
        else throw new PdfException("Unsupported key type (supported: RSA, ECDSA).");

        var attributesReader = new AsnReader(signedAttributes, AsnEncodingRules.DER);
        var attributeSet = attributesReader.ReadSetOf(skipSortOrderValidation: true);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(OidSignedData);
            using (writer.PushSequence(Context0))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);
                using (writer.PushSetOf()) WriteAlgorithm(writer, digestOid, withNull: false);
                using (writer.PushSequence()) writer.WriteObjectIdentifier(OidData);
                using (writer.PushSetOf(Context0))
                {
                    writer.WriteEncodedValue(signerCertificate.RawData);
                    foreach (var c in chain ?? Array.Empty<X509Certificate2>())
                        if (c.Thumbprint != signerCertificate.Thumbprint) writer.WriteEncodedValue(c.RawData);
                }
                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    writer.WriteInteger(1);
                    using (writer.PushSequence())
                    {
                        writer.WriteEncodedValue(signerCertificate.IssuerName.RawData);
                        writer.WriteInteger(signerCertificate.SerialNumberBytes.Span);
                    }
                    WriteAlgorithm(writer, digestOid, withNull: false);
                    using (writer.PushSetOf(Context0))
                        while (attributeSet.HasData) writer.WriteEncodedValue(attributeSet.ReadEncodedValue().Span);
                    writeSignatureAlgorithm(writer);
                    writer.WriteOctetString(signatureValue);
                }
            }
        }
        return writer.Encode();
    }

    private static void WriteAttribute(AsnWriter writer, string oid, Action<AsnWriter> value)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            using (writer.PushSetOf()) value(writer);
        }
    }

    private static void WriteAlgorithm(AsnWriter writer, string oid, bool withNull)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            if (withNull) writer.WriteNull();
        }
    }

    /// <summary>AlgorithmIdentifier id-RSASSA-PSS with RSASSA-PSS-params (RFC 4055): hash, MGF1(hash), salt = hash length.</summary>
    private static void WritePssAlgorithm(AsnWriter writer, HashAlgorithmName digest)
    {
        string digestOid = CmsBuilder.DigestOid(digest);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(OidRsaPss);
            using (writer.PushSequence())
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                    WriteAlgorithm(writer, digestOid, withNull: false); // SHA-2 parameters absent (RFC 5754), as SignedCms writes them
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true)))
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(OidMgf1);
                    WriteAlgorithm(writer, digestOid, withNull: false);
                }
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true)))
                    writer.WriteInteger(digest.Name switch { "SHA1" => 20, "SHA384" => 48, "SHA512" => 64, _ => 32 });
            }
        }
    }

    /// <summary>True if <paramref name="value"/> is exactly one DER SEQUENCE { INTEGER r, INTEGER s }.</summary>
    private static bool IsDerEcdsaSignature(byte[] value)
    {
        try
        {
            var reader = new AsnReader(value, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            sequence.ReadInteger();
            sequence.ReadInteger();
            return !reader.HasData && !sequence.HasData;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>Converts an IEEE P1363 ECDSA signature (r || s) to the DER SEQUENCE { r, s } used in CMS.</summary>
    public static byte[] P1363ToDer(byte[] signature)
    {
        int half = signature.Length / 2;
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteIntegerUnsigned(TrimLeadingZeros(signature.AsSpan(0, half)));
            writer.WriteIntegerUnsigned(TrimLeadingZeros(signature.AsSpan(half)));
        }
        return writer.Encode();
    }

    private static ReadOnlySpan<byte> TrimLeadingZeros(ReadOnlySpan<byte> value)
    {
        while (value.Length > 1 && value[0] == 0) value = value[1..];
        return value;
    }
}
