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

    /// <summary>Creates an empty dictionary.</summary>
    public PdfDictionary() { }

    /// <summary>Creates a dictionary with the /Type entry set to <paramref name="type"/>.</summary>
    public PdfDictionary(PdfName type) => _entries[PdfName.Type] = type;

    /// <inheritdoc/>
    public override PdfObjectType ObjectType => PdfObjectType.Dictionary;

    /// <summary>The number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>The keys of all entries.</summary>
    public IEnumerable<PdfName> Keys => _entries.Keys;

    /// <summary>
    /// The raw (possibly indirect) value for <paramref name="key"/>, or null if absent. Assigning <c>null</c>
    /// removes the entry.
    /// </summary>
    public PdfObject? this[PdfName key]
    {
        get => _entries.TryGetValue(key, out var v) ? v : null;
        set
        {
            if (value is null) _entries.Remove(key);
            else _entries[key] = value;
        }
    }

    /// <summary>True if the dictionary has an entry for <paramref name="key"/>.</summary>
    public bool ContainsKey(PdfName key) => _entries.ContainsKey(key);

    /// <summary>Removes the entry for <paramref name="key"/>; returns false if there was none.</summary>
    public bool Remove(PdfName key) => _entries.Remove(key);

    /// <summary>Removes all entries.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Returns the resolved value for <paramref name="key"/>, or null if absent or the null object.</summary>
    public PdfObject? Get(PdfName key)
    {
        if (!_entries.TryGetValue(key, out var v)) return null;
        var r = v.Resolve();
        return r is PdfNull ? null : r;
    }

    /// <summary>Returns the resolved value for <paramref name="key"/> if it is a <typeparamref name="T"/>, otherwise null.</summary>
    public T? Get<T>(PdfName key) where T : PdfObject => Get(key) as T;

    /// <summary>Returns the resolved value if it is a dictionary (or stream), otherwise null.</summary>
    public PdfDictionary? GetDictionary(PdfName key) => Get(key) as PdfDictionary;

    /// <summary>Returns the resolved value if it is an array, otherwise null.</summary>
    public PdfArray? GetArray(PdfName key) => Get(key) as PdfArray;

    /// <summary>Returns the resolved value if it is a stream, otherwise null.</summary>
    public PdfStream? GetStream(PdfName key) => Get(key) as PdfStream;

    /// <summary>Returns the resolved value if it is a name, otherwise null.</summary>
    public PdfName? GetName(PdfName key) => Get(key) as PdfName;

    /// <summary>Returns the resolved value if it is a string, otherwise null.</summary>
    public PdfString? GetString(PdfName key) => Get(key) as PdfString;

    /// <summary>Returns the resolved value if it is a number, otherwise null.</summary>
    public PdfNumber? GetNumber(PdfName key) => Get(key) as PdfNumber;

    /// <summary>Returns the decoded text of a string value (or the name value of a name).</summary>
    public string? GetText(PdfName key) => Get(key) switch
    {
        PdfString s => s.Text,
        PdfName n => n.Value,
        _ => null,
    };

    /// <summary>Returns the resolved number as <see cref="int"/> (see <see cref="PdfNumber.IntValue"/>), or <paramref name="defaultValue"/> if it is missing or not a number.</summary>
    public int GetInt(PdfName key, int defaultValue = 0) => Get(key) is PdfNumber n ? n.IntValue : defaultValue;

    /// <summary>Returns the resolved number as <see cref="long"/> (see <see cref="PdfNumber.LongValue"/>), or <paramref name="defaultValue"/> if it is missing or not a number.</summary>
    public long GetLong(PdfName key, long defaultValue = 0) => Get(key) is PdfNumber n ? n.LongValue : defaultValue;

    /// <summary>Returns the resolved number, or <paramref name="defaultValue"/> if it is missing or not a number.</summary>
    public double GetDouble(PdfName key, double defaultValue = 0) => Get(key) is PdfNumber n ? n.Value : defaultValue;

    /// <summary>Returns the resolved boolean, or <paramref name="defaultValue"/> if it is missing or not a boolean.</summary>
    public bool GetBool(PdfName key, bool defaultValue = false) => Get(key) is PdfBoolean b ? b.Value : defaultValue;

    /// <summary>Returns the value as normalized rectangle (see <see cref="PdfArray.ToRectangle"/>), or null if it is not an array of at least four elements.</summary>
    public PdfRectangle? GetRectangle(PdfName key) => GetArray(key)?.ToRectangle();

    /// <summary>Checks the /Type entry.</summary>
    public bool IsType(PdfName type) => GetName(PdfName.Type) == type;

    /// <summary>Sets the value for <paramref name="key"/>; <c>null</c> removes the entry.</summary>
    public void Set(PdfName key, PdfObject? value) => this[key] = value;

    /// <summary>Sets an integer value.</summary>
    public void Set(PdfName key, int value) => _entries[key] = new PdfNumber(value);

    /// <summary>Sets an integer value.</summary>
    public void Set(PdfName key, long value) => _entries[key] = new PdfNumber(value);

    /// <summary>Sets a real value.</summary>
    public void Set(PdfName key, double value) => _entries[key] = new PdfNumber(value);

    /// <summary>Sets a boolean value.</summary>
    public void Set(PdfName key, bool value) => _entries[key] = PdfBoolean.Get(value);

    /// <summary>Sets a text string value.</summary>
    public void SetText(PdfName key, string? value)
    {
        if (value is null) _entries.Remove(key);
        else _entries[key] = new PdfString(value);
    }

    /// <summary>Sets a name value (a leading slash is ignored); <c>null</c> removes the entry.</summary>
    public void SetName(PdfName key, string? value)
    {
        if (value is null) _entries.Remove(key);
        else _entries[key] = PdfName.Get(value);
    }

    /// <summary>Enumerates the entries with their raw (unresolved) values.</summary>
    public IEnumerator<KeyValuePair<PdfName, PdfObject>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Copies deep clones of all entries into <paramref name="target"/>, replacing entries with the same key.</summary>
    protected void CopyEntriesTo(PdfDictionary target)
    {
        foreach (var (k, v) in _entries) target._entries[k] = v.Clone();
    }

    /// <summary>Creates a deep copy; references are copied as references (not followed).</summary>
    public override PdfObject Clone()
    {
        var copy = new PdfDictionary();
        CopyEntriesTo(copy);
        return copy;
    }
}
