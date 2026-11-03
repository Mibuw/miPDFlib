using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MiPdf.Signatures;

/// <summary>Creates detached CMS signatures for PDF signatures (CAdES/PAdES or PKCS#7).</summary>
internal static class CmsBuilder
{
    internal const string OidSigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    internal const string OidTimestampToken = "1.2.840.113549.1.9.16.2.14";

    public static string DigestOid(HashAlgorithmName name) => name.Name switch
    {
        "SHA256" => "2.16.840.1.101.3.4.2.1",
        "SHA384" => "2.16.840.1.101.3.4.2.2",
        "SHA512" => "2.16.840.1.101.3.4.2.3",
        "SHA1" => "1.3.14.3.2.26",
        _ => throw new PdfException($"Unsupported digest algorithm {name.Name}."),
    };

    public static byte[] Hash(HashAlgorithmName name, ReadOnlySpan<byte> data) => name.Name switch
    {
        "SHA256" => SHA256.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        "SHA1" => SHA1.HashData(data),
        _ => throw new PdfException($"Unsupported digest algorithm {name.Name}."),
    };

    /// <summary>
    /// Creates the detached CMS from the digest of the signed byte ranges, so the document itself is never needed in
    /// one piece: signed attributes over the digest, the signature value from <paramref name="signer"/>, then the
    /// unsigned attributes and the optional signature time-stamp.
    /// </summary>
    public static byte[] SignDigest(byte[] documentDigest, IExternalSigner signer, PdfSignatureOptions options, DateTimeOffset signingTime,
        out bool timestamped, out string? timestampError)
    {
        timestampError = null;
        var certificate = signer.Certificate;
        var digest = options.DigestAlgorithm;
        DigestOid(digest); // rejects unsupported algorithms before anything is signed
        var attributes = CmsUtil.CreateSignedAttributes(documentDigest, certificate, options.Format, signingTime, options.AdditionalSignedAttributes);
        var hash = Hash(digest, attributes);
        bool isRsa;
        using (var rsa = certificate.GetRSAPublicKey()) isRsa = rsa != null;
        var signature = isRsa ? signer.SignHash(hash, digest, options.RsaPadding) : signer.SignHash(hash, digest);
        var cms = CmsUtil.AssembleSignedData(attributes, signature, certificate, signer.CertificateChain, digest, isRsa ? options.RsaPadding : null);
        foreach (var attribute in options.AdditionalUnsignedAttributes)
            cms = CmsUtil.AddUnsignedAttribute(cms, attribute.Oid!.Value!, attribute.RawData);

        timestamped = false;
        if (options.TimestampClient != null)
        {
            try
            {
                cms = CmsUtil.AddSignatureTimestamp(cms, options.TimestampClient, digest);
                timestamped = true;
            }
            catch (Exception ex) when (options.TimestampOptional && ex is not OutOfMemoryException)
            {
                timestampError = ex.Message;
            }
        }
        return cms;
    }

    internal static X509Certificate2 LoadCertificate(byte[] raw)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificate(raw);
#else
        return new X509Certificate2(raw);
#endif
    }

    /// <summary>ESS signing-certificate-v2 attribute value (RFC 5035) with SHA-256 cert hash and issuer/serial.</summary>
    internal static byte[] SigningCertificateV2(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())                 // SigningCertificateV2
        {
            using (writer.PushSequence())             // certs
            {
                using (writer.PushSequence())         // ESSCertIDv2 (hashAlgorithm defaults to SHA-256)
                {
                    writer.WriteOctetString(SHA256.HashData(certificate.RawData));
                    using (writer.PushSequence())     // IssuerSerial
                    {
                        using (writer.PushSequence()) // GeneralNames
                        {
                            var directoryName = new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true);
                            using (writer.PushSequence(directoryName))
                                writer.WriteEncodedValue(certificate.IssuerName.RawData);
                        }
                        writer.WriteInteger(certificate.SerialNumberBytes.Span);
                    }
                }
            }
        }
        return writer.Encode();
    }

    /// <summary>Returns the DER length of the outer element (signature containers are zero padded).</summary>
    public static byte[] TrimDer(byte[] data)
    {
        try
        {
            var reader = new AsnReader(data, AsnEncodingRules.BER);
            var encoded = reader.ReadEncodedValue();
            return encoded.ToArray();
        }
        catch (AsnContentException)
        {
            return data;
        }
    }
}
