namespace MiPdf.PostScript.Fonts;

/// <summary>
/// A CMap (Adobe Technical Note 5099): code space ranges that determine how many bytes a character code has, and the
/// mapping of codes to CIDs (with the descendant font chosen by usefont).
/// </summary>
internal sealed class PsCMap
{
    private readonly List<(byte[] Low, byte[] High)> _codeSpace = new();
    private readonly Dictionary<(int Length, uint Code), (int Cid, int Font)> _chars = new();
    private readonly List<(int Length, uint Low, uint High, int Cid, int Font)> _ranges = new();
    private readonly List<(int Length, uint Low, uint High, int Cid)> _notdef = new();

    public string Name { get; set; } = "";

    public int WMode { get; set; }

    /// <summary>The descendant font of the mappings that follow (usefont).</summary>
    public int CurrentFont { get; set; }

    /// <summary>True if codes are UTF-16 (UCS2/UTF16 CMaps): then they give the text of the glyphs.</summary>
    public bool IsUnicode => Name.Contains("UCS2", StringComparison.Ordinal) || Name.Contains("UTF16", StringComparison.Ordinal);

    public static PsCMap Identity(bool vertical) => Build(vertical ? "Identity-V" : "Identity-H", m =>
    {
        m.WMode = vertical ? 1 : 0;
        m.AddCodeSpace(new byte[] { 0, 0 }, new byte[] { 0xFF, 0xFF });
        m.AddRange(new byte[] { 0, 0 }, new byte[] { 0xFF, 0xFF }, 0);
    });

    private static PsCMap Build(string name, Action<PsCMap> fill)
    {
        var m = new PsCMap { Name = name };
        fill(m);
        return m;
    }

    public void AddCodeSpace(byte[] low, byte[] high)
    {
        if (low.Length == high.Length && low.Length is >= 1 and <= 4) _codeSpace.Add((low, high));
    }

    public void AddChar(byte[] code, int cid)
    {
        if (code.Length is >= 1 and <= 4) _chars[(code.Length, Value(code))] = (cid, CurrentFont);
    }

    public void AddRange(byte[] low, byte[] high, int cid)
    {
        if (low.Length == high.Length && low.Length is >= 1 and <= 4 && _ranges.Count < 100_000)
            _ranges.Add((low.Length, Value(low), Value(high), cid, CurrentFont));
    }

    public void AddNotdef(byte[] low, byte[] high, int cid)
    {
        if (low.Length == high.Length && low.Length is >= 1 and <= 4 && _notdef.Count < 100_000)
            _notdef.Add((low.Length, Value(low), Value(high), cid));
    }

    /// <summary>usecmap: takes over all mappings of another CMap.</summary>
    public void Use(PsCMap other)
    {
        _codeSpace.AddRange(other._codeSpace);
        foreach (var (k, v) in other._chars) _chars.TryAdd(k, v);
        _ranges.AddRange(other._ranges);
        _notdef.AddRange(other._notdef);
    }

    private static uint Value(ReadOnlySpan<byte> bytes)
    {
        uint v = 0;
        foreach (byte b in bytes) v = (v << 8) | b;
        return v;
    }

    /// <summary>
    /// Decodes the next code at <paramref name="position"/>: its length from the code space ranges, then the CID and the
    /// descendant font. Codes outside the code space take one byte and map to CID 0.
    /// </summary>
    public (int Cid, int Font, uint Code, int Length) Decode(ReadOnlySpan<byte> s, int position)
    {
        int length = 0;
        for (int n = 1; n <= 4 && position + n <= s.Length && length == 0; n++)
        {
            foreach (var (low, high) in _codeSpace)
            {
                if (low.Length != n) continue;
                bool inside = true;
                for (int i = 0; i < n && inside; i++) inside = s[position + i] >= low[i] && s[position + i] <= high[i];
                if (inside)
                {
                    length = n;
                    break;
                }
            }
        }
        if (length == 0) return (0, 0, s[position], 1);
        uint code = Value(s.Slice(position, length));
        if (_chars.TryGetValue((length, code), out var c)) return (c.Cid, c.Font, code, length);
        for (int i = _ranges.Count - 1; i >= 0; i--)
        {
            var r = _ranges[i];
            if (r.Length == length && code >= r.Low && code <= r.High) return ((int)(r.Cid + code - r.Low), r.Font, code, length);
        }
        foreach (var r in _notdef)
            if (r.Length == length && code >= r.Low && code <= r.High) return (r.Cid, 0, code, length);
        return (0, 0, code, length);
    }
}
