using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace MiPdf.Signatures;

/// <summary>
/// Verifies a detached CMS signature against the digest of the signed data, so the signed byte ranges never have to be
/// copied into one array (<see cref="SignerInfo.CheckSignature(bool)"/> needs the content in one piece): the message
/// digest attribute is compared with the digest, then the signature value over the signed attributes is checked with
/// the public key of the signer certificate.
/// </summary>
internal static class CmsVerifier
{
    private const string OidContentType = "1.2.840.113549.1.9.3";
    private const string OidMessageDigest = "1.2.840.113549.1.9.4";
    private const string RsaEncryption = "1.2.840.113549.1.1.1";

    private static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0, isConstructed: true);

    /// <summary>Maps a digest algorithm OID to its name, or null if it is not supported.</summary>
    public static HashAlgorithmName? DigestName(string? oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        _ => null,
    };

    /// <summary>
    /// Verifies the first SignerInfo of <paramref name="cms"/> (decoded from <paramref name="encoded"/>) for detached
    /// content; <paramref name="digest"/> returns the digest of the content for an algorithm.
    /// </summary>
    /// <exception cref="CryptographicException">The signature does not match the data or cannot be verified.</exception>
    public static void Verify(SignedCms cms, byte[] encoded, Func<HashAlgorithmName, byte[]> digest)
    {
        var signerInfo = cms.SignerInfos[0];
        var digestAlgorithm = DigestName(signerInfo.DigestAlgorithm.Value)
            ?? throw new CryptographicException($"Unsupported digest algorithm {signerInfo.DigestAlgorithm.Value}.");
        var certificate = signerInfo.Certificate ?? throw new CryptographicException("The signer certificate is not included.");
        var documentDigest = digest(digestAlgorithm);

        var (signedAttributes, signatureAlgorithm, signature) = ReadSignerInfo(encoded);
        byte[] signedHash;
        if (signedAttributes == null)
        {
            // no signed attributes: the signature is over the digest of the content itself
            signedHash = documentDigest;
        }
        else
        {
            var messageDigest = SingleAttribute(signerInfo, OidMessageDigest)
                ?? throw new CryptographicException("The message digest attribute is missing.");
            var expected = new AsnReader(messageDigest, AsnEncodingRules.BER).ReadOctetString();
            if (!CryptographicOperations.FixedTimeEquals(expected, documentDigest))
                throw new CryptographicException("The message digest does not match the signed data.");
            if (SingleAttribute(signerInfo, OidContentType) is { } contentType &&
                new AsnReader(contentType, AsnEncodingRules.BER).ReadObjectIdentifier() != cms.ContentInfo.ContentType.Value)
                throw new CryptographicException("The content type attribute does not match the content.");
            // the signature covers the DER encoding of the attributes with the SET OF tag instead of [0]
            signedAttributes[0] = 0x31;
            signedHash = CmsBuilder.Hash(digestAlgorithm, signedAttributes);
        }

        if (!VerifySignatureValue(certificate, signatureAlgorithm, digestAlgorithm, signedHash, signature))
            throw new CryptographicException("The signature value is invalid.");
    }

    private static byte[]? SingleAttribute(SignerInfo signerInfo, string oid)
    {
        byte[]? value = null;
        foreach (var attribute in signerInfo.SignedAttributes)
        {
            if (attribute.Oid.Value != oid) continue;
            if (value != null || attribute.Values.Count != 1) throw new CryptographicException($"The attribute {oid} must occur exactly once.");
            value = attribute.Values[0].RawData;
        }
        return value;
    }

    /// <summary>Reads the raw signed attributes ([0]), the signature algorithm and the signature value of the first SignerInfo.</summary>
    private static (byte[]? SignedAttributes, ReadOnlyMemory<byte> SignatureAlgorithm, byte[] Signature) ReadSignerInfo(byte[] encoded)
    {
        try
        {
            var signerInfo = new AsnReader(CmsUtil.ReadFirstSignerInfo(encoded, out _, out _), AsnEncodingRules.BER).ReadSequence();
            signerInfo.ReadEncodedValue(); // version
            signerInfo.ReadEncodedValue(); // sid
            signerInfo.ReadEncodedValue(); // digestAlgorithm
            byte[]? attributes = null;
            if (signerInfo.PeekTag().HasSameClassAndValue(Context0)) attributes = signerInfo.ReadEncodedValue().ToArray();
            var algorithm = signerInfo.ReadEncodedValue();
            var signature = signerInfo.ReadOctetString();
            return (attributes, algorithm, signature);
        }
        catch (Exception ex) when (ex is AsnContentException or PdfException)
        {
            throw new CryptographicException("Invalid SignerInfo: " + ex.Message, ex);
        }
    }

    private static bool VerifySignatureValue(X509Certificate2 certificate, ReadOnlyMemory<byte> algorithmIdentifier, HashAlgorithmName digestAlgorithm,
        byte[] hash, byte[] signature)
    {
        var algorithm = new AsnReader(algorithmIdentifier, AsnEncodingRules.BER).ReadSequence();
        string oid = algorithm.ReadObjectIdentifier();
        using (var rsa = certificate.GetRSAPublicKey())
        {
            if (rsa != null)
            {
                if (oid == CmsUtil.OidRsaPss)
                {
                    CheckPssParameters(algorithm, digestAlgorithm);
                    return rsa.VerifyHash(hash, signature, digestAlgorithm, RSASignaturePadding.Pss);
                }
                // rsaEncryption: the digest algorithm of the SignerInfo applies
                if (oid != RsaEncryption && RsaDigest(oid) != digestAlgorithm)
                    throw new CryptographicException($"Unsupported signature algorithm {oid} with {digestAlgorithm.Name}.");
                return rsa.VerifyHash(hash, signature, digestAlgorithm, RSASignaturePadding.Pkcs1);
            }
        }
        using (var ec = certificate.GetECDsaPublicKey())
        {
            if (ec != null)
            {
                // ecdsa-with-SHA2 and id-ecPublicKey: the digest algorithm of the SignerInfo applies
                if (oid is not ("1.2.840.10045.4.3" or "1.2.840.10045.2.1") && EcdsaDigest(oid) != digestAlgorithm)
                    throw new CryptographicException($"Unsupported signature algorithm {oid} with {digestAlgorithm.Name}.");
                return ec.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence);
            }
        }
        throw new CryptographicException("Unsupported key type (supported: RSA, ECDSA).");
    }

    /// <summary>The digest implied by an RSA PKCS#1 v1.5 signature algorithm (sha*WithRSAEncryption).</summary>
    private static HashAlgorithmName? RsaDigest(string oid) => oid switch
    {
        "1.2.840.113549.1.1.11" => HashAlgorithmName.SHA256,
        "1.2.840.113549.1.1.12" => HashAlgorithmName.SHA384,
        "1.2.840.113549.1.1.13" => HashAlgorithmName.SHA512,
        "1.2.840.113549.1.1.5" => HashAlgorithmName.SHA1,
        _ => null,
    };

    /// <summary>The digest implied by an ECDSA signature algorithm (ecdsa-with-SHA*).</summary>
    private static HashAlgorithmName? EcdsaDigest(string oid) => oid switch
    {
        "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
        "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
        "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
        "1.2.840.10045.4.1" => HashAlgorithmName.SHA1,
        _ => null,
    };

    /// <summary>RSASSA-PSS parameters must use the digest algorithm for the hash and MGF1, with a salt as long as the hash.</summary>
    private static void CheckPssParameters(AsnReader algorithm, HashAlgorithmName digestAlgorithm)
    {
        // defaults (RFC 4055): SHA-1, MGF1 with SHA-1, salt length 20
        string hash = "1.3.14.3.2.26", mgfHash = "1.3.14.3.2.26";
        int saltLength = 20;
        if (algorithm.HasData)
        {
            var parameters = algorithm.ReadSequence();
            var explicit0 = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
            var explicit1 = new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true);
            var explicit2 = new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true);
            if (parameters.HasData && parameters.PeekTag().HasSameClassAndValue(explicit0))
                hash = parameters.ReadSequence(explicit0).ReadSequence().ReadObjectIdentifier();
            if (parameters.HasData && parameters.PeekTag().HasSameClassAndValue(explicit1))
            {
                var mgf = parameters.ReadSequence(explicit1).ReadSequence();
                mgf.ReadObjectIdentifier(); // MGF1
                mgfHash = mgf.ReadSequence().ReadObjectIdentifier();
            }
            if (parameters.HasData && parameters.PeekTag().HasSameClassAndValue(explicit2))
                saltLength = (int)parameters.ReadSequence(explicit2).ReadInteger();
        }
        int expectedSalt = digestAlgorithm.Name switch { "SHA1" => 20, "SHA384" => 48, "SHA512" => 64, _ => 32 };
        if (DigestName(hash) != digestAlgorithm || DigestName(mgfHash) != digestAlgorithm || saltLength != expectedSalt)
            throw new CryptographicException("Unsupported RSASSA-PSS parameters.");
    }
}
