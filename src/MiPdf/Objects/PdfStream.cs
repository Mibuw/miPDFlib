using MiPdf.Filters;

namespace MiPdf.Objects;

/// <summary>
/// A PDF stream: a dictionary plus a byte sequence. <see cref="RawBytes"/> holds the encoded data as
/// stored in the file (after decryption); <see cref="GetBytes"/> applies the decoding filters.
/// Streams must be indirect objects; the writer allocates an object number for direct streams.
/// </summary>
public sealed class PdfStream : PdfDictionary
{
    private byte[]? _raw = Array.Empty<byte>();

    // encoded data of a stream read from a file: a slice of the file data, copied only when RawBytes is used,
    // so that saving, merging and copying pages do not duplicate large streams (images, fonts)
    private ReadOnlyMemory<byte> _source;

    /// <summary>Creates an empty stream without data.</summary>
    public PdfStream() { }

    /// <summary>Creates a stream from decoded data, optionally Flate-compressing it.</summary>
    public PdfStream(byte[] data, bool compress = true) => SetBytes(data, compress);

    internal PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> raw)
    {
        foreach (var (k, v) in dictionary) this[k] = v;
        _raw = null;
        _source = raw;
    }

    /// <inheritdoc/>
    public override PdfObjectType ObjectType => PdfObjectType.Stream;

    /// <summary>The encoded stream data.</summary>
    public byte[] RawBytes
    {
        get
        {
            if (_raw == null)
            {
                _raw = _source.ToArray();
                _source = default;
            }
            return _raw;
        }
    }

    /// <summary>The encoded data without copying it out of the file data.</summary>
    internal ReadOnlyMemory<byte> RawData => _raw ?? _source;

    /// <summary>The length of the encoded data.</summary>
    internal int RawLength => _raw?.Length ?? _source.Length;

    /// <summary>
    /// Returns the decoded data. Image-specific filters (DCT, JPX, JBIG2, CCITT) are not decoded;
    /// data encoded with those is returned as-is after all preceding generic filters.
    /// </summary>
    public byte[] GetBytes() => StreamFilters.Decode(RawBytes, this, out _);

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
        _source = default;
        Set(PdfName.Length, _raw.Length);
    }

    /// <summary>Replaces the encoded data; the /Filter and /DecodeParms entries are left untouched.</summary>
    public void SetRawBytes(byte[] raw)
    {
        _raw = raw ?? throw new ArgumentNullException(nameof(raw));
        _source = default;
        Set(PdfName.Length, _raw.Length);
    }

    /// <summary>Takes the encoded data of <paramref name="other"/>; data still in the file is shared, not copied.</summary>
    internal void CopyRawDataFrom(PdfStream other)
    {
        if (other._raw != null) SetRawBytes((byte[])other._raw.Clone());
        else
        {
            _raw = null;
            _source = other._source;
            Set(PdfName.Length, _source.Length);
        }
    }

    /// <summary>Returns the filter names in order of application.</summary>
    public IReadOnlyList<PdfName> GetFilters() => StreamFilters.GetFilterNames(this);

    /// <summary>Creates a deep copy of the dictionary entries and a copy of the encoded data.</summary>
    public override PdfObject Clone()
    {
        var copy = new PdfStream();
        CopyEntriesTo(copy);
        copy._raw = _raw == null ? null : (byte[])_raw.Clone();
        copy._source = _source;
        return copy;
    }
}
