using MiPdf.Objects;

namespace MiPdf.Signatures;

/// <summary>Which form fields a <see cref="PdfFieldLock"/> covers.</summary>
public enum FieldLockAction
{
    /// <summary>All form fields of the document.</summary>
    All,

    /// <summary>Only the listed fields.</summary>
    Include,

    /// <summary>All fields except the listed ones.</summary>
    Exclude,
}

/// <summary>
/// Field lock (FieldMDP, ISO 32000 12.8.2.4): the form fields that become read-only once a signature field is signed.
/// The signature covers the lock, so a later change of a locked field makes the signature invalid in validators such
/// as Adobe Acrobat instead of being reported as permitted form filling.
/// </summary>
/// <remarks>
/// Set it on <see cref="PdfSignatureOptions.FieldLock"/> when signing, or define it in advance on an unsigned
/// signature field with <see cref="Forms.PdfSignatureField.Lock"/> (the lock is then applied by whoever signs it).
/// Field names are fully qualified names such as "address.street".
/// </remarks>
public sealed class PdfFieldLock
{
    private PdfFieldLock(FieldLockAction action, IReadOnlyList<string> fields)
    {
        Action = action;
        Fields = fields;
    }

    public FieldLockAction Action { get; }

    /// <summary>The listed fields (empty for <see cref="FieldLockAction.All"/>).</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>Locks all form fields.</summary>
    public static PdfFieldLock All() => new(FieldLockAction.All, Array.Empty<string>());

    /// <summary>Locks the given fields.</summary>
    public static PdfFieldLock Include(params string[] fields) => new(FieldLockAction.Include, Validate(fields));

    /// <summary>Locks all fields except the given ones.</summary>
    public static PdfFieldLock Exclude(params string[] fields) => new(FieldLockAction.Exclude, Validate(fields));

    /// <summary>Returns whether the field with the given fully qualified name is locked.</summary>
    public bool Locks(string fieldName) => Action switch
    {
        FieldLockAction.Include => Fields.Contains(fieldName, StringComparer.Ordinal),
        FieldLockAction.Exclude => !Fields.Contains(fieldName, StringComparer.Ordinal),
        _ => true,
    };

    public override string ToString() => Action == FieldLockAction.All ? "All" : $"{Action}: {string.Join(", ", Fields)}";

    private static string[] Validate(string[] fields)
    {
        if (fields.Length == 0) throw new ArgumentException("At least one field name is required.", nameof(fields));
        if (fields.Any(string.IsNullOrEmpty)) throw new ArgumentException("Field names must not be empty.", nameof(fields));
        return fields.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Writes the lock as /SigFieldLock (field) or /TransformParams (FieldMDP signature reference).</summary>
    internal PdfDictionary ToDictionary(PdfName type)
    {
        var d = new PdfDictionary(type) { [PdfName.Get("Action")] = PdfName.Get(Action.ToString()) };
        if (Action != FieldLockAction.All)
            d[PdfName.Get("Fields")] = new PdfArray(Fields.Select(f => (PdfObject)new PdfString(f)));
        if (type == TransformParams) d[PdfName.V] = PdfName.Get("1.2");
        return d;
    }

    /// <summary>Reads a /SigFieldLock or FieldMDP /TransformParams dictionary; null if it is missing or invalid.</summary>
    internal static PdfFieldLock? FromDictionary(PdfDictionary? d)
    {
        if (d == null || !Enum.TryParse<FieldLockAction>(d.GetName(PdfName.Get("Action"))?.Value, out var action) || !Enum.IsDefined(action))
            return null;
        if (action == FieldLockAction.All) return All();
        var fields = d.GetArray(PdfName.Get("Fields"))?.Select(o => (o.Resolve() as PdfString)?.Text)
            .Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).Distinct(StringComparer.Ordinal).ToArray();
        return fields is { Length: > 0 } ? new PdfFieldLock(action, fields) : null;
    }

    internal static readonly PdfName SigFieldLock = PdfName.Get("SigFieldLock");
    internal static readonly PdfName TransformParams = PdfName.Get("TransformParams");
}
