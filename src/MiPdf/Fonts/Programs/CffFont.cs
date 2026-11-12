using MiPdf.Graphics;

namespace MiPdf.Fonts.Programs;

/// <summary>
/// Compact Font Format (Adobe TN 5176) with Type 2 charstrings (TN 5177): the outlines of FontFile3 /Type1C and
/// /CIDFontType0C fonts and of OpenType fonts with PostScript outlines. Name-keyed fonts map names and codes to
/// glyphs through their charset and encoding, CID-keyed fonts map CIDs through the charset and use the private
/// dictionaries selected by FDSelect.
/// </summary>
internal sealed class CffFont
{
    private readonly byte[] _data;
    private readonly List<(int Start, int End)> _charStrings;
    private readonly List<(int Start, int End)> _globalSubrs;
    private readonly List<(int Start, int End)> _strings;
    private readonly List<List<(int Start, int End)>> _localSubrs = new();
    private readonly int[] _fdSelect = Array.Empty<int>();
    private readonly int[] _charset; // glyph -> SID (name-keyed) or CID
    private readonly int[] _encoding = Enumerable.Repeat(-1, 256).ToArray(); // code -> glyph
    private Dictionary<string, int>? _byName;
    private Dictionary<int, int>? _byCid;

    public CffFont(byte[] data)
    {
        _data = data;
        if (data.Length < 4) throw new PdfException("Invalid CFF font.");
        int pos = data[2];
        ReadIndex(ref pos); // names
        var topDicts = ReadIndex(ref pos);
        _strings = ReadIndex(ref pos);
        _globalSubrs = ReadIndex(ref pos);
        if (topDicts.Count == 0) throw new PdfException("CFF font without top dictionary.");
        var top = ReadDict(topDicts[0].Start, topDicts[0].End);

        if (top.TryGetValue(1207, out var fm) && fm.Length == 6) FontMatrix = new Matrix(fm[0], fm[1], fm[2], fm[3], fm[4], fm[5]);
        int charStringsOffset = top.TryGetValue(17, out var cs) ? (int)cs[0] : throw new PdfException("CFF font without CharStrings.");
        int p = charStringsOffset;
        _charStrings = ReadIndex(ref p);
        IsCidKeyed = top.ContainsKey(1230);

        if (IsCidKeyed)
        {
            // CID-keyed: one private dictionary per font dictionary of FDArray
            if (top.TryGetValue(1236, out var fdArray))
            {
                int q = (int)fdArray[0];
                foreach (var (start, end) in ReadIndex(ref q))
                {
                    var fontDict = ReadDict(start, end);
                    _localSubrs.Add(fontDict.TryGetValue(18, out var priv) && priv.Length == 2 ? ReadPrivateSubrs((int)priv[1], (int)priv[0]) : new());
                }
            }
            if (top.TryGetValue(1237, out var fdSelect)) _fdSelect = ReadFdSelect((int)fdSelect[0], _charStrings.Count);
        }
        else if (top.TryGetValue(18, out var priv) && priv.Length == 2)
            _localSubrs.Add(ReadPrivateSubrs((int)priv[1], (int)priv[0]));

        _charset = ReadCharset(top.TryGetValue(15, out var charset) ? (int)charset[0] : 0, _charStrings.Count);
        if (!IsCidKeyed) ReadEncoding(top.TryGetValue(16, out var encoding) ? (int)encoding[0] : 0);
    }

    /// <summary>The font matrix (default 0.001: 1000 units per em).</summary>
    public Matrix FontMatrix { get; } = new(0.001, 0, 0, 0.001, 0, 0);

    public bool IsCidKeyed { get; }

    public int GlyphCount => _charStrings.Count;

    /// <summary>The glyph for a code of the font's built-in encoding, or -1.</summary>
    public int GlyphForCode(int code) => code is >= 0 and < 256 ? _encoding[code] : -1;

    public int GlyphByName(string name)
    {
        if (_byName == null)
        {
            _byName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int glyph = 0; glyph < _charset.Length; glyph++)
                if (StringFor(_charset[glyph]) is { } glyphName) _byName.TryAdd(glyphName, glyph);
        }
        return _byName.TryGetValue(name, out int g) ? g : -1;
    }

    public int GlyphByCid(int cid)
    {
        if (!IsCidKeyed) return cid < _charStrings.Count ? cid : -1;
        if (_byCid == null)
        {
            _byCid = new Dictionary<int, int>();
            for (int glyph = 0; glyph < _charset.Length; glyph++) _byCid.TryAdd(_charset[glyph], glyph);
        }
        return _byCid.TryGetValue(cid, out int g) ? g : -1;
    }

    private string? StringFor(int sid) =>
        sid < CffStandardStrings.Count ? CffStandardStrings.Get(sid)
        : sid - CffStandardStrings.Count < _strings.Count ? System.Text.Encoding.Latin1.GetString(_data, _strings[sid - CffStandardStrings.Count].Start, _strings[sid - CffStandardStrings.Count].End - _strings[sid - CffStandardStrings.Count].Start)
        : null;

    // ------------------------------------------------------------------ structures

    private List<(int Start, int End)> ReadIndex(ref int pos)
    {
        var result = new List<(int, int)>();
        if (pos + 2 > _data.Length) return result;
        int count = (_data[pos] << 8) | _data[pos + 1];
        if (count == 0)
        {
            pos += 2;
            return result;
        }
        int offSize = _data[pos + 2];
        if (offSize is < 1 or > 4) throw new PdfException("Invalid CFF index.");
        int offsets = pos + 3, dataStart = offsets + (count + 1) * offSize - 1;
        int Offset(int i)
        {
            int value = 0;
            for (int k = 0; k < offSize; k++) value = (value << 8) | _data[offsets + i * offSize + k];
            return value;
        }
        if (offsets + (count + 1) * offSize > _data.Length) throw new PdfException("Invalid CFF index.");
        for (int i = 0; i < count; i++)
        {
            int start = dataStart + Offset(i), end = dataStart + Offset(i + 1);
            result.Add((Math.Clamp(start, 0, _data.Length), Math.Clamp(end, 0, _data.Length)));
        }
        pos = Math.Min(_data.Length, dataStart + Offset(count));
        return result;
    }

    /// <summary>A DICT: operator (two-byte operators as 1200 + second byte) to operands.</summary>
    private Dictionary<int, double[]> ReadDict(int start, int end)
    {
        var dict = new Dictionary<int, double[]>();
        var operands = new List<double>();
        int p = start;
        while (p < end && p < _data.Length)
        {
            int b = _data[p++];
            if (b <= 21)
            {
                int op = b == 12 && p < end ? 1200 + _data[p++] : b;
                dict[op] = operands.ToArray();
                operands.Clear();
            }
            else if (b == 28 && p + 2 <= _data.Length) { operands.Add((short)((_data[p] << 8) | _data[p + 1])); p += 2; }
            else if (b == 29 && p + 4 <= _data.Length) { operands.Add((_data[p] << 24) | (_data[p + 1] << 16) | (_data[p + 2] << 8) | _data[p + 3]); p += 4; }
            else if (b == 30) operands.Add(ReadReal(ref p, end));
            else if (b is >= 32 and <= 246) operands.Add(b - 139);
            else if (b is >= 247 and <= 250 && p < end) operands.Add((b - 247) * 256 + _data[p++] + 108);
            else if (b is >= 251 and <= 254 && p < end) operands.Add(-(b - 251) * 256 - _data[p++] - 108);
        }
        return dict;
    }

    private double ReadReal(ref int p, int end)
    {
        var text = new System.Text.StringBuilder();
        while (p < end)
        {
            int b = _data[p++];
            foreach (int nibble in new[] { b >> 4, b & 15 })
            {
                switch (nibble)
                {
                    case <= 9: text.Append((char)('0' + nibble)); break;
                    case 0xA: text.Append('.'); break;
                    case 0xB: text.Append('E'); break;
                    case 0xC: text.Append("E-"); break;
                    case 0xE: text.Append('-'); break;
                    case 0xF:
                        return double.TryParse(text.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
                }
            }
        }
        return 0;
    }

    private List<(int Start, int End)> ReadPrivateSubrs(int offset, int size)
    {
        if (offset < 0 || offset + size > _data.Length) return new();
        var priv = ReadDict(offset, offset + size);
        if (!priv.TryGetValue(19, out var subrs)) return new();
        int p = offset + (int)subrs[0];
        return p > 0 && p < _data.Length ? ReadIndex(ref p) : new();
    }

    private int[] ReadFdSelect(int offset, int glyphs)
    {
        var result = new int[glyphs];
        if (offset <= 0 || offset >= _data.Length) return result;
        int format = _data[offset];
        if (format == 0)
            for (int g = 0; g < glyphs && offset + 1 + g < _data.Length; g++) result[g] = _data[offset + 1 + g];
        else if (format == 3)
        {
            int ranges = (_data[offset + 1] << 8) | _data[offset + 2];
            for (int r = 0; r < ranges; r++)
            {
                int rec = offset + 3 + 3 * r;
                if (rec + 5 > _data.Length) break;
                int first = (_data[rec] << 8) | _data[rec + 1], fd = _data[rec + 2], next = (_data[rec + 3] << 8) | _data[rec + 4];
                for (int g = first; g < next && g < glyphs; g++) result[g] = fd;
            }
        }
        return result;
    }

    private int[] ReadCharset(int offset, int glyphs)
    {
        var result = new int[glyphs];
        if (offset == 0) // ISOAdobe: SIDs 0..228 in order
        {
            for (int g = 0; g < glyphs; g++) result[g] = g;
            return result;
        }
        if (offset <= 2 || offset >= _data.Length) return result;
        int format = _data[offset], p = offset + 1;
        int glyph = 1;
        int U16() { int v = p + 1 < _data.Length ? (_data[p] << 8) | _data[p + 1] : 0; p += 2; return v; }
        if (format == 0)
            while (glyph < glyphs && p + 1 < _data.Length) result[glyph++] = U16();
        else
            while (glyph < glyphs && p + 2 < _data.Length)
            {
                int first = U16();
                int left = format == 1 ? _data[p++] : U16();
                for (int i = 0; i <= left && glyph < glyphs; i++) result[glyph++] = first + i;
            }
        return result;
    }

    private void ReadEncoding(int offset)
    {
        if (offset is 0 or 1)
        {
            // predefined standard (or expert, treated as standard) encoding: code -> name -> glyph
            for (int code = 0; code < 256; code++)
                if (CffStandardStrings.StandardEncoding(code) is { } name) _encoding[code] = GlyphByName(name);
            return;
        }
        if (offset >= _data.Length) return;
        int format = _data[offset] & 0x7F, p = offset + 1;
        if (format == 0)
        {
            int count = _data.ElementAtOrDefault(p++);
            for (int g = 1; g <= count && p < _data.Length; g++) _encoding[_data[p++]] = g;
        }
        else if (format == 1)
        {
            int ranges = _data.ElementAtOrDefault(p++), glyph = 1;
            for (int r = 0; r < ranges && p + 1 < _data.Length; r++)
            {
                int first = _data[p++], left = _data[p++];
                for (int i = 0; i <= left && first + i < 256; i++) _encoding[first + i] = glyph++;
            }
        }
        if ((_data[offset] & 0x80) != 0 && p < _data.Length)
        {
            // supplements: code -> SID
            int count = _data[p++];
            for (int s = 0; s < count && p + 2 < _data.Length; s++)
            {
                int code = _data[p], sid = (_data[p + 1] << 8) | _data[p + 2];
                p += 3;
                if (StringFor(sid) is { } name && GlyphByName(name) is >= 0 and var g) _encoding[code] = g;
            }
        }
    }

    // ------------------------------------------------------------------ Type 2 charstrings

    public PathGeometry? GetOutline(int glyph)
    {
        if (glyph < 0 || glyph >= _charStrings.Count) return null;
        var state = new CharStringState { Path = new PathGeometry() };
        var local = _localSubrs.Count == 0 ? new() : _localSubrs[Math.Clamp(glyph < _fdSelect.Length ? _fdSelect[glyph] : 0, 0, _localSubrs.Count - 1)];
        state.Local = local;
        try
        {
            Execute(_charStrings[glyph], state, 0);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            // damaged charstring: keep what was drawn
        }
        if (state.Open) state.Path.Close();
        return state.Path;
    }

    private sealed class CharStringState
    {
        public required PathGeometry Path;
        public List<(int Start, int End)> Local = new();
        public readonly List<double> Stack = new();
        public readonly double[] Transient = new double[32];
        public double X, Y;
        public int Stems;
        public bool WidthDone, Open, Ended;
    }

    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    private void Execute((int Start, int End) range, CharStringState s, int depth)
    {
        if (depth > 10) return;
        var stack = s.Stack;
        int p = range.Start;
        while (p < range.End && !s.Ended)
        {
            int b = _data[p++];
            if (b >= 32 || b == 28 || b == 255)
            {
                if (b == 28) { stack.Add((short)((_data[p] << 8) | _data[p + 1])); p += 2; }
                else if (b == 255) { stack.Add(((_data[p] << 24) | (_data[p + 1] << 16) | (_data[p + 2] << 8) | _data[p + 3]) / 65536.0); p += 4; }
                else if (b <= 246) stack.Add(b - 139);
                else if (b <= 250) stack.Add((b - 247) * 256 + _data[p++] + 108);
                else stack.Add(-(b - 251) * 256 - _data[p++] - 108);
                if (stack.Count > 96) throw new InvalidOperationException("CFF stack overflow");
                continue;
            }
            switch (b)
            {
                case 1 or 3 or 18 or 23: // hstem, vstem, hstemhm, vstemhm
                    Width(s, stack.Count % 2 == 1);
                    s.Stems += stack.Count / 2;
                    stack.Clear();
                    break;
                case 19 or 20: // hintmask, cntrmask (implicit vstem)
                    Width(s, stack.Count % 2 == 1);
                    s.Stems += stack.Count / 2;
                    stack.Clear();
                    p += (s.Stems + 7) / 8;
                    break;
                case 21: // rmoveto
                    Width(s, stack.Count > 2);
                    MoveTo(s, s.X + Arg(stack, 0), s.Y + Arg(stack, 1));
                    stack.Clear();
                    break;
                case 22: // hmoveto
                    Width(s, stack.Count > 1);
                    MoveTo(s, s.X + Arg(stack, 0), s.Y);
                    stack.Clear();
                    break;
                case 4: // vmoveto
                    Width(s, stack.Count > 1);
                    MoveTo(s, s.X, s.Y + Arg(stack, 0));
                    stack.Clear();
                    break;
                case 5: // rlineto
                    for (int i = 0; i + 1 < stack.Count; i += 2) LineTo(s, s.X + stack[i], s.Y + stack[i + 1]);
                    stack.Clear();
                    break;
                case 6 or 7: // hlineto, vlineto: alternating
                {
                    bool horizontal = b == 6;
                    foreach (double d in stack)
                    {
                        if (horizontal) LineTo(s, s.X + d, s.Y);
                        else LineTo(s, s.X, s.Y + d);
                        horizontal = !horizontal;
                    }
                    stack.Clear();
                    break;
                }
                case 8: // rrcurveto
                    for (int i = 0; i + 5 < stack.Count; i += 6) Curve(s, stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]);
                    stack.Clear();
                    break;
                case 24: // rcurveline
                {
                    int i = 0;
                    for (; i + 5 < stack.Count - 2; i += 6) Curve(s, stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]);
                    if (i + 1 < stack.Count) LineTo(s, s.X + stack[i], s.Y + stack[i + 1]);
                    stack.Clear();
                    break;
                }
                case 25: // rlinecurve
                {
                    int i = 0;
                    for (; i + 1 < stack.Count - 6; i += 2) LineTo(s, s.X + stack[i], s.Y + stack[i + 1]);
                    if (i + 5 < stack.Count) Curve(s, stack[i], stack[i + 1], stack[i + 2], stack[i + 3], stack[i + 4], stack[i + 5]);
                    stack.Clear();
                    break;
                }
                case 26: // vvcurveto
                {
                    int i = 0;
                    double dx1 = 0;
                    if (stack.Count % 2 == 1) dx1 = stack[i++];
                    for (; i + 3 < stack.Count; i += 4)
                    {
                        Curve(s, dx1, stack[i], stack[i + 1], stack[i + 2], 0, stack[i + 3]);
                        dx1 = 0;
                    }
                    stack.Clear();
                    break;
                }
                case 27: // hhcurveto
                {
                    int i = 0;
                    double dy1 = 0;
                    if (stack.Count % 2 == 1) dy1 = stack[i++];
                    for (; i + 3 < stack.Count; i += 4)
                    {
                        Curve(s, stack[i], dy1, stack[i + 1], stack[i + 2], stack[i + 3], 0);
                        dy1 = 0;
                    }
                    stack.Clear();
                    break;
                }
                case 30 or 31: // vhcurveto, hvcurveto
                {
                    bool horizontal = b == 31;
                    for (int i = 0; i + 3 < stack.Count; i += 4)
                    {
                        bool last = i + 4 >= stack.Count - 1;
                        double extra = last && i + 4 < stack.Count ? stack[i + 4] : 0;
                        if (horizontal) Curve(s, stack[i], 0, stack[i + 1], stack[i + 2], extra, stack[i + 3]);
                        else Curve(s, 0, stack[i], stack[i + 1], stack[i + 2], stack[i + 3], extra);
                        horizontal = !horizontal;
                    }
                    stack.Clear();
                    break;
                }
                case 10 or 29: // callsubr, callgsubr
                {
                    var subrs = b == 10 ? s.Local : _globalSubrs;
                    if (stack.Count == 0) return;
                    int index = (int)stack[^1] + Bias(subrs.Count);
                    stack.RemoveAt(stack.Count - 1);
                    if (index >= 0 && index < subrs.Count) Execute(subrs[index], s, depth + 1);
                    break;
                }
                case 11: // return
                    return;
                case 14: // endchar (with 4 arguments: an accented character as with seac)
                    Width(s, stack.Count is 1 or 5);
                    if (stack.Count >= 4)
                    {
                        double adx = stack[^4], ady = stack[^3];
                        int baseCode = (int)stack[^2], accentCode = (int)stack[^1];
                        stack.Clear();
                        Compose(s, baseCode, accentCode, adx, ady);
                    }
                    if (s.Open) s.Path.Close();
                    s.Open = false;
                    s.Ended = true;
                    return;
                case 12:
                    Escape(_data[p++], s);
                    break;
                default:
                    stack.Clear();
                    break;
            }
        }
    }

    private static double Arg(List<double> stack, int index) => index < stack.Count ? stack[index] : 0;

    private static void Width(CharStringState s, bool hasWidth)
    {
        if (s.WidthDone) return;
        s.WidthDone = true;
        if (hasWidth && s.Stack.Count > 0) s.Stack.RemoveAt(0);
    }

    private static void MoveTo(CharStringState s, double x, double y)
    {
        if (s.Open) s.Path.Close();
        s.X = x;
        s.Y = y;
        s.Path.MoveTo(x, y);
        s.Open = true;
    }

    private static void LineTo(CharStringState s, double x, double y)
    {
        if (!s.Open) MoveTo(s, s.X, s.Y);
        s.X = x;
        s.Y = y;
        s.Path.LineTo(x, y);
    }

    private static void Curve(CharStringState s, double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        if (!s.Open) MoveTo(s, s.X, s.Y);
        double x1 = s.X + dx1, y1 = s.Y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
        s.X = x2 + dx3;
        s.Y = y2 + dy3;
        s.Path.CurveTo(x1, y1, x2, y2, s.X, s.Y);
    }

    private void Escape(int op, CharStringState s)
    {
        var st = s.Stack;
        double Pop() { if (st.Count == 0) return 0; double v = st[^1]; st.RemoveAt(st.Count - 1); return v; }
        switch (op)
        {
            case 35: // flex
                if (st.Count >= 12)
                {
                    Curve(s, st[0], st[1], st[2], st[3], st[4], st[5]);
                    Curve(s, st[6], st[7], st[8], st[9], st[10], st[11]);
                }
                st.Clear();
                break;
            case 34: // hflex
                if (st.Count >= 7)
                {
                    double y = s.Y;
                    Curve(s, st[0], 0, st[1], st[2], st[3], 0);
                    Curve(s, st[4], 0, st[5], y - s.Y, st[6], 0);
                }
                st.Clear();
                break;
            case 36: // hflex1
                if (st.Count >= 9)
                {
                    double y = s.Y;
                    Curve(s, st[0], st[1], st[2], st[3], st[4], 0);
                    Curve(s, st[5], 0, st[6], st[7], st[8], y - (s.Y + st[7]));
                }
                st.Clear();
                break;
            case 37: // flex1
                if (st.Count >= 11)
                {
                    double startX = s.X, startY = s.Y;
                    double dx = st[0] + st[2] + st[4] + st[6] + st[8], dy = st[1] + st[3] + st[5] + st[7] + st[9];
                    Curve(s, st[0], st[1], st[2], st[3], st[4], st[5]);
                    if (Math.Abs(dx) > Math.Abs(dy)) Curve(s, st[6], st[7], st[8], st[9], st[10], startY - (s.Y + st[7] + st[9]));
                    else Curve(s, st[6], st[7], st[8], st[9], startX - (s.X + st[6] + st[8]), st[10]);
                }
                st.Clear();
                break;
            // arithmetic and storage operators (rarely used)
            case 3: st.Add(Pop() != 0 & Pop() != 0 ? 1 : 0); break;
            case 4: st.Add(Pop() != 0 | Pop() != 0 ? 1 : 0); break;
            case 5: st.Add(Pop() == 0 ? 1 : 0); break;
            case 9: st.Add(Math.Abs(Pop())); break;
            case 10: st.Add(Pop() + Pop()); break;
            case 11: { double b = Pop(), a = Pop(); st.Add(a - b); break; }
            case 12: { double b = Pop(), a = Pop(); st.Add(b != 0 ? a / b : 0); break; }
            case 14: st.Add(-Pop()); break;
            case 15: st.Add(Pop() == Pop() ? 1 : 0); break;
            case 18: Pop(); break;
            case 20: { int i = (int)Pop(); double v = Pop(); if (i is >= 0 and < 32) s.Transient[i] = v; break; }
            case 21: { int i = (int)Pop(); st.Add(i is >= 0 and < 32 ? s.Transient[i] : 0); break; }
            case 22: { double v2 = Pop(), v1 = Pop(), s2 = Pop(), s1 = Pop(); st.Add(v1 <= v2 ? s1 : s2); break; }
            case 23: st.Add(0.5); break;
            case 24: st.Add(Pop() * Pop()); break;
            case 26: st.Add(Math.Sqrt(Math.Abs(Pop()))); break;
            case 27: { double v = Pop(); st.Add(v); st.Add(v); break; }
            case 28: { double b = Pop(), a = Pop(); st.Add(b); st.Add(a); break; }
            case 29: { int i = (int)Pop(); st.Add(i >= 0 && i < st.Count ? st[st.Count - 1 - i] : 0); break; }
            case 30:
            {
                int j = (int)Pop(), n = (int)Pop();
                if (n > 0 && n <= st.Count)
                {
                    var items = st.GetRange(st.Count - n, n);
                    j = ((j % n) + n) % n;
                    for (int i = 0; i < n; i++) st[st.Count - n + (i + j) % n] = items[i];
                }
                break;
            }
            default:
                st.Clear();
                break;
        }
    }

    /// <summary>An accented character from two glyphs of the standard encoding (endchar with four arguments).</summary>
    private void Compose(CharStringState s, int baseCode, int accentCode, double adx, double ady)
    {
        if (CffStandardStrings.StandardEncoding(baseCode) is not { } baseName || CffStandardStrings.StandardEncoding(accentCode) is not { } accentName) return;
        if (GetOutline(GlyphByName(baseName)) is { } basePath) Append(s.Path, basePath, 0, 0);
        if (GetOutline(GlyphByName(accentName)) is { } accentPath) Append(s.Path, accentPath, adx, ady);
    }

    internal static void Append(PathGeometry target, PathGeometry source, double dx, double dy)
    {
        foreach (var seg in source.Segments)
        {
            switch (seg.Kind)
            {
                case PathGeometry.Kind.Move: target.MoveTo(seg.X1 + dx, seg.Y1 + dy); break;
                case PathGeometry.Kind.Line: target.LineTo(seg.X1 + dx, seg.Y1 + dy); break;
                case PathGeometry.Kind.Curve: target.CurveTo(seg.X1 + dx, seg.Y1 + dy, seg.X2 + dx, seg.Y2 + dy, seg.X3 + dx, seg.Y3 + dy); break;
                case PathGeometry.Kind.Close: target.Close(); break;
            }
        }
    }
}
