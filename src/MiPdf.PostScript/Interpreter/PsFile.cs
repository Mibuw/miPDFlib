using System.IO.Compression;

namespace MiPdf.PostScript.Interpreter;

/// <summary>A PostScript file object: the program, a string read as a file, a filter or an output channel.</summary>
internal abstract class PsFile
{
    private int _pushback = -1;

    public bool Closed { get; protected set; }

    public virtual bool CanRead => false;

    public virtual bool CanWrite => false;

    /// <summary>The next byte, -1 at the end of the data.</summary>
    public int ReadByte()
    {
        if (_pushback >= 0)
        {
            int b = _pushback;
            _pushback = -1;
            return b;
        }
        return Closed ? -1 : ReadRaw();
    }

    /// <summary>Returns one byte to the file (the scanner reads one byte too far after some tokens).</summary>
    public void Unread(int b)
    {
        if (b >= 0) _pushback = b;
    }

    /// <summary>True if a byte was returned with <see cref="Unread"/> and not read again yet.</summary>
    public bool HasPushback => _pushback >= 0;

    protected virtual int ReadRaw() => -1;

    public virtual void Write(ReadOnlySpan<byte> data) => throw new PsError("ioerror");

    public virtual void Flush()
    {
    }

    /// <summary>Bytes that can be read without waiting (-1 if unknown, as for filters).</summary>
    public virtual int BytesAvailable => -1;

    /// <summary>Position in bytes for <c>fileposition</c> (-1 if not positionable).</summary>
    public virtual long Position => -1;

    public virtual void Close() => Closed = true;

    /// <summary>Reads to the end of the data (used by <c>flushfile</c> on input files).</summary>
    public void Drain()
    {
        while (ReadByte() >= 0)
        {
        }
    }
}

/// <summary>Reads bytes from memory: the program or a string used as a file.</summary>
internal sealed class PsMemoryFile : PsFile
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _position;

    public PsMemoryFile(byte[] data, int start = 0, int end = -1)
    {
        _data = data;
        _position = start;
        _end = end < 0 ? data.Length : end;
    }

    public override bool CanRead => true;

    protected override int ReadRaw() => _position < _end ? _data[_position++] : -1;

    public override int BytesAvailable => Closed ? -1 : _end - _position;

    public override long Position => _position;
}

internal delegate void PsByteWriter(ReadOnlySpan<byte> data);

/// <summary>Writes text output (%stdout, %stderr) to the interpreter's output.</summary>
internal sealed class PsOutputFile : PsFile
{
    private readonly PsByteWriter _write;

    public PsOutputFile(PsByteWriter write) => _write = write;

    public override bool CanWrite => true;

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (Closed) throw new PsError("ioerror");
        _write(data);
    }
}

/// <summary>A decoding filter: reads its source as far as it needs, one byte at a time.</summary>
internal abstract class PsDecodeFilter : PsFile
{
    protected readonly PsFile Source;
    private bool _ended;

    protected PsDecodeFilter(PsFile source) => Source = source;

    public override bool CanRead => true;

    protected sealed override int ReadRaw()
    {
        if (_ended) return -1;
        int b = Decode();
        if (b < 0) _ended = true;
        return b;
    }

    /// <summary>The next decoded byte, -1 at the end of data.</summary>
    protected abstract int Decode();
}

/// <summary>ASCIIHexDecode: hex digit pairs up to '&gt;', whitespace ignored.</summary>
internal sealed class PsAsciiHexDecode : PsDecodeFilter
{
    public PsAsciiHexDecode(PsFile source) : base(source) { }

    protected override int Decode()
    {
        int high = -1;
        while (true)
        {
            int c = Source.ReadByte();
            if (c < 0 || c == '>')
                return high < 0 ? -1 : high << 4; // an odd final digit is followed by 0
            int digit = HexDigit(c);
            if (digit < 0)
            {
                if (PsScanner.IsWhitespace(c)) continue;
                throw new PsError("ioerror");
            }
            if (high < 0) high = digit;
            else return (high << 4) | digit;
        }
    }

    public static int HexDigit(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}

/// <summary>ASCII85Decode: groups of five characters up to '~&gt;', 'z' for four zero bytes.</summary>
internal sealed class PsAscii85Decode : PsDecodeFilter
{
    private readonly byte[] _group = new byte[4];
    private int _available, _next;

    public PsAscii85Decode(PsFile source) : base(source) { }

    protected override int Decode()
    {
        if (_next < _available) return _group[_next++];
        int count = 0;
        long value = 0;
        while (count < 5)
        {
            int c = Source.ReadByte();
            if (c < 0) break;
            if (PsScanner.IsWhitespace(c)) continue;
            if (c == '~')
            {
                int d = Source.ReadByte();
                if (d != '>') Source.Unread(d);
                break;
            }
            if (c == 'z' && count == 0)
            {
                _group.AsSpan().Clear();
                _available = 4;
                _next = 1;
                return 0;
            }
            if (c < '!' || c > 'u') throw new PsError("ioerror");
            value = value * 85 + (c - '!');
            count++;
        }
        if (count == 0) return -1;
        if (count == 1) throw new PsError("ioerror");
        for (int i = count; i < 5; i++) value = value * 85 + 84; // pad with 'u'
        for (int i = 3; i >= 0; i--)
        {
            _group[i] = (byte)value;
            value >>= 8;
        }
        _available = count - 1;
        _next = 1;
        return _group[0];
    }
}

/// <summary>RunLengthDecode: length byte n &lt; 128 copies n+1 bytes, n &gt; 128 repeats one byte 257-n times, 128 ends.</summary>
internal sealed class PsRunLengthDecode : PsDecodeFilter
{
    private int _literal, _repeat, _value;

    public PsRunLengthDecode(PsFile source) : base(source) { }

    protected override int Decode()
    {
        while (true)
        {
            if (_literal > 0)
            {
                _literal--;
                return Source.ReadByte();
            }
            if (_repeat > 0)
            {
                _repeat--;
                return _value;
            }
            int n = Source.ReadByte();
            if (n < 0 || n == 128) return -1;
            if (n < 128) _literal = n + 1;
            else
            {
                _value = Source.ReadByte();
                if (_value < 0) return -1;
                _repeat = 257 - n;
            }
        }
    }
}

/// <summary>
/// SubFileDecode: passes data through up to the (count+1)-th occurrence of an end string, which is consumed but not
/// passed; with an empty end string, count is a number of bytes (0: up to the end of the source).
/// </summary>
internal sealed class PsSubFileDecode : PsDecodeFilter
{
    private readonly byte[] _eod;
    private int _remaining;
    private readonly long _byteLimit;
    private long _read;
    private readonly Stack<int> _refeed = new(); // bytes to examine again before reading the source
    private readonly Queue<byte> _out = new();
    private int _matched;

    public PsSubFileDecode(PsFile source, int count, byte[] eod) : base(source)
    {
        _eod = eod;
        _remaining = count;
        _byteLimit = eod.Length == 0 && count > 0 ? count : long.MaxValue;
    }

    private int Next() => _refeed.Count > 0 ? _refeed.Pop() : Source.ReadByte();

    protected override int Decode()
    {
        if (_eod.Length == 0)
        {
            if (_read >= _byteLimit) return -1;
            _read++;
            return Source.ReadByte();
        }
        if (_out.Count > 0) return _out.Dequeue();
        while (true)
        {
            int c = Next();
            if (c < 0)
            {
                // a partial match at the end is data
                for (int i = 0; i < _matched; i++) _out.Enqueue(_eod[i]);
                _matched = 0;
                return _out.Count > 0 ? _out.Dequeue() : -1;
            }
            if (c == _eod[_matched])
            {
                if (++_matched < _eod.Length) continue;
                _matched = 0;
                if (_remaining == 0) return -1; // the end string is consumed
                _remaining--;
                foreach (byte e in _eod) _out.Enqueue(e);
                return _out.Dequeue();
            }
            if (_matched == 0) return c;
            // the partial match failed: its first byte is data, the rest and c are examined again
            _refeed.Push(c);
            for (int i = _matched - 1; i >= 1; i--) _refeed.Push(_eod[i]);
            _matched = 0;
            return _eod[0];
        }
    }
}

/// <summary>FlateDecode (zlib): reads the source one byte at a time, so it stops at the end of the compressed data.</summary>
internal sealed class PsFlateDecode : PsDecodeFilter
{
    private readonly ZLibStream _inflater;

    public PsFlateDecode(PsFile source) : base(source) => _inflater = new ZLibStream(new ByteStream(source), CompressionMode.Decompress);

    protected override int Decode()
    {
        try
        {
            return _inflater.ReadByte();
        }
        catch (InvalidDataException)
        {
            throw new PsError("ioerror");
        }
    }

    /// <summary>A stream over a PostScript file that returns one byte per read.</summary>
    private sealed class ByteStream(PsFile file) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            int b = file.ReadByte();
            if (b < 0) return 0;
            buffer[offset] = (byte)b;
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>NullEncode and other pass-through filters for writing: data goes to the target unchanged.</summary>
internal sealed class PsNullEncode : PsFile
{
    private readonly PsFile _target;

    public PsNullEncode(PsFile target) => _target = target;

    public override bool CanWrite => true;

    public override void Write(ReadOnlySpan<byte> data) => _target.Write(data);
}
