using MiPdf.Objects;

namespace MiPdf.IO;

internal sealed partial class PdfObjectStore
{
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
        int pos = 0;
        while (true)
        {
            int idx = _data.AsSpan(pos).IndexOf("obj"u8);
            if (idx < 0) break;
            int objPos = pos + idx;
            pos = objPos + 3;
            if (pos < _data.Length && PdfLexer.IsRegular(_data[pos])) continue; // e.g. "objects"
            if (objPos >= 3 && _data.AsSpan(objPos - 3, 3).SequenceEqual("end"u8)) continue;
            int start = ParseObjectHeaderBackwards(objPos, out int number, out int gen);
            if (start < 0) continue;
            _xref[number] = new XrefEntry { Type = XrefEntryType.InUse, Offset = start, Generation = gen };
        }

        // trailers (classic files): later trailers override earlier ones
        pos = 0;
        while (true)
        {
            int idx = _data.AsSpan(pos).IndexOf("trailer"u8);
            if (idx < 0) break;
            pos += idx + 7;
            var lexer = new PdfLexer(_data, pos);
            try
            {
                if (new PdfObjectParser(lexer, GetReference).ParseObject() is PdfDictionary t)
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

    /// <summary>Given the position of "obj", parses "n g " before it. Returns the start offset or -1.</summary>
    private int ParseObjectHeaderBackwards(int objPos, out int number, out int generation)
    {
        number = generation = 0;
        int p = objPos - 1;
        if (p < 0 || !PdfLexer.IsWhitespace(_data[p])) return -1;
        while (p >= 0 && PdfLexer.IsWhitespace(_data[p])) p--;
        int genEnd = p + 1;
        while (p >= 0 && char.IsAsciiDigit((char)_data[p])) p--;
        int genStart = p + 1;
        if (genStart == genEnd || p < 0 || !PdfLexer.IsWhitespace(_data[p])) return -1;
        while (p >= 0 && PdfLexer.IsWhitespace(_data[p])) p--;
        int numEnd = p + 1;
        while (p >= 0 && char.IsAsciiDigit((char)_data[p])) p--;
        int numStart = p + 1;
        if (numStart == numEnd || numEnd - numStart > 10 || genEnd - genStart > 5) return -1;
        if (p >= 0 && !PdfLexer.IsWhitespace(_data[p]) && !PdfLexer.IsDelimiter(_data[p])) return -1;
        if (!int.TryParse(_data.AsSpan(numStart, numEnd - numStart), out number) ||
            !int.TryParse(_data.AsSpan(genStart, genEnd - genStart), out generation)) return -1;
        return number > 0 ? numStart : -1;
    }
}
