namespace MiPdf.Objects;

/// <summary>Resolves indirect references; implemented by the document's object store.</summary>
internal interface IPdfObjectResolver
{
    PdfObject? ResolveReference(PdfReference reference);
}

/// <summary>An indirect reference (<c>12 0 R</c>) to an object owned by a document.</summary>
public sealed class PdfReference : PdfObject, IEquatable<PdfReference>
{
    internal PdfReference(int objectNumber, int generation, IPdfObjectResolver? resolver)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
        Resolver = resolver;
    }

    /// <inheritdoc/>
    public override PdfObjectType ObjectType => PdfObjectType.Reference;

    /// <summary>The object number of the referenced indirect object.</summary>
    public int ObjectNumber { get; }

    /// <summary>The generation number of the referenced indirect object.</summary>
    public int Generation { get; }

    internal IPdfObjectResolver? Resolver { get; }

    /// <summary>Returns the referenced object, or <see cref="PdfNull"/> if it does not exist.</summary>
    public override PdfObject Resolve() => Resolver?.ResolveReference(this) ?? PdfNull.Instance;

    /// <summary>Returns this instance; the reference is not followed.</summary>
    public override PdfObject Clone() => this;

    /// <summary>
    /// True if <paramref name="other"/> has the same object and generation number and belongs to the same
    /// document (object store).
    /// </summary>
    public bool Equals(PdfReference? other) =>
        other is not null && other.ObjectNumber == ObjectNumber && other.Generation == Generation &&
        ReferenceEquals(other.Resolver, Resolver);

    /// <summary>True if <paramref name="obj"/> is an equal <see cref="PdfReference"/>; see <see cref="Equals(PdfReference)"/>.</summary>
    public override bool Equals(object? obj) => obj is PdfReference r && Equals(r);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ObjectNumber, Generation);
}
