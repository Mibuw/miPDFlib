using MiPdf.Filters;
using MiPdf.IO;

namespace MiPdf.Objects;

/// <summary>
/// A PDF stream: a dictionary plus a byte sequence. <see cref="RawBytes"/> holds the encoded data as
/// stored in the file (after decryption); <see cref="GetBytes"/> applies the decoding filters.
/// Streams must be indirect objects; the writer allocates an object number for direct streams.
/// </summary>
public sealed class PdfStream : PdfDictionary
{
    private byte[]? _raw = Array.Empty<byte>();

    // encoded data of a stream read from a file, copied only when RawBytes is used, so that saving, merging and copying
    // pages do not duplicate large streams (images, fonts): a slice of the file data in memory, or a range of a file
    private ReadOnlyMemory<byte> _source;
    private FileRegion? _region;

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

    internal PdfStream(PdfDictionary dictionary, FileRegion raw)
    {
        foreach (var (k, v) in dictionary) this[k] = v;
        _raw = null;
        _region = raw;
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
                _raw = _region?.Read() ?? _source.ToArray();
                _source = default;
                _region = null;
            }
            return _raw;
        }
    }

    /// <summary>The length of the encoded data.</summary>
    internal int RawLength => _raw?.Length ?? _region?.Length ?? _source.Length;

    /// <summary>Writes the encoded data; data still in the file is copied from there.</summary>
    internal void WriteRawTo(PdfOutput output)
    {
        if (_raw != null) output.Write(_raw);
        else if (_region is { } region) output.Write(region);
        else output.Write(_source.Span);
    }

    /// <summary>True if both streams have the same encoded data.</summary>
    internal bool RawDataEquals(PdfStream other)
    {
        if (RawLength != other.RawLength) return false;
        if (_raw == null && other._raw == null && _region is { } a && other._region is { } b)
            if (ReferenceEquals(a.File, b.File) && a.Offset == b.Offset) return true;
        if (_region == null && other._region == null)
            return (_raw ?? _source).Span.SequenceEqual((other._raw ?? other._source).Span);
        // at least one stream is still in the file: compare a copy (the length is equal and limited to one stream)
        var mine = _region?.Read() ?? (_raw ?? _source).ToArray();
        var theirs = other._region?.Read() ?? (other._raw ?? other._source).ToArray();
        return mine.AsSpan().SequenceEqual(theirs);
    }

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
        _region = null;
        Set(PdfName.Length, _raw.Length);
    }

    /// <summary>Replaces the encoded data; the /Filter and /DecodeParms entries are left untouched.</summary>
    public void SetRawBytes(byte[] raw)
    {
        _raw = raw ?? throw new ArgumentNullException(nameof(raw));
        _source = default;
        _region = null;
        Set(PdfName.Length, _raw.Length);
    }

    /// <summary>
    /// Takes the encoded data of <paramref name="other"/>; data still in the file is shared, not copied (data in a file
    /// that is read on demand can only be read while that document is open).
    /// </summary>
    internal void CopyRawDataFrom(PdfStream other)
    {
        if (other._raw != null) SetRawBytes((byte[])other._raw.Clone());
        else
        {
            _raw = null;
            _source = other._source;
            _region = other._region;
            Set(PdfName.Length, other.RawLength);
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
        copy._region = _region;
        return copy;
    }
}
