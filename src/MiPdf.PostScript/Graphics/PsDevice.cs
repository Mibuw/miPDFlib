using MiPdf.Graphics;
using MiPdf.Objects;

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
}

/// <summary>Writes each page shown with <c>showpage</c> as a page of a PDF document.</summary>
internal sealed class PsPageDevice : PsDevice
{
    private readonly PdfDocument _document;
    private PdfPage _page = null!;
    private PsContentWriter _content = null!;

    public PsPageDevice(PdfDocument document, double width, double height)
    {
        _document = document;
        Width = width;
        Height = height;
        NewPage();
    }

    public override PsContentWriter Content => _content;

    public int PagesShown { get; private set; }

    private void NewPage()
    {
        // the page object exists from the start (resources are added while painting); it is removed if never shown
        _page = _document.AddPage(new PdfRectangle(0, 0, Width, Height));
        _content = new PsContentWriter(_page);
    }

    public override void ShowPage()
    {
        _page.Dictionary[PdfName.MediaBox] = PdfArray.FromRectangle(new PdfRectangle(0, 0, Width, Height));
        _page.Dictionary[PdfName.Contents] = _document.AddObject(new PdfStream(_content.ToArray()));
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
    /// painted (EPS files and programs without showpage).
    /// </summary>
    public void Finish()
    {
        if (PagesShown == 0 && _content.HasContent) ShowPage();
        _document.RemovePage(_document.PageCount - 1);
    }
}

/// <summary>Writes everything painted into one form XObject (EPS import); <c>showpage</c> is ignored.</summary>
internal sealed class PsFormDevice : PsDevice
{
    private readonly PsContentWriter _content;

    public PsFormDevice(PdfFormXObject form)
    {
        Form = form;
        Width = form.Width;
        Height = form.Height;
        _content = new PsContentWriter(form);
    }

    public PdfFormXObject Form { get; }

    public override PsContentWriter Content => _content;

    public override void ShowPage()
    {
    }

    public void Finish() => Form.Stream.SetBytes(_content.ToArray());
}
