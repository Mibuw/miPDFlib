using MiPdf.Objects;

namespace MiPdf.IO;

internal sealed partial class PdfObjectStore
{
    private const int ScanBlock = 4 << 20;
    private const int ScanContext = 64; // bytes before and after a match that the checks may look at

    /// <summary>
    /// Rebuilds the cross-reference information by scanning the whole file for
    /// <c>n g obj</c> headers and trailer dictionaries. Later definitions win.
    /// </summary>
    private void Repair()
    {
        if (_repaired) return;
        _repaired = true;
        var oldTrailer = Trailer;
        _xref.Clear();
        _objectStreams.Clear();

        var trailer = new PdfDictionary();
        var objectStreams = new List<int>();
        Scan("obj"u8.ToArray(), (window, objPos, min) =>
        {
            int pos = objPos + 3;
            if (pos < window.End && PdfLexer.IsRegular(window.Buffer[pos])) return; // e.g. "objects"
            if (objPos - 3 >= min && window.Buffer.AsSpan(objPos - 3, 3).SequenceEqual("end"u8)) return;
            int start = ParseObjectHeaderBackwards(window.Buffer, objPos, min, out int number, out int gen);
            if (start < 0) return;
            _xref[number] = new XrefEntry { Type = XrefEntryType.InUse, Offset = window.Base + start, Generation = gen };
        });

        // trailers (classic files): later trailers override earlier ones
        var trailerPositions = new List<long>();
        Scan("trailer"u8.ToArray(), (window, idx, _) => trailerPositions.Add(window.Base + idx + 7));
        foreach (long position in trailerPositions)
        {
            try
            {
                if (ParseAt(position, (lexer, _) => new PdfObjectParser(lexer, GetReference).ParseObject()) is PdfDictionary t)
                    foreach (var (k, v) in t) trailer[k] = v;
            }
            catch (PdfParseException) { }
        }

        // xref streams and object streams
        foreach (var (number, entry) in _xref.ToArray())
        {
            PdfObject? obj;
            try
            {
                obj = ReadIndirectObjectAt(entry.Offset, number, decrypt: false)?.Object;
            }
            catch (Exception ex) when (ex is PdfParseException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                continue;
            }
            if (obj is not PdfStream s) continue;
            var type = s.GetName(PdfName.Type);
            if (type == PdfName.XRef)
            {
                foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.ID, PdfName.Encrypt })
                    if (s[key] is { } v && !trailer.ContainsKey(key)) trailer[key] = v;
            }
            else if (type == PdfName.ObjStm) objectStreams.Add(number);
        }
        foreach (int stm in objectStreams)
        {
            try
            {
                var index = GetObjectStreamIndex(stm);
                if (index == null) continue;
                for (int i = 0; i < index.Numbers.Length; i++)
                    if (!_xref.ContainsKey(index.Numbers[i]))
                        _xref[index.Numbers[i]] = new XrefEntry { Type = XrefEntryType.Compressed, StreamNumber = stm, StreamIndex = i };
            }
            catch (Exception ex) when (ex is PdfParseException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
        }

        foreach (var (k, v) in oldTrailer)
            if (!trailer.ContainsKey(k)) trailer[k] = v;
        Trailer = trailer;
        Trailer.Remove(PdfName.Prev);
        Trailer.Remove(PdfName.XRefStm);
        Trailer.Remove(PdfName.Index);
        Trailer.Remove(PdfName.W);

        if (Trailer.Get(PdfName.Root) is not PdfDictionary)
        {
            Trailer.Remove(PdfName.Root);
            foreach (var number in _xref.Keys.OrderByDescending(n => n))
            {
                if (GetObject(number) is PdfDictionary d && d.IsType(PdfName.Catalog))
                {
                    Trailer[PdfName.Root] = GetReference(number, GetGeneration(number));
                    break;
                }
            }
        }
        if (Trailer.Get(PdfName.Root) is not PdfDictionary)
            throw new PdfParseException("The file is not a valid PDF document (no catalog found).");
        UsesXrefStreams = false;
        UpdateNextNumber();
        Trailer.Set(PdfName.Size, _nextNumber);
    }

    /// <summary>
    /// Calls <paramref name="match"/> for every occurrence of <paramref name="pattern"/> in file order, with the window,
    /// the index of the match in <see cref="FileWindow.Buffer"/> and the lowest index the checks may read. Data in memory
    /// is scanned in one piece, files in blocks with <see cref="ScanContext"/> bytes of overlap.
    /// </summary>
    private void Scan(byte[] pattern, Action<FileWindow, int, int> match)
    {
        long from = 0;
        while (from < _file.Length)
        {
            long windowStart = Math.Max(0, from - ScanContext);
            var window = _file.GetWindow(windowStart, (_file.WindowLimit ?? ScanBlock) + 2 * ScanContext);
            int min = window.Start;
            int searchFrom = window.Start + (int)(from - windowStart);
            // matches that start in the last ScanContext bytes are left to the next block (unless this is the end)
            int searchTo = window.ReachesEnd ? window.End : window.End - ScanContext;
            int pos = searchFrom;
            while (pos < searchTo)
            {
                int idx = window.Buffer.AsSpan(pos, window.End - pos).IndexOf(pattern);
                if (idx < 0 || pos + idx >= searchTo) break;
                match(window, pos + idx, min);
                pos += idx + pattern.Length;
            }
            if (window.ReachesEnd) break;
            from = window.Base + searchTo;
        }
    }

    /// <summary>Given the position of "obj", parses "n g " before it (not before <paramref name="min"/>). Returns the start index or -1.</summary>
    private static int ParseObjectHeaderBackwards(byte[] data, int objPos, int min, out int number, out int generation)
    {
        number = generation = 0;
        int p = objPos - 1;
        if (p < min || !PdfLexer.IsWhitespace(data[p])) return -1;
        while (p >= min && PdfLexer.IsWhitespace(data[p])) p--;
        int genEnd = p + 1;
        while (p >= min && char.IsAsciiDigit((char)data[p])) p--;
        int genStart = p + 1;
        if (genStart == genEnd || p < min || !PdfLexer.IsWhitespace(data[p])) return -1;
        while (p >= min && PdfLexer.IsWhitespace(data[p])) p--;
        int numEnd = p + 1;
        while (p >= min && char.IsAsciiDigit((char)data[p])) p--;
        int numStart = p + 1;
        if (numStart == numEnd || numEnd - numStart > 10 || genEnd - genStart > 5) return -1;
        if (p >= min && !PdfLexer.IsWhitespace(data[p]) && !PdfLexer.IsDelimiter(data[p])) return -1;
        if (!int.TryParse(data.AsSpan(numStart, numEnd - numStart), out number) ||
            !int.TryParse(data.AsSpan(genStart, genEnd - genStart), out generation)) return -1;
        return number > 0 ? numStart : -1;
    }
}
