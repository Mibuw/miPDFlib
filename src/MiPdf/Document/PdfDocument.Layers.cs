using MiPdf.Objects;

namespace MiPdf;

public sealed partial class PdfDocument
{
    private static readonly PdfName OCProperties = PdfName.Get("OCProperties");
    private static readonly PdfName OCGs = PdfName.Get("OCGs");

    /// <summary>The layers (optional content groups) of the document, in the order of /OCProperties /OCGs.</summary>
    public IReadOnlyList<PdfLayer> Layers =>
        Catalog.GetDictionary(OCProperties)?.GetArray(OCGs) is { } groups
            ? groups.OfType<PdfReference>().Where(r => r.Resolve() is PdfDictionary)
                .Select(r => new PdfLayer(this, (PdfDictionary)r.Resolve()!, r)).ToList()
            : Array.Empty<PdfLayer>();

    /// <summary>
    /// Adds a layer. Draw its content between <see cref="Graphics.PdfCanvas.BeginLayer"/> and
    /// <see cref="Graphics.PdfCanvas.EndLayer"/>, or assign it to annotations (<see cref="PdfAnnotation.Layer"/>).
    /// </summary>
    /// <param name="name">Name shown in the layers panel of viewers.</param>
    /// <param name="visible">Shown when the document is opened.</param>
    public PdfLayer AddLayer(string name, bool visible = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var group = new PdfDictionary(PdfName.Get("OCG"));
        group.SetText(PdfName.Name, name);
        var reference = AddObject(group);
        var properties = Catalog.GetDictionary(OCProperties);
        if (properties == null) Catalog[OCProperties] = properties = new PdfDictionary();
        var groups = properties.GetArray(OCGs);
        if (groups == null) properties[OCGs] = groups = new PdfArray();
        groups.Add(reference);
        var configuration = LayerConfiguration(create: true)!;
        var order = configuration.GetArray(PdfName.Get("Order"));
        if (order == null) configuration[PdfName.Get("Order")] = order = new PdfArray();
        order.Add(reference);
        var layer = new PdfLayer(this, group, reference);
        if (!visible) layer.IsVisible = false;
        return layer;
    }

    /// <summary>The default configuration of optional content (/OCProperties /D).</summary>
    internal PdfDictionary? LayerConfiguration(bool create = false)
    {
        var properties = Catalog.GetDictionary(OCProperties);
        if (properties?.GetDictionary(PdfName.Get("D")) is { } existing) return existing;
        if (!create) return null;
        if (properties == null) Catalog[OCProperties] = properties = new PdfDictionary();
        var configuration = new PdfDictionary();
        configuration.SetText(PdfName.Name, "Default"); // required by PDF/A-2 and later
        properties[PdfName.Get("D")] = configuration;
        return configuration;
    }

    internal static bool ContainsReference(PdfArray array, PdfReference reference) =>
        array.Any(item => item is PdfReference r && r.ObjectNumber == reference.ObjectNumber);

    internal bool IsLayerVisible(PdfReference group)
    {
        var configuration = LayerConfiguration();
        if (configuration == null) return true;
        if (configuration.GetName(PdfName.Get("BaseState"))?.Value == "OFF")
            return configuration.GetArray(PdfName.Get("ON")) is { } on && ContainsReference(on, group);
        return configuration.GetArray(PdfName.Get("OFF")) is not { } off || !ContainsReference(off, group);
    }

    /// <summary>Sets visibility ("OFF" = hidden) or lock state ("Locked") of a layer in the default configuration.</summary>
    internal void SetLayerState(PdfLayer layer, string key, bool add)
    {
        var configuration = LayerConfiguration(create: true)!;
        if (key == "OFF")
        {
            // visibility: hidden groups are listed in /OFF, or visible ones in /ON when the base state is OFF
            bool baseOff = configuration.GetName(PdfName.Get("BaseState"))?.Value == "OFF";
            Remove(configuration.GetArray(PdfName.Get("ON")));
            Remove(configuration.GetArray(PdfName.Get("OFF")));
            if (baseOff == add) return;
            key = baseOff ? "ON" : "OFF";
        }
        else
        {
            Remove(configuration.GetArray(PdfName.Get(key)));
            if (!add) return;
        }
        var array = configuration.GetArray(PdfName.Get(key));
        if (array == null) configuration[PdfName.Get(key)] = array = new PdfArray();
        array.Add(layer.Reference);

        void Remove(PdfArray? from)
        {
            if (from == null) return;
            for (int i = from.Count - 1; i >= 0; i--)
                if (from[i] is PdfReference r && r.ObjectNumber == layer.Reference.ObjectNumber) from.RemoveAt(i);
        }
    }

    /// <summary>
    /// True if content marked with an optional content group or membership dictionary (/OCMD) is hidden in the
    /// default configuration. Membership visibility expressions (/VE) are approximated by /P.
    /// </summary>
    internal bool IsHiddenContent(PdfObject? optionalContent)
    {
        if (optionalContent is not PdfReference reference || reference.Resolve() is not PdfDictionary dict) return false;
        if (dict.GetName(PdfName.Type)?.Value != "OCMD") return !IsLayerVisible(reference);
        var groups = dict[OCGs] switch
        {
            PdfArray array => array.OfType<PdfReference>().ToList(),
            PdfReference single => new List<PdfReference> { single },
            _ => new List<PdfReference>(),
        };
        if (groups.Count == 0) return false;
        var visible = groups.Select(IsLayerVisible).ToList();
        return (dict.GetName(PdfName.P)?.Value ?? "AnyOn") switch
        {
            "AllOn" => !visible.All(v => v),
            "AnyOff" => visible.All(v => v),
            "AllOff" => visible.Any(v => v),
            _ => !visible.Any(v => v),
        };
    }
}
