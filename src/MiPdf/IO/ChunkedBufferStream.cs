namespace MiPdf.IO;

/// <summary>
/// A write-only in-memory stream that stores the data in chunks. Unlike a <see cref="MemoryStream"/> it never copies
/// the data while growing, so <see cref="ToArray"/> needs at most the output size twice instead of up to four times.
/// </summary>
internal sealed class ChunkedBufferStream : Stream
{
    private const int MinChunk = 64 * 1024;
    private const int MaxChunk = 16 * 1024 * 1024;

    private readonly List<byte[]> _chunks = new();
    private int _used; // bytes used in the last chunk
    private long _length;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            if (_chunks.Count == 0 || _used == _chunks[^1].Length)
            {
                // chunks grow with the output: few chunks for large files, little waste for small ones
                _chunks.Add(new byte[(int)Math.Clamp(_length, MinChunk, MaxChunk)]);
                _used = 0;
            }
            var chunk = _chunks[^1];
            int n = Math.Min(buffer.Length, chunk.Length - _used);
            buffer[..n].CopyTo(chunk.AsSpan(_used));
            _used += n;
            _length += n;
            buffer = buffer[n..];
        }
    }

    public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

    /// <summary>Returns the written data as one array.</summary>
    public byte[] ToArray()
    {
        var result = new byte[_length];
        CopyTo(result);
        return result;
    }

    /// <summary>Copies the written data to <paramref name="target"/>, which must hold at least <see cref="Length"/> bytes.</summary>
    public void CopyTo(Span<byte> target)
    {
        int position = 0;
        for (int i = 0; i < _chunks.Count; i++)
        {
            int n = i == _chunks.Count - 1 ? _used : _chunks[i].Length;
            _chunks[i].AsSpan(0, n).CopyTo(target[position..]);
            position += n;
        }
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
