namespace MiPdf.Objects;

/// <summary>The kind of a <see cref="PdfObject"/> (ISO 32000-2, 7.3).</summary>
public enum PdfObjectType
{
    Null,
    Boolean,
    Number,
    String,
    Name,
    Array,
    Dictionary,
    Stream,
    Reference,
}

/// <summary>
/// Base class of all PDF objects. Objects are mutable; containers (arrays, dictionaries, streams)
/// may hold direct objects or <see cref="PdfReference"/>s to indirect objects.
/// </summary>
public abstract class PdfObject
{
    public abstract PdfObjectType ObjectType { get; }

    /// <summary>Follows indirect references. Returns <c>this</c> for direct objects.</summary>
    public virtual PdfObject Resolve() => this;

    /// <summary>Creates a deep copy of this object. References are copied as references (not followed).</summary>
    public abstract PdfObject Clone();

    public override string ToString() => IO.PdfObjectSerializer.ToDebugString(this);
}
