using System.Text;
using MiPdf.Content;
using MiPdf.Objects;
using MiPdf.PdfA;

namespace MiPdf.Tagging;

/// <summary>
/// Checks the machine-checkable PDF/UA-1 requirements (Matterhorn protocol subset): tagging, untagged content,
/// metadata, title, language, embedded fonts, alternate text of figures, annotations, form fields and permissions.
/// Semantic aspects (correct reading order, meaningful alternate texts) still need human review;
/// use a full validator such as veraPDF or PAC for certification.
/// </summary>
public static class PdfUAChecker
{
    private static readonly HashSet<string> PaintingOperators = new() { "Tj", "TJ", "'", "\"", "Do", "sh", "BI", "f", "F", "f*", "B", "B*", "b", "b*", "S", "s" };

    public static IReadOnlyList<string> Check(PdfDocument document)
    {
        var issues = new List<string>();
        var catalog = document.Catalog;
        if (!document.IsTagged) issues.Add("Document is not marked as tagged (/MarkInfo /Marked true).");
        var treeRoot = catalog.GetDictionary(PdfName.Get("StructTreeRoot"));
        if (treeRoot == null) issues.Add("Structure tree (/StructTreeRoot) is missing.");
        if (string.IsNullOrWhiteSpace(document.Language)) issues.Add("Document language (/Lang) is not set.");
        if (catalog.GetDictionary(PdfName.Get("ViewerPreferences"))?.GetBool(PdfName.Get("DisplayDocTitle")) != true)
            issues.Add("ViewerPreferences /DisplayDocTitle must be true.");

        string xmp = document.GetXmpMetadata() is { } x ? Encoding.UTF8.GetString(x) : "";
        if (!xmp.Contains("pdfuaid:part", StringComparison.Ordinal)) issues.Add("XMP metadata does not contain the PDF/UA identifier (pdfuaid:part).");
        if (string.IsNullOrWhiteSpace(document.Info.Title) || !xmp.Contains("<dc:title>", StringComparison.Ordinal))
            issues.Add("Document title is missing (Info /Title and XMP dc:title).");

        if (document.IsEncrypted && document.Trailer.GetDictionary(PdfName.Encrypt) is { } encrypt && (encrypt.GetInt(PdfName.P) & 512) == 0)
            issues.Add("Encryption must allow content extraction for accessibility (permission bit 10).");

        var fonts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var resources = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < document.PageCount; i++)
        {
            var page = document.GetPage(i + 1);
            string where = $"page {i + 1}";
            if (page.GetInheritable(PdfName.Resources) is PdfDictionary res) CheckFonts(res, where, issues, fonts, resources);
            int untagged = CountUntaggedContent(page.GetContentBytes(), out bool usesMcid);
            if (untagged > 0) issues.Add($"{where}: {untagged} content operation(s) are neither tagged nor marked as artifact.");
            if (usesMcid && !page.Dictionary.ContainsKey(PdfName.Get("StructParents")))
                issues.Add($"{where}: marked content is used but the page has no /StructParents entry.");

            var annotations = page.Annotations;
            if (annotations.Any(a => a.Subtype != "Popup") && page.Dictionary.GetName(PdfName.Get("Tabs"))?.Value != "S")
                issues.Add($"{where}: pages with annotations must have /Tabs /S.");
            foreach (var annotation in annotations)
            {
                if (annotation.Subtype is "Popup" || (annotation.Dictionary.GetInt(PdfName.F) & 2) != 0) continue;
                var normal = annotation.Dictionary.GetDictionary(PdfName.AP)?.Get(PdfName.N);
                IEnumerable<PdfObject> appearances = normal is PdfStream ? new[] { normal } : normal is PdfDictionary states ? states.Select(kv => kv.Value.Resolve()) : Array.Empty<PdfObject>();
                foreach (var ap in appearances)
                    if (ap is PdfStream apStream && apStream.GetDictionary(PdfName.Resources) is { } apRes) CheckFonts(apRes, where + " annotation", issues, fonts, resources);
                bool zeroSize = annotation.Rect.Width <= 0 || annotation.Rect.Height <= 0; // e.g. invisible signature fields
                if (!zeroSize && !annotation.Dictionary.ContainsKey(PdfName.Get("StructParent")))
                    issues.Add($"{where}: {annotation.Subtype} annotation is not included in the structure tree.");
                if (annotation.Subtype == "Link" && string.IsNullOrWhiteSpace(annotation.Contents))
                    issues.Add($"{where}: link annotation without alternate description (/Contents).");
                if (annotation.Subtype == "Widget" && !HasFieldDescription(annotation.Dictionary))
                    issues.Add($"{where}: form field without description (/TU).");
            }
        }

        if (treeRoot != null)
        {
            var roleMap = treeRoot.GetDictionary(PdfName.Get("RoleMap"));
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            int lastHeading = 0;
            CheckStructure(treeRoot.Get(PdfName.K), roleMap, issues, visited, ref lastHeading, 0);
        }
        return issues;
    }

    private static bool HasFieldDescription(PdfDictionary widget)
    {
        var node = widget;
        for (int i = 0; node != null && i < 32; i++)
        {
            if (!string.IsNullOrWhiteSpace(node.GetText(PdfName.Get("TU")))) return true;
            node = node.GetDictionary(PdfName.Parent);
        }
        return false;
    }

    private static void CheckFonts(PdfDictionary res, string where, List<string> issues, HashSet<PdfDictionary> fonts, HashSet<PdfDictionary> visited)
    {
        if (!visited.Add(res)) return;
        if (res.GetDictionary(PdfName.Font) is { } fontDict)
        {
            foreach (var (name, value) in fontDict)
            {
                if (value.Resolve() is not PdfDictionary font || !fonts.Add(font)) continue;
                if (!PdfAChecker.IsEmbedded(font))
                    issues.Add($"{where}: font {font.GetName(PdfName.BaseFont)?.Value ?? name.Value} is not embedded.");
            }
        }
        if (res.GetDictionary(PdfName.XObject) is { } xobjects)
        {
            foreach (var (_, value) in xobjects)
                if (value.Resolve() is PdfStream xo && xo.GetDictionary(PdfName.Resources) is { } inner) CheckFonts(inner, where, issues, fonts, visited);
        }
    }

    /// <summary>Counts painting operators outside of tagged (MCID) or artifact marked content.</summary>
    internal static int CountUntaggedContent(byte[] content, out bool usesMcid)
    {
        usesMcid = false;
        var stack = new Stack<bool>(); // true = inside tagged content or artifact
        int count = 0;
        foreach (var op in ContentParser.Parse(content))
        {
            switch (op.Operator)
            {
                case "BMC":
                case "BDC":
                    bool covered = stack.Count > 0 && stack.Peek();
                    var tag = op.Operands.Count > 0 ? op.Operands[0] as PdfName : null;
                    if (tag?.Value == "Artifact") covered = true;
                    if (op.Operator == "BDC" && op.Operands.Count > 1 && op.Operands[1] is PdfDictionary props && props.ContainsKey(PdfName.Get("MCID")))
                    {
                        covered = true;
                        usesMcid = true;
                    }
                    stack.Push(covered);
                    break;
                case "EMC":
                    if (stack.Count > 0) stack.Pop();
                    break;
                default:
                    if (PaintingOperators.Contains(op.Operator) && !(stack.Count > 0 && stack.Peek())) count++;
                    break;
            }
        }
        return count;
    }

    private static void CheckStructure(PdfObject? node, PdfDictionary? roleMap, List<string> issues, HashSet<PdfDictionary> visited, ref int lastHeading, int depth)
    {
        if (node == null || depth > 100) return;
        if (node.Resolve() is PdfArray array)
        {
            foreach (var item in array) CheckStructure(item, roleMap, issues, visited, ref lastHeading, depth);
            return;
        }
        if (node.Resolve() is not PdfDictionary element || !visited.Add(element)) return;
        if (element.GetName(PdfName.Type)?.Value is "MCR" or "OBJR") return;
        if (element.GetName(PdfName.S) is not { } typeName) return;

        string type = typeName.Value;
        for (int i = 0; i < 10 && !StructureType.All.Contains(type) && roleMap?.GetName(PdfName.Get(type)) is { } mapped; i++) type = mapped.Value;
        if (!StructureType.All.Contains(type)) issues.Add($"Structure type '{typeName.Value}' is neither standard nor role-mapped to a standard type.");

        if (type is StructureType.Figure or StructureType.Formula &&
            string.IsNullOrWhiteSpace(element.GetText(PdfName.Get("Alt"))) && string.IsNullOrWhiteSpace(element.GetText(PdfName.ActualText)))
            issues.Add($"{type} element without alternate text (/Alt).");

        if (type.Length == 2 && type[0] == 'H' && char.IsAsciiDigit(type[1]))
        {
            int level = type[1] - '0';
            if (level > lastHeading + 1) issues.Add($"Heading level skipped: {type} follows {(lastHeading == 0 ? "no heading" : "H" + lastHeading)}.");
            lastHeading = level;
        }
        CheckStructure(element.Get(PdfName.K), roleMap, issues, visited, ref lastHeading, depth + 1);
    }
}
