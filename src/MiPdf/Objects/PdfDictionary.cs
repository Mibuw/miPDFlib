using System.Collections;

namespace MiPdf.Objects;

/// <summary>
/// A PDF dictionary. The indexer returns the raw (possibly indirect) value; the typed getters
/// (<see cref="GetDictionary"/>, <see cref="GetInt"/>, ...) resolve references transparently.
/// Assigning <c>null</c> through the indexer removes the key.
/// </summary>
public class PdfDictionary : PdfObject, IEnumerable<KeyValuePair<PdfName, PdfObject>>
{
    private readonly Dictionary<PdfName, PdfObject> _entries = new();

    public PdfDictionary() { }

    public PdfDictionary(PdfName type) => _entries[PdfName.Type] = type;

    public override PdfObjectType ObjectType => PdfObjectType.Dictionary;

    public int Count => _entries.Count;

    public IEnumerable<PdfName> Keys => _entries.Keys;

    public PdfObject? this[PdfName key]
    {
        get => _entries.TryGetValue(key, out var v) ? v : null;
        set
        {
            if (value is null) _entries.Remove(key);
            else _entries[key] = value;
        }
    }

    public bool ContainsKey(PdfName key) => _entries.ContainsKey(key);

    public bool Remove(PdfName key) => _entries.Remove(key);

    public void Clear() => _entries.Clear();

    /// <summary>Returns the resolved value for <paramref name="key"/>, or null if absent or the null object.</summary>
    public PdfObject? Get(PdfName key)
    {
        if (!_entries.TryGetValue(key, out var v)) return null;
        var r = v.Resolve();
        return r is PdfNull ? null : r;
    }

    public T? Get<T>(PdfName key) where T : PdfObject => Get(key) as T;

    public PdfDictionary? GetDictionary(PdfName key) => Get(key) as PdfDictionary;

    public PdfArray? GetArray(PdfName key) => Get(key) as PdfArray;

    public PdfStream? GetStream(PdfName key) => Get(key) as PdfStream;

    public PdfName? GetName(PdfName key) => Get(key) as PdfName;

    public PdfString? GetString(PdfName key) => Get(key) as PdfString;

    public PdfNumber? GetNumber(PdfName key) => Get(key) as PdfNumber;

    /// <summary>Returns the decoded text of a string value (or the name value of a name).</summary>
    public string? GetText(PdfName key) => Get(key) switch
    {
        PdfString s => s.Text,
        PdfName n => n.Value,
        _ => null,
    };

    public int GetInt(PdfName key, int defaultValue = 0) => Get(key) is PdfNumber n ? n.IntValue : defaultValue;

    public long GetLong(PdfName key, long defaultValue = 0) => Get(key) is PdfNumber n ? n.LongValue : defaultValue;

    public double GetDouble(PdfName key, double defaultValue = 0) => Get(key) is PdfNumber n ? n.Value : defaultValue;

    public bool GetBool(PdfName key, bool defaultValue = false) => Get(key) is PdfBoolean b ? b.Value : defaultValue;

    public PdfRectangle? GetRectangle(PdfName key) => GetArray(key)?.ToRectangle();

    /// <summary>Checks the /Type entry.</summary>
    public bool IsType(PdfName type) => GetName(PdfName.Type) == type;

    public void Set(PdfName key, PdfObject? value) => this[key] = value;

    public void Set(PdfName key, int value) => _entries[key] = new PdfNumber(value);

    public void Set(PdfName key, long value) => _entries[key] = new PdfNumber(value);

    public void Set(PdfName key, double value) => _entries[key] = new PdfNumber(value);

    public void Set(PdfName key, bool value) => _entries[key] = PdfBoolean.Get(value);

    /// <summary>Sets a text string value.</summary>
    public void SetText(PdfName key, string? value)
    {
        if (value is null) _entries.Remove(key);
        else _entries[key] = new PdfString(value);
    }

    public void SetName(PdfName key, string? value)
    {
        if (value is null) _entries.Remove(key);
        else _entries[key] = PdfName.Get(value);
    }

    public IEnumerator<KeyValuePair<PdfName, PdfObject>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    protected void CopyEntriesTo(PdfDictionary target)
    {
        foreach (var (k, v) in _entries) target._entries[k] = v.Clone();
    }

    public override PdfObject Clone()
    {
        var copy = new PdfDictionary();
        CopyEntriesTo(copy);
        return copy;
    }
}
