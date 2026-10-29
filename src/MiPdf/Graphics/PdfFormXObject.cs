using MiPdf.Objects;

namespace MiPdf.Graphics;

/// <summary>Something that owns a resource dictionary: a page or a form XObject.</summary>
public interface IResourceContainer
{
    PdfDocument Document { get; }

    /// <summary>Registers a resource and returns the name under which it can be used in content.</summary>
    PdfName AddResource(PdfName category, PdfObject resource, string prefix);
}

/// <summary>
/// A form XObject: a reusable piece of content with its own coordinate system and resources
/// (used for templates, stamps and annotation/field appearances).
/// </summary>
public sealed class PdfFormXObject : IResourceContainer
{
    /// <summary>Creates a new form XObject with the given bounding box.</summary>
    public PdfFormXObject(PdfDocument document, PdfRectangle bbox)
    {
        Document = document;
        Stream = new PdfStream();
        Stream[PdfName.Type] = PdfName.XObject;
        Stream[PdfName.Subtype] = PdfName.Form;
        Stream[PdfName.BBox] = PdfArray.FromRectangle(bbox);
        Stream[PdfName.Resources] = new PdfDictionary();
        Reference = document.AddObject(Stream);
    }

    /// <summary>Wraps an existing form XObject stream.</summary>
    public PdfFormXObject(PdfDocument document, PdfStream stream)
    {
        Document = document;
        Stream = stream;
        Reference = document.GetReference(stream) ?? document.AddObject(stream);
    }

    public PdfDocument Document { get; }

    public PdfStream Stream { get; }

    public PdfReference Reference { get; }

    public PdfRectangle BBox
    {
        get => Stream.GetRectangle(PdfName.BBox) ?? default;
        set => Stream[PdfName.BBox] = PdfArray.FromRectangle(value);
    }

    public double Width => BBox.Width;

    public double Height => BBox.Height;

    /// <summary>Optional transformation matrix [a b c d e f].</summary>
    public double[]? Matrix
    {
        get => Stream.GetArray(PdfName.Get("Matrix"))?.ToDoubleArray();
        set => Stream[PdfName.Get("Matrix")] = value != null ? PdfArray.FromNumbers(value) : null;
    }

    public PdfDictionary Resources
    {
        get
        {
            if (Stream.GetDictionary(PdfName.Resources) is { } r) return r;
            var created = new PdfDictionary();
            Stream[PdfName.Resources] = created;
            return created;
        }
    }

    /// <summary>Returns a canvas that writes into this XObject. Dispose the canvas to store the content.</summary>
    public PdfCanvas GetCanvas() => new(this);

    internal void SetContent(byte[] content) => Stream.SetBytes(content);

    internal byte[] GetContent() => Stream.RawBytes.Length == 0 ? Array.Empty<byte>() : Stream.GetBytes();

    public PdfName AddResource(PdfName category, PdfObject resource, string prefix) =>
        ResourceHelper.Add(Resources, category, resource, prefix);
}

internal static class ResourceHelper
{
    public static PdfName Add(PdfDictionary resources, PdfName category, PdfObject resource, string prefix)
    {
        var dict = resources.GetDictionary(category);
        if (dict == null || resources[category] is PdfReference)
        {
            dict = dict != null ? (PdfDictionary)dict.Clone() : new PdfDictionary();
            resources[category] = dict;
        }
        if (resource is PdfReference r)
        {
            foreach (var (name, value) in dict)
                if (value is PdfReference existing && existing.Equals(r)) return name;
        }
        for (int i = 1; ; i++)
        {
            var name = PdfName.Get(prefix + i);
            if (!dict.ContainsKey(name))
            {
                dict[name] = resource;
                return name;
            }
        }
    }
}
