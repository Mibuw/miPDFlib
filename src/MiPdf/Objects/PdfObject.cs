namespace MiPdf.Objects;

/// <summary>The kind of a <see cref="PdfObject"/> (ISO 32000-2, 7.3).</summary>
public enum PdfObjectType
{
    /// <summary>The <c>null</c> object (<see cref="PdfNull"/>).</summary>
    Null,
    /// <summary>A boolean (<see cref="PdfBoolean"/>).</summary>
    Boolean,
    /// <summary>An integer or real number (<see cref="PdfNumber"/>).</summary>
    Number,
    /// <summary>A literal or hexadecimal string (<see cref="PdfString"/>).</summary>
    String,
    /// <summary>A name (<see cref="PdfName"/>).</summary>
    Name,
    /// <summary>An array (<see cref="PdfArray"/>).</summary>
    Array,
    /// <summary>A dictionary (<see cref="PdfDictionary"/>).</summary>
    Dictionary,
    /// <summary>A stream (<see cref="PdfStream"/>).</summary>
    Stream,
    /// <summary>An indirect reference (<see cref="PdfReference"/>).</summary>
    Reference,
}

/// <summary>
/// Base class of all PDF objects. Objects are mutable; containers (arrays, dictionaries, streams)
/// may hold direct objects or <see cref="PdfReference"/>s to indirect objects.
/// </summary>
public abstract class PdfObject
{
    /// <summary>The kind of this object.</summary>
    public abstract PdfObjectType ObjectType { get; }

    /// <summary>Follows indirect references. Returns <c>this</c> for direct objects.</summary>
    public virtual PdfObject Resolve() => this;

    /// <summary>Creates a deep copy of this object. References are copied as references (not followed).</summary>
    public abstract PdfObject Clone();

    /// <summary>Returns the object serialized in PDF syntax, for debugging.</summary>
    public override string ToString() => IO.PdfObjectSerializer.ToDebugString(this);
}
