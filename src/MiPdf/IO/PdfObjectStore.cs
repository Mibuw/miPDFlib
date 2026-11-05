using MiPdf.Objects;

namespace MiPdf.IO;

internal enum XrefEntryType
{
    Free,
    InUse,
    Compressed,
}

internal sealed class XrefEntry
{
    public XrefEntryType Type;
    public long Offset;          // InUse: file offset
    public int Generation;
    public int StreamNumber;     // Compressed: object stream number
    public int StreamIndex;      // Compressed: index within object stream
    public int Section;          // index of the xref section (0 = newest)
}

/// <summary>Decrypts strings and streams of objects read from an encrypted file.</summary>
internal interface IObjectDecryptor
{
    byte[] DecryptString(byte[] data, int objectNumber, int generation);

    byte[] DecryptStream(byte[] data, int objectNumber, int generation, PdfStream stream);
}

/// <summary>
/// Holds all indirect objects of a document: lazily loaded objects from the original file and
/// objects added or replaced in memory. Implements reference resolution.
/// </summary>
internal sealed partial class PdfObjectStore : IPdfObjectResolver, IDisposable
{
    private const int InitialWindow = 4096; // most objects are smaller; larger ones grow the window
    private const int MaxWindow = int.MaxValue - 1024;

    private readonly PdfFileData _file;
    private readonly Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, PdfObject> _objects = new();
    private readonly Dictionary<int, int> _generations = new();
    private readonly Dictionary<(int, int), PdfReference> _references = new();
    private readonly Dictionary<PdfObject, PdfReference> _objectToReference = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _added = new();
    private readonly HashSet<int> _deleted = new();
    private readonly HashSet<int> _loading = new();
    private readonly Dictionary<int, ObjectStreamIndex> _objectStreams = new();
    private int _nextNumber = 1;
    private bool _repaired;

    /// <summary>Creates an empty store for a new document.</summary>
    public PdfObjectStore()
    {
        _file = PdfFileData.Empty;
        Trailer = new PdfDictionary();
    }

    /// <summary>Opens an existing file (in memory or read on demand).</summary>
    public PdfObjectStore(PdfFileData file)
    {
        _file = file;
        Trailer = new PdfDictionary();
        HeaderOffset = FindHeader(out var version);
        Version = version;
        if (HeaderOffset > 0 && XrefIsRelativeToHeader())
        {
            // data before %PDF- is not part of the PDF (e.g. print job control data, PDF 2.0 7.5.2) and all
            // offsets count from the header: continue with the PDF data only
            _file = file.Slice(HeaderOffset, file.Length - HeaderOffset);
            HeaderOffset = 0;
        }
        try
        {
            ReadXref();
            // encrypted files: objects can only be validated once the decryptor is known
            if (Trailer.ContainsKey(PdfName.Encrypt))
            {
                if (Trailer[PdfName.Root] is not PdfReference) throw new PdfParseException("Missing document catalog");
            }
            else if (Trailer.Get(PdfName.Root) is not PdfDictionary) throw new PdfParseException("Missing document catalog");
        }
        catch (Exception ex) when (ex is PdfParseException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidCastException)
        {
            Repair();
        }
    }

    /// <summary>The data of the original file (without data before the header if offsets count from the header).</summary>
    public PdfFileData File => _file;

    public bool IsFromFile => _file.Length > 0;

    /// <summary>Closes the file of documents that are read on demand.</summary>
    public void Dispose() => _file.Dispose();

    public int HeaderOffset { get; }

    /// <summary>Header version, e.g. "1.7".</summary>
    public string Version { get; } = "1.7";

    /// <summary>Offset of the last xref section in the original file (for incremental updates).</summary>
    public long LastXrefOffset { get; private set; } = -1;

    /// <summary>True if the newest xref section of the original file is an xref stream.</summary>
    public bool UsesXrefStreams { get; private set; }

    public bool WasRepaired => _repaired;

    public PdfDictionary Trailer { get; private set; }

    /// <summary>Setting the decryptor discards all cached objects so they are re-read decrypted.</summary>
    public IObjectDecryptor? Decryptor
    {
        get => _decryptor;
        set
        {
            _decryptor = value;
            _objects.Clear();
            _objectToReference.Clear();
            _objectStreams.Clear();
        }
    }

    private IObjectDecryptor? _decryptor;

    /// <summary>Object number of the encryption dictionary (its strings are not encrypted).</summary>
    public int EncryptObjectNumber { get; set; } = -1;

    /// <summary>Highest object number in use + 1.</summary>
    public int Size => _nextNumber;

    public PdfReference GetReference(int number, int generation)
    {
        if (!_references.TryGetValue((number, generation), out var r))
        {
            r = new PdfReference(number, generation, this);
            _references[(number, generation)] = r;
        }
        return r;
    }

    public PdfObject? ResolveReference(PdfReference reference)
    {
        if (!ReferenceEquals(reference.Resolver, this)) return reference.Resolver?.ResolveReference(reference);
        return GetObject(reference.ObjectNumber);
    }

    /// <summary>Returns the object with the given number, loading it from the file if needed.</summary>
    public PdfObject? GetObject(int number)
    {
        if (_objects.TryGetValue(number, out var obj)) return obj;
        if (_deleted.Contains(number) || !_xref.TryGetValue(number, out var entry) || entry.Type == XrefEntryType.Free)
            return null;
        if (!_loading.Add(number)) return null; // circular /Length or object stream reference
        try
        {
            obj = LoadObject(number, entry);
            if (obj == null && !_repaired)
            {
                Repair();
                if (_xref.TryGetValue(number, out var repaired))
                {
                    entry = repaired;
                    obj = LoadObject(number, entry);
                }
            }
        }
        finally
        {
            _loading.Remove(number);
        }
        if (obj != null)
        {
            _objects[number] = obj;
            _generations[number] = entry.Type == XrefEntryType.InUse ? entry.Generation : 0;
            _objectToReference[obj] = GetReference(number, _generations[number]);
        }
        return obj;
    }

    /// <summary>Returns the indirect reference of an object owned by this store, if any.</summary>
    public PdfReference? GetReferenceOf(PdfObject obj) =>
        obj is PdfReference r ? r : _objectToReference.TryGetValue(obj, out var reference) ? reference : null;

    /// <summary>Adds a new indirect object and returns its reference. Returns the existing reference if already indirect.</summary>
    public PdfReference Add(PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (obj is PdfReference r) return r;
        if (_objectToReference.TryGetValue(obj, out var existing)) return existing;
        int number = _nextNumber++;
        _objects[number] = obj;
        _generations[number] = 0;
        _added.Add(number);
        var reference = GetReference(number, 0);
        _objectToReference[obj] = reference;
        return reference;
    }

    /// <summary>Reserves an object number without storing an object (e.g. for an xref stream).</summary>
    public int AllocateNumber() => _nextNumber++;

    /// <summary>Replaces the object with the given number.</summary>
    public void Replace(int number, PdfObject obj)
    {
        if (_objects.TryGetValue(number, out var old)) _objectToReference.Remove(old);
        _objects[number] = obj;
        _deleted.Remove(number);
        if (!_generations.ContainsKey(number))
            _generations[number] = _xref.TryGetValue(number, out var e) && e.Type == XrefEntryType.InUse ? e.Generation : 0;
        if (!_xref.ContainsKey(number)) _added.Add(number);
        _objectToReference[obj] = GetReference(number, _generations[number]);
        if (number >= _nextNumber) _nextNumber = number + 1;
    }

    public void Delete(int number)
    {
        if (_objects.Remove(number, out var old)) _objectToReference.Remove(old);
        _deleted.Add(number);
        _added.Remove(number);
    }

    public int GetGeneration(int number) =>
        _generations.TryGetValue(number, out var g) ? g :
        _xref.TryGetValue(number, out var e) && e.Type == XrefEntryType.InUse ? e.Generation : 0;

    /// <summary>All object numbers that exist (in the file or in memory) and are not deleted.</summary>
    public IEnumerable<int> GetObjectNumbers()
    {
        var numbers = new SortedSet<int>(_objects.Keys);
        foreach (var (n, e) in _xref)
            if (e.Type != XrefEntryType.Free) numbers.Add(n);
        numbers.ExceptWith(_deleted);
        return numbers;
    }

    public IReadOnlyCollection<int> LoadedObjectNumbers => _objects.Keys;

    public bool IsAdded(int number) => _added.Contains(number);

    public IReadOnlyCollection<int> DeletedObjectNumbers => _deleted;

    public bool ExistsInFile(int number) => _xref.TryGetValue(number, out var e) && e.Type != XrefEntryType.Free;

    /// <summary>
    /// True if <paramref name="number"/> has the same cross-reference entry in both stores, i.e. the object was not
    /// redefined or deleted in between (used to find the changes made after a signature: <paramref name="other"/>
    /// is the signed revision of the same file).
    /// </summary>
    public bool HasSameEntry(int number, PdfObjectStore other)
    {
        if (!_xref.TryGetValue(number, out var a) || !other._xref.TryGetValue(number, out var b) || a.Type != b.Type) return false;
        switch (a.Type)
        {
            case XrefEntryType.InUse:
                return a.Offset == b.Offset && a.Generation == b.Generation;
            case XrefEntryType.Compressed:
                return a.StreamNumber == b.StreamNumber && a.StreamIndex == b.StreamIndex
                    && _xref.TryGetValue(a.StreamNumber, out var sa) && other._xref.TryGetValue(a.StreamNumber, out var sb)
                    && sa.Type == XrefEntryType.InUse && sb.Type == XrefEntryType.InUse && sa.Offset == sb.Offset;
            default:
                return false;
        }
    }

    /// <summary>Loads a fresh copy of an object from the original file (used for change detection).</summary>
    public PdfObject? LoadOriginal(int number)
    {
        if (!_xref.TryGetValue(number, out var entry) || entry.Type == XrefEntryType.Free) return null;
        return LoadObject(number, entry);
    }

    /// <summary>Receives problems tolerated while reading (set by the document).</summary>
    internal DiagnosticSink? Diagnostics { get; set; }

    private PdfObject? LoadObject(int number, XrefEntry entry)
    {
        try
        {
            var obj = entry.Type switch
            {
                XrefEntryType.InUse => ReadIndirectObjectAt(entry.Offset, number, decrypt: true)?.Object,
                XrefEntryType.Compressed => ReadFromObjectStream(number, entry),
                _ => null,
            };
            if (obj == null && entry.Type is XrefEntryType.InUse or XrefEntryType.Compressed)
                Diagnostics?.Report(PdfDiagnosticSeverity.Warning, PdfDiagnosticCode.ObjectUnreadable,
                    $"Object {number} is not where the cross-reference table says; it is treated as null.", objectNumber: number);
            return obj;
        }
        catch (Exception ex) when (ex is PdfParseException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            Diagnostics?.Report(PdfDiagnosticSeverity.Warning, PdfDiagnosticCode.ObjectUnreadable,
                $"Object {number} cannot be read ({ex.Message}); it is treated as null.", objectNumber: number);
            return null;
        }
    }

    private sealed record IndirectObject(int Number, int Generation, PdfObject Object, long EndPosition);

    private sealed record ParsedHeader(int Number, int Generation, PdfObject Object, long AfterObject, long StreamStart);

    /// <summary>
    /// Parses at a file offset. Data in memory is parsed in place; data read from a file is parsed in a window that is
    /// enlarged (and the parse repeated) as long as the parse reaches the end of the window before the end of the file.
    /// <paramref name="parse"/> must not change the store: it may run more than once.
    /// </summary>
    private T ParseAt<T>(long offset, Func<PdfLexer, FileWindow, T> parse, int initialWindow = InitialWindow)
    {
        int size = _file.WindowLimit ?? initialWindow;
        while (true)
        {
            var window = _file.GetWindow(offset, size);
            var lexer = new PdfLexer(window.Buffer, window.Start, window.End - window.Start);
            bool canGrow = !window.ReachesEnd && size < MaxWindow;
            try
            {
                var result = parse(lexer, window);
                if (!canGrow || !lexer.HitEnd) return result;
            }
            catch (Exception ex) when (canGrow && lexer.HitEnd && ex is PdfParseException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                // the object continues after the window
            }
            size = (int)Math.Min((long)size * 4, MaxWindow);
        }
    }

    /// <summary>Reads <c>n g obj ... endobj</c> at the given offset.</summary>
    private IndirectObject? ReadIndirectObjectAt(long offset, int expectedNumber, bool decrypt)
    {
        if (offset < 0 || offset >= _file.Length) return null;
        var parsed = ParseAt(offset, (lexer, window) =>
        {
            if (!lexer.Next() || lexer.Kind != TokenKind.Integer) return null;
            int number = (int)lexer.IntValue;
            if (!lexer.Next() || lexer.Kind != TokenKind.Integer) return null;
            int generation = (int)lexer.IntValue;
            if (!lexer.Next() || !lexer.IsKeyword("obj")) return null;
            if (expectedNumber >= 0 && number != expectedNumber) return null;

            var parser = new PdfObjectParser(lexer, GetReference);
            var obj = parser.ParseObject() ?? PdfNull.Instance;
            long afterObject = window.Base + lexer.Position;
            long streamStart = lexer.Next() && lexer.IsKeyword("stream") && obj is PdfDictionary ? window.Base + lexer.Position : -1;
            return new ParsedHeader(number, generation, obj, afterObject, streamStart);
        });
        if (parsed == null) return null;

        var obj = parsed.Object;
        long end = parsed.AfterObject;
        if (parsed.StreamStart >= 0)
        {
            var stream = ReadStreamData((PdfDictionary)obj, parsed.StreamStart, out end);
            if (decrypt && Decryptor != null && parsed.Number != EncryptObjectNumber && !stream.IsType(PdfName.XRef))
                stream.SetRawBytes(Decryptor.DecryptStream(stream.RawBytes, parsed.Number, parsed.Generation, stream));
            obj = stream;
        }
        if (decrypt && Decryptor != null && parsed.Number != EncryptObjectNumber)
            DecryptStrings(obj, parsed.Number, parsed.Generation);
        return new IndirectObject(parsed.Number, parsed.Generation, obj, end);
    }

    private PdfStream ReadStreamData(PdfDictionary dict, long position, out long end)
    {
        // the "stream" keyword is followed by CRLF or LF (tolerate a lone CR)
        if (_file.ReadByte(position) == '\r') position++;
        if (_file.ReadByte(position) == '\n') position++;
        long length = -1;
        var lengthObj = dict[PdfName.Length];
        if (lengthObj is PdfNumber n) length = n.LongValue;
        else if (lengthObj is PdfReference r && r.Resolve() is PdfNumber rn) length = rn.LongValue;

        if (length < 0 || position + length > _file.Length || !IsAtEndStream(position + length))
        {
            long idx = _file.IndexOf("endstream"u8, position);
            if (idx < 0) idx = _file.Length;
            long e = idx;
            if (e > position && _file.ReadByte(e - 1) == '\n') e--;
            if (e > position && _file.ReadByte(e - 1) == '\r') e--;
            length = e - position;
        }
        if (length > int.MaxValue) throw new PdfParseException("Stream data larger than 2 GB", position);
        long dataEnd = position + length;
        end = ParseAt(dataEnd, (lexer, window) => lexer.Next() && lexer.IsKeyword("endstream") ? window.Base + lexer.Position : dataEnd, 256);
        // data in memory is shared, data in a file is read when it is needed
        return _file.TryGetMemory(position, (int)length) is { } memory
            ? new PdfStream(dict, memory)
            : new PdfStream(dict, new FileRegion(_file, position, (int)length));
    }

    private bool IsAtEndStream(long position)
    {
        var window = _file.GetWindow(position, 512);
        var rest = window.Buffer.AsSpan(window.Start, window.End - window.Start);
        int p = 0;
        while (p < rest.Length && PdfLexer.IsWhitespace(rest[p])) p++;
        rest = rest[p..];
        return rest.StartsWith("endstream"u8) || rest.StartsWith("endobj"u8);
    }

    private void DecryptStrings(PdfObject obj, int number, int generation)
    {
        switch (obj)
        {
            case PdfString s:
                s.Bytes = Decryptor!.DecryptString(s.Bytes, number, generation);
                break;
            case PdfArray a:
                foreach (var item in a) DecryptStrings(item, number, generation);
                break;
            case PdfDictionary d:
                // signature /Contents are not encrypted (ISO 32000-2, 7.6.2)
                bool isSignature = d.GetName(PdfName.Type) is { } t && (t == PdfName.Sig || t.Value == "DocTimeStamp") && d.ContainsKey(PdfName.ByteRange);
                foreach (var (k, v) in d)
                {
                    if (isSignature && k == PdfName.Contents) continue;
                    DecryptStrings(v, number, generation);
                }
                break;
        }
    }

    private sealed class ObjectStreamIndex
    {
        public required byte[] Data;
        public required int First;
        public required int[] Numbers;
        public required int[] Offsets;
    }

    private PdfObject? ReadFromObjectStream(int number, XrefEntry entry)
    {
        var index = GetObjectStreamIndex(entry.StreamNumber);
        if (index == null) return null;
        int i = entry.StreamIndex;
        if (i < 0 || i >= index.Numbers.Length || index.Numbers[i] != number)
            i = Array.IndexOf(index.Numbers, number);
        if (i < 0) return null;
        int start = index.First + index.Offsets[i];
        if (start < 0 || start >= index.Data.Length) return null;
        var lexer = new PdfLexer(index.Data, start);
        var parser = new PdfObjectParser(lexer, GetReference);
        return parser.ParseObject();
    }

    private ObjectStreamIndex? GetObjectStreamIndex(int streamNumber)
    {
        if (_objectStreams.TryGetValue(streamNumber, out var cached)) return cached;
        if (GetObject(streamNumber) is not PdfStream stream) return null;
        var data = stream.GetBytes();
        int n = stream.GetInt(PdfName.N);
        int first = stream.GetInt(PdfName.First);
        var lexer = new PdfLexer(data, 0, Math.Min(first, data.Length));
        var numbers = new List<int>(n);
        var offsets = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (!lexer.Next() || lexer.Kind != TokenKind.Integer) break;
            int num = (int)lexer.IntValue;
            if (!lexer.Next() || lexer.Kind != TokenKind.Integer) break;
            numbers.Add(num);
            offsets.Add((int)lexer.IntValue);
        }
        var result = new ObjectStreamIndex { Data = data, First = first, Numbers = numbers.ToArray(), Offsets = offsets.ToArray() };
        _objectStreams[streamNumber] = result;
        return result;
    }

    private int FindHeader(out string version)
    {
        version = "1.7";
        var window = _file.GetWindow(0, 1100);
        var head = window.Buffer.AsSpan(window.Start, Math.Min(window.End - window.Start, 1100));
        int idx = head[..Math.Min(head.Length, 1024)].IndexOf("%PDF-"u8);
        if (idx < 0) return 0;
        int p = idx + 5;
        int start = p;
        while (p < head.Length && (char.IsAsciiDigit((char)head[p]) || head[p] == '.')) p++;
        if (p > start) version = System.Text.Encoding.ASCII.GetString(head[start..p]);
        return idx;
    }
}
