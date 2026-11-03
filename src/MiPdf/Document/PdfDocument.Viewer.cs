using MiPdf.Objects;

namespace MiPdf;

/// <summary>Page layout a viewer uses when the document is opened (/PageLayout, ISO 32000-1, 7.7.2).</summary>
public enum PdfPageLayout
{
    /// <summary>One page at a time (the default).</summary>
    SinglePage,
    /// <summary>The pages in one continuous column.</summary>
    OneColumn,
    /// <summary>The pages in two columns, odd pages on the left.</summary>
    TwoColumnLeft,
    /// <summary>The pages in two columns, odd pages on the right.</summary>
    TwoColumnRight,
    /// <summary>Two pages at a time, odd pages on the left (PDF 1.5).</summary>
    TwoPageLeft,
    /// <summary>Two pages at a time, odd pages on the right (PDF 1.5).</summary>
    TwoPageRight,
}

/// <summary>Which panel a viewer shows when the document is opened (/PageMode, ISO 32000-1, 7.7.2).</summary>
public enum PdfPageMode
{
    /// <summary>Neither bookmarks nor thumbnails (the default).</summary>
    UseNone,
    /// <summary>The bookmarks panel.</summary>
    UseOutlines,
    /// <summary>The page thumbnails panel.</summary>
    UseThumbs,
    /// <summary>Full-screen mode, without menu bar, window controls or other windows.</summary>
    FullScreen,
    /// <summary>The optional content (layers) panel (PDF 1.5).</summary>
    UseOC,
    /// <summary>The attachments panel (PDF 1.6).</summary>
    UseAttachments,
}

/// <summary>
/// Viewer preferences of the document (/ViewerPreferences, ISO 32000-1, 12.2). Missing entries read as
/// <c>false</c>, the PDF default; setting <c>false</c> removes the entry.
/// </summary>
public sealed class PdfViewerPreferences
{
    private static readonly PdfName Key = PdfName.Get("ViewerPreferences");
    private readonly PdfDocument _document;

    internal PdfViewerPreferences(PdfDocument document) => _document = document;

    /// <summary>The underlying dictionary; created on first write access.</summary>
    public PdfDictionary? Dictionary => _document.Catalog.GetDictionary(Key);

    /// <summary>Hide the viewer's tool bars (/HideToolbar).</summary>
    public bool HideToolbar { get => Get("HideToolbar"); set => Set("HideToolbar", value); }

    /// <summary>Hide the viewer's menu bar (/HideMenubar).</summary>
    public bool HideMenubar { get => Get("HideMenubar"); set => Set("HideMenubar", value); }

    /// <summary>Hide user interface elements such as scroll bars, leaving only the page contents (/HideWindowUI).</summary>
    public bool HideWindowUI { get => Get("HideWindowUI"); set => Set("HideWindowUI", value); }

    /// <summary>Resize the document window to fit the first page (/FitWindow).</summary>
    public bool FitWindow { get => Get("FitWindow"); set => Set("FitWindow", value); }

    /// <summary>Center the document window on the screen (/CenterWindow).</summary>
    public bool CenterWindow { get => Get("CenterWindow"); set => Set("CenterWindow", value); }

    /// <summary>Show the document title from the metadata instead of the file name in the title bar (/DisplayDocTitle;
    /// required by PDF/UA).</summary>
    public bool DisplayDocTitle { get => Get("DisplayDocTitle"); set => Set("DisplayDocTitle", value); }

    private bool Get(string key) => Dictionary?.GetBool(PdfName.Get(key)) == true;

    private void Set(string key, bool value)
    {
        var prefs = Dictionary;
        if (!value)
        {
            prefs?.Remove(PdfName.Get(key));
            return;
        }
        if (prefs == null) _document.Catalog[Key] = prefs = new PdfDictionary();
        prefs.Set(PdfName.Get(key), true);
    }
}

public sealed partial class PdfDocument
{
    private PdfViewerPreferences? _viewerPreferences;

    /// <summary>Page layout used when the document is opened (/PageLayout); <see cref="PdfPageLayout.SinglePage"/>
    /// if missing or unknown.</summary>
    public PdfPageLayout PageLayout
    {
        get => Enum.TryParse(Catalog.GetName(PdfName.Get("PageLayout"))?.Value, out PdfPageLayout layout) ? layout : PdfPageLayout.SinglePage;
        set => Catalog[PdfName.Get("PageLayout")] = PdfName.Get(value.ToString());
    }

    /// <summary>Panel shown when the document is opened (/PageMode); <see cref="PdfPageMode.UseNone"/> if missing or
    /// unknown. Creating the bookmark tree (first access of <see cref="Outlines"/>) sets <see cref="PdfPageMode.UseOutlines"/>.</summary>
    public PdfPageMode PageMode
    {
        get => Enum.TryParse(Catalog.GetName(PdfName.Get("PageMode"))?.Value, out PdfPageMode mode) ? mode : PdfPageMode.UseNone;
        set => Catalog[PdfName.Get("PageMode")] = PdfName.Get(value.ToString());
    }

    /// <summary>Viewer preferences (/ViewerPreferences).</summary>
    public PdfViewerPreferences ViewerPreferences => _viewerPreferences ??= new PdfViewerPreferences(this);
}
