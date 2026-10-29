using MiPdf.Objects;

namespace MiPdf;

/// <summary>Accessor for the document information dictionary (trailer /Info).</summary>
public sealed class PdfDocumentInfo
{
    private readonly PdfDocument _document;

    internal PdfDocumentInfo(PdfDocument document) => _document = document;

    /// <summary>The underlying dictionary; created on first write access.</summary>
    public PdfDictionary? Dictionary => _document.Trailer.GetDictionary(PdfName.Info);

    private PdfDictionary GetOrCreate()
    {
        var info = Dictionary;
        if (info == null)
        {
            info = new PdfDictionary();
            _document.Trailer[PdfName.Info] = _document.AddObject(info);
        }
        return info;
    }

    public string? Title { get => Get(PdfName.Title); set => Set(PdfName.Title, value); }

    public string? Author { get => Get(PdfName.Author); set => Set(PdfName.Author, value); }

    public string? Subject { get => Get(PdfName.Subject); set => Set(PdfName.Subject, value); }

    public string? Keywords { get => Get(PdfName.Keywords); set => Set(PdfName.Keywords, value); }

    public string? Creator { get => Get(PdfName.Creator); set => Set(PdfName.Creator, value); }

    public string? Producer { get => Get(PdfName.Producer); set => Set(PdfName.Producer, value); }

    public DateTimeOffset? CreationDate
    {
        get => PdfDate.Parse(Get(PdfName.CreationDate));
        set => Set(PdfName.CreationDate, value is { } d ? PdfDate.Format(d) : null);
    }

    public DateTimeOffset? ModificationDate
    {
        get => PdfDate.Parse(Get(PdfName.ModDate));
        set => Set(PdfName.ModDate, value is { } d ? PdfDate.Format(d) : null);
    }

    /// <summary>Reads a custom entry.</summary>
    public string? Get(PdfName key) => Dictionary?.GetText(key);

    /// <summary>Removes an entry.</summary>
    public void Remove(PdfName key) => Dictionary?.Remove(key);

    /// <summary>Writes a custom entry; <c>null</c> removes it.</summary>
    public void Set(PdfName key, string? value)
    {
        if (value == null)
        {
            Dictionary?.Remove(key);
            return;
        }
        GetOrCreate().SetText(key, value);
    }
}
