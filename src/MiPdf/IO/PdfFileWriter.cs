using System.Security.Cryptography;
using System.Text;
using MiPdf.Filters;
using MiPdf.Objects;

namespace MiPdf.IO;

/// <summary>Options controlling how a document is written.</summary>
public sealed class PdfSaveOptions
{
    /// <summary>
    /// Appends changes to the original file instead of rewriting it (ISO 32000-2, 7.5.6).
    /// Required to preserve existing digital signatures. Only valid for documents opened from a file.
    /// </summary>
    public bool Incremental { get; set; }

    /// <summary>
    /// Packs non-stream objects into compressed object streams and writes a cross-reference stream
    /// (PDF 1.5+). Produces considerably smaller files. Ignored for incremental saves.
    /// </summary>
    public bool UseObjectStreams { get; set; }

    /// <summary>Flate-compresses streams that are stored without any filter (full saves only).</summary>
    public bool CompressUncompressedStreams { get; set; } = true;

    /// <summary>Overrides the PDF version written into the header, e.g. "1.7" or "2.0".</summary>
    public string? Version { get; set; }
}

/// <summary>Writes the objects of a <see cref="PdfObjectStore"/> as a PDF file.</summary>
internal sealed class PdfFileWriter
{
    private const int ObjectsPerObjectStream = 100;

    private readonly PdfObjectStore _store;
    private readonly PdfSaveOptions _options;
    private readonly IObjectEncryptor? _encryptor;

    public PdfFileWriter(PdfObjectStore store, PdfSaveOptions options, IObjectEncryptor? encryptor)
    {
        _store = store;
        _options = options;
        _encryptor = encryptor;
    }

    // ---------------------------------------------------------------- full save

    public void WriteFull(Stream stream, string version)
    {
        var trailerSource = _store.Trailer;
        var order = new List<PdfObject>();
        var numbers = new Dictionary<PdfObject, int>(ReferenceEqualityComparer.Instance);
        var oldToNew = new Dictionary<int, int>();
        CollectReachable(trailerSource, order, numbers, oldToNew);

        int encryptNumber = trailerSource[PdfName.Encrypt] is PdfReference er && oldToNew.TryGetValue(er.ObjectNumber, out var en) ? en : -1;
        int xrefStreamNumber = -1;

        (int, int)? MapReference(PdfReference r)
        {
            if (!ReferenceEquals(r.Resolver, _store))
                throw new PdfException($"Reference {r.ObjectNumber} {r.Generation} R belongs to another document. Use PdfDocument.ImportObject to copy objects between documents.");
            return oldToNew.TryGetValue(r.ObjectNumber, out var n) ? (n, 0) : null;
        }

        (int, int) IndirectStream(PdfStream s) => (numbers[s], 0);

        var serializer = new PdfObjectSerializer
        {
            MapReference = MapReference,
            IndirectStream = IndirectStream,
            Encryptor = _encryptor,
            IsUnencryptedObject = n => n == encryptNumber || n == xrefStreamNumber,
        };

        if (_options.CompressUncompressedStreams)
        {
            foreach (var obj in order)
                if (obj is PdfStream s && s.GetFilters().Count == 0 && s.RawBytes.Length > 64 && !IsMetadata(s))
                    s.SetBytes(s.RawBytes, compress: true);
        }

        var output = new PdfOutput(stream);
        WriteHeader(output, version);
        int size = order.Count + 1;
        var offsets = new long[size];
        var compressed = new (int Stream, int Index)[size];

        var trailer = new PdfDictionary();
        CopyTrailerEntries(trailerSource, trailer);

        if (_options.UseObjectStreams)
        {
            var packable = new List<int>();
            for (int i = 0; i < order.Count; i++)
            {
                int number = i + 1;
                var obj = order[i];
                if (obj is PdfStream || number == encryptNumber) continue;
                packable.Add(number);
            }
            // write streams and unpackable objects directly
            var packSet = new HashSet<int>(packable);
            for (int i = 0; i < order.Count; i++)
            {
                int number = i + 1;
                if (packSet.Contains(number)) continue;
                offsets[number] = output.Position;
                serializer.WriteIndirect(output, number, 0, order[i]);
            }
            // object streams
            var plain = new PdfObjectSerializer { MapReference = MapReference, IndirectStream = IndirectStream };
            var objStmNumbers = new List<int>();
            int nextNumber = size;
            for (int chunk = 0; chunk < packable.Count; chunk += ObjectsPerObjectStream)
            {
                var members = packable.Skip(chunk).Take(ObjectsPerObjectStream).ToList();
                int stmNumber = nextNumber++;
                objStmNumbers.Add(stmNumber);
                var header = new StringBuilder();
                using var body = new MemoryStream();
                var bodyOut = new PdfOutput(body);
                for (int k = 0; k < members.Count; k++)
                {
                    header.Append(members[k]).Append(' ').Append(bodyOut.Position).Append(' ');
                    plain.Write(order[members[k] - 1], bodyOut);
                    bodyOut.WriteByte((byte)'\n');
                    compressed[members[k]] = (stmNumber, k);
                }
                var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
                var content = new byte[headerBytes.Length + body.Length];
                headerBytes.CopyTo(content, 0);
                body.ToArray().CopyTo(content, headerBytes.Length);
                var objStm = new PdfStream(content);
                objStm[PdfName.Type] = PdfName.ObjStm;
                objStm.Set(PdfName.N, members.Count);
                objStm.Set(PdfName.First, headerBytes.Length);
                Array.Resize(ref offsets, nextNumber + 1);
                Array.Resize(ref compressed, nextNumber + 1);
                offsets[stmNumber] = output.Position;
                serializer.WriteIndirect(output, stmNumber, 0, objStm);
            }
            int xrefNumber = nextNumber++;
            xrefStreamNumber = xrefNumber;
            Array.Resize(ref offsets, nextNumber);
            Array.Resize(ref compressed, nextNumber);
            offsets[xrefNumber] = output.Position;
            var entries = new List<(int Number, int Type, long Field2, int Field3)>();
            entries.Add((0, 0, 0, 65535));
            for (int n = 1; n < nextNumber; n++)
            {
                if (compressed[n].Stream > 0) entries.Add((n, 2, compressed[n].Stream, compressed[n].Index));
                else entries.Add((n, 1, offsets[n], 0));
            }
            trailer.Set(PdfName.Size, nextNumber);
            WriteXrefStream(output, serializer, trailer, entries, xrefNumber, prev: -1);
        }
        else
        {
            for (int i = 0; i < order.Count; i++)
            {
                offsets[i + 1] = output.Position;
                serializer.WriteIndirect(output, i + 1, 0, order[i]);
            }
            long xrefOffset = output.Position;
            output.WriteLine("xref").WriteLine($"0 {size}").Write("0000000000 65535 f\r\n");
            for (int n = 1; n < size; n++) output.Write($"{offsets[n]:D10} 00000 n\r\n");
            trailer.Set(PdfName.Size, size);
            output.WriteLine("trailer");
            serializer.Write(trailer, output);
            output.Write($"\nstartxref\n{xrefOffset}\n%%EOF\n");
        }
    }

    private static bool IsMetadata(PdfStream s) => s.GetName(PdfName.Type) == PdfName.Metadata;

    private void CopyTrailerEntries(PdfDictionary source, PdfDictionary target)
    {
        foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.Encrypt })
            if (source[key] is { } v) target[key] = v;
        target[PdfName.ID] = BuildId(source);
    }

    private static PdfArray BuildId(PdfDictionary source)
    {
        var seed = Encoding.UTF8.GetBytes($"{DateTime.UtcNow.Ticks}-{Guid.NewGuid()}");
        var fresh = new PdfString(MD5.HashData(seed), hex: true);
        if (source.GetArray(PdfName.ID) is { Count: >= 1 } existing && existing.Get(0) is PdfString first)
            return new PdfArray(first, fresh);
        return new PdfArray(fresh, fresh);
    }

    /// <summary>Breadth-first traversal from the trailer; assigns new object numbers in order.</summary>
    private void CollectReachable(PdfDictionary trailer, List<PdfObject> order, Dictionary<PdfObject, int> numbers, Dictionary<int, int> oldToNew)
    {
        var queue = new Queue<PdfObject>();

        void Visit(PdfObject value)
        {
            switch (value)
            {
                case PdfReference r:
                    if (!ReferenceEquals(r.Resolver, _store)) return; // reported during serialization
                    if (oldToNew.ContainsKey(r.ObjectNumber)) return;
                    var target = _store.GetObject(r.ObjectNumber);
                    if (target == null) return;
                    if (numbers.TryGetValue(target, out var already))
                    {
                        oldToNew[r.ObjectNumber] = already; // same object reachable under two numbers
                        return;
                    }
                    order.Add(target);
                    numbers[target] = order.Count;
                    oldToNew[r.ObjectNumber] = order.Count;
                    queue.Enqueue(target);
                    break;
                case PdfStream s: // direct stream: must become indirect
                    if (numbers.ContainsKey(s)) return;
                    order.Add(s);
                    numbers[s] = order.Count;
                    queue.Enqueue(s);
                    break;
                case PdfDictionary d:
                    foreach (var (_, v) in d) Visit(v);
                    break;
                case PdfArray a:
                    foreach (var v in a) Visit(v);
                    break;
            }
        }

        foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.Encrypt })
            if (trailer[key] is { } v) Visit(v);
        while (queue.Count > 0)
        {
            var obj = queue.Dequeue();
            switch (obj)
            {
                case PdfDictionary d:
                    foreach (var (_, v) in d) Visit(v);
                    break;
                case PdfArray a:
                    foreach (var v in a) Visit(v);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- incremental save

    public void WriteIncremental(Stream stream)
    {
        if (!_store.IsFromFile) throw new PdfException("Incremental saving requires a document opened from a file.");
        var original = _store.OriginalData;
        var output = new PdfOutput(stream);
        output.Write(original);
        if (original.Length > 0 && original[^1] != '\n' && original[^1] != '\r') output.WriteByte((byte)'\n');

        int encryptNumber = _store.Trailer[PdfName.Encrypt] is PdfReference er ? er.ObjectNumber : -1;
        var directStreams = new Dictionary<PdfStream, PdfReference>(ReferenceEqualityComparer.Instance);
        var changed = FindChangedObjects(directStreams);
        int xrefNumber = _options.UseObjectStreams || _store.UsesXrefStreams ? _store.AllocateNumber() : -1;

        (int, int)? MapReference(PdfReference r)
        {
            if (!ReferenceEquals(r.Resolver, _store))
                throw new PdfException($"Reference {r.ObjectNumber} {r.Generation} R belongs to another document. Use PdfDocument.ImportObject to copy objects between documents.");
            return _store.GetObject(r.ObjectNumber) != null ? (r.ObjectNumber, r.Generation) : null;
        }

        var serializer = new PdfObjectSerializer
        {
            MapReference = MapReference,
            IndirectStream = s => directStreams.TryGetValue(s, out var r) ? (r.ObjectNumber, r.Generation) : throw new PdfException("Unregistered direct stream."),
            Encryptor = _encryptor,
            IsUnencryptedObject = n => n == encryptNumber || n == xrefNumber,
        };

        var offsets = new SortedDictionary<int, (long Offset, int Generation, bool Free)>();
        foreach (int number in changed)
        {
            if (number == xrefNumber) continue;
            var obj = _store.GetObject(number)!;
            int gen = _store.GetGeneration(number);
            offsets[number] = (output.Position, gen, false);
            serializer.WriteIndirect(output, number, gen, obj);
        }
        foreach (int number in _store.DeletedObjectNumbers)
            if (_store.ExistsInFile(number)) offsets[number] = (0, Math.Min(_store.GetGeneration(number) + 1, 65535), true);

        var trailer = new PdfDictionary();
        CopyTrailerEntries(_store.Trailer, trailer);
        long prev = _store.LastXrefOffset;

        if (xrefNumber > 0)
        {
            var entries = offsets.Select(kv => (kv.Key, kv.Value.Free ? 0 : 1, kv.Value.Free ? 0L : kv.Value.Offset, kv.Value.Generation)).ToList();
            entries.Add((xrefNumber, 1, output.Position, 0));
            trailer.Set(PdfName.Size, _store.Size);
            WriteXrefStream(output, serializer, trailer, entries, xrefNumber, prev);
        }
        else
        {
            long xrefOffset = output.Position;
            output.WriteLine("xref");
            var all = offsets.ToList();
            if (!offsets.ContainsKey(0)) output.Write("0 1\r\n0000000000 65535 f\r\n");
            int i = 0;
            while (i < all.Count)
            {
                int j = i;
                while (j + 1 < all.Count && all[j + 1].Key == all[j].Key + 1) j++;
                output.Write($"{all[i].Key} {j - i + 1}\r\n");
                for (int k = i; k <= j; k++)
                {
                    var e = all[k].Value;
                    output.Write(e.Free ? $"0000000000 {e.Generation:D5} f\r\n" : $"{e.Offset:D10} {e.Generation:D5} n\r\n");
                }
                i = j + 1;
            }
            trailer.Set(PdfName.Size, _store.Size);
            trailer.Set(PdfName.Prev, prev);
            output.WriteLine("trailer");
            serializer.Write(trailer, output);
            output.Write($"\nstartxref\n{xrefOffset}\n%%EOF\n");
        }
    }

    /// <summary>
    /// Determines objects to write in an incremental update: new objects and loaded objects whose
    /// serialization differs from the original file. Direct streams are registered as new objects.
    /// </summary>
    private List<int> FindChangedObjects(Dictionary<PdfStream, PdfReference> directStreams)
    {
        var plain = new PdfObjectSerializer
        {
            IndirectStream = s => (0, 0),
            MapReference = r => (r.ObjectNumber, r.Generation),
        };
        var result = new List<int>();
        var candidates = _store.LoadedObjectNumbers.ToList();
        foreach (int number in candidates)
        {
            var current = _store.GetObject(number);
            if (current == null) continue;
            bool changed = _store.IsAdded(number);
            if (!changed)
            {
                var original = _store.LoadOriginal(number);
                changed = original == null || !Serialize(plain, number, current).AsSpan().SequenceEqual(Serialize(plain, number, original));
            }
            if (changed) result.Add(number);
        }
        // register direct streams contained in changed objects (they become new indirect objects)
        var pending = new Queue<int>(result);
        while (pending.Count > 0)
        {
            var obj = _store.GetObject(pending.Dequeue());
            foreach (var s in FindDirectStreams(obj))
            {
                if (directStreams.ContainsKey(s)) continue;
                var r = _store.Add(s);
                directStreams[s] = r;
                result.Add(r.ObjectNumber);
                pending.Enqueue(r.ObjectNumber);
            }
        }
        result.Sort();
        return result;
    }

    private static IEnumerable<PdfStream> FindDirectStreams(PdfObject? root)
    {
        var stack = new Stack<PdfObject>();
        void PushChildren(PdfObject? o)
        {
            if (o is PdfDictionary d) foreach (var (_, v) in d) stack.Push(v);
            else if (o is PdfArray a) foreach (var v in a) stack.Push(v);
        }
        PushChildren(root);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (o is PdfStream s) yield return s;
            else PushChildren(o);
        }
    }

    private static byte[] Serialize(PdfObjectSerializer serializer, int number, PdfObject obj)
    {
        using var ms = new MemoryStream();
        serializer.WriteIndirect(new PdfOutput(ms), number, 0, obj);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- helpers

    private static void WriteHeader(PdfOutput output, string version)
    {
        output.Write($"%PDF-{version}\n");
        output.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
    }

    private static void WriteXrefStream(PdfOutput output, PdfObjectSerializer serializer, PdfDictionary trailer,
        List<(int Number, int Type, long Field2, int Field3)> entries, int xrefNumber, long prev)
    {
        entries.Sort((a, b) => a.Number.CompareTo(b.Number));
        long maxField2 = entries.Count == 0 ? 0 : entries.Max(e => e.Field2);
        int w1 = BytesNeeded(maxField2);
        int w2 = Math.Max(1, BytesNeeded(entries.Count == 0 ? 0 : entries.Max(e => e.Field3)));
        var index = new PdfArray();
        using var data = new MemoryStream();
        int i = 0;
        while (i < entries.Count)
        {
            int j = i;
            while (j + 1 < entries.Count && entries[j + 1].Number == entries[j].Number + 1) j++;
            index.Add(new PdfNumber(entries[i].Number));
            index.Add(new PdfNumber(j - i + 1));
            for (int k = i; k <= j; k++)
            {
                data.WriteByte((byte)entries[k].Type);
                WriteField(data, entries[k].Field2, w1);
                WriteField(data, entries[k].Field3, w2);
            }
            i = j + 1;
        }
        var xref = new PdfStream(data.ToArray());
        foreach (var (k, v) in trailer) xref[k] = v;
        xref[PdfName.Type] = PdfName.XRef;
        xref[PdfName.W] = PdfArray.FromNumbers(1, w1, w2);
        xref[PdfName.Index] = index;
        if (prev >= 0) xref.Set(PdfName.Prev, prev);
        long offset = output.Position;
        serializer.WriteIndirect(output, xrefNumber, 0, xref);
        output.Write($"startxref\n{offset}\n%%EOF\n");
    }

    private static int BytesNeeded(long value)
    {
        int n = 1;
        while (value > 0xFF)
        {
            value >>= 8;
            n++;
        }
        return n;
    }

    private static void WriteField(Stream s, long value, int width)
    {
        for (int i = width - 1; i >= 0; i--) s.WriteByte((byte)(value >> (8 * i)));
    }
}
