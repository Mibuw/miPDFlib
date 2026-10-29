using System.Text;
using MiPdf.IO;

namespace MiPdf.Text;

/// <summary>
/// A parsed CMap: code space ranges (to split byte strings into codes), code → Unicode mappings
/// (ToUnicode CMaps) and code → CID mappings (encoding CMaps).
/// </summary>
internal sealed class CMap
{
    private readonly List<(byte[] Low, byte[] High)> _codeSpaces = new();
    private readonly Dictionary<int, string> _unicode = new();
    private long _entries;
    private readonly List<(int Low, int High, int Cid)> _cidRanges = new();
    private readonly Dictionary<int, int> _cids = new();

    public bool HasCodeSpaces => _codeSpaces.Count > 0;

    public bool HasCidMappings => _cids.Count > 0 || _cidRanges.Count > 0;

    public static CMap Identity2Byte()
    {
        var c = new CMap();
        c._codeSpaces.Add((new byte[] { 0, 0 }, new byte[] { 0xFF, 0xFF }));
        return c;
    }

    public string? GetUnicode(int code) => _unicode.TryGetValue(code, out var s) ? s : null;

    public int GetCid(int code)
    {
        if (_cids.TryGetValue(code, out int cid)) return cid;
        foreach (var (low, high, start) in _cidRanges)
            if (code >= low && code <= high) return start + code - low;
        return code;
    }

    /// <summary>Reads the next code from <paramref name="bytes"/> at <paramref name="pos"/>.</summary>
    public int ReadCode(byte[] bytes, ref int pos, out int length)
    {
        if (_codeSpaces.Count == 0)
        {
            length = 1;
            return bytes[pos++];
        }
        int code = 0;
        for (int n = 1; n <= 4 && pos + n <= bytes.Length; n++)
        {
            code = (code << 8) | bytes[pos + n - 1];
            foreach (var (low, high) in _codeSpaces)
            {
                if (low.Length != n) continue;
                bool inRange = true;
                for (int i = 0; i < n; i++)
                {
                    byte b = bytes[pos + i];
                    if (b < low[i] || b > high[i])
                    {
                        inRange = false;
                        break;
                    }
                }
                if (inRange)
                {
                    pos += n;
                    length = n;
                    return code;
                }
            }
        }
        // no matching range: use the length of the shortest range
        int fallback = Math.Max(1, _codeSpaces.Min(r => r.Low.Length));
        fallback = Math.Min(fallback, bytes.Length - pos);
        code = 0;
        for (int i = 0; i < fallback; i++) code = (code << 8) | bytes[pos + i];
        pos += fallback;
        length = fallback;
        return code;
    }

    public static CMap Parse(byte[] data)
    {
        var cmap = new CMap();
        var lexer = new PdfLexer(data);
        var tokens = new List<(TokenKind Kind, string Text, byte[] Bytes, long Int)>();
        string? section = null;
        while (lexer.Next())
        {
            if (lexer.Kind == TokenKind.Keyword)
            {
                switch (lexer.Text)
                {
                    case "begincodespacerange":
                    case "beginbfchar":
                    case "beginbfrange":
                    case "begincidchar":
                    case "begincidrange":
                        section = lexer.Text;
                        tokens.Clear();
                        continue;
                    case "endcodespacerange":
                    case "endbfchar":
                    case "endbfrange":
                    case "endcidchar":
                    case "endcidrange":
                        if (section != null) cmap.Apply(section, tokens);
                        section = null;
                        continue;
                }
            }
            if (section == null) continue;
            switch (lexer.Kind)
            {
                case TokenKind.HexString:
                case TokenKind.String:
                    tokens.Add((TokenKind.HexString, "", lexer.Bytes, 0));
                    break;
                case TokenKind.Integer:
                    tokens.Add((TokenKind.Integer, "", Array.Empty<byte>(), lexer.IntValue));
                    break;
                case TokenKind.ArrayStart:
                case TokenKind.ArrayEnd:
                    tokens.Add((lexer.Kind, "", Array.Empty<byte>(), 0));
                    break;
                case TokenKind.Name:
                    tokens.Add((TokenKind.Name, lexer.Text, Array.Empty<byte>(), 0));
                    break;
            }
        }
        return cmap;
    }

    private static int ToInt(byte[] b)
    {
        int v = 0;
        foreach (var x in b.Take(4)) v = (v << 8) | x;
        return v;
    }

    private static string Utf16(byte[] b)
    {
        if (b.Length == 1) return ((char)b[0]).ToString();
        return Encoding.BigEndianUnicode.GetString(b, 0, b.Length & ~1);
    }

    private void Apply(string section, List<(TokenKind Kind, string Text, byte[] Bytes, long Int)> t)
    {
        int i = 0;
        switch (section)
        {
            case "begincodespacerange":
                for (; i + 1 < t.Count; i += 2)
                    if (t[i].Kind == TokenKind.HexString && t[i + 1].Kind == TokenKind.HexString && t[i].Bytes.Length == t[i + 1].Bytes.Length)
                        _codeSpaces.Add((t[i].Bytes, t[i + 1].Bytes));
                break;
            case "beginbfchar":
                for (; i + 1 < t.Count; i += 2)
                {
                    if (t[i].Kind != TokenKind.HexString) continue;
                    var dst = t[i + 1];
                    string? value = dst.Kind switch
                    {
                        TokenKind.HexString => Utf16(dst.Bytes),
                        TokenKind.Name => Fonts.GlyphNames.ToUnicode(dst.Text),
                        _ => null,
                    };
                    if (value != null) _unicode[ToInt(t[i].Bytes)] = value;
                }
                break;
            case "beginbfrange":
                while (i + 2 < t.Count)
                {
                    if (t[i].Kind != TokenKind.HexString || t[i + 1].Kind != TokenKind.HexString)
                    {
                        i++;
                        continue;
                    }
                    int low = ToInt(t[i].Bytes), high = ToInt(t[i + 1].Bytes);
                    if (high - low > 0xFFFF) high = low + 0xFFFF;
                    _entries += Math.Max(1, high - low + 1);
                    if (_entries > PdfLimits.Current.MaxCMapEntries) return; // protects against CPU exhaustion
                    if (t[i + 2].Kind == TokenKind.ArrayStart)
                    {
                        int j = i + 3, code = low;
                        while (j < t.Count && t[j].Kind != TokenKind.ArrayEnd)
                        {
                            if (t[j].Kind == TokenKind.HexString && code <= high) _unicode[code] = Utf16(t[j].Bytes);
                            code++;
                            j++;
                        }
                        i = j + 1;
                    }
                    else
                    {
                        var start = t[i + 2].Bytes;
                        if (start.Length > 0)
                        {
                            for (int code = low; code <= high; code++)
                            {
                                var dst = (byte[])start.Clone();
                                int add = code - low;
                                // increment the last byte(s)
                                for (int k = dst.Length - 1; k >= 0 && add > 0; k--)
                                {
                                    int v = dst[k] + add;
                                    dst[k] = (byte)v;
                                    add = v >> 8;
                                }
                                _unicode[code] = Utf16(dst);
                            }
                        }
                        i += 3;
                    }
                }
                break;
            case "begincidchar":
                for (; i + 1 < t.Count; i += 2)
                    if (t[i].Kind == TokenKind.HexString && t[i + 1].Kind == TokenKind.Integer)
                        _cids[ToInt(t[i].Bytes)] = (int)t[i + 1].Int;
                break;
            case "begincidrange":
                for (; i + 2 < t.Count; i += 3)
                    if (t[i].Kind == TokenKind.HexString && t[i + 1].Kind == TokenKind.HexString && t[i + 2].Kind == TokenKind.Integer)
                        _cidRanges.Add((ToInt(t[i].Bytes), ToInt(t[i + 1].Bytes), (int)t[i + 2].Int));
                break;
        }
    }
}
