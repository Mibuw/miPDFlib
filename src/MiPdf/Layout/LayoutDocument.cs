using MiPdf.Graphics;

namespace MiPdf.Layout;

/// <summary>Information passed to header/footer callbacks.</summary>
public sealed class PageContext
{
    internal PageContext(PdfPage page, PdfCanvas canvas, int pageNumber, int totalPages, PdfRectangle contentArea)
    {
        Page = page;
        Canvas = canvas;
        PageNumber = pageNumber;
        TotalPages = totalPages;
        ContentArea = contentArea;
    }

    public PdfPage Page { get; }

    public PdfCanvas Canvas { get; }

    /// <summary>1-based page number within the pages created by the layout document.</summary>
    public int PageNumber { get; }

    public int TotalPages { get; }

    /// <summary>The area inside the margins.</summary>
    public PdfRectangle ContentArea { get; }

    /// <summary>The full page (media box).</summary>
    public PdfRectangle PageArea => Page.MediaBox;
}

/// <summary>
/// Flows <see cref="LayoutElement"/>s (paragraphs, tables, images) onto pages, creating new pages as needed.
/// Headers and footers are drawn when the layout is closed, so they can show the total page count.
/// </summary>
/// <example>
/// <code>
/// using var pdf = PdfDocument.Create();
/// using (var layout = new LayoutDocument(pdf, PageSize.A4, new Thickness(50)))
/// {
///     layout.Footer = ctx => ctx.Canvas.DrawText($"Page {ctx.PageNumber} of {ctx.TotalPages}",
///         PdfStandardFont.Helvetica, 8, ctx.ContentArea.Right, 30, alignment: TextAlignment.Right);
///     layout.Add(new Paragraph("Hello world", PdfStandardFont.HelveticaBold, 18));
/// }
/// pdf.Save("out.pdf");
/// </code>
/// </example>
public sealed class LayoutDocument : IDisposable
{
    private readonly List<PdfPage> _pages = new();
    private PdfCanvas? _canvas;
    private bool _closed;

    public LayoutDocument(PdfDocument document, PdfRectangle? pageSize = null, Thickness? margins = null)
    {
        Document = document;
        PageSize = pageSize ?? MiPdf.PageSize.A4;
        Margins = margins ?? new Thickness(56.7); // 2 cm
    }

    public PdfDocument Document { get; }

    public PdfRectangle PageSize { get; set; }

    public Thickness Margins { get; set; }

    /// <summary>Called for every page when the layout is closed (draw headers here).</summary>
    public Action<PageContext>? Header { get; set; }

    /// <summary>Called for every page when the layout is closed (draw footers/page numbers here).</summary>
    public Action<PageContext>? Footer { get; set; }

    /// <summary>Called when a new page is started (e.g. to draw a background or letterhead).</summary>
    public Action<PageContext>? PageStarted { get; set; }

    /// <summary>
    /// Structure element that receives the tags of added elements when the document is tagged
    /// (default: the root "Document" element). Set it to a "Sect" or "Div" element to build a deeper structure.
    /// </summary>
    public Tagging.PdfStructureElement? StructureParent
    {
        get => _structureParent ?? Document.StructureTree?.Root;
        set
        {
            _structureParent = value;
            if (_canvas != null) _canvas.StructureParent = StructureParent;
        }
    }

    private Tagging.PdfStructureElement? _structureParent;

    /// <summary>The page currently being filled (null before the first element).</summary>
    public PdfPage? CurrentPage => _pages.Count > 0 ? _pages[^1] : null;

    /// <summary>Pages created by this layout document.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>Current vertical position (PDF y coordinate of the top of the free area).</summary>
    public double CurrentY { get; private set; }

    /// <summary>Remaining height on the current page.</summary>
    public double RemainingHeight => CurrentPage == null ? 0 : CurrentY - ContentArea.Bottom;

    public PdfRectangle ContentArea => new(PageSize.Left + Margins.Left, PageSize.Bottom + Margins.Bottom,
        PageSize.Right - Margins.Right, PageSize.Top - Margins.Top);

    /// <summary>The canvas of the current page (for custom drawing at absolute positions).</summary>
    public PdfCanvas Canvas
    {
        get
        {
            if (_canvas == null) NewPage();
            return _canvas!;
        }
    }

    /// <summary>Adds an element at the current position, continuing on new pages as needed.</summary>
    public LayoutDocument Add(LayoutElement element)
    {
        if (_closed) throw new InvalidOperationException("The layout document is closed.");
        if (element is PageBreak)
        {
            NewPage();
            return this;
        }
        if (CurrentPage == null) NewPage();
        var area = ContentArea;
        LayoutBox? box = element.Prepare(area.Width);
        bool freshPage = IsPageEmpty();
        while (box != null)
        {
            var rest = box.Render(_canvas!, area.Left, CurrentY, RemainingHeight, freshPage, out double used);
            CurrentY -= used;
            if (rest == null) break;
            if (freshPage && used <= 0) break; // cannot make progress (should not happen)
            NewPage();
            freshPage = true;
            box = rest;
        }
        return this;
    }

    public LayoutDocument Add(string text) => Add(new Paragraph(text));

    /// <summary>Moves the cursor down by <paramref name="height"/> points.</summary>
    public LayoutDocument AddSpace(double height)
    {
        if (CurrentPage == null) NewPage();
        CurrentY = Math.Max(ContentArea.Bottom, CurrentY - height);
        return this;
    }

    private bool IsPageEmpty() => Math.Abs(CurrentY - ContentArea.Top) < 0.001;

    /// <summary>Starts a new page.</summary>
    public PdfPage NewPage()
    {
        _canvas?.Flush();
        var page = Document.AddPage(PageSize);
        _pages.Add(page);
        _canvas = page.GetCanvas();
        _canvas.StructureParent = StructureParent;
        CurrentY = ContentArea.Top;
        if (PageStarted != null) AsArtifact(_canvas, Tagging.ArtifactType.Background, () => PageStarted(new PageContext(page, _canvas, _pages.Count, 0, ContentArea)));
        return page;
    }

    /// <summary>Finishes the current page and draws headers and footers.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _canvas?.Flush();
        if (Header == null && Footer == null) return;
        for (int i = 0; i < _pages.Count; i++)
        {
            using var canvas = _pages[i].GetCanvas();
            var ctx = new PageContext(_pages[i], canvas, i + 1, _pages.Count, ContentArea);
            AsArtifact(canvas, Tagging.ArtifactType.Pagination, () =>
            {
                Header?.Invoke(ctx);
                Footer?.Invoke(ctx);
            });
        }
    }

    /// <summary>Header, footer and page backgrounds are artifacts in tagged documents.</summary>
    private static void AsArtifact(PdfCanvas canvas, Tagging.ArtifactType type, Action draw)
    {
        if (!canvas.IsTagged)
        {
            draw();
            return;
        }
        var parent = canvas.StructureParent;
        canvas.StructureParent = null;
        canvas.BeginArtifact(type);
        draw();
        canvas.EndArtifact();
        canvas.StructureParent = parent;
    }

    public void Dispose() => Close();
}
