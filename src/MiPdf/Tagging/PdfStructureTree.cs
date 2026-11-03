using MiPdf.Objects;

namespace MiPdf.Tagging;

/// <summary>Standard structure types (ISO 32000-1, 14.8.4) for <see cref="PdfStructureElement.AddChild"/>.</summary>
public static class StructureType
{
    /// <summary>"Document": the complete document (root element of the structure).</summary>
    public const string Document = "Document";
    /// <summary>"Part": a large division of a document, e.g. a chapter group.</summary>
    public const string Part = "Part";
    /// <summary>"Art": an article, a self-contained body of text.</summary>
    public const string Article = "Art";
    /// <summary>"Index": a sequence of index entries with references.</summary>
    public const string Index = "Index";
    /// <summary>"NonStruct": a grouping element without structural meaning (its children are kept).</summary>
    public const string NonStruct = "NonStruct";
    /// <summary>"Private": content specific to the producing application.</summary>
    public const string Private = "Private";
    /// <summary>"H": a heading without a level.</summary>
    public const string H = "H";
    /// <summary>"Reference": a citation to content elsewhere in the document.</summary>
    public const string Reference = "Reference";
    /// <summary>"BibEntry": a bibliography entry.</summary>
    public const string BibEntry = "BibEntry";
    /// <summary>"Code": computer program text.</summary>
    public const string Code = "Code";
    /// <summary>"Ruby": a ruby annotation (small text next to base text, e.g. pronunciation).</summary>
    public const string Ruby = "Ruby";
    /// <summary>"RB": the base text of a ruby annotation.</summary>
    public const string RubyBase = "RB";
    /// <summary>"RT": the ruby text.</summary>
    public const string RubyText = "RT";
    /// <summary>"RP": ruby punctuation around the ruby text.</summary>
    public const string RubyPunctuation = "RP";
    /// <summary>"Warichu": a comment or annotation set in smaller text on two lines within a line.</summary>
    public const string Warichu = "Warichu";
    /// <summary>"WT": the text of a warichu.</summary>
    public const string WarichuText = "WT";
    /// <summary>"WP": warichu punctuation.</summary>
    public const string WarichuPunctuation = "WP";
    /// <summary>"Sect": a section, a general container element.</summary>
    public const string Sect = "Sect";
    /// <summary>"Div": a generic block-level grouping element.</summary>
    public const string Div = "Div";
    /// <summary>"P": a paragraph.</summary>
    public const string Paragraph = "P";
    /// <summary>"H1": heading level 1.</summary>
    public const string H1 = "H1";
    /// <summary>"H2": heading level 2.</summary>
    public const string H2 = "H2";
    /// <summary>"H3": heading level 3.</summary>
    public const string H3 = "H3";
    /// <summary>"H4": heading level 4.</summary>
    public const string H4 = "H4";
    /// <summary>"H5": heading level 5.</summary>
    public const string H5 = "H5";
    /// <summary>"H6": heading level 6.</summary>
    public const string H6 = "H6";
    /// <summary>"L": a list; its children are <see cref="ListItem"/> elements.</summary>
    public const string List = "L";
    /// <summary>"LI": a list item, containing a <see cref="Label"/> and a <see cref="ListBody"/>.</summary>
    public const string ListItem = "LI";
    /// <summary>"Lbl": the label of a list item (bullet or number).</summary>
    public const string Label = "Lbl";
    /// <summary>"LBody": the body (content) of a list item.</summary>
    public const string ListBody = "LBody";
    /// <summary>"Table": a table.</summary>
    public const string Table = "Table";
    /// <summary>"THead": the header row group of a table.</summary>
    public const string TableHead = "THead";
    /// <summary>"TBody": the body row group of a table.</summary>
    public const string TableBody = "TBody";
    /// <summary>"TFoot": the footer row group of a table.</summary>
    public const string TableFoot = "TFoot";
    /// <summary>"TR": a table row.</summary>
    public const string TableRow = "TR";
    /// <summary>"TH": a table header cell.</summary>
    public const string TableHeaderCell = "TH";
    /// <summary>"TD": a table data cell.</summary>
    public const string TableDataCell = "TD";
    /// <summary>"Span": a generic inline element.</summary>
    public const string Span = "Span";
    /// <summary>"Link": a hyperlink; associate the link annotation with <see cref="PdfStructureElement.AddAnnotation"/>.</summary>
    public const string Link = "Link";
    /// <summary>"Annot": an annotation that is not a link or a widget.</summary>
    public const string Annotation = "Annot";
    /// <summary>"Figure": an image or graphic; PDF/UA requires <see cref="PdfStructureElement.AlternateText"/>.</summary>
    public const string Figure = "Figure";
    /// <summary>"Formula": a mathematical formula.</summary>
    public const string Formula = "Formula";
    /// <summary>"Form": a form field (widget annotation).</summary>
    public const string Form = "Form";
    /// <summary>"Note": a footnote or endnote.</summary>
    public const string Note = "Note";
    /// <summary>"Caption": a caption of a figure or table.</summary>
    public const string Caption = "Caption";
    /// <summary>"Quote": an inline quotation.</summary>
    public const string Quote = "Quote";
    /// <summary>"BlockQuote": a block-level quotation.</summary>
    public const string BlockQuote = "BlockQuote";
    /// <summary>"TOC": a table of contents.</summary>
    public const string TableOfContents = "TOC";
    /// <summary>"TOCI": an item of a table of contents.</summary>
    public const string TableOfContentsItem = "TOCI";

    internal static readonly HashSet<string> All = new()
    {
        Document, Part, Article, Sect, Div, BlockQuote, Caption, TableOfContents, TableOfContentsItem, Index, NonStruct, Private,
        Paragraph, H, H1, H2, H3, H4, H5, H6, List, ListItem, Label, ListBody, Table, TableRow, TableHeaderCell, TableDataCell,
        TableHead, TableBody, TableFoot, Span, Quote, Note, Reference, BibEntry, Code, Link, Annotation, Ruby, RubyBase, RubyText,
        RubyPunctuation, Warichu, WarichuText, WarichuPunctuation, Figure, Formula, Form,
    };

    /// <summary>Returns "H1".."H6" for a heading level.</summary>
    public static string Heading(int level) => "H" + Math.Clamp(level, 1, 6);
}

/// <summary>Artifact types for <see cref="Graphics.PdfCanvas.BeginArtifact"/> (content that is not part of the logical structure).</summary>
public enum ArtifactType
{
    /// <summary>Untyped artifact.</summary>
    None,

    /// <summary>Running heads, page numbers.</summary>
    Pagination,

    /// <summary>Layout decoration (rules, table borders, backgrounds).</summary>
    Layout,

    /// <summary>Cut marks, color bars.</summary>
    Page,

    /// <summary>Background images, watermarks.</summary>
    Background,
}

/// <summary>
/// An element of the logical structure tree (tagged PDF). Content on pages is linked to an element by drawing it
/// between <see cref="Graphics.PdfCanvas.BeginTag"/> and <see cref="Graphics.PdfCanvas.EndTag"/>.
/// </summary>
public sealed class PdfStructureElement
{
    private readonly PdfStructureTree _tree;

    internal PdfStructureElement(PdfStructureTree tree, PdfDictionary dictionary, PdfReference reference)
    {
        _tree = tree;
        Dictionary = dictionary;
        Reference = reference;
    }

    /// <summary>The underlying structure element dictionary (/Type /StructElem).</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The indirect reference of the structure element dictionary.</summary>
    public PdfReference Reference { get; }

    /// <summary>The structure type (/S), e.g. "P", "H1", "Table".</summary>
    public string Type => Dictionary.GetName(PdfName.S)?.Value ?? "";

    /// <summary>Alternate description (/Alt), required for figures by PDF/UA.</summary>
    public string? AlternateText
    {
        get => Dictionary.GetText(PdfName.Get("Alt"));
        set => Dictionary.SetText(PdfName.Get("Alt"), value);
    }

    /// <summary>Replacement text (/ActualText), e.g. for ligatures or text drawn as graphics.</summary>
    public string? ActualText
    {
        get => Dictionary.GetText(PdfName.ActualText);
        set => Dictionary.SetText(PdfName.ActualText, value);
    }

    /// <summary>Title of the element (/T).</summary>
    public string? Title
    {
        get => Dictionary.GetText(PdfName.T);
        set => Dictionary.SetText(PdfName.T, value);
    }

    /// <summary>Natural language of the element (/Lang), e.g. "en-US".</summary>
    public string? Language
    {
        get => Dictionary.GetText(PdfName.Get("Lang"));
        set => Dictionary.SetText(PdfName.Get("Lang"), value);
    }

    /// <summary>Creates a child element at the end of this element's children.</summary>
    public PdfStructureElement AddChild(string type, string? alternateText = null)
    {
        var child = _tree.CreateElement(type, this);
        if (alternateText != null) child.AlternateText = alternateText;
        Kids().Add(child.Reference);
        return child;
    }

    /// <summary>Sets a structure attribute, e.g. <c>SetAttribute("Table", "Scope", PdfName.Get("Column"))</c>.</summary>
    public PdfStructureElement SetAttribute(string owner, string key, PdfObject value)
    {
        var attributes = Dictionary.Get(PdfName.Get("A"));
        PdfDictionary? target = null;
        IEnumerable<PdfObject?> candidates = attributes is PdfArray array ? array.Select(a => (PdfObject?)a.Resolve()) : new[] { attributes };
        foreach (var candidate in candidates)
            if (candidate is PdfDictionary d && d.GetName(PdfName.Get("O"))?.Value == owner) target = d;
        if (target == null)
        {
            target = new PdfDictionary();
            target[PdfName.Get("O")] = PdfName.Get(owner);
            if (attributes == null) Dictionary[PdfName.Get("A")] = target;
            else if (attributes is PdfArray a) a.Add(target);
            else Dictionary[PdfName.Get("A")] = new PdfArray(Dictionary[PdfName.Get("A")]!, target);
        }
        target[PdfName.Get(key)] = value;
        return this;
    }

    /// <summary>Links an annotation (link, widget, ...) to this element via an object reference (OBJR).</summary>
    public void AddAnnotation(PdfAnnotation annotation, PdfPage page) => _tree.AddObjectReference(this, annotation, page);

    internal PdfArray Kids()
    {
        if (Dictionary.Get(PdfName.K) is PdfArray kids) return kids;
        var array = new PdfArray();
        if (Dictionary[PdfName.K] is { } existing) array.Add(existing);
        Dictionary[PdfName.K] = array;
        return array;
    }
}

/// <summary>
/// The structure tree of a tagged PDF (/StructTreeRoot): the logical structure (headings, paragraphs, tables, figures)
/// that screen readers and reflow use. Obtain it with <see cref="PdfDocument.EnableTagging"/>.
/// </summary>
public sealed class PdfStructureTree
{
    private readonly PdfDocument _document;
    private readonly PdfDictionary _root;
    private readonly SortedDictionary<int, PdfObject> _parentTree = new();
    private readonly Dictionary<PdfDictionary, PageMarks> _pages = new(ReferenceEqualityComparer.Instance);
    private int _nextKey;
    private bool _dirty;

    private sealed class PageMarks(int key, PdfArray owners)
    {
        public int Key { get; } = key;
        public PdfArray Owners { get; } = owners;
    }

    internal PdfStructureTree(PdfDocument document)
    {
        _document = document;
        var catalog = document.Catalog;
        if (catalog.GetDictionary(PdfName.Get("StructTreeRoot")) is { } existing)
        {
            _root = existing;
            if (existing.GetDictionary(PdfName.Get("ParentTree")) is { } parentTree) ReadNumberTree(parentTree, 0);
            _nextKey = Math.Max(existing.GetInt(PdfName.Get("ParentTreeNextKey")), _parentTree.Count == 0 ? 0 : _parentTree.Keys.Max() + 1);
            Root = FindDocumentElement() ?? CreateRootElement();
        }
        else
        {
            _root = new PdfDictionary(PdfName.Get("StructTreeRoot"));
            catalog[PdfName.Get("StructTreeRoot")] = document.AddObject(_root);
            Root = CreateRootElement();
        }
        var markInfo = catalog.GetDictionary(PdfName.Get("MarkInfo"));
        if (markInfo == null) catalog[PdfName.Get("MarkInfo")] = markInfo = new PdfDictionary();
        markInfo.Set(PdfName.Get("Marked"), true);
        document.BeforeSave += _ => WriteParentTree();
    }

    /// <summary>The top-level "Document" element; add your content structure below it.</summary>
    public PdfStructureElement Root { get; }

    /// <summary>The structure tree root dictionary.</summary>
    public PdfDictionary Dictionary => _root;

    /// <summary>Maps a custom structure type to a standard type (/RoleMap), e.g. "Heading" → "H1".</summary>
    public void MapRole(string customType, string standardType)
    {
        var roleMap = _root.GetDictionary(PdfName.Get("RoleMap"));
        if (roleMap == null) _root[PdfName.Get("RoleMap")] = roleMap = new PdfDictionary();
        roleMap[PdfName.Get(customType)] = PdfName.Get(standardType);
    }

    private PdfStructureElement CreateRootElement()
    {
        var element = CreateElement(StructureType.Document, null);
        if (_root.Get(PdfName.K) is PdfArray kids) kids.Add(element.Reference);
        else if (_root[PdfName.K] is { } single) _root[PdfName.K] = new PdfArray(single, element.Reference);
        else _root[PdfName.K] = element.Reference;
        return element;
    }

    private PdfStructureElement? FindDocumentElement()
    {
        var k = _root[PdfName.K];
        if (k is PdfArray { Count: 1 } array) k = array[0];
        return k is PdfReference r && r.Resolve() is PdfDictionary d ? new PdfStructureElement(this, d, r) : null;
    }

    internal PdfStructureElement CreateElement(string type, PdfStructureElement? parent)
    {
        var dict = new PdfDictionary(PdfName.Get("StructElem"));
        dict[PdfName.S] = PdfName.Get(type);
        var reference = _document.AddObject(dict);
        dict[PdfName.P] = parent?.Reference ?? _document.GetReference(_root);
        return new PdfStructureElement(this, dict, reference);
    }

    /// <summary>Registers a new marked-content sequence of <paramref name="page"/> for <paramref name="element"/> and returns its MCID.</summary>
    internal int AddMarkedContent(PdfStructureElement element, PdfPage page)
    {
        var marks = GetPageMarks(page);
        int mcid = marks.Owners.Count;
        marks.Owners.Add(element.Reference);
        var kids = element.Kids();
        if (element.Dictionary[PdfName.Get("Pg")] == null)
        {
            element.Dictionary[PdfName.Get("Pg")] = page.Reference;
            kids.Add(new PdfNumber(mcid));
        }
        else if (element.Dictionary[PdfName.Get("Pg")] is PdfReference pg && pg.ObjectNumber == page.Reference.ObjectNumber)
            kids.Add(new PdfNumber(mcid));
        else
        {
            var mcr = new PdfDictionary(PdfName.Get("MCR"));
            mcr[PdfName.Get("Pg")] = page.Reference;
            mcr.Set(PdfName.Get("MCID"), mcid);
            kids.Add(mcr);
        }
        _dirty = true;
        return mcid;
    }

    internal void AddObjectReference(PdfStructureElement element, PdfAnnotation annotation, PdfPage page)
    {
        if (annotation.Reference == null) throw new ArgumentException("The annotation must be an indirect object.");
        var objr = new PdfDictionary(PdfName.Get("OBJR"));
        objr[PdfName.Get("Obj")] = annotation.Reference;
        objr[PdfName.Get("Pg")] = page.Reference;
        element.Kids().Add(objr);
        element.Dictionary[PdfName.Get("Pg")] ??= page.Reference;
        int key = _nextKey++;
        annotation.Dictionary.Set(PdfName.Get("StructParent"), key);
        _parentTree[key] = element.Reference;
        _dirty = true;
    }

    private PageMarks GetPageMarks(PdfPage page)
    {
        if (_pages.TryGetValue(page.Dictionary, out var marks)) return marks;
        PdfArray? owners = null;
        int key;
        if (page.Dictionary.Get(PdfName.Get("StructParents")) is PdfNumber n && _parentTree.TryGetValue(n.IntValue, out var existing))
        {
            key = n.IntValue;
            owners = existing.Resolve() as PdfArray;
        }
        else key = _nextKey++;
        if (owners == null)
        {
            owners = new PdfArray();
            _parentTree[key] = owners;
        }
        page.Dictionary.Set(PdfName.Get("StructParents"), key);
        marks = new PageMarks(key, owners);
        _pages[page.Dictionary] = marks;
        return marks;
    }

    private void ReadNumberTree(PdfDictionary node, int depth)
    {
        if (depth > 32) return;
        if (node.GetArray(PdfName.Get("Nums")) is { } nums)
        {
            for (int i = 0; i + 1 < nums.Count; i += 2)
                if (nums[i].Resolve() is PdfNumber key) _parentTree[key.IntValue] = nums[i + 1];
        }
        if (node.GetArray(PdfName.Get("Kids")) is { } kids)
            foreach (var kid in kids)
                if (kid.Resolve() is PdfDictionary d) ReadNumberTree(d, depth + 1);
    }

    private void WriteParentTree()
    {
        if (!_dirty && _root.ContainsKey(PdfName.Get("ParentTree"))) return;
        var nums = new PdfArray();
        foreach (var (key, value) in _parentTree)
        {
            nums.Add(new PdfNumber(key));
            // arrays are written as indirect objects to keep the tree compact and shareable
            nums.Add(value is PdfArray array ? _document.GetReference(array) ?? _document.AddObject(array) : value);
        }
        var tree = new PdfDictionary();
        tree[PdfName.Get("Nums")] = nums;
        _root[PdfName.Get("ParentTree")] = _document.AddObject(tree);
        _root.Set(PdfName.Get("ParentTreeNextKey"), _nextKey);
        _dirty = false;
    }
}
