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

    public void ErasePage() => Content.Erase();

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

    public PsPageDevice(PdfDocument document, double width, double height)
    {
        Document = document;
        Images = new PsPdfImages(document);
        Width = width;
        Height = height;
        NewPage();
    }

    public override PsContentWriter Content => _content;

    public int PagesShown { get; private set; }

    private void NewPage()
    {
        // the page object exists from the start (resources are added while painting); it is removed if never shown
        _page = Document!.AddPage(new PdfRectangle(0, 0, Width, Height));
        _content = new PsContentWriter(_page);
    }

    public override void ShowPage()
    {
        _page.Dictionary[PdfName.MediaBox] = PdfArray.FromRectangle(new PdfRectangle(0, 0, Width, Height));
        _page.Dictionary[PdfName.Contents] = Document!.AddObject(new PdfStream(_content.ToArray()));
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
        Document!.RemovePage(Document.PageCount - 1);
        Fonts?.Finish();
    }
}

/// <summary>Writes everything painted into one form XObject (EPS import); <c>showpage</c> is ignored.</summary>
internal sealed class PsFormDevice : PsDevice
{
    private readonly PsContentWriter _content;

    public PsFormDevice(PdfFormXObject form)
    {
        Form = form;
        Document = form.Document;
        Images = new PsPdfImages(form.Document);
        Width = form.Width;
        Height = form.Height;
        _content = new PsContentWriter(form);
    }

    public PdfFormXObject Form { get; }

    public override PsContentWriter Content => _content;

    public override void ShowPage()
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
