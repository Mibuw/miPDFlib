using System.Collections;

namespace MiPdf.Objects;

/// <summary>A PDF array. Typed getters resolve indirect references.</summary>
public sealed class PdfArray : PdfObject, IList<PdfObject>
{
    private readonly List<PdfObject> _items;

    public PdfArray() => _items = new List<PdfObject>();

    public PdfArray(IEnumerable<PdfObject> items) => _items = new List<PdfObject>(items);

    public PdfArray(params PdfObject[] items) => _items = new List<PdfObject>(items);

    public override PdfObjectType ObjectType => PdfObjectType.Array;

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public PdfObject this[int index]
    {
        get => _items[index];
        set => _items[index] = value ?? PdfNull.Instance;
    }

    public static PdfArray FromNumbers(params double[] values)
    {
        var a = new PdfArray();
        foreach (var v in values) a.Add(new PdfNumber(v));
        return a;
    }

    public static PdfArray FromNumbers(params int[] values)
    {
        var a = new PdfArray();
        foreach (var v in values) a.Add(new PdfNumber(v));
        return a;
    }

    public static PdfArray FromRectangle(PdfRectangle r) => FromNumbers(r.Left, r.Bottom, r.Right, r.Top);

    /// <summary>Returns the resolved element at <paramref name="index"/>, or null if out of range.</summary>
    public PdfObject? Get(int index) => index >= 0 && index < _items.Count ? _items[index].Resolve() : null;

    public T? Get<T>(int index) where T : PdfObject => Get(index) as T;

    public PdfDictionary? GetDictionary(int index) => Get(index) as PdfDictionary;

    public PdfArray? GetArray(int index) => Get(index) as PdfArray;

    public PdfName? GetName(int index) => Get(index) as PdfName;

    public PdfString? GetString(int index) => Get(index) as PdfString;

    public PdfStream? GetStream(int index) => Get(index) as PdfStream;

    public double GetDouble(int index, double defaultValue = 0) => Get(index) is PdfNumber n ? n.Value : defaultValue;

    public int GetInt(int index, int defaultValue = 0) => Get(index) is PdfNumber n ? n.IntValue : defaultValue;

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

    public void Add(PdfObject item) => _items.Add(item ?? PdfNull.Instance);

    public void AddRange(IEnumerable<PdfObject> items)
    {
        foreach (var i in items) Add(i);
    }

    public void Clear() => _items.Clear();

    public bool Contains(PdfObject item) => _items.Contains(item);

    public void CopyTo(PdfObject[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    public int IndexOf(PdfObject item) => _items.IndexOf(item);

    public void Insert(int index, PdfObject item) => _items.Insert(index, item ?? PdfNull.Instance);

    public bool Remove(PdfObject item) => _items.Remove(item);

    public void RemoveAt(int index) => _items.RemoveAt(index);

    public IEnumerator<PdfObject> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override PdfObject Clone()
    {
        var copy = new PdfArray();
        foreach (var item in _items) copy._items.Add(item.Clone());
        return copy;
    }
}
