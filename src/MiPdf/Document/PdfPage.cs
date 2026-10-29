using System.Text;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf;

/// <summary>A page of a <see cref="PdfDocument"/>.</summary>
public sealed partial class PdfPage : IResourceContainer
{
    internal static readonly PdfName[] InheritableKeys = { PdfName.Resources, PdfName.MediaBox, PdfName.CropBox, PdfName.Rotate };

    internal PdfPage(PdfDocument document, PdfDictionary dictionary, PdfReference reference)
    {
        Document = document;
        Dictionary = dictionary;
        Reference = reference;
    }

    public PdfDocument Document { get; }

    /// <summary>The page dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The indirect reference of the page dictionary.</summary>
    public PdfReference Reference { get; }

    /// <summary>Returns an attribute, following the /Parent chain for inheritable attributes.</summary>
    public PdfObject? GetInheritable(PdfName key)
    {
        var node = Dictionary;
        for (int depth = 0; node != null && depth < 64; depth++)
        {
            var value = node.Get(key);
            if (value != null) return value;
            node = node.GetDictionary(PdfName.Parent);
        }
        return null;
    }

    /// <summary>Copies inherited attributes into the page dictionary itself.</summary>
    internal void MaterializeInheritedAttributes()
    {
        foreach (var key in InheritableKeys)
        {
            if (Dictionary.ContainsKey(key)) continue;
            var value = GetInheritable(key);
            if (value != null) Dictionary[key] = Document.GetReference(value) ?? value.Clone();
        }
        if (!Dictionary.ContainsKey(PdfName.MediaBox)) Dictionary[PdfName.MediaBox] = PdfArray.FromRectangle(PageSize.Letter);
    }

    /// <summary>The media box (physical page size). Defaults to US Letter if missing.</summary>
    public PdfRectangle MediaBox
    {
        get => (GetInheritable(PdfName.MediaBox) as PdfArray)?.ToRectangle() ?? PageSize.Letter;
        set => Dictionary[PdfName.MediaBox] = PdfArray.FromRectangle(value);
    }

    /// <summary>The crop box (visible region). Defaults to the media box.</summary>
    public PdfRectangle CropBox
    {
        get => (GetInheritable(PdfName.CropBox) as PdfArray)?.ToRectangle() is { } r ? Intersect(r, MediaBox) : MediaBox;
        set => Dictionary[PdfName.CropBox] = PdfArray.FromRectangle(value);
    }

    public PdfRectangle? TrimBox
    {
        get => Dictionary.GetRectangle(PdfName.TrimBox);
        set => Dictionary[PdfName.TrimBox] = value is { } r ? PdfArray.FromRectangle(r) : null;
    }

    public PdfRectangle? BleedBox
    {
        get => Dictionary.GetRectangle(PdfName.BleedBox);
        set => Dictionary[PdfName.BleedBox] = value is { } r ? PdfArray.FromRectangle(r) : null;
    }

    public PdfRectangle? ArtBox
    {
        get => Dictionary.GetRectangle(PdfName.ArtBox);
        set => Dictionary[PdfName.ArtBox] = value is { } r ? PdfArray.FromRectangle(r) : null;
    }

    private static PdfRectangle Intersect(PdfRectangle a, PdfRectangle b)
    {
        var r = new PdfRectangle(Math.Max(a.Left, b.Left), Math.Max(a.Bottom, b.Bottom), Math.Min(a.Right, b.Right), Math.Min(a.Top, b.Top));
        return r.Width > 0 && r.Height > 0 ? r : a;
    }

    /// <summary>Page rotation in degrees (0, 90, 180 or 270), clockwise when displayed.</summary>
    public int Rotation
    {
        get
        {
            int r = (GetInheritable(PdfName.Rotate) as PdfNumber)?.IntValue ?? 0;
            r %= 360;
            if (r < 0) r += 360;
            return r / 90 * 90;
        }
        set
        {
            int r = value % 360;
            if (r < 0) r += 360;
            if (r % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90.", nameof(value));
            Dictionary.Set(PdfName.Rotate, r);
        }
    }

    /// <summary>Displayed width in points (crop box, considering rotation).</summary>
    public double Width => Rotation % 180 == 0 ? CropBox.Width : CropBox.Height;

    /// <summary>Displayed height in points (crop box, considering rotation).</summary>
    public double Height => Rotation % 180 == 0 ? CropBox.Height : CropBox.Width;

    /// <summary>
    /// The page's own resource dictionary. Inherited resources are copied into the page on first access,
    /// so that adding resources never affects other pages.
    /// </summary>
    public PdfDictionary Resources
    {
        get
        {
            if (Dictionary.Get(PdfName.Resources) is PdfDictionary own) return own;
            var inherited = GetInheritable(PdfName.Resources) as PdfDictionary;
            var copy = inherited != null ? (PdfDictionary)inherited.Clone() : new PdfDictionary();
            Dictionary[PdfName.Resources] = copy;
            return copy;
        }
    }

    /// <summary>The content streams of the page in order.</summary>
    public IReadOnlyList<PdfStream> GetContentStreams()
    {
        return Dictionary.Get(PdfName.Contents) switch
        {
            PdfStream s => new[] { s },
            PdfArray a => a.Select(o => o.Resolve()).OfType<PdfStream>().ToArray(),
            _ => Array.Empty<PdfStream>(),
        };
    }

    /// <summary>Returns the decoded, concatenated content of the page.</summary>
    public byte[] GetContentBytes()
    {
        var streams = GetContentStreams();
        if (streams.Count == 1) return streams[0].GetBytes();
        using var ms = new MemoryStream();
        foreach (var s in streams)
        {
            ms.Write(s.GetBytes());
            ms.WriteByte((byte)'\n');
        }
        return ms.ToArray();
    }

    /// <summary>Replaces the page content with a single stream.</summary>
    public void SetContent(byte[] content, bool compress = true)
    {
        var stream = new PdfStream(content, compress);
        Dictionary[PdfName.Contents] = Document.AddObject(stream);
    }

    /// <summary>
    /// Appends content drawn on top of the existing content. With <paramref name="isolate"/> the existing
    /// content is wrapped in <c>q ... Q</c> so its graphics state cannot leak into the new content.
    /// </summary>
    public void AppendContent(byte[] content, bool isolate = true, bool compress = true)
    {
        var contents = GetContentArray();
        if (isolate && contents.Count > 0 && !IsIsolated(contents))
        {
            contents.Insert(0, Document.AddObject(new PdfStream(Encoding.ASCII.GetBytes("q\n"), compress: false)));
            contents.Add(Document.AddObject(new PdfStream(Encoding.ASCII.GetBytes("\nQ\n"), compress: false)));
        }
        contents.Add(Document.AddObject(new PdfStream(content, compress)));
    }

    /// <summary>Prepends content drawn below the existing content (e.g. backgrounds, watermarks under text).</summary>
    public void PrependContent(byte[] content, bool compress = true)
    {
        var contents = GetContentArray();
        byte[] wrapped = [.. "q\n"u8, .. content, .. "\nQ\n"u8];
        contents.Insert(0, Document.AddObject(new PdfStream(wrapped, compress)));
    }

    private bool IsIsolated(PdfArray contents) =>
        contents.Count >= 2 && contents.GetStream(0) is { } first && first.GetBytes().AsSpan().SequenceEqual("q\n"u8);

    /// <summary>Converts /Contents into an array (so streams can be added) and returns it.</summary>
    private PdfArray GetContentArray()
    {
        var raw = Dictionary[PdfName.Contents];
        switch (raw?.Resolve())
        {
            case PdfArray a:
                if (raw is PdfReference) Dictionary[PdfName.Contents] = a; // make it direct so changes stay local
                return a;
            case PdfStream s:
                var array = new PdfArray(raw is PdfReference ? raw : Document.AddObject(s));
                Dictionary[PdfName.Contents] = array;
                return array;
            default:
                var empty = new PdfArray();
                Dictionary[PdfName.Contents] = empty;
                return empty;
        }
    }

    /// <summary>The annotations array of the page (created on demand).</summary>
    public PdfArray GetOrCreateAnnotations()
    {
        if (Dictionary.Get(PdfName.Annots) is PdfArray a) return a;
        var annots = new PdfArray();
        Dictionary[PdfName.Annots] = annots;
        return annots;
    }

    /// <summary>
    /// Adds <paramref name="resource"/> to the resource category (e.g. /Font, /XObject) and returns its name.
    /// If the same indirect object is already registered, its existing name is returned.
    /// </summary>
    public PdfName AddResource(PdfName category, PdfObject resource, string prefix) =>
        ResourceHelper.Add(Resources, category, resource, prefix);

    /// <summary>
    /// Returns a canvas for drawing on this page. The drawing is added when the canvas is disposed.
    /// With <paramref name="background"/> the content is placed below the existing page content.
    /// </summary>
    public PdfCanvas GetCanvas(bool background = false) => new(this, background);
}
