using System.Collections;

namespace MiPdf.Objects;

/// <summary>A PDF array. Typed getters resolve indirect references.</summary>
public sealed class PdfArray : PdfObject, IList<PdfObject>
{
    private readonly List<PdfObject> _items;

    /// <summary>Creates an empty array.</summary>
    public PdfArray() => _items = new List<PdfObject>();

    /// <summary>Creates an array containing <paramref name="items"/>.</summary>
    public PdfArray(IEnumerable<PdfObject> items) => _items = new List<PdfObject>(items);

    /// <summary>Creates an array containing <paramref name="items"/>.</summary>
    public PdfArray(params PdfObject[] items) => _items = new List<PdfObject>(items);

    /// <inheritdoc/>
    public override PdfObjectType ObjectType => PdfObjectType.Array;

    /// <summary>The number of elements.</summary>
    public int Count => _items.Count;

    /// <summary>Always false; the array is mutable.</summary>
    public bool IsReadOnly => false;

    /// <summary>
    /// The raw (possibly indirect) element at the 0-based <paramref name="index"/>. Assigning <c>null</c> stores
    /// <see cref="PdfNull.Instance"/>.
    /// </summary>
    public PdfObject this[int index]
    {
        get => _items[index];
        set => _items[index] = value ?? PdfNull.Instance;
    }

    /// <summary>Creates an array of real numbers.</summary>
    public static PdfArray FromNumbers(params double[] values)
    {
        var a = new PdfArray();
        foreach (var v in values) a.Add(new PdfNumber(v));
        return a;
    }

    /// <summary>Creates an array of integer numbers.</summary>
    public static PdfArray FromNumbers(params int[] values)
    {
        var a = new PdfArray();
        foreach (var v in values) a.Add(new PdfNumber(v));
        return a;
    }

    /// <summary>Creates a rectangle array [llx lly urx ury] (left, bottom, right, top).</summary>
    public static PdfArray FromRectangle(PdfRectangle r) => FromNumbers(r.Left, r.Bottom, r.Right, r.Top);

    /// <summary>Returns the resolved element at <paramref name="index"/>, or null if out of range.</summary>
    public PdfObject? Get(int index) => index >= 0 && index < _items.Count ? _items[index].Resolve() : null;

    /// <summary>Returns the resolved element at <paramref name="index"/> if it is a <typeparamref name="T"/>, otherwise null.</summary>
    public T? Get<T>(int index) where T : PdfObject => Get(index) as T;

    /// <summary>Returns the resolved element at <paramref name="index"/> if it is a dictionary (or stream), otherwise null.</summary>
    public PdfDictionary? GetDictionary(int index) => Get(index) as PdfDictionary;

    /// <summary>Returns the resolved element at <paramref name="index"/> if it is an array, otherwise null.</summary>
    public PdfArray? GetArray(int index) => Get(index) as PdfArray;

    /// <summary>Returns the resolved element at <paramref name="index"/> if it is a name, otherwise null.</summary>
    public PdfName? GetName(int index) => Get(index) as PdfName;

    /// <summary>Returns the resolved element at <paramref name="index"/> if it is a string, otherwise null.</summary>
    public PdfString? GetString(int index) => Get(index) as PdfString;

    /// <summary>Returns the resolved element at <paramref name="index"/> if it is a stream, otherwise null.</summary>
    public PdfStream? GetStream(int index) => Get(index) as PdfStream;

    /// <summary>Returns the resolved number at <paramref name="index"/>, or <paramref name="defaultValue"/> if it is missing or not a number.</summary>
    public double GetDouble(int index, double defaultValue = 0) => Get(index) is PdfNumber n ? n.Value : defaultValue;

    /// <summary>Returns the resolved number at <paramref name="index"/> as <see cref="int"/> (see <see cref="PdfNumber.IntValue"/>), or <paramref name="defaultValue"/> if it is missing or not a number.</summary>
    public int GetInt(int index, int defaultValue = 0) => Get(index) is PdfNumber n ? n.IntValue : defaultValue;

    /// <summary>Returns all elements as numbers; elements that are not numbers become 0.</summary>
    public double[] ToDoubleArray()
    {
        var result = new double[_items.Count];
        for (int i = 0; i < result.Length; i++) result[i] = GetDouble(i);
        return result;
    }

    /// <summary>Interprets the array as rectangle [llx lly urx ury] and normalizes it.</summary>
    public PdfRectangle? ToRectangle()
    {
        if (_items.Count < 4) return null;
        return PdfRectangle.FromCorners(GetDouble(0), GetDouble(1), GetDouble(2), GetDouble(3));
    }

    /// <summary>Appends an element; <c>null</c> is stored as <see cref="PdfNull.Instance"/>.</summary>
    public void Add(PdfObject item) => _items.Add(item ?? PdfNull.Instance);

    /// <summary>Appends several elements; <c>null</c> elements are stored as <see cref="PdfNull.Instance"/>.</summary>
    public void AddRange(IEnumerable<PdfObject> items)
    {
        foreach (var i in items) Add(i);
    }

    /// <summary>Removes all elements.</summary>
    public void Clear() => _items.Clear();

    /// <summary>
    /// True if the array contains <paramref name="item"/>. Numbers, names, strings, booleans, null and references are
    /// compared by value, arrays, dictionaries and streams by reference (the same instance); references are not
    /// resolved.
    /// </summary>
    public bool Contains(PdfObject item) => _items.Contains(item);

    /// <summary>Copies the raw elements to <paramref name="array"/>, starting at <paramref name="arrayIndex"/>.</summary>
    public void CopyTo(PdfObject[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <summary>Returns the 0-based index of <paramref name="item"/>, or -1 if it is not in the array (compared like
    /// <see cref="Contains"/>).</summary>
    public int IndexOf(PdfObject item) => _items.IndexOf(item);

    /// <summary>Inserts an element at the 0-based <paramref name="index"/>; <c>null</c> is stored as <see cref="PdfNull.Instance"/>.</summary>
    public void Insert(int index, PdfObject item) => _items.Insert(index, item ?? PdfNull.Instance);

    /// <summary>Removes the first occurrence of <paramref name="item"/>; returns false if it was not found.</summary>
    public bool Remove(PdfObject item) => _items.Remove(item);

    /// <summary>Removes the element at the 0-based <paramref name="index"/>.</summary>
    public void RemoveAt(int index) => _items.RemoveAt(index);

    /// <summary>Enumerates the raw (unresolved) elements.</summary>
    public IEnumerator<PdfObject> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Creates a deep copy; references are copied as references (not followed).</summary>
    public override PdfObject Clone()
    {
        var copy = new PdfArray();
        foreach (var item in _items) copy._items.Add(item.Clone());
        return copy;
    }
}
