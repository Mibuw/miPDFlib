using MiPdf.Forms;
using MiPdf.Objects;

namespace MiPdf.Signatures;

/// <summary>Kind of a change made in an incremental update after a signature.</summary>
public enum DocumentChangeKind
{
    /// <summary>Validation data in the Document Security Store (certificates, OCSP responses, CRLs).</summary>
    ValidationData,

    /// <summary>A document time-stamp that was added.</summary>
    DocumentTimestamp,

    /// <summary>A signature field that was signed, added or changed.</summary>
    Signature,

    /// <summary>The value or appearance of an existing form field, or the form's default resources.</summary>
    FormFill,

    /// <summary>Annotations (comments, markup, stamps, ...) that were added, changed or removed; form fields are not included.</summary>
    Annotation,

    /// <summary>The document information or the XMP metadata.</summary>
    Metadata,

    /// <summary>The logical structure (tags), e.g. for a new signature field in a tagged document.</summary>
    Structure,

    /// <summary>Form fields other than signature fields that were added, or form fields that were removed.</summary>
    FormFields,

    /// <summary>Page content, page resources or page properties such as the page size.</summary>
    PageContent,

    /// <summary>Pages that were added, removed or reordered.</summary>
    Pages,

    /// <summary>Other changes, e.g. of embedded files, bookmarks, document-level actions or permissions.</summary>
    Other,
}

/// <summary>A change made in an incremental update after a signature (see <see cref="SignatureValidationResult.ChangesAfterSigning"/>).</summary>
public sealed class DocumentChange
{
    internal DocumentChange(DocumentChangeKind kind, string description, bool isPermitted)
    {
        Kind = kind;
        Description = description;
        IsPermitted = isPermitted;
    }

    /// <summary>What was changed.</summary>
    public DocumentChangeKind Kind { get; }

    /// <summary>A short description, e.g. "field 'name': value changed" or "page 2: content changed".</summary>
    public string Description { get; }

    /// <summary>
    /// True if the change is permitted. In a certified document this follows the certification level (DocMDP):
    /// <see cref="CertificationLevel.NoChanges"/> permits validation data, document time-stamps and metadata,
    /// <see cref="CertificationLevel.FormFilling"/> also form filling and signing, and
    /// <see cref="CertificationLevel.FormFillingAndAnnotations"/> also annotations. Without certification, the same
    /// changes as for <see cref="CertificationLevel.FormFillingAndAnnotations"/> are permitted: those that do not alter
    /// the signed content. Changes of the page content, of pages, other form fields or other parts of the document are
    /// never permitted.
    /// </summary>
    public bool IsPermitted { get; }

    /// <summary>Returns kind, description and whether the change is permitted.</summary>
    public override string ToString() => $"{Kind}: {Description}{(IsPermitted ? "" : " (not permitted)")}";
}

/// <summary>
/// Finds the changes made after a signature by comparing the signed revision with the complete file: objects whose
/// cross-reference entry differs were added, changed or deleted later. Each changed object is assigned to the part
/// of the document it belongs to (validation data, a signature, a form field, an annotation, a page, ...).
/// </summary>
internal static class ChangeAnalyzer
{
    private const int MaxVisits = 2_000_000;

    private static readonly PdfName[] SkippedKeys =
        { PdfName.Parent, PdfName.P, PdfName.Get("Pg"), PdfName.Get("Popup"), PdfName.Get("IRT") };

    public static List<DocumentChange> Analyze(PdfDocument signed, PdfDocument current, CertificationLevel? certification)
    {
        var analysis = new Analysis(signed, current);
        analysis.Run();
        var kinds = analysis.Changes.Select(c => c.Kind).ToHashSet();
        var permitted = Permitted(certification);
        // structure changes that only accompany validation data or document time-stamps (tagged time-stamp fields)
        if (kinds.Contains(DocumentChangeKind.Structure) && kinds.All(k => k is DocumentChangeKind.Structure
                or DocumentChangeKind.ValidationData or DocumentChangeKind.DocumentTimestamp))
            permitted.Add(DocumentChangeKind.Structure);
        return analysis.Changes.Select(c => new DocumentChange(c.Kind, c.Description, permitted.Contains(c.Kind))).ToList();
    }

    private static HashSet<DocumentChangeKind> Permitted(CertificationLevel? certification)
    {
        // metadata does not change the content; EU DSS also accepts it at every level (as an extension change)
        var permitted = new HashSet<DocumentChangeKind>
            { DocumentChangeKind.ValidationData, DocumentChangeKind.DocumentTimestamp, DocumentChangeKind.Metadata };
        if (certification is CertificationLevel.NoChanges) return permitted;
        permitted.UnionWith(new[] { DocumentChangeKind.Signature, DocumentChangeKind.FormFill, DocumentChangeKind.Structure });
        if (certification is CertificationLevel.FormFilling) return permitted;
        permitted.Add(DocumentChangeKind.Annotation);
        return permitted;
    }

    private sealed class Analysis(PdfDocument signed, PdfDocument current)
    {
        // objects already assigned to a part of the document; they are not assigned again
        private readonly HashSet<int> _assigned = new();
        private readonly HashSet<string> _descriptions = new(StringComparer.Ordinal);
        private int _visits;

        public List<(DocumentChangeKind Kind, string Description)> Changes { get; } = new();

        private void Add(DocumentChangeKind kind, string description)
        {
            if (_descriptions.Add(description)) Changes.Add((kind, description));
        }

        private bool Changed(int number) => !current.Store.HasSameEntry(number, signed.Store);

        /// <summary>
        /// Visits the objects reachable from <paramref name="start"/> that are not yet assigned and assigns them;
        /// returns true if one of them was changed. Pages, page tree nodes and annotations (other than the start) are
        /// not entered, and links to parents are not followed.
        /// </summary>
        private bool Reach(PdfObject? start)
        {
            bool changed = false;
            var pending = new Stack<PdfObject>();
            if (start is PdfReference startRef)
            {
                if (!_assigned.Add(startRef.ObjectNumber)) return false;
                changed = Changed(startRef.ObjectNumber);
                start = startRef.Resolve();
            }
            if (start != null) pending.Push(start);
            while (pending.Count > 0 && _visits++ < MaxVisits)
            {
                switch (pending.Pop())
                {
                    case PdfReference r:
                        var target = r.Resolve();
                        if (target is PdfDictionary d && (IsPageNode(d) || IsAnnotation(d))) break;
                        if (!_assigned.Add(r.ObjectNumber)) break;
                        if (Changed(r.ObjectNumber)) changed = true;
                        if (target != null) pending.Push(target);
                        break;
                    case PdfDictionary dict:
                        foreach (var (key, value) in dict)
                            if (Array.IndexOf(SkippedKeys, key) < 0) pending.Push(value);
                        break;
                    case PdfArray array:
                        foreach (var item in array) pending.Push(item);
                        break;
                }
            }
            return changed;
        }

        /// <summary>Marks an object as assigned without reporting it (it is analysed by comparing its entries).</summary>
        private bool Claim(PdfObject? obj) => obj is PdfReference r && _assigned.Add(r.ObjectNumber) && Changed(r.ObjectNumber);

        private static bool IsPageNode(PdfDictionary d) => d.GetName(PdfName.Type)?.Value is "Page" or "Pages";

        private static bool IsAnnotation(PdfDictionary d) => d.ContainsKey(PdfName.Subtype) && d.ContainsKey(PdfName.Rect);

        public void Run()
        {
            var catalog = current.Catalog;
            var signedCatalog = signed.Catalog;

            if (Reach(catalog[PdfName.Get("DSS")])) Add(DocumentChangeKind.ValidationData, "validation data (DSS) added or changed");
            AnalyzeSignatureValues();
            if (Reach(current.Trailer[PdfName.Info]) || !SameValue(current.Trailer[PdfName.Info], signed.Trailer[PdfName.Info]))
                Add(DocumentChangeKind.Metadata, "document information changed");
            if (Reach(catalog[PdfName.Metadata]) || !SameValue(catalog[PdfName.Metadata], signedCatalog[PdfName.Metadata]))
                Add(DocumentChangeKind.Metadata, "XMP metadata changed");
            AnalyzePages();
            AnalyzeFields();
            AnalyzeAcroForm(catalog[PdfName.AcroForm], signedCatalog[PdfName.AcroForm]);
            if (Reach(catalog[PdfName.Get("StructTreeRoot")]) || !SameValue(catalog[PdfName.Get("StructTreeRoot")], signedCatalog[PdfName.Get("StructTreeRoot")]))
                Add(DocumentChangeKind.Structure, "logical structure (tags) changed");

            var handled = new[] { "DSS", "Metadata", "AcroForm", "StructTreeRoot", "Pages", "Type" };
            foreach (var key in catalog.Keys.Concat(signedCatalog.Keys).Distinct())
            {
                if (Array.IndexOf(handled, key.Value) >= 0) continue;
                var value = catalog[key];
                bool changed = Reach(value) | !SameValue(value, signedCatalog[key]);
                if (!changed) continue;
                if (key.Value == "MarkInfo") Add(DocumentChangeKind.Structure, "logical structure (/MarkInfo) changed");
                // developer extensions declare the ESIC/ADBE extension levels of PAdES validation data
                else if (key.Value == "Extensions") Add(DocumentChangeKind.ValidationData, "developer extensions (/Extensions) changed");
                else Add(DocumentChangeKind.Other, $"document catalog: /{key.Value} {(value == null ? "removed" : signedCatalog[key] == null ? "added" : "changed")}");
            }
        }

        /// <summary>Signature dictionaries (/V) of signature fields: new signatures and document time-stamps.</summary>
        private void AnalyzeSignatureValues()
        {
            var before = signed.GetForm()?.Fields.OfType<PdfSignatureField>()
                .GroupBy(f => f.FullName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
                ?? new Dictionary<string, PdfSignatureField>(StringComparer.Ordinal);
            foreach (var field in current.GetForm()?.Fields.OfType<PdfSignatureField>() ?? Enumerable.Empty<PdfSignatureField>())
            {
                var value = field.Dictionary[PdfName.V];
                if (value == null) continue;
                bool changed = Reach(value);
                before.TryGetValue(field.FullName, out var old);
                var oldValue = old?.Dictionary[PdfName.V];
                if (!changed && SameValue(value, oldValue)) continue;
                if (oldValue != null) Add(DocumentChangeKind.Other, $"signature in field '{field.FullName}' replaced or changed");
                else if (field.SignatureDictionary?.GetName(PdfName.SubFilter)?.Value == "ETSI.RFC3161")
                    Add(DocumentChangeKind.DocumentTimestamp, $"document time-stamp '{field.FullName}' added");
                else Add(DocumentChangeKind.Signature, $"field '{field.FullName}' signed");
            }
        }

        private void AnalyzePages()
        {
            var signedPages = signed.Pages.ToDictionary(p => p.Reference.ObjectNumber);
            var currentPages = current.Pages;
            var currentNumbers = currentPages.Select(p => p.Reference.ObjectNumber).ToList();
            var signedNumbers = signed.Pages.Select(p => p.Reference.ObjectNumber).ToList();
            int added = currentNumbers.Count(n => !signedPages.ContainsKey(n));
            int removed = signedNumbers.Count(n => !currentNumbers.Contains(n));
            if (added > 0) Add(DocumentChangeKind.Pages, $"{added} page(s) added");
            if (removed > 0) Add(DocumentChangeKind.Pages, $"{removed} page(s) removed");
            if (added == 0 && removed == 0 && !currentNumbers.SequenceEqual(signedNumbers)) Add(DocumentChangeKind.Pages, "pages reordered");
            AnalyzePageTree(current.Catalog[PdfName.Pages], 0);

            for (int i = 0; i < currentPages.Count; i++)
            {
                var page = currentPages[i];
                if (!signedPages.TryGetValue(page.Reference.ObjectNumber, out var before)) continue;
                string where = $"page {i + 1}";
                bool pageChanged = _assigned.Add(page.Reference.ObjectNumber) && Changed(page.Reference.ObjectNumber);
                bool content = false, structure = false;
                foreach (var key in page.Dictionary.Keys.Concat(before.Dictionary.Keys).Distinct())
                {
                    if (key == PdfName.Parent || key == PdfName.Annots) continue;
                    var value = page.Dictionary[key];
                    bool differs = (pageChanged && !SameValue(value, before.Dictionary[key])) | Reach(value);
                    if (!differs) continue;
                    if (key.Value is "StructParents" or "Tabs") structure = true;
                    else content = true;
                }
                if (!page.Dictionary.ContainsKey(PdfName.Resources) && Reach(Inherited(page.Dictionary, PdfName.Resources))) content = true;
                if (content) Add(DocumentChangeKind.PageContent, $"{where}: content changed");
                if (structure) Add(DocumentChangeKind.Structure, $"{where}: structure entries changed");
                AnalyzeAnnotations(page, before, where);
            }
        }

        /// <summary>The raw value of an inheritable page entry from the nearest page tree node that has it.</summary>
        private static PdfObject? Inherited(PdfDictionary page, PdfName key)
        {
            var node = page;
            for (int depth = 0; node != null && depth < 64; depth++)
            {
                if (node[key] is { } value) return value;
                node = node.GetDictionary(PdfName.Parent);
            }
            return null;
        }

        /// <summary>Inherited page properties in the page tree nodes (the page order is compared separately).</summary>
        private void AnalyzePageTree(PdfObject? node, int depth)
        {
            if (depth > 64 || node is not PdfReference r || r.Resolve() is not PdfDictionary dict
                || dict.GetName(PdfName.Type)?.Value == "Page" || !_assigned.Add(r.ObjectNumber)) return;
            if (Changed(r.ObjectNumber) && signed.Store.GetObject(r.ObjectNumber) is PdfDictionary old)
            {
                foreach (var key in dict.Keys.Concat(old.Keys).Distinct())
                    if (key.Value is not ("Kids" or "Count" or "Parent") && !SameValue(dict[key], old[key]))
                        Add(DocumentChangeKind.PageContent, $"page tree: inherited /{key.Value} changed");
            }
            foreach (var key in dict.Keys)
                if (key.Value is not ("Kids" or "Parent") && Reach(dict[key]))
                    Add(DocumentChangeKind.PageContent, $"page tree: inherited /{key.Value} changed");
            if (dict.GetArray(PdfName.Kids) is { } kids)
                foreach (var kid in kids) AnalyzePageTree(kid, depth + 1);
        }

        /// <summary>Annotations other than form field widgets (those are analysed with their fields).</summary>
        private void AnalyzeAnnotations(PdfPage page, PdfPage before, string where)
        {
            Claim(page.Dictionary[PdfName.Annots]);
            var oldAnnots = AnnotationReferences(before.Dictionary);
            var newAnnots = AnnotationReferences(page.Dictionary);
            foreach (var (number, reference) in newAnnots)
            {
                if (reference.Resolve() is not PdfDictionary annot || IsWidget(annot)) continue;
                string subtype = annot.GetName(PdfName.Subtype)?.Value ?? "?";
                if (!oldAnnots.ContainsKey(number))
                {
                    Reach(reference);
                    Add(DocumentChangeKind.Annotation, $"{where}: {subtype} annotation added");
                }
                else if (Reach(reference)) Add(DocumentChangeKind.Annotation, $"{where}: {subtype} annotation changed");
            }
            foreach (var (number, reference) in oldAnnots)
            {
                if (newAnnots.ContainsKey(number) || signed.Store.GetObject(number) is not PdfDictionary annot) continue;
                if (IsWidget(annot)) Add(DocumentChangeKind.FormFields, $"{where}: form field widget removed");
                else Add(DocumentChangeKind.Annotation, $"{where}: {annot.GetName(PdfName.Subtype)?.Value ?? "?"} annotation removed");
            }
            if (page.Dictionary.GetArray(PdfName.Annots) is { } array && array.Any(a => a is PdfDictionary)
                && !SameValue(page.Dictionary[PdfName.Annots], before.Dictionary[PdfName.Annots]))
                Add(DocumentChangeKind.Annotation, $"{where}: annotations changed");
        }

        private static Dictionary<int, PdfReference> AnnotationReferences(PdfDictionary page)
        {
            var result = new Dictionary<int, PdfReference>();
            foreach (var item in page.GetArray(PdfName.Annots) ?? new PdfArray())
                if (item is PdfReference r) result.TryAdd(r.ObjectNumber, r);
            return result;
        }

        private static bool IsWidget(PdfDictionary annot) => annot.GetName(PdfName.Subtype)?.Value == "Widget";

        private void AnalyzeFields()
        {
            var before = signed.GetForm()?.Fields
                .GroupBy(f => f.FullName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
                ?? new Dictionary<string, PdfFormField>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in current.GetForm()?.Fields ?? Enumerable.Empty<PdfFormField>())
            {
                if (!names.Add(field.FullName)) continue;
                bool changed = Reach(current.GetReference(field.Dictionary) ?? (PdfObject)field.Dictionary);
                foreach (var widget in field.Widgets)
                    changed |= Reach(current.GetReference(widget.Dictionary) ?? (PdfObject)widget.Dictionary);
                // parent fields (values and attributes are inherited)
                var parent = field.Dictionary[PdfName.Parent];
                for (int depth = 0; parent is PdfReference && depth < 32; depth++)
                {
                    changed |= Reach(parent);
                    parent = (parent.Resolve() as PdfDictionary)?[PdfName.Parent];
                }
                before.TryGetValue(field.FullName, out var old);
                bool reportedSigned = _descriptions.Contains($"field '{field.FullName}' signed")
                    || _descriptions.Contains($"document time-stamp '{field.FullName}' added");
                if (field is PdfSignatureField)
                {
                    if (reportedSigned) continue;
                    if (old == null) Add(DocumentChangeKind.Signature, $"signature field '{field.FullName}' added");
                    else if (changed) Add(DocumentChangeKind.Signature, $"signature field '{field.FullName}' changed");
                }
                else if (old == null) Add(DocumentChangeKind.FormFields, $"field '{field.FullName}' added");
                else if (changed)
                {
                    Add(DocumentChangeKind.FormFill, old.Value != field.Value
                        ? $"field '{field.FullName}': value changed"
                        : $"field '{field.FullName}': appearance or properties changed");
                }
            }
            foreach (var name in before.Keys.Where(n => !names.Contains(n)))
                Add(DocumentChangeKind.FormFields, before[name] is PdfSignatureField ? $"signature field '{name}' removed" : $"field '{name}' removed");
        }

        /// <summary>The form dictionary: the field list is analysed with the fields, the rest counts as form filling.</summary>
        private void AnalyzeAcroForm(PdfObject? value, PdfObject? signedValue)
        {
            Claim(value);
            var form = value?.Resolve() as PdfDictionary;
            var old = signedValue?.Resolve() as PdfDictionary;
            if (form == null && old == null) return;
            if (form == null || old == null)
            {
                Add(DocumentChangeKind.FormFields, form == null ? "form removed" : "form added");
                return;
            }
            Claim(form[PdfName.Get("Fields")]);
            foreach (var key in form.Keys.Concat(old.Keys).Distinct())
            {
                if (key.Value is "Fields" or "SigFlags") continue;
                bool changed = Reach(form[key]) | !SameValue(form[key], old[key]);
                if (changed) Add(DocumentChangeKind.FormFill, $"form: /{key.Value} changed");
            }
        }

        /// <summary>
        /// Compares two values of the signed revision and the current file: references by object number (their
        /// content is checked through the cross-reference entries), direct objects by value.
        /// </summary>
        private static bool SameValue(PdfObject? a, PdfObject? b, int depth = 0)
        {
            if (a == null || b == null) return a == null && b == null;
            if (depth > 32) return false;
            switch (a)
            {
                case PdfReference ra:
                    return b is PdfReference rb && ra.ObjectNumber == rb.ObjectNumber && ra.Generation == rb.Generation;
                case PdfStream sa:
                    return b is PdfStream sb && SameEntries(sa, sb, depth) && sa.RawDataEquals(sb);
                case PdfDictionary da:
                    return b is PdfDictionary db && b is not PdfStream && SameEntries(da, db, depth);
                case PdfArray aa:
                    if (b is not PdfArray ab || aa.Count != ab.Count) return false;
                    for (int i = 0; i < aa.Count; i++)
                        if (!SameValue(aa[i], ab[i], depth + 1)) return false;
                    return true;
                case PdfString s:
                    return b is PdfString t && s.Bytes.AsSpan().SequenceEqual(t.Bytes);
                case PdfNumber n:
                    return b is PdfNumber m && n.Value == m.Value;
                case PdfName name:
                    return b is PdfName other && name.Value == other.Value;
                case PdfBoolean x:
                    return b is PdfBoolean y && x.Value == y.Value;
                default:
                    return a.GetType() == b.GetType();
            }
        }

        private static bool SameEntries(PdfDictionary a, PdfDictionary b, int depth)
        {
            if (a.Count != b.Count) return false;
            foreach (var (key, value) in a)
                if (!SameValue(value, b[key], depth + 1)) return false;
            return true;
        }
    }
}
