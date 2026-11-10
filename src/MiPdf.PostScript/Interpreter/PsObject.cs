using System.Runtime.CompilerServices;

namespace MiPdf.PostScript.Interpreter;

/// <summary>The PostScript object types (PLRM 3.3).</summary>
internal enum PsType : byte
{
    Null,
    Integer,
    Real,
    Boolean,
    Name,
    String,
    Array,
    PackedArray,
    Dictionary,
    Operator,
    File,
    Mark,
    Save,
    FontId,
    GState,
}

/// <summary>Attributes of an object: executable, and the access of arrays, strings and files (dictionaries keep theirs).</summary>
[Flags]
internal enum PsAttributes : byte
{
    None = 0,
    Executable = 1,
    ReadOnly = 2,
    ExecuteOnly = 4,
    NoAccess = 8,
}

/// <summary>
/// A PostScript object (PLRM 3.3): simple objects carry their value, composite objects refer to shared storage.
/// Integers are 64 bits and reals single precision, as in Ghostscript.
/// </summary>
internal readonly struct PsObject
{
    public readonly PsType Type;
    public readonly PsAttributes Attributes;
    public readonly long Int;
    public readonly float Real;
    public readonly object? Ref;

    private PsObject(PsType type, PsAttributes attributes, long i, float r, object? reference)
    {
        Type = type;
        Attributes = attributes;
        Int = i;
        Real = r;
        Ref = reference;
    }

    public static readonly PsObject Null = new(PsType.Null, PsAttributes.None, 0, 0, null);
    public static readonly PsObject Mark = new(PsType.Mark, PsAttributes.None, 0, 0, null);
    public static readonly PsObject True = new(PsType.Boolean, PsAttributes.None, 1, 0, null);
    public static readonly PsObject False = new(PsType.Boolean, PsAttributes.None, 0, 0, null);

    public static PsObject Integer(long value) => new(PsType.Integer, PsAttributes.None, value, 0, null);
    public static PsObject RealValue(float value) => new(PsType.Real, PsAttributes.None, 0, value, null);
    public static PsObject Boolean(bool value) => value ? True : False;
    public static PsObject Name(PsName name, bool executable = false) =>
        new(PsType.Name, executable ? PsAttributes.Executable : PsAttributes.None, 0, 0, name);
    public static PsObject String(PsString value, PsAttributes attributes = PsAttributes.None) => new(PsType.String, attributes, 0, 0, value);
    public static PsObject Array(PsArray value, PsAttributes attributes = PsAttributes.None) =>
        new(value.Packed ? PsType.PackedArray : PsType.Array, value.Packed ? attributes | PsAttributes.ReadOnly : attributes, 0, 0, value);
    public static PsObject Dictionary(PsDict value) => new(PsType.Dictionary, PsAttributes.None, 0, 0, value);
    public static PsObject Operator(PsOperator op) => new(PsType.Operator, PsAttributes.Executable, 0, 0, op);
    public static PsObject File(PsFile file, PsAttributes attributes = PsAttributes.None) => new(PsType.File, attributes, 0, 0, file);
    public static PsObject Save(PsSave save) => new(PsType.Save, PsAttributes.None, 0, 0, save);
    public static PsObject Other(PsType type, object value) => new(type, PsAttributes.None, 0, 0, value);

    public bool IsExecutable => (Attributes & PsAttributes.Executable) != 0;
    public bool IsNumber => Type is PsType.Integer or PsType.Real;
    public bool IsArray => Type is PsType.Array or PsType.PackedArray;
    public bool IsProcedure => IsArray && IsExecutable;

    /// <summary>The numeric value (integers converted).</summary>
    public double Number => Type == PsType.Integer ? Int : Real;

    public bool Bool => Int != 0;
    public PsName AsName => (PsName)Ref!;
    public PsString AsString => (PsString)Ref!;
    public PsArray AsArray => (PsArray)Ref!;
    public PsDict AsDict => (PsDict)Ref!;
    public PsOperator AsOperator => (PsOperator)Ref!;
    public PsFile AsFile => (PsFile)Ref!;

    public PsObject WithAttributes(PsAttributes attributes) => new(Type, attributes, Int, Real, Ref);

    public PsObject AsExecutable() => WithAttributes(Attributes | PsAttributes.Executable);
    public PsObject AsLiteral() => WithAttributes(Attributes & ~PsAttributes.Executable);

    /// <summary>Readable: not execute-only or without access (dictionaries: their own access).</summary>
    public bool CanRead => Type == PsType.Dictionary ? AsDict.Access < PsDictAccess.ExecuteOnly
        : (Attributes & (PsAttributes.ExecuteOnly | PsAttributes.NoAccess)) == 0;

    /// <summary>Writable: not read-only, execute-only or without access (dictionaries: their own access).</summary>
    public bool CanWrite => Type == PsType.Dictionary ? AsDict.Access == PsDictAccess.Unlimited
        : (Attributes & (PsAttributes.ReadOnly | PsAttributes.ExecuteOnly | PsAttributes.NoAccess)) == 0;

    /// <summary>
    /// The PostScript <c>eq</c>: numbers by value, strings by content (also against names), other composite objects by
    /// identity.
    /// </summary>
    public static bool Equal(in PsObject a, in PsObject b)
    {
        if (a.IsNumber && b.IsNumber)
            return a.Type == PsType.Integer && b.Type == PsType.Integer ? a.Int == b.Int : a.Number == b.Number;
        if (a.Type is PsType.String or PsType.Name && b.Type is PsType.String or PsType.Name)
        {
            if (a.Type == PsType.Name && b.Type == PsType.Name) return ReferenceEquals(a.Ref, b.Ref);
            return Bytes(a).SequenceEqual(Bytes(b));
        }
        if (a.IsArray && b.IsArray) return a.AsArray.SameAs(b.AsArray);
        if (a.Type != b.Type) return false;
        return a.Type switch
        {
            PsType.Null or PsType.Mark => true,
            PsType.Boolean => a.Int == b.Int,
            _ => ReferenceEquals(a.Ref, b.Ref),
        };
    }

    private static ReadOnlySpan<byte> Bytes(in PsObject o) => o.Type == PsType.Name ? o.AsName.Bytes : o.AsString.Span;

    public override string ToString() => Formatter.Syntax(this, 0);
}

/// <summary>An interned name (names are byte strings; compared by identity).</summary>
internal sealed class PsName
{
    public readonly string Text; // Latin-1
    public readonly byte[] Bytes;

    public PsName(string text)
    {
        Text = text;
        Bytes = PsText.Latin1.GetBytes(text);
    }

    public override string ToString() => Text;
}

internal static class PsText
{
    public static readonly System.Text.Encoding Latin1 = System.Text.Encoding.Latin1;
}

/// <summary>The VM a composite object lives in: global VM is not affected by <c>restore</c>.</summary>
internal abstract class PsComposite
{
    /// <summary>The save level at which the object was created (0 before any <c>save</c>).</summary>
    public int Level;

    public bool Global;

    /// <summary>The save level at which the contents were last recorded for <c>restore</c>.</summary>
    public int JournaledLevel;
}

/// <summary>The bytes of a string; strings share them through <c>getinterval</c>. Restore does not undo string changes.</summary>
internal sealed class PsStringStorage : PsComposite
{
    public readonly byte[] Bytes;

    public PsStringStorage(byte[] bytes) => Bytes = bytes;
}

/// <summary>A string: a window into shared storage.</summary>
internal sealed class PsString
{
    public readonly PsStringStorage Storage;
    public readonly int Offset, Length;

    public PsString(PsStringStorage storage, int offset, int length)
    {
        Storage = storage;
        Offset = offset;
        Length = length;
    }

    public Span<byte> Span => Storage.Bytes.AsSpan(Offset, Length);

    public byte this[int index]
    {
        get => Storage.Bytes[Offset + index];
        set => Storage.Bytes[Offset + index] = value;
    }

    public PsString Interval(int index, int count) => new(Storage, Offset + index, count);

    public string Text => PsText.Latin1.GetString(Storage.Bytes, Offset, Length);

    public byte[] ToArray() => Span.ToArray();
}

/// <summary>The elements of an array; arrays share them through <c>getinterval</c>.</summary>
internal sealed class PsArrayStorage : PsComposite
{
    public readonly PsObject[] Items;

    public PsArrayStorage(PsObject[] items) => Items = items;
}

/// <summary>An array or packed array: a window into shared storage.</summary>
internal sealed class PsArray
{
    public readonly PsArrayStorage Storage;
    public readonly int Offset, Length;
    public readonly bool Packed;

    public PsArray(PsArrayStorage storage, int offset, int length, bool packed = false)
    {
        Storage = storage;
        Offset = offset;
        Length = length;
        Packed = packed;
    }

    public PsObject this[int index] => Storage.Items[Offset + index];

    public ReadOnlySpan<PsObject> Span => Storage.Items.AsSpan(Offset, Length);

    public PsArray Interval(int index, int count) => new(Storage, Offset + index, count, Packed);

    /// <summary>Identity in the sense of <c>eq</c>: the same elements of the same storage.</summary>
    public bool SameAs(PsArray other) => ReferenceEquals(Storage, other.Storage) && Offset == other.Offset && Length == other.Length;
}

internal enum PsDictAccess : byte
{
    Unlimited,
    ReadOnly,
    ExecuteOnly,
    NoAccess,
}

/// <summary>A dictionary; keys are compared as by <c>eq</c> (string keys are stored as names).</summary>
internal sealed class PsDict : PsComposite
{
    public readonly Dictionary<PsObject, PsObject> Map;
    public int MaxLength;
    public PsDictAccess Access;

    public PsDict(int capacity)
    {
        Map = new Dictionary<PsObject, PsObject>(Math.Clamp(capacity, 0, 1024), PsKeyComparer.Instance);
        MaxLength = capacity;
    }

    public int Count => Map.Count;
}

/// <summary>Equality and hash of dictionary keys (keys are normalized: strings to names, integral reals to integers).</summary>
internal sealed class PsKeyComparer : IEqualityComparer<PsObject>
{
    public static readonly PsKeyComparer Instance = new();

    public bool Equals(PsObject a, PsObject b)
    {
        if (a.Type != b.Type) return false;
        return a.Type switch
        {
            PsType.Integer or PsType.Boolean => a.Int == b.Int,
            PsType.Real => a.Real.Equals(b.Real),
            PsType.Null or PsType.Mark => true,
            PsType.Array or PsType.PackedArray => a.AsArray.SameAs(b.AsArray),
            _ => ReferenceEquals(a.Ref, b.Ref),
        };
    }

    public int GetHashCode(PsObject o) => o.Type switch
    {
        PsType.Integer or PsType.Boolean => o.Int.GetHashCode(),
        PsType.Real => o.Real.GetHashCode(),
        PsType.Null or PsType.Mark => (int)o.Type,
        PsType.Array or PsType.PackedArray => RuntimeHelpers.GetHashCode(o.AsArray.Storage) ^ o.AsArray.Offset,
        _ => o.Ref == null ? 0 : RuntimeHelpers.GetHashCode(o.Ref),
    };
}

/// <summary>A built-in operator.</summary>
internal sealed class PsOperator
{
    public readonly PsName Name;
    public readonly Action<PsInterpreter> Run;

    public PsOperator(PsName name, Action<PsInterpreter> run)
    {
        Name = name;
        Run = run;
    }
}

/// <summary>The snapshot of a <c>save</c>.</summary>
internal sealed class PsSave
{
    public readonly int Level;
    public readonly bool Global;
    public bool Valid = true;

    public PsSave(int level, bool global)
    {
        Level = level;
        Global = global;
    }
}
