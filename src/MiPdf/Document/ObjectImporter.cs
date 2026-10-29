using MiPdf.Objects;

namespace MiPdf;

/// <summary>
/// Deep-copies objects from one document into another. Shared objects (fonts, images) are copied once
/// per importer. References to page objects that are not part of the import are replaced by null so that
/// e.g. link annotations do not drag in the whole source document.
/// </summary>
internal sealed class ObjectImporter
{
    private static readonly PdfName StructParents = PdfName.Get("StructParents");
    private static readonly PdfName StructParent = PdfName.Get("StructParent");

    private readonly PdfDocument _target;
    private readonly Dictionary<PdfObject, PdfReference> _copied = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfDictionary, PdfReference> _pages = new(ReferenceEqualityComparer.Instance);

    public ObjectImporter(PdfDocument target) => _target = target;

    /// <summary>Registers a source page that is (or will be) imported, so references to it are kept.</summary>
    public void RegisterPage(PdfDictionary sourcePage, PdfReference targetPage) => _pages[sourcePage] = targetPage;

    /// <summary>Copies a page dictionary (without /Parent) including its resources and content.</summary>
    public PdfDictionary ImportPageDictionary(PdfPage source)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in source.Dictionary)
        {
            if (key == PdfName.Parent || key == StructParents || key == PdfName.Get("B") || key == PdfName.Get("PieceInfo")) continue;
            copy[key] = Import(value);
        }
        foreach (var key in PdfPage.InheritableKeys)
        {
            if (copy.ContainsKey(key)) continue;
            var inherited = source.GetInheritable(key);
            if (inherited != null) copy[key] = Import(inherited);
        }
        if (!copy.ContainsKey(PdfName.MediaBox)) copy[PdfName.MediaBox] = PdfArray.FromRectangle(source.MediaBox);
        return copy;
    }

    public PdfObject Import(PdfObject obj)
    {
        switch (obj)
        {
            case PdfReference r:
                return ImportIndirect(r.Resolve());
            case PdfStream s:
                return ImportIndirect(s); // direct streams become indirect
            case PdfDictionary d:
                return CopyDictionary(d);
            case PdfArray a:
                var array = new PdfArray();
                foreach (var item in a) array.Add(Import(item));
                return array;
            default:
                return obj.Clone();
        }
    }

    private PdfObject ImportIndirect(PdfObject target)
    {
        if (target is PdfNull) return PdfNull.Instance;
        if (target is PdfDictionary page && page is not PdfStream && page.IsType(PdfName.Page))
            return _pages.TryGetValue(page, out var mapped) ? mapped : PdfNull.Instance;
        if (_copied.TryGetValue(target, out var existing)) return existing;

        // reserve the number first: the object graph may contain cycles
        var placeholder = _target.AddObject(new PdfDictionary());
        _copied[target] = placeholder;
        PdfObject copy = target switch
        {
            PdfStream s => CopyStream(s),
            PdfDictionary d => CopyDictionary(d),
            PdfArray => Import(target),
            _ => target.Clone(),
        };
        _target.ReplaceObject(placeholder, copy);
        return placeholder;
    }

    private PdfDictionary CopyDictionary(PdfDictionary d)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in d)
        {
            if (key == StructParent) continue; // the structure tree is not copied
            copy[key] = Import(value);
        }
        return copy;
    }

    private PdfStream CopyStream(PdfStream s)
    {
        var copy = new PdfStream();
        foreach (var (key, value) in s)
        {
            if (key == PdfName.Length) continue;
            copy[key] = Import(value);
        }
        copy.SetRawBytes((byte[])s.RawBytes.Clone());
        return copy;
    }
}
