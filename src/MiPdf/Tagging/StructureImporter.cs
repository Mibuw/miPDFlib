using MiPdf.Objects;

namespace MiPdf.Tagging;

/// <summary>
/// Copies the logical structure of imported pages: the structure elements that own marked content or annotations
/// on these pages, together with their ancestors. Content of pages that are not imported is left out, and the parent
/// tree of the target is extended for the new pages. A top-level "Document" element of the source is merged into the
/// "Document" element of the target, so that merged documents keep a single root element.
/// </summary>
internal static class StructureImporter
{
    private const int MaxElements = 1_000_000;

    private static readonly PdfName StructTreeRoot = PdfName.Get("StructTreeRoot");
    private static readonly PdfName ParentTree = PdfName.Get("ParentTree");
    private static readonly PdfName StructParents = PdfName.Get("StructParents");
    private static readonly PdfName StructParent = PdfName.Get("StructParent");
    private static readonly PdfName Pg = PdfName.Get("Pg");
    private static readonly PdfName Mcid = PdfName.Get("MCID");
    private static readonly PdfName Obj = PdfName.Get("Obj");
    private static readonly PdfName Stm = PdfName.Get("Stm");
    private static readonly PdfName Ns = PdfName.Get("NS");
    private static readonly PdfName Lang = PdfName.Get("Lang");

    public static void Import(PdfDocument source, PdfDocument target, ObjectImporter importer,
        IReadOnlyList<(PdfPage Source, PdfPage Target)> pages, bool targetWasEmpty)
    {
        if (source.Catalog.GetDictionary(StructTreeRoot) is not { } sourceRoot) return;
        // pages without tags are already in the target: a partial structure tree would not describe them
        if (!targetWasEmpty && target.Catalog.GetDictionary(StructTreeRoot) == null) return;

        var parentTree = new Dictionary<int, PdfObject>();
        if (sourceRoot.GetDictionary(ParentTree) is { } tree) ReadNumberTree(tree, parentTree, 0);

        // elements that own content on the imported pages
        var needed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var marks = new List<(PdfPage Page, int Mcid, PdfDictionary Element)>();
        var annotations = new List<(PdfDictionary Annotation, PdfDictionary Element)>();
        var pageMap = new Dictionary<PdfDictionary, PdfPage>(ReferenceEqualityComparer.Instance);
        foreach (var (sourcePage, targetPage) in pages)
        {
            pageMap[sourcePage.Dictionary] = targetPage;
            if (sourcePage.Dictionary.Get(StructParents) is PdfNumber key && parentTree.TryGetValue(key.IntValue, out var entry)
                && entry.Resolve() is PdfArray owners)
            {
                for (int mcid = 0; mcid < owners.Count && marks.Count < MaxElements; mcid++)
                {
                    if (owners[mcid].Resolve() is not PdfDictionary element) continue;
                    needed.Add(element);
                    marks.Add((targetPage, mcid, element));
                }
            }
            foreach (var item in sourcePage.Dictionary.GetArray(PdfName.Annots) ?? new PdfArray())
            {
                if (item.Resolve() is PdfDictionary annotation && annotation.Get(StructParent) is PdfNumber annotationKey
                    && parentTree.TryGetValue(annotationKey.IntValue, out var owner) && owner.Resolve() is PdfDictionary element
                    && importer.TryGetCopy(annotation, out var copy) && copy.Resolve() is PdfDictionary copied)
                {
                    needed.Add(element);
                    annotations.Add((copied, element));
                }
            }
        }
        if (needed.Count == 0) return;

        // ... and their ancestors
        foreach (var element in needed.ToList())
        {
            var parent = element.GetDictionary(PdfName.P);
            for (int depth = 0; parent != null && depth < 100 && !ReferenceEquals(parent, sourceRoot) && parent.ContainsKey(PdfName.S); depth++)
            {
                if (!needed.Add(parent)) break;
                parent = parent.GetDictionary(PdfName.P);
            }
        }

        var structure = target.EnableTagging();
        var root = structure.Root;
        var copies = new Dictionary<PdfDictionary, PdfReference>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<(PdfDictionary Source, PdfReference Copy)>();
        string? sourceLanguage = source.Language;
        if (targetWasEmpty && target.Language == null && sourceLanguage != null) target.Language = sourceLanguage;
        bool otherLanguage = sourceLanguage != null && !string.Equals(sourceLanguage, target.Language, StringComparison.OrdinalIgnoreCase);

        foreach (var top in Kids(sourceRoot).Select(k => k.Resolve()).OfType<PdfDictionary>())
        {
            if (!needed.Contains(top) || copies.ContainsKey(top)) continue;
            if (top.GetName(PdfName.S)?.Value == StructureType.Document)
            {
                // merged into the target's Document element
                copies[top] = root.Reference;
                foreach (var child in Kids(top).Select(k => k.Resolve()).OfType<PdfDictionary>())
                    if (needed.Contains(child) && !copies.ContainsKey(child)) AddTopLevel(child);
            }
            else AddTopLevel(top);
        }

        void AddTopLevel(PdfDictionary element)
        {
            var copy = Reserve(element, root.Reference);
            root.Kids().Add(copy);
            var language = element.Get(Lang) ?? (otherLanguage ? new PdfString(sourceLanguage!) : null);
            if (language != null) ((PdfDictionary)copy.Resolve()!)[Lang] = language.Clone();
        }

        PdfReference Reserve(PdfDictionary element, PdfReference parent)
        {
            var copy = new PdfDictionary(PdfName.Get("StructElem"));
            copy[PdfName.P] = parent;
            var reference = target.AddObject(copy);
            copies[element] = reference;
            pending.Enqueue((element, reference));
            return reference;
        }

        Drain();
        // elements with content on the pages that are not reachable from the root (e.g. without /P): below the Document element
        foreach (var element in marks.Select(m => m.Element).Concat(annotations.Select(a => a.Element)))
        {
            if (copies.ContainsKey(element)) continue;
            var top = element;
            for (int depth = 0; depth < 100 && top.GetDictionary(PdfName.P) is { } parent && needed.Contains(parent) && !copies.ContainsKey(parent); depth++)
                top = parent;
            AddTopLevel(top);
            Drain();
        }

        foreach (var (page, mcid, element) in marks)
            if (copies.TryGetValue(element, out var owner)) structure.SetMarkedContentOwner(page, mcid, owner);
        foreach (var (annotation, element) in annotations)
            if (copies.TryGetValue(element, out var owner)) structure.SetAnnotationOwner(annotation, owner);
        MergeMaps(sourceRoot, structure.Dictionary, importer);

        void Drain()
        {
            while (pending.Count > 0 && copies.Count < MaxElements)
            {
                var (element, reference) = pending.Dequeue();
                var copy = (PdfDictionary)reference.Resolve()!;
                foreach (var (key, value) in element)
                {
                    if (key == PdfName.P || key == PdfName.K || key == Pg || key == PdfName.Get("ID") || copy.ContainsKey(key)) continue;
                    if (key == Ns)
                    {
                        if (value.Resolve() is PdfDictionary ns && ns.GetText(Ns) is { } uri) copy[Ns] = structure.GetNamespace(uri);
                        continue;
                    }
                    copy[key] = importer.Import(value);
                }
                var page = Mapped(element.GetDictionary(Pg));
                if (page != null) copy[Pg] = page.Reference;
                var kids = new PdfArray();
                foreach (var kid in Kids(element))
                {
                    switch (kid.Resolve())
                    {
                        case PdfNumber mcid when page != null:
                            kids.Add(new PdfNumber(mcid.IntValue));
                            break;
                        case PdfDictionary mcr when mcr.GetName(PdfName.Type)?.Value == "MCR":
                            // marked content in form XObjects (/Stm) is not imported
                            var mcrPage = Mapped(mcr.GetDictionary(Pg)) ?? page;
                            if (mcr.ContainsKey(Stm) || mcrPage == null) break;
                            var newMcr = new PdfDictionary(PdfName.Get("MCR"));
                            newMcr[Pg] = mcrPage.Reference;
                            newMcr.Set(Mcid, mcr.GetInt(Mcid));
                            kids.Add(newMcr);
                            break;
                        case PdfDictionary objr when objr.GetName(PdfName.Type)?.Value == "OBJR":
                            if (objr.Get(Obj) is not { } obj || !importer.TryGetCopy(obj, out var objCopy)) break;
                            var newObjr = new PdfDictionary(PdfName.Get("OBJR"));
                            newObjr[Obj] = objCopy;
                            if ((Mapped(objr.GetDictionary(Pg)) ?? page) is { } objPage) newObjr[Pg] = objPage.Reference;
                            kids.Add(newObjr);
                            break;
                        case PdfDictionary child when child.ContainsKey(PdfName.S) && needed.Contains(child):
                            kids.Add(copies.TryGetValue(child, out var existing) ? existing : Reserve(child, reference));
                            break;
                    }
                }
                copy[PdfName.K] = kids;
            }
        }

        PdfPage? Mapped(PdfDictionary? sourcePage) => sourcePage != null && pageMap.TryGetValue(sourcePage, out var p) ? p : null;
    }

    private static IEnumerable<PdfObject> Kids(PdfDictionary element) =>
        element[PdfName.K] switch
        {
            null => Array.Empty<PdfObject>(),
            var k when k.Resolve() is PdfArray array => array,
            var k => new[] { k },
        };

    /// <summary>Role and class mappings of the source that the target does not have yet.</summary>
    private static void MergeMaps(PdfDictionary sourceRoot, PdfDictionary targetRoot, ObjectImporter importer)
    {
        foreach (var name in new[] { "RoleMap", "ClassMap" })
        {
            if (sourceRoot.GetDictionary(PdfName.Get(name)) is not { Count: > 0 } sourceMap) continue;
            var map = targetRoot.GetDictionary(PdfName.Get(name));
            if (map == null) targetRoot[PdfName.Get(name)] = map = new PdfDictionary();
            foreach (var (key, value) in sourceMap)
                if (!map.ContainsKey(key)) map[key] = importer.Import(value);
        }
    }

    private static void ReadNumberTree(PdfDictionary node, Dictionary<int, PdfObject> result, int depth)
    {
        if (depth > 32) return;
        if (node.GetArray(PdfName.Get("Nums")) is { } nums)
            for (int i = 0; i + 1 < nums.Count; i += 2)
                if (nums[i].Resolve() is PdfNumber key) result[key.IntValue] = nums[i + 1];
        if (node.GetArray(PdfName.Kids) is { } kids)
            foreach (var kid in kids)
                if (kid.Resolve() is PdfDictionary d) ReadNumberTree(d, result, depth + 1);
    }
}
