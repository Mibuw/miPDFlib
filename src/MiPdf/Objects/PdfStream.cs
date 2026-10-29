using MiPdf.Filters;

namespace MiPdf.Objects;

/// <summary>
/// A PDF stream: a dictionary plus a byte sequence. <see cref="RawBytes"/> holds the encoded data as
/// stored in the file (after decryption); <see cref="GetBytes"/> applies the decoding filters.
/// Streams must be indirect objects; the writer allocates an object number for direct streams.
/// </summary>
public sealed class PdfStream : PdfDictionary
{
    private byte[] _raw = Array.Empty<byte>();

    public PdfStream() { }

    /// <summary>Creates a stream from decoded data, optionally Flate-compressing it.</summary>
    public PdfStream(byte[] data, bool compress = true) => SetBytes(data, compress);

    internal PdfStream(PdfDictionary dictionary, byte[] raw)
    {
        foreach (var (k, v) in dictionary) this[k] = v;
        _raw = raw;
    }

    public override PdfObjectType ObjectType => PdfObjectType.Stream;

    /// <summary>The encoded stream data.</summary>
    public byte[] RawBytes => _raw;

    /// <summary>
    /// Returns the decoded data. Image-specific filters (DCT, JPX, JBIG2, CCITT) are not decoded;
    /// data encoded with those is returned as-is after all preceding generic filters.
    /// </summary>
    public byte[] GetBytes() => StreamFilters.Decode(_raw, this, out _);

    /// <summary>Replaces the stream data; with <paramref name="compress"/> the data is Flate-encoded.</summary>
    public void SetBytes(byte[] data, bool compress = true)
    {
        ArgumentNullException.ThrowIfNull(data);
        Remove(PdfName.DecodeParms);
        if (compress)
        {
            _raw = FlateFilter.Encode(data);
            this[PdfName.Filter] = PdfName.FlateDecode;
        }
        else
        {
            _raw = data;
            Remove(PdfName.Filter);
        }
        Set(PdfName.Length, _raw.Length);
    }

    /// <summary>Replaces the encoded data; the /Filter and /DecodeParms entries are left untouched.</summary>
    public void SetRawBytes(byte[] raw)
    {
        _raw = raw ?? throw new ArgumentNullException(nameof(raw));
        Set(PdfName.Length, _raw.Length);
    }

    /// <summary>Returns the filter names in order of application.</summary>
    public IReadOnlyList<PdfName> GetFilters() => StreamFilters.GetFilterNames(this);

    public override PdfObject Clone()
    {
        var copy = new PdfStream();
        CopyEntriesTo(copy);
        copy._raw = (byte[])_raw.Clone();
        return copy;
    }
}
