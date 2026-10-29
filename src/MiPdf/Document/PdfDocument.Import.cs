using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Copies pages of another document to the end of this document (or at <paramref name="insertIndex"/>).
    /// Resources shared between the copied pages are copied only once. Links between copied pages are kept.
    /// </summary>
    /// <param name="source">The source document.</param>
    /// <param name="pageNumbers">1-based page numbers to copy; all pages if null.</param>
    /// <param name="insertIndex">0-based position in this document; appended if null.</param>
    /// <returns>The new pages in this document.</returns>
    public IReadOnlyList<PdfPage> ImportPages(PdfDocument source, IEnumerable<int>? pageNumbers = null, int? insertIndex = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var numbers = (pageNumbers ?? Enumerable.Range(1, source.PageCount)).ToList();
        var sourcePages = numbers.Select(source.GetPage).ToList();
        var result = new List<PdfPage>();
        int index = insertIndex ?? PageCount;

        if (ReferenceEquals(source, this))
        {
            foreach (var page in sourcePages)
            {
                page.MaterializeInheritedAttributes();
                var copy = (PdfDictionary)page.Dictionary.Clone();
                copy.Remove(PdfName.Parent);
                copy.Remove(PdfName.Annots); // annotations belong to exactly one page
                var newPage = new PdfPage(this, copy, AddObject(copy));
                InsertPage(index++, newPage);
                result.Add(newPage);
            }
            return result;
        }

        var importer = new ObjectImporter(this);
        // reserve target page objects first so that links between imported pages are preserved
        var targets = new List<(PdfPage Source, PdfReference Target)>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in sourcePages)
        {
            var reference = AddObject(new PdfDictionary(PdfName.Page));
            if (seen.Add(page.Dictionary)) importer.RegisterPage(page.Dictionary, reference);
            targets.Add((page, reference));
        }
        foreach (var (page, reference) in targets)
        {
            var dict = importer.ImportPageDictionary(page);
            dict[PdfName.Type] = PdfName.Page;
            ReplaceObject(reference, dict);
            var newPage = new PdfPage(this, dict, reference);
            InsertPage(index++, newPage);
            result.Add(newPage);
        }
        OnPagesImported(source, importer, targets);
        return result;
    }

    /// <summary>Called after pages were imported (used by forms to merge fields).</summary>
    partial void OnPagesImported(PdfDocument source, ObjectImporter importer, List<(PdfPage Source, PdfReference Target)> pages);

    /// <summary>Copies a single page of another document.</summary>
    public PdfPage ImportPage(PdfPage sourcePage, int? insertIndex = null) =>
        ImportPages(sourcePage.Document, new[] { sourcePage.Document.Pages.ToList().IndexOf(sourcePage) + 1 }, insertIndex)[0];

    /// <summary>Deep-copies an object (and everything it references) from another document into this one.</summary>
    public PdfObject ImportObject(PdfObject obj) => new ObjectImporter(this).Import(obj);

    /// <summary>
    /// Converts a page of another (or this) document into a form XObject that can be drawn on any page,
    /// e.g. to use a PDF letterhead as background (<c>canvas.DrawXObject(template, 0, 0)</c>).
    /// </summary>
    public PdfFormXObject ImportPageAsTemplate(PdfPage sourcePage)
    {
        var importer = new ObjectImporter(this);
        var box = sourcePage.CropBox;
        // map the crop box to (0, 0, displayed width, displayed height), honoring the page rotation
        string cm = sourcePage.Rotation switch
        {
            90 => $"0 -1 1 0 {F(-box.Bottom)} {F(box.Right)} cm\n",
            180 => $"-1 0 0 -1 {F(box.Right)} {F(box.Top)} cm\n",
            270 => $"0 1 -1 0 {F(box.Top)} {F(-box.Left)} cm\n",
            _ => $"1 0 0 1 {F(-box.Left)} {F(-box.Bottom)} cm\n",
        };
        byte[] content = [.. System.Text.Encoding.ASCII.GetBytes("q " + cm), .. sourcePage.GetContentBytes(), .. "\nQ\n"u8];
        var template = new PdfFormXObject(this, new PdfRectangle(0, 0, sourcePage.Width, sourcePage.Height));
        if (sourcePage.GetInheritable(PdfName.Resources) is PdfDictionary resources)
            template.Stream[PdfName.Resources] = ReferenceEquals(sourcePage.Document, this) ? resources : importer.Import(resources);
        template.Stream.SetBytes(content);
        return template;

        static string F(double v) => PdfNumber.FormatReal(v);
    }

    /// <summary>Creates a new document containing copies of the given pages (1-based page numbers).</summary>
    public PdfDocument ExtractPages(IEnumerable<int> pageNumbers)
    {
        var result = Create();
        result.Info.Producer = Info.Producer ?? "miPDFlib";
        result.ImportPages(this, pageNumbers);
        return result;
    }

    /// <summary>Splits the document into documents of at most <paramref name="pagesPerDocument"/> pages.</summary>
    public IEnumerable<PdfDocument> Split(int pagesPerDocument = 1)
    {
        if (pagesPerDocument < 1) throw new ArgumentOutOfRangeException(nameof(pagesPerDocument));
        for (int start = 1; start <= PageCount; start += pagesPerDocument)
            yield return ExtractPages(Enumerable.Range(start, Math.Min(pagesPerDocument, PageCount - start + 1)));
    }

    /// <summary>Merges documents into a new document (pages in order).</summary>
    public static PdfDocument Merge(IEnumerable<PdfDocument> documents)
    {
        var result = Create();
        foreach (var doc in documents) result.ImportPages(doc);
        return result;
    }

    /// <summary>Merges PDF files into a new document.</summary>
    public static PdfDocument Merge(params string[] paths) => Merge(paths.Select(p => Open(p)));
}
