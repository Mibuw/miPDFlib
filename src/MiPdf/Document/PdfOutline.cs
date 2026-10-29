using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf;

/// <summary>
/// A bookmark (outline item). The document's root is <see cref="PdfDocument.Outlines"/>; its children are
/// the top-level bookmarks. Changes are written directly into the outline dictionaries.
/// </summary>
public sealed class PdfOutline
{
    private static readonly PdfName FirstKey = PdfName.First;
    private static readonly PdfName LastKey = PdfName.Get("Last");
    private static readonly PdfName NextKey = PdfName.Next;
    private static readonly PdfName PrevKey = PdfName.Prev;

    private readonly PdfDocument _document;

    internal PdfOutline(PdfDocument document, PdfDictionary dictionary, PdfReference reference)
    {
        _document = document;
        Dictionary = dictionary;
        Reference = reference;
    }

    public PdfDictionary Dictionary { get; }

    public PdfReference Reference { get; }

    public bool IsRoot => !Dictionary.ContainsKey(PdfName.Parent);

    public string Title
    {
        get => Dictionary.GetText(PdfName.Title) ?? "";
        set => Dictionary.SetText(PdfName.Title, value);
    }

    /// <summary>Whether the children are shown expanded.</summary>
    public bool IsOpen
    {
        get => Dictionary.GetInt(PdfName.Count) > 0;
        set
        {
            int count = Math.Abs(Dictionary.GetInt(PdfName.Count));
            if (count > 0) Dictionary.Set(PdfName.Count, value ? count : -count);
            if (Dictionary.GetDictionary(PdfName.Parent) is { } parent && Dictionary[PdfName.Parent] is PdfReference parentRef)
                new PdfOutline(_document, parent, parentRef).UpdateCounts();
        }
    }

    public PdfColor? Color
    {
        get => Dictionary.GetArray(PdfName.C) is { Count: 3 } c ? PdfColor.Rgb(c.GetDouble(0), c.GetDouble(1), c.GetDouble(2)) : null;
        set
        {
            if (value is not { } v) Dictionary.Remove(PdfName.C);
            else
            {
                var rgb = v.ColorSpace == PdfColorSpace.Rgb ? v : PdfColor.Rgb(v.C1, v.C1, v.C1);
                Dictionary[PdfName.C] = PdfArray.FromNumbers(rgb.C1, rgb.C2, rgb.C3);
            }
        }
    }

    public bool Bold
    {
        get => (Dictionary.GetInt(PdfName.F) & 2) != 0;
        set => Dictionary.Set(PdfName.F, value ? Dictionary.GetInt(PdfName.F) | 2 : Dictionary.GetInt(PdfName.F) & ~2);
    }

    public bool Italic
    {
        get => (Dictionary.GetInt(PdfName.F) & 1) != 0;
        set => Dictionary.Set(PdfName.F, value ? Dictionary.GetInt(PdfName.F) | 1 : Dictionary.GetInt(PdfName.F) & ~1);
    }

    /// <summary>The 1-based target page number, or null if the bookmark has no page destination.</summary>
    public int? PageNumber
    {
        get
        {
            var page = _document.ResolveDestinationPage(Dictionary.Get(PdfName.Dest) ?? GetActionDestination());
            return page;
        }
    }

    private PdfObject? GetActionDestination()
    {
        var action = Dictionary.GetDictionary(PdfName.Get("A"));
        return action?.GetName(PdfName.S)?.Value == "GoTo" ? action.Get(PdfName.Get("D")) : null;
    }

    /// <summary>The child bookmarks.</summary>
    public IReadOnlyList<PdfOutline> Children
    {
        get
        {
            var list = new List<PdfOutline>();
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var current = Dictionary[FirstKey] as PdfReference;
            while (current?.Resolve() is PdfDictionary d && visited.Add(d) && list.Count < 100_000)
            {
                list.Add(new PdfOutline(_document, d, current));
                current = d[NextKey] as PdfReference;
            }
            return list;
        }
    }

    /// <summary>Adds a child bookmark pointing to <paramref name="page"/> (optionally at a vertical position).</summary>
    public PdfOutline AddChild(string title, PdfPage? page, double? top = null)
    {
        var dict = new PdfDictionary();
        dict.SetText(PdfName.Title, title);
        dict[PdfName.Parent] = Reference;
        if (page != null) dict[PdfName.Dest] = CreateDestination(page, top);
        var reference = _document.AddObject(dict);

        if (Dictionary[LastKey] is PdfReference lastRef && lastRef.Resolve() is PdfDictionary last)
        {
            last[NextKey] = reference;
            dict[PrevKey] = lastRef;
        }
        else Dictionary[FirstKey] = reference;
        Dictionary[LastKey] = reference;
        UpdateCounts();
        return new PdfOutline(_document, dict, reference);
    }

    /// <summary>Removes this bookmark (and its children) from its parent.</summary>
    public void Remove()
    {
        if (Dictionary.GetDictionary(PdfName.Parent) is not { } parent) throw new InvalidOperationException("The root cannot be removed.");
        var prev = Dictionary[PrevKey];
        var next = Dictionary[NextKey];
        if (prev?.Resolve() is PdfDictionary p) p[NextKey] = next;
        else parent[FirstKey] = next;
        if (next?.Resolve() is PdfDictionary n) n[PrevKey] = prev;
        else parent[LastKey] = prev;
        var parentRef = Dictionary[PdfName.Parent] as PdfReference;
        if (parentRef != null) new PdfOutline(_document, parent, parentRef).UpdateCounts();
    }

    internal static PdfArray CreateDestination(PdfPage page, double? top) =>
        top.HasValue
            ? new PdfArray(page.Reference, PdfName.Get("XYZ"), PdfNull.Instance, new PdfNumber(top.Value), PdfNull.Instance)
            : new PdfArray(page.Reference, PdfName.Get("Fit"));

    /// <summary>Recomputes /Count from this item up to the root (negative counts mark closed items).</summary>
    private void UpdateCounts()
    {
        var node = Dictionary;
        for (int depth = 0; node != null && depth < 64; depth++)
        {
            int visible = 0, guard = 0;
            var child = node[FirstKey]?.Resolve() as PdfDictionary;
            while (child != null && guard++ < 100_000)
            {
                visible += 1 + Math.Max(0, child.GetInt(PdfName.Count));
                child = child[NextKey]?.Resolve() as PdfDictionary;
            }
            bool closed = node.ContainsKey(PdfName.Parent) && node.GetInt(PdfName.Count) < 0;
            if (visible == 0) node.Remove(PdfName.Count);
            else node.Set(PdfName.Count, closed ? -visible : visible);
            node = node.GetDictionary(PdfName.Parent);
        }
    }
}

public sealed partial class PdfDocument
{
    /// <summary>The root of the bookmark tree (created on first access). Add top-level bookmarks with <c>Outlines.AddChild</c>.</summary>
    public PdfOutline Outlines
    {
        get
        {
            var catalog = Catalog;
            if (catalog.GetDictionary(PdfName.Outlines) is { } existing)
            {
                var reference = catalog[PdfName.Outlines] as PdfReference ?? AddObject(existing);
                catalog[PdfName.Outlines] = reference;
                return new PdfOutline(this, existing, reference);
            }
            var root = new PdfDictionary(PdfName.Outlines);
            var r = AddObject(root);
            catalog[PdfName.Outlines] = r;
            catalog[PdfName.Get("PageMode")] = PdfName.Get("UseOutlines");
            return new PdfOutline(this, root, r);
        }
    }

    /// <summary>True if the document has bookmarks.</summary>
    public bool HasOutlines => Catalog.GetDictionary(PdfName.Outlines)?.ContainsKey(PdfName.First) == true;

    /// <summary>Resolves an explicit or named destination to a 1-based page number.</summary>
    internal int? ResolveDestinationPage(PdfObject? destination)
    {
        destination = destination?.Resolve();
        if (destination is PdfString or PdfName) destination = LookupNamedDestination(destination);
        if (destination is PdfDictionary d) destination = d.Get(PdfName.Get("D"));
        if (destination is not PdfArray array || array.Count == 0) return null;
        if (array[0] is PdfReference pageRef)
        {
            var target = pageRef.Resolve();
            var pages = Pages;
            for (int i = 0; i < pages.Count; i++)
                if (ReferenceEquals(pages[i].Dictionary, target)) return i + 1;
            return null;
        }
        if (array[0] is PdfNumber n) return n.IntValue + 1; // remote destinations use page indexes
        return null;
    }

    private PdfObject? LookupNamedDestination(PdfObject name)
    {
        if (name is PdfName n) return Catalog.GetDictionary(PdfName.Dests)?.Get(n);
        var key = ((PdfString)name).Bytes;
        var tree = Catalog.GetDictionary("Names")?.GetDictionary(PdfName.Dests);
        return tree == null ? null : FindInNameTree(tree, key, 0);
    }

    private static PdfObject? FindInNameTree(PdfDictionary node, byte[] key, int depth)
    {
        if (depth > 32) return null;
        if (node.GetArray(PdfName.Get("Names")) is { } names)
            for (int i = 0; i + 1 < names.Count; i += 2)
                if (names.Get(i) is PdfString s && s.Bytes.AsSpan().SequenceEqual(key)) return names.Get(i + 1);
        if (node.GetArray(PdfName.Kids) is { } kids)
            foreach (var kid in kids)
                if (kid.Resolve() is PdfDictionary k && FindInNameTree(k, key, depth + 1) is { } found) return found;
        return null;
    }
}
