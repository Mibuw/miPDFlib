using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MiPdf.IO;

/// <summary>A window of the file data for the lexer: bytes [<see cref="Start"/>, <see cref="End"/>) of <see cref="Buffer"/>.</summary>
/// <param name="Buffer">The bytes.</param>
/// <param name="Start">Index in <see cref="Buffer"/> of the requested file offset.</param>
/// <param name="End">End of the valid bytes in <see cref="Buffer"/>.</param>
/// <param name="Base">File offset of <c>Buffer[0]</c>: file offset = <c>Base + index</c>.</param>
/// <param name="ReachesEnd">True if the window extends to the end of the file data.</param>
internal readonly record struct FileWindow(byte[] Buffer, int Start, int End, long Base, bool ReachesEnd);

/// <summary>
/// The bytes of a PDF file: an array in memory (<see cref="FromArray"/>) or a file that is read when needed
/// (<see cref="OpenFile"/>; no size limit, the file stays open until <see cref="Dispose"/>).
/// </summary>
internal abstract class PdfFileData : IDisposable
{
    public static readonly PdfFileData Empty = new ArrayFileData(Array.Empty<byte>(), 0, 0);

    public static PdfFileData FromArray(byte[] data) => new ArrayFileData(data, 0, data.Length);

    /// <summary>Opens a file for reading; other processes may read it, and it may be renamed or replaced.</summary>
    /// <param name="path">The file.</param>
    /// <param name="windowLimit">Tests only: size of the first window of each parse and of the repair scan blocks.</param>
    public static PdfFileData OpenFile(string path, int? windowLimit = null) => new FileHandleData(Path.GetFullPath(path), windowLimit);

    /// <summary>Tests only: overrides the initial window size of parses and the block size of scans.</summary>
    public virtual int? WindowLimit => null;

    public abstract long Length { get; }

    /// <summary>The full path of the file the data is read from, or null for data in memory.</summary>
    public virtual string? FilePath => null;

    /// <summary>Returns at least <paramref name="minLength"/> bytes from <paramref name="offset"/> (fewer at the end of the data).</summary>
    public abstract FileWindow GetWindow(long offset, int minLength);

    /// <summary>Copies bytes from <paramref name="offset"/> into <paramref name="target"/>; returns the number of bytes copied.</summary>
    public abstract int Read(long offset, Span<byte> target);

    /// <summary>The data of a range without copying it, if the data is in memory.</summary>
    public abstract ReadOnlyMemory<byte>? TryGetMemory(long offset, int length);

    /// <summary>A view of the first <paramref name="length"/> bytes from <paramref name="offset"/> (the signed revision, data after a preamble).</summary>
    public abstract PdfFileData Slice(long offset, long length);

    public int ReadByte(long offset)
    {
        if (offset < 0 || offset >= Length) return -1;
        Span<byte> b = stackalloc byte[1];
        return Read(offset, b) == 1 ? b[0] : -1;
    }

    public byte[] ReadBytes(long offset, int length)
    {
        var result = new byte[Math.Max(0, Math.Min(length, Length - offset))];
        Read(offset, result);
        return result;
    }

    /// <summary>Copies a range to <paramref name="output"/> in blocks.</summary>
    public virtual void CopyTo(Stream output, long offset, long length) => ForEachBlock(offset, length, block => output.Write(block));

    /// <summary>Appends a range to <paramref name="hash"/> in blocks.</summary>
    public virtual void AppendTo(IncrementalHash hash, long offset, long length) => ForEachBlock(offset, length, block => hash.AppendData(block));

    private delegate void BlockAction(ReadOnlySpan<byte> block);

    /// <summary>Reads a range in blocks of up to 1 MB into a pooled buffer.</summary>
    private void ForEachBlock(long offset, long length, BlockAction action)
    {
        if (length <= 0) return;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 1 << 20));
        try
        {
            while (length > 0)
            {
                int n = Read(offset, buffer.AsSpan(0, (int)Math.Min(length, buffer.Length)));
                if (n <= 0) throw new EndOfStreamException("Unexpected end of the PDF data.");
                action(buffer.AsSpan(0, n));
                offset += n;
                length -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Finds <paramref name="pattern"/> at or after <paramref name="from"/>; -1 if not found.</summary>
    public long IndexOf(ReadOnlySpan<byte> pattern, long from)
    {
        const int Block = 1 << 20;
        while (from < Length)
        {
            var window = GetWindow(from, Block);
            int idx = window.Buffer.AsSpan(window.Start, window.End - window.Start).IndexOf(pattern);
            if (idx >= 0) return window.Base + window.Start + idx;
            if (window.ReachesEnd) return -1;
            from = window.Base + window.End - (pattern.Length - 1); // a match may span two windows
        }
        return -1;
    }

    public virtual void Dispose() { }
}

/// <summary>File data in an array (no copies: windows and ranges refer to the array).</summary>
internal sealed class ArrayFileData(byte[] data, int start, int length) : PdfFileData
{
    public override long Length => length;

    public override FileWindow GetWindow(long offset, int minLength)
    {
        offset = Math.Clamp(offset, 0, length);
        return new FileWindow(data, start + (int)offset, start + length, -start, ReachesEnd: true);
    }

    public override int Read(long offset, Span<byte> target)
    {
        if (offset < 0 || offset >= length) return 0;
        int n = (int)Math.Min(target.Length, length - offset);
        data.AsSpan(start + (int)offset, n).CopyTo(target);
        return n;
    }

    public override ReadOnlyMemory<byte>? TryGetMemory(long offset, int count) => data.AsMemory(start + (int)offset, count);

    public override PdfFileData Slice(long offset, long count) =>
        new ArrayFileData(data, start + (int)offset, (int)Math.Min(count, length - offset));

    public override void CopyTo(Stream output, long offset, long count) => output.Write(data, start + (int)offset, (int)count);

    public override void AppendTo(IncrementalHash hash, long offset, long count) => hash.AppendData(data, start + (int)offset, (int)count);
}

/// <summary>
/// File data read on demand with <see cref="RandomAccess"/>. The file is opened with <see cref="FileShare.Delete"/>, so
/// it can be replaced while the document is open (the handle keeps reading the old data).
/// </summary>
internal sealed class FileHandleData : PdfFileData
{
    private readonly FileHandleOwner _owner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _ownsHandle; // slices share the handle of the data they were created from

    private readonly int? _windowLimit;

    public FileHandleData(string path, int? windowLimit)
    {
        _windowLimit = windowLimit;
        _owner = new FileHandleOwner(path, File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
        _start = 0;
        _length = RandomAccess.GetLength(_owner.Handle);
        _ownsHandle = true;
    }

    private FileHandleData(FileHandleOwner owner, long start, long length, int? windowLimit)
    {
        _windowLimit = windowLimit;
        _owner = owner;
        _start = start;
        _length = length;
    }

    public override long Length => _length;

    public override string? FilePath => _owner.Path;

    public override int? WindowLimit => _windowLimit;

    public override FileWindow GetWindow(long offset, int minLength)
    {
        offset = Math.Clamp(offset, 0, _length);
        int count = (int)Math.Min(Math.Max(minLength, 0), _length - offset);
        var buffer = new byte[count];
        int n = Read(offset, buffer);
        return new FileWindow(buffer, 0, n, offset, ReachesEnd: offset + n >= _length);
    }

    public override int Read(long offset, Span<byte> target)
    {
        if (offset < 0 || offset >= _length) return 0;
        target = target[..(int)Math.Min(target.Length, _length - offset)];
        int total = 0;
        while (total < target.Length)
        {
            int n = RandomAccess.Read(_owner.Handle, target[total..], _start + offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public override ReadOnlyMemory<byte>? TryGetMemory(long offset, int length) => null;

    public override PdfFileData Slice(long offset, long length) =>
        new FileHandleData(_owner, _start + offset, Math.Min(length, _length - offset), _windowLimit);

    public override void Dispose()
    {
        if (_ownsHandle) _owner.Handle.Dispose();
    }

    private sealed record FileHandleOwner(string Path, SafeFileHandle Handle);
}

/// <summary>Encoded stream data that is still in a file (read when it is needed).</summary>
internal sealed record FileRegion(PdfFileData File, long Offset, int Length)
{
    public byte[] Read()
    {
        var data = new byte[Length];
        if (File.Read(Offset, data) != Length) throw new PdfException("Unexpected end of the PDF data.");
        return data;
    }
}
