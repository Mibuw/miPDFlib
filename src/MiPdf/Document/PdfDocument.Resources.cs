using MiPdf.Fonts;
using MiPdf.Images;
using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf;

public sealed partial class PdfDocument
{
    private readonly Dictionary<PdfFont, FontResource> _fontResources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfImage, PdfReference> _images = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(double, double, string?), PdfReference> _graphicsStates = new();

    /// <summary>Raised before the document is written; used by features that finalize objects lazily.</summary>
    internal event Action<PdfSaveOptions>? BeforeSave;

    internal FontResource GetFontResource(PdfFont font)
    {
        if (!_fontResources.TryGetValue(font, out var resource))
        {
            resource = font.CreateResource(this);
            _fontResources[font] = resource;
        }
        return resource;
    }

    /// <summary>Returns the image XObject for <paramref name="image"/>, embedding it on first use.</summary>
    public PdfReference GetImageReference(PdfImage image)
    {
        if (!_images.TryGetValue(image, out var reference))
        {
            reference = AddObject(image.CreateStream(this));
            _images[image] = reference;
        }
        return reference;
    }

    /// <summary>Returns an ExtGState with the given constant alpha values and blend mode.</summary>
    internal PdfReference GetGraphicsState(double fillAlpha, double strokeAlpha, string? blendMode)
    {
        var key = (Math.Round(fillAlpha, 3), Math.Round(strokeAlpha, 3), blendMode);
        if (!_graphicsStates.TryGetValue(key, out var reference))
        {
            var gs = new PdfDictionary(PdfName.ExtGState);
            gs.Set(PdfName.Get("ca"), key.Item1);
            gs.Set(PdfName.Get("CA"), key.Item2);
            if (blendMode != null) gs[PdfName.Get("BM")] = PdfName.Get(blendMode);
            reference = AddObject(gs);
            _graphicsStates[key] = reference;
        }
        return reference;
    }

    /// <summary>Completes fonts used since the last save so that new text can be extracted before saving.</summary>
    internal void PrepareFontsForReading()
    {
        foreach (var font in _fontResources.Values) font.PrepareForReading();
    }

    partial void OnBeforeSave(PdfSaveOptions options)
    {
        CheckRedactedSave(options);
        ApplyAccessibility(options);
        BeforeSave?.Invoke(options);
        ApplyConformance(options);
        foreach (var font in _fontResources.Values) font.Finish();
    }
}
