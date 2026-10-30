using System.Security.Cryptography;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Images;

namespace MiPdf.Signatures;

/// <summary>Format of the CMS signature container (/SubFilter).</summary>
public enum PdfSignatureFormat
{
    /// <summary>PAdES (ETSI.CAdES.detached) with signing-certificate-v2 attribute – recommended (eIDAS).</summary>
    PAdES,

    /// <summary>Classic PKCS#7 detached signature (adbe.pkcs7.detached) with signing-time and signing-certificate-v2 attributes.</summary>
    Pkcs7Detached,
}

/// <summary>Certification level (DocMDP permissions) for certifying signatures.</summary>
public enum CertificationLevel
{
    /// <summary>No changes allowed after certification.</summary>
    NoChanges = 1,

    /// <summary>Form filling and further signatures allowed.</summary>
    FormFilling = 2,

    /// <summary>Form filling, signatures and annotations allowed.</summary>
    FormFillingAndAnnotations = 3,
}

/// <summary>Where the image of a visible signature is placed.</summary>
public enum SignatureImagePosition
{
    /// <summary>Image on the left, text on the right.</summary>
    Left,

    /// <summary>Image as background behind the text.</summary>
    Background,

    /// <summary>Only the image (e.g. a scanned handwritten signature).</summary>
    ImageOnly,

    /// <summary>Image in the upper part, text in a strip at the bottom.</summary>
    Top,
}

/// <summary>PAdES baseline level of a signature.</summary>
public enum PadesLevel
{
    /// <summary>B-B: basic signature.</summary>
    BB,

    /// <summary>B-T: with signature time-stamp.</summary>
    BT,

    /// <summary>B-LT: with validation data (certificates, OCSP responses, CRLs) in the DSS.</summary>
    BLT,

    /// <summary>B-LTA: B-LT plus a document time-stamp.</summary>
    BLTA,
}

/// <summary>Appearance of a visible signature.</summary>
public sealed class PdfSignatureAppearance
{
    /// <summary>
    /// Text template. Placeholders: {name}, {date}, {reason}, {location}, {contact}. Lines whose
    /// placeholders are empty are omitted. Default: signer, date, reason and location.
    /// </summary>
    public string? Text { get; set; }

    public PdfImage? Image { get; set; }

    public SignatureImagePosition ImagePosition { get; set; } = SignatureImagePosition.Left;

    public PdfFont Font { get; set; } = PdfStandardFont.Helvetica;

    /// <summary>Font size; 0 = fit the text into the rectangle.</summary>
    public double FontSize { get; set; }

    public PdfColor TextColor { get; set; } = PdfColor.Black;

    public PdfColor? Background { get; set; }

    public PdfColor? Border { get; set; }

    /// <summary>Format for {date}.</summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd HH:mm:ss zzz";

    /// <summary>Share of the height used by the text strip for <see cref="SignatureImagePosition.Top"/> (default 0.3).</summary>
    public double TextStripRatio { get; set; } = 0.3;

    /// <summary>
    /// Draw the appearance upright as seen in viewers when the page is rotated (/Rotate), or rotated by the /MK /R of an
    /// existing signature field (default true). The rectangle stays in unrotated page coordinates; on a page rotated by
    /// 90 or 270 degrees the visible width is the rectangle's height. false = draw in unrotated page orientation.
    /// </summary>
    public bool KeepUpright { get; set; } = true;

    /// <summary>
    /// Custom drawing into the appearance, called after background and image and before the text.
    /// Arguments: canvas, width, height (origin bottom-left, points). Set <see cref="Text"/> to "" for no text,
    /// e.g. to draw handwritten ink with <see cref="InkRenderer"/>.
    /// </summary>
    public Action<PdfCanvas, double, double>? Draw { get; set; }
}

/// <summary>Options for <see cref="PdfSigner"/>.</summary>
public sealed class PdfSignatureOptions
{
    /// <summary>Name of the signature field. An existing empty signature field is used; otherwise a new one is created.</summary>
    public string? FieldName { get; set; }

    /// <summary>1-based page for a new visible signature field.</summary>
    public int PageNumber { get; set; } = 1;

    /// <summary>Position of a new signature field; null or empty = invisible signature.</summary>
    public PdfRectangle? Rectangle { get; set; }

    public string? Reason { get; set; }

    public string? Location { get; set; }

    public string? ContactInfo { get; set; }

    /// <summary>Signer name shown in viewers (/Name); default: common name of the certificate.</summary>
    public string? SignerName { get; set; }

    /// <summary>Claimed signing time (/M). Default: now.</summary>
    public DateTimeOffset? SigningTime { get; set; }

    public PdfSignatureFormat Format { get; set; } = PdfSignatureFormat.PAdES;

    public HashAlgorithmName DigestAlgorithm { get; set; } = HashAlgorithmName.SHA256;

    /// <summary>
    /// Signature padding for RSA keys: <see cref="RSASignaturePadding.Pkcs1"/> (PKCS#1 v1.5, default) or
    /// <see cref="RSASignaturePadding.Pss"/> (RSASSA-PSS with MGF1 over <see cref="DigestAlgorithm"/> and a salt as long
    /// as the digest). Ignored for ECDSA keys. External signers must implement
    /// <see cref="IExternalSigner.SignHash(byte[], HashAlgorithmName, RSASignaturePadding)"/> for PSS.
    /// </summary>
    public RSASignaturePadding RsaPadding { get; set; } = RSASignaturePadding.Pkcs1;

    /// <summary>Time-stamp authority for a signature time-stamp (PAdES B-T).</summary>
    public ITimestampClient? TimestampClient { get; set; }

    /// <summary>Makes this a certification (author) signature with the given DocMDP permissions.</summary>
    public CertificationLevel? Certification { get; set; }

    /// <summary>
    /// Form fields locked by this signature (FieldMDP), e.g. <c>PdfFieldLock.Include("name", "date")</c>. A later change
    /// of a locked field invalidates the signature. If null, a lock defined on the signature field
    /// (<see cref="Forms.PdfSignatureField.Lock"/>) is applied. Can be combined with <see cref="Certification"/>.
    /// </summary>
    public PdfFieldLock? FieldLock { get; set; }

    /// <summary>Appearance of a visible signature (used when <see cref="Rectangle"/> is set).</summary>
    public PdfSignatureAppearance Appearance { get; set; } = new();

    /// <summary>Password for encrypted documents.</summary>
    public string? Password { get; set; }

    /// <summary>Reserved size of the signature container in bytes; 0 = estimate automatically.</summary>
    public int SignatureSize { get; set; }

    /// <summary>
    /// If the time-stamp authority fails, sign without time-stamp (level B-B) instead of throwing.
    /// <see cref="PdfSigner.SignDetailed(byte[], IExternalSigner, PdfSignatureOptions?)"/> reports the reached level.
    /// </summary>
    public bool TimestampOptional { get; set; }

    /// <summary>
    /// Additional signed CMS attributes (e.g. encrypted biometric data under a private OID). They are covered by the
    /// signature. Example: <c>new AsnEncodedData("1.3.6.1.4.1.32473.1.1", derOctetString)</c>.
    /// </summary>
    public IList<AsnEncodedData> AdditionalSignedAttributes { get; } = new List<AsnEncodedData>();

    /// <summary>
    /// Additional entries written into the signature dictionary (/V of the field), e.g. application data such as
    /// "/Biometrics". Keys are PDF names without slash. The entries are covered by the signature (they lie inside the
    /// signed byte range). Strings can be added with <c>new PdfString(text)</c>.
    /// </summary>
    public IDictionary<string, MiPdf.Objects.PdfObject> SignatureDictionaryEntries { get; } = new Dictionary<string, MiPdf.Objects.PdfObject>();

    /// <summary>Name of the signing application written to /Prop_Build /App /Name (default "miPDFlib").</summary>
    public string SignatureCreator { get; set; } = "miPDFlib";

    /// <summary>Additional unsigned CMS attributes.</summary>
    public IList<AsnEncodedData> AdditionalUnsignedAttributes { get; } = new List<AsnEncodedData>();

    /// <summary>
    /// Sign documents that had to be repaired when opening: they are saved completely first (only possible if the
    /// document contains no signatures yet). Default false: such documents are rejected.
    /// </summary>
    public bool RepairDamagedDocument { get; set; }
}
