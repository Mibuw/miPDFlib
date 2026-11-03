using MiPdf.Objects;

namespace MiPdf.Graphics;

/// <summary>Something that owns a resource dictionary: a page or a form XObject.</summary>
public interface IResourceContainer
{
    /// <summary>The document the container belongs to.</summary>
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

    /// <summary>The document that owns this XObject.</summary>
    public PdfDocument Document { get; }

    /// <summary>The underlying XObject stream (dictionary and content).</summary>
    public PdfStream Stream { get; }

    /// <summary>The indirect reference of <see cref="Stream"/> in <see cref="Document"/>.</summary>
    public PdfReference Reference { get; }

    /// <summary>The bounding box (/BBox) in form space; an empty rectangle if missing.</summary>
    public PdfRectangle BBox
    {
        get => Stream.GetRectangle(PdfName.BBox) ?? default;
        set => Stream[PdfName.BBox] = PdfArray.FromRectangle(value);
    }

    /// <summary>Width of <see cref="BBox"/> in points.</summary>
    public double Width => BBox.Width;

    /// <summary>Height of <see cref="BBox"/> in points.</summary>
    public double Height => BBox.Height;

    /// <summary>Optional transformation from form space to user space (/Matrix); null if missing or malformed (the
    /// identity matrix applies then). Setting null removes the entry.</summary>
    public Matrix? Matrix
    {
        get => Stream.GetArray(PdfName.Get("Matrix"))?.ToDoubleArray() is { Length: 6 } m ? new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]) : null;
        set => Stream[PdfName.Get("Matrix")] = value is { } m ? PdfArray.FromNumbers(m.A, m.B, m.C, m.D, m.E, m.F) : null;
    }

    /// <summary>The resource dictionary of the XObject; created if missing.</summary>
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

    internal byte[] GetContent() => Stream.RawLength == 0 ? Array.Empty<byte>() : Stream.GetBytes();

    /// <summary>
    /// Registers <paramref name="resource"/> in the <paramref name="category"/> sub-dictionary of <see cref="Resources"/>
    /// (e.g. /Font, /XObject) under a new name <paramref name="prefix"/>1, 2, ... and returns that name.
    /// A reference that is already registered returns its existing name.
    /// </summary>
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
