using System.Collections.Concurrent;

namespace MiPdf.Objects;

/// <summary>
/// A PDF name object (e.g. <c>/Type</c>). Instances are interned, so reference comparison is valid,
/// but <see cref="Equals(object)"/> also compares by value. The value excludes the leading slash.
/// </summary>
public sealed partial class PdfName : PdfObject, IEquatable<PdfName>
{
    private static readonly ConcurrentDictionary<string, PdfName> Cache = new(StringComparer.Ordinal);

    private PdfName(string value) => Value = value;

    public string Value { get; }

    public override PdfObjectType ObjectType => PdfObjectType.Name;

    public static PdfName Get(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 && value[0] == '/') value = value[1..];
        return Cache.GetOrAdd(value, static v => new PdfName(v));
    }

    public static implicit operator PdfName(string value) => Get(value);

    public override PdfObject Clone() => this;

    public bool Equals(PdfName? other) => other is not null && (ReferenceEquals(this, other) || other.Value == Value);

    public override bool Equals(object? obj) => obj is PdfName n && Equals(n);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(PdfName? a, PdfName? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(PdfName? a, PdfName? b) => !(a == b);
}

// Frequently used names.
public sealed partial class PdfName
{
    public static readonly PdfName AA = Get("AA");
    public static readonly PdfName AcroForm = Get("AcroForm");
    public static readonly PdfName ActualText = Get("ActualText");
    public static readonly PdfName AESV2 = Get("AESV2");
    public static readonly PdfName AESV3 = Get("AESV3");
    public static readonly PdfName Annot = Get("Annot");
    public static readonly PdfName Annots = Get("Annots");
    public static readonly PdfName AP = Get("AP");
    public static readonly PdfName ArtBox = Get("ArtBox");
    public static readonly PdfName AS = Get("AS");
    public static readonly PdfName ASCII85Decode = Get("ASCII85Decode");
    public static readonly PdfName ASCIIHexDecode = Get("ASCIIHexDecode");
    public static readonly PdfName Author = Get("Author");
    public static readonly PdfName BaseEncoding = Get("BaseEncoding");
    public static readonly PdfName BaseFont = Get("BaseFont");
    public static readonly PdfName BBox = Get("BBox");
    public static readonly PdfName BitsPerComponent = Get("BitsPerComponent");
    public static readonly PdfName BleedBox = Get("BleedBox");
    public static readonly PdfName Border = Get("Border");
    public static readonly PdfName BS = Get("BS");
    public static readonly PdfName ByteRange = Get("ByteRange");
    public static readonly PdfName C = Get("C");
    public static readonly PdfName Catalog = Get("Catalog");
    public static readonly PdfName CCITTFaxDecode = Get("CCITTFaxDecode");
    public static readonly PdfName CF = Get("CF");
    public static readonly PdfName CFM = Get("CFM");
    public static readonly PdfName Ch = Get("Ch");
    public static readonly PdfName CIDFontType0 = Get("CIDFontType0");
    public static readonly PdfName CIDFontType2 = Get("CIDFontType2");
    public static readonly PdfName CIDSystemInfo = Get("CIDSystemInfo");
    public static readonly PdfName CIDToGIDMap = Get("CIDToGIDMap");
    public static readonly PdfName ColorSpace = Get("ColorSpace");
    public static readonly PdfName Colors = Get("Colors");
    public static readonly PdfName Columns = Get("Columns");
    public static readonly PdfName Contents = Get("Contents");
    public static readonly PdfName Count = Get("Count");
    public static readonly PdfName CreationDate = Get("CreationDate");
    public static readonly PdfName Creator = Get("Creator");
    public static readonly PdfName CropBox = Get("CropBox");
    public static readonly PdfName Crypt = Get("Crypt");
    public static readonly PdfName DA = Get("DA");
    public static readonly PdfName DCTDecode = Get("DCTDecode");
    public static readonly PdfName Decode = Get("Decode");
    public static readonly PdfName DecodeParms = Get("DecodeParms");
    public static readonly PdfName DescendantFonts = Get("DescendantFonts");
    public static readonly PdfName Dest = Get("Dest");
    public static readonly PdfName Dests = Get("Dests");
    public static readonly PdfName DeviceCMYK = Get("DeviceCMYK");
    public static readonly PdfName DeviceGray = Get("DeviceGray");
    public static readonly PdfName DeviceRGB = Get("DeviceRGB");
    public static readonly PdfName Differences = Get("Differences");
    public static readonly PdfName DP = Get("DP");
    public static readonly PdfName DR = Get("DR");
    public static readonly PdfName DV = Get("DV");
    public static readonly PdfName EarlyChange = Get("EarlyChange");
    public static readonly PdfName Encoding = Get("Encoding");
    public static readonly PdfName Encrypt = Get("Encrypt");
    public static readonly PdfName EncryptMetadata = Get("EncryptMetadata");
    public static readonly PdfName ExtGState = Get("ExtGState");
    public static readonly PdfName Extends = Get("Extends");
    public static readonly PdfName F = Get("F");
    public static readonly PdfName Ff = Get("Ff");
    public static readonly PdfName Fields = Get("Fields");
    public static readonly PdfName Filter = Get("Filter");
    public static readonly PdfName First = Get("First");
    public static readonly PdfName FirstChar = Get("FirstChar");
    public static readonly PdfName FitH = Get("FitH");
    public static readonly PdfName FlateDecode = Get("FlateDecode");
    public static readonly PdfName Font = Get("Font");
    public static readonly PdfName FontDescriptor = Get("FontDescriptor");
    public static readonly PdfName FontFile = Get("FontFile");
    public static readonly PdfName FontFile2 = Get("FontFile2");
    public static readonly PdfName FontFile3 = Get("FontFile3");
    public static readonly PdfName FontName = Get("FontName");
    public static readonly PdfName Form = Get("Form");
    public static readonly PdfName FT = Get("FT");
    public static readonly PdfName ID = Get("ID");
    public static readonly PdfName Identity = Get("Identity");
    public static readonly PdfName IdentityH = Get("Identity-H");
    public static readonly PdfName Image = Get("Image");
    public static readonly PdfName Index = Get("Index");
    public static readonly PdfName Info = Get("Info");
    public static readonly PdfName JBIG2Decode = Get("JBIG2Decode");
    public static readonly PdfName JPXDecode = Get("JPXDecode");
    public static readonly PdfName Keywords = Get("Keywords");
    public static readonly PdfName K = Get("K");
    public static readonly PdfName Kids = Get("Kids");
    public static readonly PdfName LastChar = Get("LastChar");
    public static readonly PdfName Length = Get("Length");
    public static readonly PdfName Link = Get("Link");
    public static readonly PdfName LZWDecode = Get("LZWDecode");
    public static readonly PdfName M = Get("M");
    public static readonly PdfName Mask = Get("Mask");
    public static readonly PdfName MediaBox = Get("MediaBox");
    public static readonly PdfName Metadata = Get("Metadata");
    public static readonly PdfName ModDate = Get("ModDate");
    public static readonly PdfName MK = Get("MK");
    public static readonly PdfName N = Get("N");
    public static readonly PdfName Name = Get("Name");
    public static readonly PdfName NeedAppearances = Get("NeedAppearances");
    public static readonly PdfName Next = Get("Next");
    public static readonly PdfName ObjStm = Get("ObjStm");
    public static readonly PdfName Off = Get("Off");
    public static readonly PdfName Opt = Get("Opt");
    public static readonly PdfName Outlines = Get("Outlines");
    public static readonly PdfName P = Get("P");
    public static readonly PdfName Page = Get("Page");
    public static readonly PdfName Pages = Get("Pages");
    public static readonly PdfName Parent = Get("Parent");
    public static readonly PdfName Pattern = Get("Pattern");
    public static readonly PdfName Perms = Get("Perms");
    public static readonly PdfName Predictor = Get("Predictor");
    public static readonly PdfName Prev = Get("Prev");
    public static readonly PdfName ProcSet = Get("ProcSet");
    public static readonly PdfName Producer = Get("Producer");
    public static readonly PdfName Properties = Get("Properties");
    public static readonly PdfName Q = Get("Q");
    public static readonly PdfName Rect = Get("Rect");
    public static readonly PdfName Resources = Get("Resources");
    public static readonly PdfName Root = Get("Root");
    public static readonly PdfName Rotate = Get("Rotate");
    public static readonly PdfName RunLengthDecode = Get("RunLengthDecode");
    public static readonly PdfName S = Get("S");
    public static readonly PdfName Shading = Get("Shading");
    public static readonly PdfName Sig = Get("Sig");
    public static readonly PdfName SigFlags = Get("SigFlags");
    public static readonly PdfName Size = Get("Size");
    public static readonly PdfName SMask = Get("SMask");
    public static readonly PdfName StdCF = Get("StdCF");
    public static readonly PdfName StmF = Get("StmF");
    public static readonly PdfName StrF = Get("StrF");
    public static readonly PdfName StructTreeRoot = Get("StructTreeRoot");
    public static readonly PdfName Subject = Get("Subject");
    public static readonly PdfName SubFilter = Get("SubFilter");
    public static readonly PdfName Subtype = Get("Subtype");
    public static readonly PdfName T = Get("T");
    public static readonly PdfName Title = Get("Title");
    public static readonly PdfName ToUnicode = Get("ToUnicode");
    public static readonly PdfName Trapped = Get("Trapped");
    public static readonly PdfName TrimBox = Get("TrimBox");
    public static readonly PdfName TrueType = Get("TrueType");
    public static readonly PdfName Tx = Get("Tx");
    public static readonly PdfName Type = Get("Type");
    public static readonly PdfName Type0 = Get("Type0");
    public static readonly PdfName Type1 = Get("Type1");
    public static readonly PdfName Type3 = Get("Type3");
    public static readonly PdfName URI = Get("URI");
    public static readonly PdfName V = Get("V");
    public static readonly PdfName W = Get("W");
    public static readonly PdfName Widget = Get("Widget");
    public static readonly PdfName Widths = Get("Widths");
    public static readonly PdfName WinAnsiEncoding = Get("WinAnsiEncoding");
    public static readonly PdfName XML = Get("XML");
    public static readonly PdfName XObject = Get("XObject");
    public static readonly PdfName XRef = Get("XRef");
    public static readonly PdfName XRefStm = Get("XRefStm");
}
