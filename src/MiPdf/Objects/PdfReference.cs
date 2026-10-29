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

    public override PdfObjectType ObjectType => PdfObjectType.Reference;

    public int ObjectNumber { get; }

    public int Generation { get; }

    internal IPdfObjectResolver? Resolver { get; }

    /// <summary>Returns the referenced object, or <see cref="PdfNull"/> if it does not exist.</summary>
    public override PdfObject Resolve() => Resolver?.ResolveReference(this) ?? PdfNull.Instance;

    public override PdfObject Clone() => this;

    public bool Equals(PdfReference? other) =>
        other is not null && other.ObjectNumber == ObjectNumber && other.Generation == Generation &&
        ReferenceEquals(other.Resolver, Resolver);

    public override bool Equals(object? obj) => obj is PdfReference r && Equals(r);

    public override int GetHashCode() => HashCode.Combine(ObjectNumber, Generation);
}
