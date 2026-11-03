using MiPdf.Objects;

namespace MiPdf.IO;

internal sealed partial class PdfObjectStore
{
    /// <summary>Reads the cross-reference sections starting at <c>startxref</c> and following /Prev.</summary>
    private void ReadXref()
    {
        long offset = FindStartXref();
        if (offset < 0) throw new PdfParseException("startxref not found");
        LastXrefOffset = offset;
        var visited = new HashSet<long>();
        int section = 0;
        PdfDictionary? newestTrailer = null;
        while (offset >= 0 && visited.Add(offset))
        {
            var trailer = ReadXrefSection(ref offset, section, isFirst: section == 0) ??
                          throw new PdfParseException("Invalid xref section", offset);
            if (newestTrailer == null) newestTrailer = trailer;
            else
            {
                // keep essential keys from older trailers if the newest lacks them
                foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.ID, PdfName.Encrypt })
                    if (!newestTrailer.ContainsKey(key) && trailer[key] is { } v) newestTrailer[key] = v;
            }
            offset = trailer.GetLong(PdfName.Prev, -1);
            section++;
        }
        Trailer = newestTrailer ?? throw new PdfParseException("No trailer found");
        Trailer.Remove(PdfName.Prev);
        Trailer.Remove(PdfName.XRefStm);
        UpdateNextNumber();
    }

    private void UpdateNextNumber()
    {
        int max = 0;
        foreach (var n in _xref.Keys) max = Math.Max(max, n);
        _nextNumber = Math.Max(Math.Max(max + 1, Trailer.GetInt(PdfName.Size)), _nextNumber);
    }

    private long FindStartXref()
    {
        long searchStart = Math.Max(0, _file.Length - 4096);
        var window = _file.GetWindow(searchStart, 4096);
        int idx = window.Buffer.AsSpan(window.Start, window.End - window.Start).LastIndexOf("startxref"u8);
        if (idx < 0) return -1;
        long position = window.Base + window.Start + idx + 9;
        return ParseAt(position, (lexer, _) => lexer.Next() && lexer.Kind == TokenKind.Integer ? lexer.IntValue : -1, 256);
    }

    /// <summary>True if startxref points to an xref section only when counted from the %PDF- header.</summary>
    private bool XrefIsRelativeToHeader()
    {
        long offset = FindStartXref();
        return offset >= 0 && !IsXrefAt(offset) && IsXrefAt(offset + HeaderOffset);
    }

    private bool IsXrefAt(long offset)
    {
        if (offset < 0 || offset >= _file.Length) return false;
        return ParseAt(offset, (lexer, _) =>
        {
            if (!lexer.Next()) return false;
            if (lexer.IsKeyword("xref")) return true;
            return lexer.Kind == TokenKind.Integer && lexer.Next() && lexer.Kind == TokenKind.Integer && lexer.Next() && lexer.IsKeyword("obj");
        }, 256);
    }

    /// <summary>Reads one xref section (table or stream). Offsets may be relative to the header.</summary>
    private PdfDictionary? ReadXrefSection(ref long offset, int section, bool isFirst)
    {
        foreach (long candidate in new[] { offset, offset + HeaderOffset })
        {
            if (candidate < 0 || candidate >= _file.Length) continue;
            var first = ParseAt(candidate, (lexer, _) => lexer.Next() ? (lexer.IsKeyword("xref"), lexer.Kind == TokenKind.Integer) : (false, false), 256);
            if (first.Item1)
            {
                if (isFirst) UsesXrefStreams = false;
                offset = candidate;
                var (trailer, entries) = ParseAt(candidate, (lexer, _) =>
                {
                    lexer.Next(); // xref
                    return ReadXrefTable(lexer, section);
                });
                foreach (var (number, entry) in entries) AddEntry(number, entry);
                if (trailer.GetLong(PdfName.XRefStm, -1) is var stm and >= 0)
                    ReadXrefStream(stm, section, hybrid: true);
                return trailer;
            }
            if (first.Item2)
            {
                var dict = ReadXrefStream(candidate, section, hybrid: false);
                if (dict != null)
                {
                    if (isFirst) UsesXrefStreams = true;
                    offset = candidate;
                    return dict;
                }
            }
        }
        return null;
    }

    /// <summary>Parses an xref table after the <c>xref</c> keyword; the entries are returned, not added (the parse may be repeated).</summary>
    private (PdfDictionary Trailer, List<(int Number, XrefEntry Entry)> Entries) ReadXrefTable(PdfLexer lexer, int section)
    {
        var entries = new List<(int, XrefEntry)>();
        while (true)
        {
            int save = lexer.Position;
            if (!lexer.Next()) throw new PdfParseException("Unexpected end of xref table");
            if (lexer.IsKeyword("trailer")) break;
            if (lexer.Kind != TokenKind.Integer)
            {
                lexer.Position = save;
                break;
            }
            long start = lexer.IntValue;
            if (!lexer.Next() || lexer.Kind != TokenKind.Integer) throw new PdfParseException("Invalid xref subsection", lexer.TokenStart);
            long count = lexer.IntValue;
            for (long i = 0; i < count; i++)
            {
                if (!lexer.Next() || lexer.Kind != TokenKind.Integer) throw new PdfParseException("Invalid xref entry", lexer.TokenStart);
                long entryOffset = lexer.IntValue;
                if (!lexer.Next() || lexer.Kind != TokenKind.Integer) throw new PdfParseException("Invalid xref entry", lexer.TokenStart);
                int gen = (int)lexer.IntValue;
                if (!lexer.Next() || lexer.Kind != TokenKind.Keyword) throw new PdfParseException("Invalid xref entry", lexer.TokenStart);
                bool inUse = lexer.Text == "n";
                long number = start + i;
                // common producer bug: subsection "1 n" while the first entry is the free head of object 0
                if (i == 0 && start == 1 && !inUse && gen == 65535 && entryOffset == 0)
                {
                    start = 0;
                    number = 0;
                }
                entries.Add(((int)number, new XrefEntry
                {
                    Type = inUse ? XrefEntryType.InUse : XrefEntryType.Free,
                    Offset = entryOffset,
                    Generation = gen,
                    Section = section,
                }));
            }
        }
        var parser = new PdfObjectParser(lexer, GetReference);
        var trailer = parser.ParseObject() as PdfDictionary ?? throw new PdfParseException("Invalid trailer", lexer.TokenStart);
        return (trailer, entries);
    }

    private void AddEntry(int number, XrefEntry entry, bool hybrid = false)
    {
        if (number < 0) return;
        if (_xref.TryGetValue(number, out var existing))
        {
            // newer sections win; entries of a hybrid xref stream may override free entries of the same section
            bool overrideFree = hybrid && existing.Section == entry.Section && existing.Type == XrefEntryType.Free;
            if (!overrideFree) return;
        }
        if (number == 0 && entry.Type != XrefEntryType.Free) return;
        _xref[number] = entry;
    }

    private PdfDictionary? ReadXrefStream(long offset, int section, bool hybrid)
    {
        var indirect = ReadIndirectObjectAt(offset, -1, decrypt: false);
        if (indirect?.Object is not PdfStream stream || stream.GetName(PdfName.Type) != PdfName.XRef) return null;
        var w = stream.GetArray(PdfName.W);
        if (w == null || w.Count < 3) throw new PdfParseException("Invalid /W in xref stream", offset);
        int w0 = w.GetInt(0), w1 = w.GetInt(1), w2 = w.GetInt(2);
        int rowLength = w0 + w1 + w2;
        if (rowLength <= 0) throw new PdfParseException("Invalid /W in xref stream", offset);
        var data = stream.GetBytes();
        var index = stream.GetArray(PdfName.Index) ?? PdfArray.FromNumbers(0, stream.GetInt(PdfName.Size));
        int pos = 0;
        for (int s = 0; s + 1 < index.Count; s += 2)
        {
            int start = index.GetInt(s), count = index.GetInt(s + 1);
            for (int i = 0; i < count && pos + rowLength <= data.Length; i++, pos += rowLength)
            {
                long type = w0 == 0 ? 1 : ReadField(data, pos, w0);
                long f2 = ReadField(data, pos + w0, w1);
                long f3 = ReadField(data, pos + w0 + w1, w2);
                var entry = type switch
                {
                    0 => new XrefEntry { Type = XrefEntryType.Free, Generation = (int)f3, Section = section },
                    1 => new XrefEntry { Type = XrefEntryType.InUse, Offset = f2, Generation = (int)f3, Section = section },
                    2 => new XrefEntry { Type = XrefEntryType.Compressed, StreamNumber = (int)f2, StreamIndex = (int)f3, Section = section },
                    _ => null, // unknown types are treated as null references
                };
                if (entry != null) AddEntry(start + i, entry, hybrid);
            }
        }
        return stream;
    }

    private static long ReadField(byte[] data, int pos, int width)
    {
        long v = 0;
        for (int i = 0; i < width; i++) v = (v << 8) | data[pos + i];
        return v;
    }
}
