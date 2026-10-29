using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
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

    public static byte[] Sign(byte[] content, IExternalSigner signer, PdfSignatureOptions options, DateTimeOffset signingTime) =>
        Sign(content, signer, options, signingTime, out _, out _);

    public static byte[] Sign(byte[] content, IExternalSigner signer, PdfSignatureOptions options, DateTimeOffset signingTime,
        out bool timestamped, out string? timestampError)
    {
        timestampError = null;
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var certificate = signer.Certificate;
        CmsSigner cmsSigner;
        AsymmetricAlgorithm? facade = null;
        if (signer is CertificateSigner && certificate.HasPrivateKey)
        {
            cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate);
        }
        else
        {
            facade = certificate.GetRSAPublicKey() is { } rsa
                ? new ExternalRsa(signer, rsa)
                : certificate.GetECDsaPublicKey() is { } ec
                    ? new ExternalECDsa(signer, ec, options.DigestAlgorithm)
                    : throw new PdfException("Unsupported key type (supported: RSA, ECDSA).");
            cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, LoadCertificate(certificate.RawData), facade);
        }
        try
        {
            cmsSigner.DigestAlgorithm = new Oid(DigestOid(options.DigestAlgorithm));
            if (options.RsaPadding != RSASignaturePadding.Pkcs1)
            {
                using var rsaKey = certificate.GetRSAPublicKey();
                if (rsaKey != null) cmsSigner.SignaturePadding = options.RsaPadding;
            }
            cmsSigner.IncludeOption = X509IncludeOption.EndCertOnly;
            foreach (var c in signer.CertificateChain)
                if (c.Thumbprint != certificate.Thumbprint) cmsSigner.Certificates.Add(c);
            // signing-certificate-v2 binds the certificate to the signature (also recommended for PKCS#7 signatures)
            cmsSigner.SignedAttributes.Add(new AsnEncodedData(OidSigningCertificateV2, SigningCertificateV2(certificate)));
            if (options.Format == PdfSignatureFormat.Pkcs7Detached)
                cmsSigner.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
            foreach (var attribute in options.AdditionalSignedAttributes) cmsSigner.SignedAttributes.Add(attribute);
            foreach (var attribute in options.AdditionalUnsignedAttributes) cmsSigner.UnsignedAttributes.Add(attribute);
            cms.ComputeSignature(cmsSigner, silent: true);
        }
        finally
        {
            facade?.Dispose();
        }

        timestamped = false;
        if (options.TimestampClient != null)
        {
            try
            {
                AddSignatureTimestamp(cms, options.TimestampClient, options.DigestAlgorithm);
                timestamped = true;
            }
            catch (Exception ex) when (options.TimestampOptional && ex is not OutOfMemoryException)
            {
                timestampError = ex.Message;
            }
        }
        return cms.Encode();
    }

    internal static X509Certificate2 LoadCertificate(byte[] raw)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificate(raw);
#else
        return new X509Certificate2(raw);
#endif
    }

    /// <summary>Adds a signature time-stamp (unsigned attribute) over the signature value.</summary>
    public static void AddSignatureTimestamp(SignedCms cms, ITimestampClient tsa, HashAlgorithmName hashAlgorithm)
    {
        var signerInfo = cms.SignerInfos[0];
        var hash = Hash(hashAlgorithm, signerInfo.GetSignature());
        var token = tsa.GetTimestampToken(hash, hashAlgorithm);
        signerInfo.AddUnsignedAttribute(new AsnEncodedData(OidTimestampToken, token));
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
