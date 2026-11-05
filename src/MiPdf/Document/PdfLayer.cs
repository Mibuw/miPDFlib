using MiPdf.Objects;

namespace MiPdf;

/// <summary>
/// A layer (optional content group, ISO 32000-2, 8.11): content that viewers can show or hide, e.g. a watermark,
/// a language version or the dimensions of a technical drawing. Visibility and lock state are those of the default
/// configuration (/OCProperties /D) that viewers apply when opening the document.
/// </summary>
public sealed class PdfLayer
{
    private readonly PdfDocument _document;

    internal PdfLayer(PdfDocument document, PdfDictionary dictionary, PdfReference reference)
    {
        _document = document;
        Dictionary = dictionary;
        Reference = reference;
    }

    /// <summary>The optional content group dictionary (/Type /OCG).</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The indirect reference of the group.</summary>
    public PdfReference Reference { get; }

    /// <summary>The name shown in the layers panel of viewers.</summary>
    public string Name
    {
        get => Dictionary.GetText(PdfName.Name) ?? "";
        set => Dictionary.SetText(PdfName.Name, value);
    }

    /// <summary>True if the layer is shown when the document is opened (default configuration).</summary>
    public bool IsVisible
    {
        get => _document.IsLayerVisible(Reference);
        set => _document.SetLayerState(this, "OFF", !value);
    }

    /// <summary>True if viewers do not let the user change the visibility of the layer (/Locked of the default configuration).</summary>
    public bool IsLocked
    {
        get => _document.LayerConfiguration()?.GetArray(PdfName.Get("Locked")) is { } locked && PdfDocument.ContainsReference(locked, Reference);
        set => _document.SetLayerState(this, "Locked", value);
    }

    /// <summary>Returns the name of the layer.</summary>
    public override string ToString() => Name;
}
