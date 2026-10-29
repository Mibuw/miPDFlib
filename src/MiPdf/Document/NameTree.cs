using MiPdf.Objects;

namespace MiPdf;

/// <summary>Reading and writing of name trees (ISO 32000-1, 7.9.6).</summary>
internal static class NameTree
{
    /// <summary>Returns all entries (key bytes, raw value) of a name tree in key order.</summary>
    public static List<(byte[] Key, PdfObject Value)> Read(PdfDictionary? root)
    {
        var result = new List<(byte[], PdfObject)>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        void Walk(PdfDictionary node, int depth)
        {
            if (depth > 32 || !visited.Add(node)) return;
            if (node.GetArray(PdfName.Get("Names")) is { } names)
                for (int i = 0; i + 1 < names.Count; i += 2)
                    if (names[i].Resolve() is PdfString key) result.Add((key.Bytes, names[i + 1]));
            if (node.GetArray(PdfName.Kids) is { } kids)
                foreach (var kid in kids)
                    if (kid.Resolve() is PdfDictionary k) Walk(k, depth + 1);
        }
        if (root != null) Walk(root, 0);
        result.Sort((a, b) => a.Item1.AsSpan().SequenceCompareTo(b.Item1));
        return result;
    }

    /// <summary>Creates a flat name tree node with the entries sorted by key.</summary>
    public static PdfDictionary Write(IEnumerable<(byte[] Key, PdfObject Value)> entries)
    {
        var names = new PdfArray();
        foreach (var (key, value) in entries.OrderBy(e => e.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))))
        {
            names.Add(new PdfString(key));
            names.Add(value);
        }
        var node = new PdfDictionary();
        node[PdfName.Get("Names")] = names;
        return node;
    }
}
