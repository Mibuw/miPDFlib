using System.Text;
using System.Text.RegularExpressions;
using MiPdf.Fonts;
using MiPdf.Layout;
using MiPdf.Objects;
using MiPdf.Tagging;

namespace MiPdf.Tests.Tagging;

public class StructureImportTests
{
    /// <summary>A tagged document of three pages: heading, a paragraph with a link, a long table, a closing heading.</summary>
    private static PdfDocument CreateTagged(string title, string language)
    {
        var doc = PdfDocument.Create();
        doc.Language = language;
        doc.EnableTagging();
        using (var layout = new LayoutDocument(doc, PageSize.A6))
        {
            layout.Add(new Paragraph(title, PdfStandardFont.HelveticaBold, 14).AsHeading(1));
            layout.Add(new Paragraph("See the ").AddLink("documentation", "https://example.com").Add("."));
            var table = new Table(1, 3);
            table.AddHeaderCell("No.");
            table.AddHeaderCell("Text");
            for (int i = 1; i <= 60; i++) { table.AddCell(i.ToString()); table.AddCell($"Row {i}"); }
            layout.Add(table);
            layout.Add(new Paragraph("End", PdfStandardFont.HelveticaBold, 12).AsHeading(2));
        }
        return PdfDocument.Open(doc.ToArray());
    }

    private static Dictionary<int, PdfObject> ParentTree(PdfDocument doc)
    {
        var result = new Dictionary<int, PdfObject>();
        var nums = doc.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("ParentTree"))!.GetArray(PdfName.Get("Nums"))!;
        for (int i = 0; i + 1 < nums.Count; i += 2) result[((PdfNumber)nums[i]).IntValue] = nums[i + 1];
        return result;
    }

    /// <summary>Every marked-content sequence of every page has an owner in the parent tree that points back to the page.</summary>
    private static void AssertMarkedContentHasOwners(PdfDocument doc)
    {
        var parentTree = ParentTree(doc);
        foreach (var page in doc.Pages)
        {
            var content = Encoding.Latin1.GetString(page.GetContentBytes());
            var mcids = Regex.Matches(content, @"/MCID (\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
            if (mcids.Count == 0) continue;
            var owners = (PdfArray)parentTree[page.Dictionary.GetInt(PdfName.Get("StructParents"))].Resolve()!;
            foreach (int mcid in mcids)
            {
                var owner = (PdfDictionary)owners[mcid].Resolve()!;
                Assert.NotNull(owner.GetName(PdfName.S));
                var pg = owner.Get(PdfName.Get("Pg"));
                var kids = owner.Get(PdfName.K) as PdfArray ?? new PdfArray(owner[PdfName.K]!);
                bool linked = (ReferenceEquals(pg, page.Dictionary) && kids.Any(k => k.Resolve() is PdfNumber n && n.IntValue == mcid))
                    || kids.Any(k => k.Resolve() is PdfDictionary mcr && mcr.GetInt(PdfName.Get("MCID"), -1) == mcid
                        && ReferenceEquals(mcr.Get(PdfName.Get("Pg")), page.Dictionary));
                Assert.True(linked, $"MCID {mcid} is not a kid of its owner");
            }
        }
    }

    private static List<string> ElementTypes(PdfDocument doc)
    {
        var result = new List<string>();
        var pending = new Stack<PdfObject>();
        pending.Push(doc.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))![PdfName.K]!);
        while (pending.Count > 0)
        {
            switch (pending.Pop().Resolve())
            {
                case PdfArray array:
                    for (int i = array.Count - 1; i >= 0; i--) pending.Push(array[i]);
                    break;
                case PdfDictionary element when element.GetName(PdfName.S) is { } type:
                    result.Add(type.Value);
                    if (element[PdfName.K] is { } kids) pending.Push(kids);
                    break;
            }
        }
        return result;
    }

    [Fact]
    public void ExtractedPagesKeepTheirStructure()
    {
        using var source = CreateTagged("Report", "en-US");
        Assert.True(source.PageCount >= 3);
        var extracted = PdfDocument.Open(source.ExtractPages(new[] { 2 }).ToArray());

        Assert.True(extracted.IsTagged);
        Assert.Equal("en-US", extracted.Language);
        AssertMarkedContentHasOwners(extracted);
        var types = ElementTypes(extracted);
        Assert.Equal(StructureType.Document, types[0]);
        Assert.Contains(StructureType.Table, types);
        Assert.Contains(StructureType.TableDataCell, types);
        Assert.DoesNotContain(StructureType.H1, types); // the heading is on page 1
        Assert.DoesNotContain(StructureType.Link, types);
    }

    [Fact]
    public void AnnotationsKeepTheirStructureElement()
    {
        using var source = CreateTagged("Report", "en-US");
        var extracted = PdfDocument.Open(source.ExtractPages(new[] { 1 }).ToArray());
        var link = Assert.Single(extracted.GetPage(1).Annotations, a => a.Subtype == "Link");
        int key = link.Dictionary.GetInt(PdfName.Get("StructParent"), -1);
        var owner = (PdfDictionary)ParentTree(extracted)[key].Resolve()!;
        Assert.Equal(StructureType.Link, owner.GetName(PdfName.S)!.Value);
        var objr = ((PdfArray)owner.Get(PdfName.K)!).Select(k => k.Resolve()).OfType<PdfDictionary>()
            .Single(d => d.GetName(PdfName.Type)?.Value == "OBJR");
        Assert.Same(link.Dictionary, objr.Get(PdfName.Get("Obj")));
        AssertMarkedContentHasOwners(extracted);
    }

    [Fact]
    public void MergedDocumentsShareOneDocumentElement()
    {
        using var english = CreateTagged("English", "en-US");
        using var german = CreateTagged("Deutsch", "de-AT");
        var merged = PdfDocument.Open(PdfDocument.Merge(new[] { english, german }).ToArray());

        var root = merged.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var top = root.Get(PdfName.K) is PdfArray array ? Assert.Single(array).Resolve() : root.Get(PdfName.K);
        var document = Assert.IsAssignableFrom<PdfDictionary>(top);
        Assert.Equal(StructureType.Document, document.GetName(PdfName.S)!.Value);
        Assert.Equal(2, ElementTypes(merged).Count(t => t == StructureType.H1));
        // the elements of the second document keep its language
        var germanElements = ((PdfArray)document.Get(PdfName.K)!).Select(k => k.Resolve()).OfType<PdfDictionary>()
            .Where(e => e.GetText(PdfName.Get("Lang")) == "de-AT").ToList();
        Assert.NotEmpty(germanElements);
        Assert.Equal("en-US", merged.Language);
        AssertMarkedContentHasOwners(merged);
    }

    [Fact]
    public void ElementsOutsideTheTreeKeepTheirContent()
    {
        using var source = CreateTagged("Report", "en-US");
        // detach the first heading from the tree, as some producers do with their own element types
        var root = source.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var document = (PdfDictionary)(root.Get(PdfName.K) is PdfArray a ? a[0].Resolve()! : root.Get(PdfName.K)!);
        var kids = (PdfArray)document.Get(PdfName.K)!;
        var heading = kids.Select(k => k.Resolve()).OfType<PdfDictionary>().First(e => e.GetName(PdfName.S)!.Value == StructureType.H1);
        kids.RemoveAt(kids.ToList().FindIndex(k => ReferenceEquals(k.Resolve(), heading)));
        heading.Remove(PdfName.P);

        var extracted = PdfDocument.Open(source.ExtractPages(new[] { 1 }).ToArray());
        AssertMarkedContentHasOwners(extracted);
        Assert.Contains(StructureType.H1, ElementTypes(extracted));
    }

    [Fact]
    public void NoStructureIsAddedToUntaggedPages()
    {
        var untagged = PdfDocument.Create();
        untagged.AddPage();
        using var tagged = CreateTagged("Tagged", "en-US");
        var merged = PdfDocument.Open(PdfDocument.Merge(new[] { PdfDocument.Open(untagged.ToArray()), tagged }).ToArray());
        Assert.False(merged.IsTagged);
        Assert.Null(merged.Catalog.Get(PdfName.Get("StructTreeRoot")));
    }

    [Fact]
    public void PageCopiedWithinTheDocumentDoesNotShareStructureEntries()
    {
        using var doc = CreateTagged("Report", "en-US");
        var copy = doc.ImportPages(doc, new[] { 1 })[0];
        Assert.False(copy.Dictionary.ContainsKey(PdfName.Get("StructParents")));
        Assert.True(doc.GetPage(1).Dictionary.ContainsKey(PdfName.Get("StructParents")));
    }
}
