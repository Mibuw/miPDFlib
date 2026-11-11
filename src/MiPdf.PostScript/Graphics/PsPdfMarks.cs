using MiPdf.Objects;

namespace MiPdf.PostScript.Graphics;

/// <summary>
/// What pdfmark adds to the document beyond single objects: bookmarks (built when the job ends, as /Count refers to
/// the bookmarks that follow), named destinations, article threads, embedded files, named objects and the entries of
/// PAGES pdfmarks.
/// </summary>
internal sealed class PsPdfMarks
{
    private const int MaxEntries = 100_000;

    private readonly PdfDocument _document;

    public PsPdfMarks(PdfDocument document) => _document = document;

    // ------------------------------------------------------------------ named objects

    /// <summary>Named objects (<c>{name}</c>) by name.</summary>
    public Dictionary<string, PdfReference> Named { get; } = new();

    /// <summary>The data of named streams, written when the job ends (or at CLOSE).</summary>
    public Dictionary<PdfReference, MemoryStream> StreamData { get; } = new();

    /// <summary>A named object; created as an empty dictionary when it is used before it is defined.</summary>
    public PdfReference NamedObject(string name)
    {
        if (!Named.TryGetValue(name, out var reference))
        {
            if (Named.Count >= MaxEntries) throw new Interpreter.PsError("limitcheck");
            Named[name] = reference = _document.AddObject(new PdfDictionary());
        }
        return reference;
    }

    /// <summary>Defines a named object (OBJ): an existing one keeps its reference, but gets the new type.</summary>
    public PdfReference DefineNamed(string name, PdfObject value)
    {
        var reference = NamedObject(name);
        if (reference.Resolve()?.GetType() != value.GetType()) _document.ReplaceObject(reference, value);
        return reference;
    }

    /// <summary>Writes the data of a named stream.</summary>
    public void CloseStream(PdfReference reference)
    {
        if (!StreamData.Remove(reference, out var data) || reference.Resolve() is not PdfStream stream) return;
        // with a /Filter from the program, the data is encoded already
        if (stream.ContainsKey(PdfName.Filter)) stream.SetRawBytes(data.ToArray());
        else stream.SetBytes(data.ToArray());
    }

    /// <summary>The document information dictionary (created when needed).</summary>
    public PdfDictionary Info()
    {
        if (_document.Info.Dictionary is { } info) return info;
        info = new PdfDictionary();
        _document.Trailer[PdfName.Info] = _document.AddObject(info);
        return info;
    }

    // ------------------------------------------------------------------ bookmarks

    private sealed class OutlineNode
    {
        public PdfDictionary Dict = new();
        public readonly List<OutlineNode> Children = new();
        public bool Closed;
    }

    private readonly OutlineNode _outlines = new();
    private readonly List<(OutlineNode Node, int Remaining)> _open = new();
    private int _outlineCount;

    /// <summary>
    /// Adds a bookmark (OUT): it is a child of the last bookmark whose /Count is not used up yet, and the next
    /// |count| bookmarks become its children (closed for a negative count).
    /// </summary>
    public void AddOutline(PdfDictionary item, int count)
    {
        if (++_outlineCount > MaxEntries) return;
        while (_open.Count > 0 && _open[^1].Remaining <= 0) _open.RemoveAt(_open.Count - 1);
        var parent = _outlines;
        if (_open.Count > 0)
        {
            parent = _open[^1].Node;
            _open[^1] = (parent, _open[^1].Remaining - 1);
        }
        var node = new OutlineNode { Dict = item, Closed = count < 0 };
        parent.Children.Add(node);
        if (count != 0) _open.Add((node, Math.Min(Math.Abs(count), MaxEntries)));
    }

    // ------------------------------------------------------------------ names, threads, pages

    private readonly List<(byte[] Name, PdfObject Value)> _destinations = new();
    private readonly List<(byte[] Name, PdfObject Value)> _embeddedFiles = new();
    private readonly Dictionary<string, (PdfDictionary Info, List<(PdfReference Page, PdfArray Rect)> Beads)> _threads = new();
    private readonly List<string> _threadOrder = new();

    /// <summary>The entries of PAGES pdfmarks: given to every page that does not have them.</summary>
    public PdfDictionary PagesEntries { get; } = new();

    public void AddDestination(byte[] name, PdfObject value)
    {
        if (_destinations.Count < MaxEntries) _destinations.Add((name, value));
    }

    public void AddEmbeddedFile(byte[] name, PdfObject value)
    {
        if (_embeddedFiles.Count < MaxEntries) _embeddedFiles.Add((name, value));
    }

    /// <summary>A bead of an article thread (ARTICLE); the thread is named by its title, its info comes from the first bead.</summary>
    public void AddBead(string title, PdfDictionary info, PdfReference page, PdfArray rect)
    {
        if (!_threads.TryGetValue(title, out var thread))
        {
            if (_threads.Count >= MaxEntries) return;
            _threads[title] = thread = (info, new List<(PdfReference, PdfArray)>());
            _threadOrder.Add(title);
        }
        if (thread.Beads.Count < MaxEntries) thread.Beads.Add((page, rect));
    }

    // ------------------------------------------------------------------ the end of the job

    /// <summary>Writes bookmarks, name trees, threads, PAGES entries and the data of named streams.</summary>
    public void Finish()
    {
        foreach (var reference in StreamData.Keys.ToList()) CloseStream(reference);
        var catalog = _document.Catalog;

        if (_outlines.Children.Count > 0)
        {
            var root = new PdfDictionary { [PdfName.Type] = PdfName.Outlines };
            var rootReference = _document.AddObject(root);
            int visible = LinkOutlines(_outlines, root, rootReference);
            if (visible > 0) root[PdfName.Count] = new PdfNumber(visible);
            catalog[PdfName.Outlines] = rootReference;
        }

        if (_destinations.Count > 0 || _embeddedFiles.Count > 0)
        {
            var names = catalog.GetDictionary(PdfName.Get("Names")) ?? new PdfDictionary();
            // later definitions of a name replace earlier ones
            static IEnumerable<(byte[], PdfObject)> Last(List<(byte[] Name, PdfObject Value)> entries) =>
                entries.GroupBy(e => Convert.ToHexString(e.Name)).Select(g => (g.Last().Name, g.Last().Value));
            if (_destinations.Count > 0) names[PdfName.Get("Dests")] = NameTree.Write(Last(_destinations));
            if (_embeddedFiles.Count > 0) names[PdfName.Get("EmbeddedFiles")] = NameTree.Write(Last(_embeddedFiles));
            catalog[PdfName.Get("Names")] = names;
        }

        if (_threadOrder.Count > 0)
        {
            var threads = new PdfArray();
            foreach (string title in _threadOrder)
            {
                var (info, beads) = _threads[title];
                if (beads.Count == 0) continue;
                var thread = new PdfDictionary { [PdfName.Type] = PdfName.Get("Thread"), [PdfName.Get("I")] = info };
                var threadReference = _document.AddObject(thread);
                var beadDicts = beads.Select(b => new PdfDictionary
                {
                    [PdfName.Type] = PdfName.Get("Bead"), [PdfName.Get("T")] = threadReference, [PdfName.P] = b.Page, [PdfName.Get("R")] = b.Rect,
                }).ToList();
                var references = beadDicts.Select(_document.AddObject).ToList();
                for (int i = 0; i < beadDicts.Count; i++)
                {
                    beadDicts[i][PdfName.Get("N")] = references[(i + 1) % references.Count];
                    beadDicts[i][PdfName.Get("V")] = references[(i + references.Count - 1) % references.Count];
                    if (beads[i].Page.Resolve() is PdfDictionary page)
                    {
                        var list = page.GetArray(PdfName.Get("B")) ?? new PdfArray();
                        list.Add(references[i]);
                        page[PdfName.Get("B")] = list;
                    }
                }
                thread[PdfName.Get("F")] = references[0];
                threads.Add(threadReference);
            }
            if (threads.Count > 0) catalog[PdfName.Get("Threads")] = threads;
        }

        if (PagesEntries.Count > 0)
            for (int i = 1; i <= _document.PageCount; i++)
            {
                var page = _document.GetPage(i).Dictionary;
                foreach (var (key, value) in PagesEntries)
                    if (!page.ContainsKey(key)) page[key] = value;
            }
    }

    /// <summary>Links the children of a bookmark; returns the number of descendants visible when it is open.</summary>
    private int LinkOutlines(OutlineNode node, PdfDictionary dict, PdfReference reference)
    {
        if (node.Children.Count == 0) return 0;
        var references = node.Children.Select(c => _document.AddObject(c.Dict)).ToList();
        int visible = 0;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            child.Dict[PdfName.Parent] = reference;
            if (i > 0) child.Dict[PdfName.Prev] = references[i - 1];
            if (i + 1 < references.Count) child.Dict[PdfName.Next] = references[i + 1];
            int below = LinkOutlines(child, child.Dict, references[i]);
            if (below > 0) child.Dict[PdfName.Count] = new PdfNumber(child.Closed ? -below : below);
            visible += 1 + (child.Closed ? 0 : below);
        }
        dict[PdfName.First] = references[0];
        dict[PdfName.Get("Last")] = references[^1];
        return visible;
    }
}
