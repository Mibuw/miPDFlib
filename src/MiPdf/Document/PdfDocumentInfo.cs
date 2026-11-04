using MiPdf.Objects;

namespace MiPdf;

/// <summary>Accessor for the document information dictionary (trailer /Info).</summary>
public sealed class PdfDocumentInfo
{
    private readonly PdfDocument _document;

    internal PdfDocumentInfo(PdfDocument document) => _document = document;

    private PdfStream? _xmpSource;
    private PdfDictionary? _xmpValues;

    /// <summary>The underlying dictionary; created on first write access.</summary>
    /// <remarks>
    /// Documents without a document information dictionary (PDF 2.0 deprecates it, PDF/A-4 forbids it) are read from
    /// the XMP metadata instead; the first change then creates the dictionary from these values.
    /// </remarks>
    public PdfDictionary? Dictionary => _document.Trailer.GetDictionary(PdfName.Info);

    private PdfDictionary GetOrCreate()
    {
        var info = Dictionary;
        if (info == null)
        {
            info = new PdfDictionary();
            if (FromXmp() is { } values)
                foreach (var (key, value) in values) info[key] = value;
            _document.Trailer[PdfName.Info] = _document.AddObject(info);
        }
        return info;
    }

    /// <summary>The information properties of the XMP metadata (cached until the metadata stream is replaced).</summary>
    private PdfDictionary? FromXmp()
    {
        var stream = _document.Catalog.GetStream(PdfName.Metadata);
        if (stream == null) return null;
        if (!ReferenceEquals(stream, _xmpSource))
        {
            _xmpValues = PdfA.XmpMetadata.ReadInfo(stream.GetBytes());
            _xmpSource = stream;
        }
        return _xmpValues;
    }

    /// <summary>Document title (/Title); <c>null</c> removes the entry.</summary>
    public string? Title { get => Get(PdfName.Title); set => Set(PdfName.Title, value); }

    /// <summary>Author (/Author); <c>null</c> removes the entry.</summary>
    public string? Author { get => Get(PdfName.Author); set => Set(PdfName.Author, value); }

    /// <summary>Subject (/Subject); <c>null</c> removes the entry.</summary>
    public string? Subject { get => Get(PdfName.Subject); set => Set(PdfName.Subject, value); }

    /// <summary>Keywords (/Keywords); <c>null</c> removes the entry.</summary>
    public string? Keywords { get => Get(PdfName.Keywords); set => Set(PdfName.Keywords, value); }

    /// <summary>Application that created the original content (/Creator); <c>null</c> removes the entry.</summary>
    public string? Creator { get => Get(PdfName.Creator); set => Set(PdfName.Creator, value); }

    /// <summary>Application that produced the PDF (/Producer); <c>null</c> removes the entry.</summary>
    public string? Producer { get => Get(PdfName.Producer); set => Set(PdfName.Producer, value); }

    /// <summary>Creation date (/CreationDate); null if missing or not parsable. <c>null</c> removes the entry.</summary>
    public DateTimeOffset? CreationDate
    {
        get => PdfDate.Parse(Get(PdfName.CreationDate));
        set => Set(PdfName.CreationDate, value is { } d ? PdfDate.Format(d) : null);
    }

    /// <summary>Modification date (/ModDate); null if missing or not parsable. <c>null</c> removes the entry.</summary>
    public DateTimeOffset? ModificationDate
    {
        get => PdfDate.Parse(Get(PdfName.ModDate));
        set => Set(PdfName.ModDate, value is { } d ? PdfDate.Format(d) : null);
    }

    /// <summary>Reads a custom entry.</summary>
    public string? Get(PdfName key) => (Dictionary ?? FromXmp())?.GetText(key);

    /// <summary>Removes an entry.</summary>
    public void Remove(PdfName key)
    {
        if (Dictionary != null || FromXmp()?.ContainsKey(key) == true) GetOrCreate().Remove(key);
    }

    /// <summary>Writes a custom entry; <c>null</c> removes it.</summary>
    public void Set(PdfName key, string? value)
    {
        if (value == null)
        {
            Remove(key);
            return;
        }
        GetOrCreate().SetText(key, value);
    }
}
