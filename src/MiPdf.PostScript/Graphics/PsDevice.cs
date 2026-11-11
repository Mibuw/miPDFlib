using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.PostScript.Fonts;

namespace MiPdf.PostScript.Graphics;

/// <summary>
/// The output device of the interpreter: pages of a PDF document (the converter) or one form XObject (EPS import).
/// Device space is 1/720 inch with the origin at the lower left corner.
/// </summary>
internal abstract class PsDevice
{
    public abstract PsContentWriter Content { get; }

    /// <summary>The page (or graphic) size in points.</summary>
    public double Width { get; protected set; }

    public double Height { get; protected set; }

    /// <summary>The document written to (null for the null device).</summary>
    public PdfDocument? Document { get; protected init; }

    /// <summary>The fonts of the document.</summary>
    public PsPdfFonts? Fonts { get; set; }

    /// <summary>The images of the document.</summary>
    public PsPdfImages? Images { get; set; }

    /// <summary>The default transformation from user space (points) to device space.</summary>
    public virtual Matrix DefaultMatrix => Matrix.Scale(PsContentWriter.DeviceScale, PsContentWriter.DeviceScale);

    /// <summary><c>showpage</c>: the page is finished.</summary>
    public abstract void ShowPage();

    /// <summary><c>setpagedevice</c> with a page size.</summary>
    public virtual void SetPageSize(double width, double height)
    {
        Width = width;
        Height = height;
    }

    public virtual void ErasePage() => Content.Erase();

    public virtual void Fill(PsGState gs, PsDeviceColor color, bool evenOdd) => Content.Fill(gs, color, evenOdd);

    public virtual void Stroke(PsGState gs, PsDeviceColor color) => Content.Stroke(gs, color);

    public virtual void Text(PsGState gs, PsDeviceColor color, PdfFontEntry font, Matrix tm, ReadOnlySpan<byte> codes, int mode, double lineWidth) =>
        Content.ShowText(gs, color, font.Reference, tm, codes, mode, lineWidth);

    /// <summary>Paints an image; <paramref name="unitToDevice"/> maps the unit square to device space.</summary>
    public virtual void Image(PsGState gs, Matrix unitToDevice, PsImageOutput image, PsDeviceColor color)
    {
        if (Document == null) return;
        Images ??= new PsPdfImages(Document);
        var xobject = PsPdfImages.IsInline(image) ? null : Images.Get(image);
        Content.Image(gs, unitToDevice, image, xobject, color);
    }

    /// <summary>Paints a shading; <paramref name="toDevice"/> maps the shading's space to device space.</summary>
    public virtual void PaintShading(PsGState gs, PdfReference shading, Matrix toDevice) => Content.Shading(gs, shading, toDevice);

    /// <summary>Paints a form XObject; <paramref name="toDevice"/> maps form space to device space.</summary>
    public virtual void PaintForm(PsGState gs, PdfReference form, Matrix toDevice) => Content.Form(gs, form, toDevice);
}

/// <summary><c>nulldevice</c>: painting has no effect (used to measure or to run code for its side effects).</summary>
internal sealed class PsNullDevice : PsDevice
{
    private readonly PsContentWriter _content = new(null);

    public PsNullDevice()
    {
        Width = Height = 0;
    }

    public override PsContentWriter Content => _content;

    public override void ShowPage() => _content.Erase();

    /// <summary>The null device has the identity as default matrix (PLRM nulldevice).</summary>
    public override Matrix DefaultMatrix => Matrix.Identity;

    public override void Text(PsGState gs, PsDeviceColor color, PdfFontEntry font, Matrix tm, ReadOnlySpan<byte> codes, int mode, double lineWidth)
    {
    }

    public override void Image(PsGState gs, Matrix unitToDevice, PsImageOutput image, PsDeviceColor color)
    {
    }

    public override void PaintShading(PsGState gs, PdfReference shading, Matrix toDevice)
    {
    }

    public override void PaintForm(PsGState gs, PdfReference form, Matrix toDevice)
    {
    }
}

/// <summary>Writes each page shown with <c>showpage</c> as a page of a PDF document.</summary>
internal sealed class PsPageDevice : PsDevice
{
    private PdfPage _page = null!;
    private PsContentWriter _content = null!;
    private readonly Matrix _defaultMatrix;

    /// <summary>The characters shown on the current page whose baseline runs at 0, 90, 180 or 270 degrees.</summary>
    private readonly long[] _textByDirection = new long[4];

    /// <param name="document">The document the pages are added to.</param>
    /// <param name="width">The page width in points.</param>
    /// <param name="height">The page height in points.</param>
    /// <param name="left">The left edge of the page in PostScript default user space (an EPS file cropped to its bounding box).</param>
    /// <param name="bottom">The bottom edge of the page in PostScript default user space.</param>
    public PsPageDevice(PdfDocument document, double width, double height, double left = 0, double bottom = 0)
    {
        Document = document;
        Images = new PsPdfImages(document);
        Marks = new PsPdfMarks(document);
        Width = width;
        Height = height;
        _defaultMatrix = Matrix.Translate(-left, -bottom).Multiply(base.DefaultMatrix);
        NewPage();
    }

    public override Matrix DefaultMatrix => _defaultMatrix;

    public override void ErasePage()
    {
        base.ErasePage();
        Array.Clear(_textByDirection);
    }

    /// <summary>
    /// Pages get a /Rotate entry so that they are read upright, as Ghostscript's AutoRotatePages /PageByPage does: by
    /// the orientation from the DSC comments, otherwise by the direction most of the text runs in.
    /// </summary>
    public bool AutoRotate { get; init; }

    /// <summary>The orientation of the pages from the DSC comments (0 or 90): of all pages, and of single pages.</summary>
    public int? DocumentOrientation { get; init; }

    public IReadOnlyList<int?> PageOrientations { get; init; } = Array.Empty<int?>();

    public override void Text(PsGState gs, PsDeviceColor color, PdfFontEntry font, Matrix tm, ReadOnlySpan<byte> codes, int mode, double lineWidth)
    {
        base.Text(gs, color, font, tm, codes, mode, lineWidth);
        double angle = Math.Atan2(tm.B, tm.A) * 180 / Math.PI;
        double quarter = Math.Round(angle / 90);
        if (Math.Abs(angle - quarter * 90) <= 1) _textByDirection[((int)quarter % 4 + 4) % 4] += codes.Length;
    }

    /// <summary>The rotation of the page being finished (0 if it stays as it is).</summary>
    private int PageRotation()
    {
        int? dsc = PagesShown < PageOrientations.Count ? PageOrientations[PagesShown] : null;
        if ((dsc ?? DocumentOrientation) is { } orientation) return orientation;
        int best = 0;
        for (int i = 1; i < 4; i++)
            if (_textByDirection[i] > _textByDirection[best]) best = i;
        return best * 90;
    }

    /// <summary>What pdfmark adds to the document.</summary>
    public PsPdfMarks Marks { get; }

    /// <summary>The number of the page being painted (1-based).</summary>
    public int CurrentPageNumber => PagesShown + 1;

    /// <summary>
    /// The page with a number (1-based), created ahead if it does not exist yet (pdfmark refers to later pages): the
    /// device paints into it when it gets there. Null for numbers out of reach.
    /// </summary>
    public PdfPage? PageObject(int number)
    {
        if (number < 1 || number > CurrentPageNumber + 10_000) return null;
        while (Document!.PageCount < number) Document.AddPage(new PdfRectangle(0, 0, Width, Height));
        return Document.GetPage(number);
    }

    public override PsContentWriter Content => _content;

    public int PagesShown { get; private set; }

    private void NewPage()
    {
        // the page object exists from the start (resources are added while painting); it is removed if never shown
        _page = PageObject(CurrentPageNumber)!;
        _page.Dictionary[PdfName.MediaBox] = PdfArray.FromRectangle(new PdfRectangle(0, 0, Width, Height));
        _content = new PsContentWriter(_page);
    }

    public override void ShowPage()
    {
        _page.Dictionary[PdfName.MediaBox] = PdfArray.FromRectangle(new PdfRectangle(0, 0, Width, Height));
        _page.Dictionary[PdfName.Contents] = Document!.AddObject(new PdfStream(_content.ToArray()));
        // a rotation set with pdfmark is kept
        if (AutoRotate && PageRotation() is var rotation and not 0 && !_page.Dictionary.ContainsKey(PdfName.Rotate))
            _page.Dictionary[PdfName.Rotate] = new PdfNumber(rotation);
        Array.Clear(_textByDirection);
        PagesShown++;
        NewPage();
    }

    public override void SetPageSize(double width, double height)
    {
        base.SetPageSize(width, height);
        _page.Dictionary[PdfName.MediaBox] = PdfArray.FromRectangle(new PdfRectangle(0, 0, Width, Height));
    }

    /// <summary>
    /// Ends the job: a page that was not shown is dropped, except when no page was shown at all but something was
    /// painted (EPS files and programs without showpage). The fonts are written.
    /// </summary>
    public void Finish()
    {
        if (PagesShown == 0 && _content.HasContent) ShowPage();
        // the page begun after the last showpage, and pages created ahead that were never reached
        while (Document!.PageCount > PagesShown) Document.RemovePage(Document.PageCount - 1);
        Fonts?.Finish();
        Marks.Finish();
    }
}

/// <summary>
/// Writes everything painted into one form XObject (EPS import); <c>showpage</c> is ignored. As with Ghostscript's
/// EPSCrop, the device is as large as the bounding box and the default matrix moves its lower left corner to the
/// origin. The content does not rely on the graphics state it is drawn with.
/// </summary>
internal sealed class PsFormDevice : PsDevice
{
    private readonly PsContentWriter _content;
    private readonly Matrix _defaultMatrix;

    /// <param name="form">The form XObject; its size is the size of the device.</param>
    /// <param name="left">The left edge of the graphic in PostScript default user space.</param>
    /// <param name="bottom">The bottom edge of the graphic in PostScript default user space.</param>
    public PsFormDevice(PdfFormXObject form, double left = 0, double bottom = 0)
    {
        Form = form;
        Document = form.Document;
        Images = new PsPdfImages(form.Document);
        Width = form.Width;
        Height = form.Height;
        _defaultMatrix = Matrix.Translate(-left, -bottom).Multiply(Matrix.Scale(PsContentWriter.DeviceScale, PsContentWriter.DeviceScale));
        _content = new PsContentWriter(form, unknownStart: true);
    }

    public PdfFormXObject Form { get; }

    public override PsContentWriter Content => _content;

    public override Matrix DefaultMatrix => _defaultMatrix;

    public override void ShowPage()
    {
    }

    /// <summary>The graphic keeps its bounding box.</summary>
    public override void SetPageSize(double width, double height)
    {
    }

    public void Finish()
    {
        Form.Stream.SetBytes(_content.ToArray());
        Fonts?.Finish();
    }
}

/// <summary>Captures the glyph procedure of a Type 3 font: painting goes into the glyph's content (glyph space).</summary>
internal sealed class PsGlyphDevice : PsDevice
{
    private readonly PsContentWriter _content;

    public PsGlyphDevice(PsDevice? parent, PsContentWriter content)
    {
        _content = content;
        Document = parent?.Document;
        Fonts = parent?.Fonts;
        Images = parent?.Images;
    }

    public override PsContentWriter Content => _content;

    public override Matrix DefaultMatrix => Matrix.Identity;

    public override void ShowPage()
    {
    }
}

/// <summary>
/// Captures what a pattern's or a form's PaintProc paints into the content of a pattern cell or form XObject. Painting
/// happens in device space as on the parent device; the content transforms it back to pattern or form space.
/// </summary>
internal sealed class PsCaptureDevice : PsDevice
{
    private readonly PsContentWriter _content;
    private readonly Matrix _defaultMatrix;

    public PsCaptureDevice(PsDevice parent, PsContentWriter content)
    {
        _content = content;
        _defaultMatrix = parent.DefaultMatrix;
        Document = parent.Document;
        Fonts = parent.Fonts;
        Images = parent.Images;
        Width = parent.Width;
        Height = parent.Height;
    }

    public override PsContentWriter Content => _content;

    public override Matrix DefaultMatrix => _defaultMatrix;

    public override void ShowPage()
    {
    }

    public override void SetPageSize(double width, double height)
    {
    }
}

/// <summary>charpath with Type 3 fonts: what the glyph procedure paints is collected as path (in device space).</summary>
internal sealed class PsPathDevice : PsDevice
{
    private readonly PsContentWriter _content = new(null);

    public PsPathDevice(PsDevice? parent)
    {
        Document = parent?.Document;
        Fonts = parent?.Fonts;
    }

    public readonly List<PsSegment> Collected = new();

    public override PsContentWriter Content => _content;

    public override Matrix DefaultMatrix => Matrix.Identity;

    public override void ShowPage()
    {
    }

    public override void Fill(PsGState gs, PsDeviceColor color, bool evenOdd) => Collected.AddRange(gs.Path.Segments);

    public override void Stroke(PsGState gs, PsDeviceColor color) => Collected.AddRange(PsStroker.Outline(gs).Segments);

    public override void Text(PsGState gs, PsDeviceColor color, PdfFontEntry font, Matrix tm, ReadOnlySpan<byte> codes, int mode, double lineWidth)
    {
    }

    public override void Image(PsGState gs, Matrix unitToDevice, PsImageOutput image, PsDeviceColor color)
    {
    }

    public override void PaintShading(PsGState gs, PdfReference shading, Matrix toDevice)
    {
    }
}

/// <summary>Registers resources in a resource dictionary (of a Type 3 font).</summary>
internal sealed class PsDictionaryResources : IResourceContainer
{
    private readonly PdfDictionary _resources;

    public PsDictionaryResources(PdfDocument document, PdfDictionary resources)
    {
        Document = document;
        _resources = resources;
    }

    public PdfDocument Document { get; }

    public PdfName AddResource(PdfName category, PdfObject resource, string prefix) => ResourceHelper.Add(_resources, category, resource, prefix);
}
