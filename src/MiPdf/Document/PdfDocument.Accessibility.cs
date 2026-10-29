using MiPdf.IO;
using MiPdf.Objects;
using MiPdf.Tagging;

namespace MiPdf;

/// <summary>PDF/UA conformance levels (ISO 14289).</summary>
public enum PdfUAConformance
{
    /// <summary>PDF/UA-1 (ISO 14289-1).</summary>
    PdfUA1,
}

public sealed partial class PdfDocument
{
    private PdfStructureTree? _structureTree;
    private PdfUAConformance? _uaConformance;

    /// <summary>The structure tree if tagging was enabled with <see cref="EnableTagging"/>, otherwise null.</summary>
    public PdfStructureTree? StructureTree => _structureTree;

    /// <summary>True if the document is marked as tagged PDF (/MarkInfo /Marked true).</summary>
    public bool IsTagged => Catalog.GetDictionary(PdfName.Get("MarkInfo"))?.GetBool(PdfName.Get("Marked")) == true;

    /// <summary>
    /// Turns the document into a tagged PDF and returns its structure tree. Existing structure trees are continued.
    /// Afterwards the layout engine tags paragraphs, tables, images and barcodes automatically; custom drawing can be
    /// tagged with <see cref="Graphics.PdfCanvas.BeginTag"/> or marked as artifact.
    /// </summary>
    public PdfStructureTree EnableTagging() => _structureTree ??= new PdfStructureTree(this);

    /// <summary>Default natural language of the document (/Lang), e.g. "de-AT". Required by PDF/UA.</summary>
    public string? Language
    {
        get => Catalog.GetText(PdfName.Get("Lang"));
        set => Catalog.SetText(PdfName.Get("Lang"), value);
    }

    /// <summary>
    /// PDF/UA conformance to produce. Setting it enables tagging; saving adds the PDF/UA identification to the XMP
    /// metadata, sets DisplayDocTitle, the tab order of pages with annotations and tags annotations that are not
    /// in the structure tree yet. Use <see cref="CheckPdfUA"/> to find remaining problems (e.g. untagged content,
    /// fonts that are not embedded, missing alternate text).
    /// </summary>
    public PdfUAConformance? UAConformance
    {
        get => _uaConformance;
        set
        {
            _uaConformance = value;
            if (value != null) EnableTagging();
        }
    }

    /// <summary>
    /// Checks the document against the machine-checkable PDF/UA rules (see <see cref="PdfUAChecker"/>).
    /// If <see cref="UAConformance"/> is set, the fixes that saving applies (metadata, tab order, annotation tags)
    /// are applied first, so the result reflects the document as it will be saved.
    /// </summary>
    public IReadOnlyList<string> CheckPdfUA()
    {
        if (_uaConformance != null)
        {
            var options = new PdfSaveOptions();
            ApplyAccessibility(options);
            ApplyConformance(options);
        }
        return PdfUAChecker.Check(this);
    }

    private void ApplyAccessibility(PdfSaveOptions options)
    {
        if (_uaConformance == null) return;
        var tree = EnableTagging();
        var prefs = Catalog.GetDictionary(PdfName.Get("ViewerPreferences"));
        if (prefs == null) Catalog[PdfName.Get("ViewerPreferences")] = prefs = new PdfDictionary();
        prefs.Set(PdfName.Get("DisplayDocTitle"), true);
        foreach (var page in Pages)
        {
            var annotations = page.Annotations;
            if (annotations.Count == 0) continue;
            page.Dictionary[PdfName.Get("Tabs")] = PdfName.S;
            foreach (var annotation in annotations)
            {
                if (annotation.Subtype is "Popup" || annotation.Dictionary.ContainsKey(PdfName.Get("StructParent")) || annotation.Reference == null)
                    continue;
                if ((annotation.Dictionary.GetInt(PdfName.F) & 2) != 0) continue; // hidden annotations are not tagged
                string type = annotation.Subtype switch
                {
                    "Link" => StructureType.Link,
                    "Widget" => StructureType.Form,
                    _ => StructureType.Annotation,
                };
                if (annotation.Subtype == "Link" && annotation.Contents == null && annotation.Uri is { } uri) annotation.Contents = uri;
                var element = tree.Root.AddChild(type);
                if (annotation.Subtype == "Widget" && FieldDescription(annotation.Dictionary) is { } description) element.AlternateText = description;
                element.AddAnnotation(annotation, page);
            }
        }
    }

    private static string? FieldDescription(PdfDictionary widget)
    {
        var node = widget;
        for (int i = 0; node != null && i < 32; i++)
        {
            if (node.GetText(PdfName.Get("TU")) is { } tu) return tu;
            node = node.GetDictionary(PdfName.Parent);
        }
        return null;
    }
}
