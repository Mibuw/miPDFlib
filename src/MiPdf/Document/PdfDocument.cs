using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf;

/// <summary>
/// A PDF document. Use <see cref="Create"/> for new documents or <see cref="Open(string, string?)"/> to load
/// existing files. Objects of opened files are loaded lazily; unchanged objects are written back as they are.
/// </summary>
public sealed partial class PdfDocument : IDisposable
{
    private List<PdfPage>? _pages;
    private bool _pageTreeDirty;
    private readonly PdfDocumentInfo _info;

    private PdfDocument(PdfObjectStore store)
    {
        Store = store;
        _info = new PdfDocumentInfo(this);
    }

    internal PdfObjectStore Store { get; }

    /// <summary>The trailer dictionary (contains /Root, /Info, /ID, /Encrypt).</summary>
    public PdfDictionary Trailer => Store.Trailer;

    /// <summary>The document catalog (/Root).</summary>
    public PdfDictionary Catalog =>
        Trailer.GetDictionary(PdfName.Root) ?? throw new PdfException("The document has no catalog.");

    /// <summary>Document information (title, author, ...).</summary>
    public PdfDocumentInfo Info => _info;

    /// <summary>PDF version of the document, e.g. "1.7". Taken from the header or the catalog's /Version.</summary>
    public string Version { get; set; } = "1.7";

    /// <summary>True if the cross-reference information of the file was damaged and has been reconstructed.</summary>
    public bool WasRepaired => Store.WasRepaired;

    /// <summary>True if the document was opened from existing PDF data.</summary>
    public bool IsOpenedFromFile => Store.IsFromFile;

    // ------------------------------------------------------------------ creation / opening

    /// <summary>Creates a new, empty document.</summary>
    public static PdfDocument Create()
    {
        var doc = new PdfDocument(new PdfObjectStore());
        var pages = new PdfDictionary(PdfName.Pages);
        pages[PdfName.Kids] = new PdfArray();
        pages.Set(PdfName.Count, 0);
        var catalog = new PdfDictionary(PdfName.Catalog);
        catalog[PdfName.Pages] = doc.AddObject(pages);
        doc.Trailer[PdfName.Root] = doc.AddObject(catalog);
        doc.Info.Producer = "miPDFlib";
        doc.Info.CreationDate = DateTimeOffset.Now;
        doc._pages = new List<PdfPage>();
        return doc;
    }

    public static PdfDocument Open(string path, string? password = null) => Open(File.ReadAllBytes(path), password);

    public static PdfDocument Open(Stream stream, string? password = null)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Open(ms.ToArray(), password);
    }

    /// <summary>Opens a document from its bytes. The array must not be modified while the document is in use.</summary>
    public static PdfDocument Open(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            var doc = new PdfDocument(new PdfObjectStore(data));
            doc.Version = doc.Store.Version;
            doc.InitSecurity(password);
            if (doc.Catalog.GetName(PdfName.Get("Version")) is { } v && string.CompareOrdinal(v.Value, doc.Version) > 0)
                doc.Version = v.Value;
            return doc;
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex))
        {
            throw new PdfParseException("The document cannot be read: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ objects

    /// <summary>Registers <paramref name="obj"/> as indirect object and returns a reference to it.</summary>
    public PdfReference AddObject(PdfObject obj) => Store.Add(obj);

    /// <summary>Returns the indirect object with the given number, or null.</summary>
    public PdfObject? GetObject(int objectNumber) => Store.GetObject(objectNumber);

    /// <summary>Returns the reference of an indirect object of this document, or null for direct objects.</summary>
    public PdfReference? GetReference(PdfObject obj) => Store.GetReferenceOf(obj);

    /// <summary>Replaces the indirect object behind <paramref name="reference"/>.</summary>
    public void ReplaceObject(PdfReference reference, PdfObject obj) => Store.Replace(reference.ObjectNumber, obj);

    /// <summary>Enumerates the numbers of all indirect objects.</summary>
    public IEnumerable<int> GetObjectNumbers() => Store.GetObjectNumbers();

    // ------------------------------------------------------------------ pages

    /// <summary>The pages of the document in order.</summary>
    public IReadOnlyList<PdfPage> Pages => EnsurePages();

    public int PageCount => EnsurePages().Count;

    /// <summary>Returns the page at the 1-based <paramref name="pageNumber"/>.</summary>
    public PdfPage GetPage(int pageNumber)
    {
        var pages = EnsurePages();
        if (pageNumber < 1 || pageNumber > pages.Count) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        return pages[pageNumber - 1];
    }

    /// <summary>Appends a new empty page (default A4 portrait).</summary>
    public PdfPage AddPage(PdfRectangle? size = null) => InsertPage(PageCount, size);

    /// <summary>Inserts a new empty page at the 0-based <paramref name="index"/>.</summary>
    public PdfPage InsertPage(int index, PdfRectangle? size = null)
    {
        var dict = new PdfDictionary(PdfName.Page);
        dict[PdfName.MediaBox] = PdfArray.FromRectangle(size ?? PageSize.A4);
        dict[PdfName.Resources] = new PdfDictionary();
        var reference = AddObject(dict);
        var page = new PdfPage(this, dict, reference);
        InsertPage(index, page);
        return page;
    }

    internal void InsertPage(int index, PdfPage page)
    {
        var pages = EnsurePages();
        if (index < 0 || index > pages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        pages.Insert(index, page);
        _pageTreeDirty = true;
    }

    /// <summary>Removes the page at the 0-based <paramref name="index"/>.</summary>
    public void RemovePage(int index)
    {
        var pages = EnsurePages();
        if (index < 0 || index >= pages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        pages.RemoveAt(index);
        _pageTreeDirty = true;
    }

    /// <summary>Moves a page from one 0-based position to another.</summary>
    public void MovePage(int fromIndex, int toIndex)
    {
        var pages = EnsurePages();
        if (fromIndex < 0 || fromIndex >= pages.Count) throw new ArgumentOutOfRangeException(nameof(fromIndex));
        if (toIndex < 0 || toIndex >= pages.Count) throw new ArgumentOutOfRangeException(nameof(toIndex));
        var page = pages[fromIndex];
        pages.RemoveAt(fromIndex);
        pages.Insert(toIndex, page);
        _pageTreeDirty = true;
    }

    /// <summary>Reorders the pages; <paramref name="order"/> lists 0-based indexes of the current pages (may omit or repeat pages).</summary>
    public void ReorderPages(IEnumerable<int> order)
    {
        var pages = EnsurePages();
        var newList = order.Select(i => pages[i]).ToList();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < newList.Count; i++)
        {
            if (!seen.Add(newList[i].Dictionary))
            {
                // a page may only appear once in the page tree: duplicate it
                var copy = (PdfDictionary)newList[i].Dictionary.Clone();
                newList[i] = new PdfPage(this, copy, AddObject(copy));
            }
        }
        pages.Clear();
        pages.AddRange(newList);
        _pageTreeDirty = true;
    }

    private List<PdfPage> EnsurePages()
    {
        if (_pages != null) return _pages;
        var list = new List<PdfPage>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var root = Catalog[PdfName.Pages];
        CollectPages(root, list, visited, 0);
        _pages = list;
        return list;
    }

    private void CollectPages(PdfObject? node, List<PdfPage> list, HashSet<PdfDictionary> visited, int depth)
    {
        if (node?.Resolve() is not PdfDictionary dict || depth > 256 || !visited.Add(dict)) return;
        var kids = dict.GetArray(PdfName.Kids);
        bool isPage = dict.IsType(PdfName.Page) || (kids == null && !dict.IsType(PdfName.Pages));
        if (isPage)
        {
            var reference = node as PdfReference ?? GetReference(dict) ?? AddObject(dict);
            list.Add(new PdfPage(this, dict, reference));
            return;
        }
        if (kids == null) return;
        foreach (var kid in kids) CollectPages(kid, list, visited, depth + 1);
    }

    /// <summary>Rewrites the page tree as a single flat /Pages node if pages were added, removed or reordered.</summary>
    private void RebuildPageTreeIfNeeded()
    {
        if (!_pageTreeDirty || _pages == null) return;
        var catalog = Catalog;
        var root = catalog.GetDictionary(PdfName.Pages);
        PdfReference rootRef;
        if (root == null)
        {
            root = new PdfDictionary(PdfName.Pages);
            rootRef = AddObject(root);
            catalog[PdfName.Pages] = rootRef;
        }
        else rootRef = catalog[PdfName.Pages] as PdfReference ?? AddObject(root);

        foreach (var page in _pages) page.MaterializeInheritedAttributes();
        // attributes on the root node would be inherited by all pages: they are materialized, drop them
        foreach (var key in PdfPage.InheritableKeys) root.Remove(key);

        var kids = new PdfArray();
        foreach (var page in _pages)
        {
            page.Dictionary[PdfName.Parent] = rootRef;
            kids.Add(page.Reference);
        }
        root[PdfName.Kids] = kids;
        root.Set(PdfName.Count, _pages.Count);
        root.Remove(PdfName.Parent);
        _pageTreeDirty = false;
    }

    // ------------------------------------------------------------------ saving

    /// <summary>Saves the document to a file.</summary>
    public void Save(string path, PdfSaveOptions? options = null)
    {
        // write into memory first: the target may be the file the document was opened from
        var bytes = ToArray(options);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Saves the document to a stream.</summary>
    public void Save(Stream stream, PdfSaveOptions? options = null)
    {
        options ??= new PdfSaveOptions();
        PrepareForSave(options);
        var writer = new PdfFileWriter(Store, options, CreateEncryptor(options));
        if (options.Incremental) writer.WriteIncremental(stream);
        else writer.WriteFull(stream, EffectiveVersion(options));
        stream.Flush();
    }

    /// <summary>Saves the document and returns the bytes.</summary>
    public byte[] ToArray(PdfSaveOptions? options = null)
    {
        using var ms = new MemoryStream();
        Save(ms, options);
        return ms.ToArray();
    }

    private void PrepareForSave(PdfSaveOptions options)
    {
        RebuildPageTreeIfNeeded();
        OnBeforeSave(options);
        if (options.Incremental && string.CompareOrdinal(Version, Store.Version) > 0)
            Catalog[PdfName.Get("Version")] = PdfName.Get(Version);
    }

    private string EffectiveVersion(PdfSaveOptions options)
    {
        var version = options.Version ?? Version;
        if (options.UseObjectStreams && string.CompareOrdinal(version, "1.5") < 0) version = "1.5";
        return version;
    }

    /// <summary>Hooks for features that need to finalize objects before writing (forms, signatures, ...).</summary>
    partial void OnBeforeSave(PdfSaveOptions options);

    public void Dispose()
    {
        // all data is held in memory; nothing to release yet. Kept for API stability (future file-backed reading).
    }
}
