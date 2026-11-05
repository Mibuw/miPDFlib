using System.Text;
using MiPdf.Graphics;
using MiPdf.Rendering.Raster;

namespace MiPdf.Rendering.Fonts;

/// <summary>
/// Type 1 fonts (FontFile, Adobe Type 1 Font Format): the clear-text part with the encoding and font matrix, the
/// eexec-encrypted private part with the subroutines and charstrings, and the Type 1 charstring interpreter (with
/// flex, hint replacement and seac accents).
/// </summary>
internal sealed class Type1Font
{
    private readonly Dictionary<string, byte[]> _charStrings = new(StringComparer.Ordinal);
    private readonly List<byte[]> _subrs = new();
    private readonly string?[] _encoding = new string?[256];
    private int _lenIV = 4;

    public Type1Font(byte[] data, int clearLength)
    {
        if (data.Length >= 2 && data[0] == 0x80 && data[1] == 1) data = FromPfb(data, ref clearLength);
        if (clearLength <= 0 || clearLength > data.Length) clearLength = FindEexec(data);
        var clear = Encoding.Latin1.GetString(data, 0, Math.Min(clearLength, data.Length));
        ReadClearText(clear);
        var encrypted = data.AsSpan(Math.Min(clearLength, data.Length)).ToArray();
        // the eexec part is binary or hexadecimal
        if (encrypted.Take(4).All(b => Uri.IsHexDigit((char)b) || b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t'))
            encrypted = FromHex(encrypted);
        var plain = Decrypt(encrypted, 55665, 4);
        ReadPrivate(plain);
    }

    public Matrix FontMatrix { get; private set; } = new(0.001, 0, 0, 0.001, 0, 0);

    /// <summary>The glyph name of a code in the font's built-in encoding, or null.</summary>
    public string? NameForCode(int code) => code is >= 0 and < 256 ? _encoding[code] : null;

    public bool HasGlyph(string name) => _charStrings.ContainsKey(name);

    private static byte[] FromPfb(byte[] data, ref int clearLength)
    {
        // PFB segments: 0x80 type(1 ascii, 2 binary, 3 eof) length(4 LE)
        var result = new List<byte>(data.Length);
        int p = 0;
        bool first = true;
        while (p + 6 <= data.Length && data[p] == 0x80 && data[p + 1] is 1 or 2)
        {
            int length = BitConverter.ToInt32(data, p + 2);
            p += 6;
            if (length < 0 || p + length > data.Length) break;
            result.AddRange(data.AsSpan(p, length).ToArray());
            if (first) clearLength = length;
            first = false;
            p += length;
        }
        return result.ToArray();
    }

    private static int FindEexec(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        int index = text.IndexOf("eexec", StringComparison.Ordinal);
        if (index < 0) return data.Length;
        index += 5;
        while (index < data.Length && data[index] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t') index++;
        return index;
    }

    private static byte[] FromHex(byte[] data)
    {
        var result = new List<byte>(data.Length / 2);
        int high = -1;
        foreach (byte b in data)
        {
            int v = b switch { >= (byte)'0' and <= (byte)'9' => b - '0', >= (byte)'a' and <= (byte)'f' => b - 'a' + 10, >= (byte)'A' and <= (byte)'F' => b - 'A' + 10, _ => -1 };
            if (v < 0) continue;
            if (high < 0) high = v;
            else
            {
                result.Add((byte)(high * 16 + v));
                high = -1;
            }
        }
        return result.ToArray();
    }

    private static byte[] Decrypt(byte[] data, int key, int skip)
    {
        var result = new byte[Math.Max(0, data.Length - skip)];
        int r = key;
        for (int i = 0; i < data.Length; i++)
        {
            int c = data[i];
            int plain = c ^ (r >> 8);
            r = ((c + r) * 52845 + 22719) & 0xFFFF;
            if (i >= skip) result[i - skip] = (byte)plain;
        }
        return result;
    }

    private void ReadClearText(string text)
    {
        int matrix = text.IndexOf("/FontMatrix", StringComparison.Ordinal);
        if (matrix >= 0)
        {
            int open = text.IndexOf('[', matrix), close = open >= 0 ? text.IndexOf(']', open) : -1;
            if (open >= 0 && close > open)
            {
                var values = text[(open + 1)..close].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : double.NaN).ToArray();
                if (values.Length == 6 && values.All(double.IsFinite)) FontMatrix = new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]);
            }
        }
        int encoding = text.IndexOf("/Encoding", StringComparison.Ordinal);
        if (encoding < 0) return;
        if (text.AsSpan(encoding).StartsWith("/Encoding StandardEncoding", StringComparison.Ordinal))
        {
            for (int code = 0; code < 256; code++) _encoding[code] = CffStandardStrings.StandardEncoding(code);
            return;
        }
        // "dup <code> /<name> put" entries
        int end = text.IndexOf("readonly def", encoding, StringComparison.Ordinal);
        if (end < 0) end = text.IndexOf(" def", encoding, StringComparison.Ordinal);
        var tokens = text[encoding..(end > encoding ? end : text.Length)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 3 < tokens.Length; i++)
            if (tokens[i] == "dup" && int.TryParse(tokens[i + 1], out int code) && tokens[i + 2].StartsWith('/') && tokens[i + 3] == "put" && code is >= 0 and < 256)
                _encoding[code] = tokens[i + 2][1..];
    }

    private void ReadPrivate(byte[] plain)
    {
        var text = Encoding.Latin1.GetString(plain);
        int lenIV = text.IndexOf("/lenIV", StringComparison.Ordinal);
        if (lenIV >= 0 && int.TryParse(text.AsSpan(lenIV + 6).TrimStart().ToString().Split(' ', '\r', '\n')[0], out int value)) _lenIV = value;

        // Subrs: "dup <index> <length> RD <binary> NP"
        int subrs = text.IndexOf("/Subrs", StringComparison.Ordinal);
        int charStrings = text.IndexOf("/CharStrings", StringComparison.Ordinal);
        if (subrs >= 0)
        {
            int p = subrs;
            int limit = charStrings > subrs ? charStrings : plain.Length;
            while (p < limit)
            {
                int dup = text.IndexOf("dup ", p, StringComparison.Ordinal);
                if (dup < 0 || dup >= limit) break;
                var parts = ReadTokens(text, dup + 4, 3, out int dataStart);
                if (parts.Length < 3 || !int.TryParse(parts[0], out int index) || !int.TryParse(parts[1], out int length) || length < 0 || dataStart + length > plain.Length)
                {
                    p = dup + 4;
                    continue;
                }
                var charString = DecryptCharString(plain.AsSpan(dataStart, length).ToArray());
                while (_subrs.Count <= index && _subrs.Count < 65536) _subrs.Add(Array.Empty<byte>());
                if (index < _subrs.Count) _subrs[index] = charString;
                p = dataStart + length;
            }
        }
        // CharStrings: "/<name> <length> RD <binary> ND"
        if (charStrings >= 0)
        {
            int p = text.IndexOf("begin", charStrings, StringComparison.Ordinal);
            p = p < 0 ? charStrings + 12 : p + 5;
            while (p < plain.Length)
            {
                int slash = text.IndexOf('/', p);
                if (slash < 0) break;
                int nameEnd = slash + 1;
                while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('/' or '[' or '(' or '{')) nameEnd++;
                string name = text[(slash + 1)..nameEnd];
                var parts = ReadTokens(text, nameEnd, 2, out int dataStart);
                if (parts.Length == 2 && int.TryParse(parts[0], out int length) && length >= 0 && dataStart + length <= plain.Length)
                {
                    _charStrings[name] = DecryptCharString(plain.AsSpan(dataStart, length).ToArray());
                    p = dataStart + length;
                }
                else p = nameEnd;
                if (text.AsSpan(slash, Math.Min(20, text.Length - slash)).StartsWith("/FontName", StringComparison.Ordinal) && _charStrings.Count > 0) break;
            }
        }
    }

    /// <summary>Reads <paramref name="count"/> whitespace-separated tokens; the binary data starts after one space.</summary>
    private static string[] ReadTokens(string text, int start, int count, out int dataStart)
    {
        var tokens = new List<string>();
        int p = start;
        while (tokens.Count < count && p < text.Length)
        {
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            int tokenStart = p;
            while (p < text.Length && !char.IsWhiteSpace(text[p])) p++;
            if (p > tokenStart) tokens.Add(text[tokenStart..p]);
        }
        dataStart = p + 1;
        return tokens.ToArray();
    }

    private byte[] DecryptCharString(byte[] data) => _lenIV < 0 ? data : Decrypt(data, 4330, _lenIV);

    // ------------------------------------------------------------------ Type 1 charstrings

    public PathGeometry? GetOutline(string name, int depth = 0)
    {
        if (!_charStrings.TryGetValue(name, out var charString) || depth > 2) return null;
        var state = new State { Path = new PathGeometry() };
        try
        {
            Execute(charString, state, 0, depth);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            // damaged charstring: keep what was drawn
        }
        return state.Path;
    }

    private sealed class State
    {
        public required PathGeometry Path;
        public readonly List<double> Stack = new();
        public readonly Stack<double> PsStack = new();
        public double X, Y, SideBearingX;
        public bool Flex, Ended;
        public readonly List<(double X, double Y)> FlexPoints = new();
    }

    private void Execute(byte[] code, State s, int level, int depth)
    {
        if (level > 10) return;
        var st = s.Stack;
        int p = 0;
        while (p < code.Length && !s.Ended)
        {
            int b = code[p++];
            if (b >= 32)
            {
                if (b <= 246) st.Add(b - 139);
                else if (b <= 250) st.Add((b - 247) * 256 + code[p++] + 108);
                else if (b <= 254) st.Add(-(b - 251) * 256 - code[p++] - 108);
                else { st.Add((code[p] << 24) | (code[p + 1] << 16) | (code[p + 2] << 8) | code[p + 3]); p += 4; }
                if (st.Count > 64) throw new InvalidOperationException("Type 1 stack overflow");
                continue;
            }
            switch (b)
            {
                case 13: // hsbw
                    if (st.Count >= 2) { s.SideBearingX = st[0]; s.X = st[0]; s.Y = 0; }
                    st.Clear();
                    break;
                case 21: // rmoveto
                    MoveRelative(s, Get(st, 0), Get(st, 1));
                    st.Clear();
                    break;
                case 22: // hmoveto
                    MoveRelative(s, Get(st, 0), 0);
                    st.Clear();
                    break;
                case 4: // vmoveto
                    MoveRelative(s, 0, Get(st, 0));
                    st.Clear();
                    break;
                case 5: // rlineto
                    Line(s, s.X + Get(st, 0), s.Y + Get(st, 1));
                    st.Clear();
                    break;
                case 6: // hlineto
                    Line(s, s.X + Get(st, 0), s.Y);
                    st.Clear();
                    break;
                case 7: // vlineto
                    Line(s, s.X, s.Y + Get(st, 0));
                    st.Clear();
                    break;
                case 8: // rrcurveto
                    Curve(s, Get(st, 0), Get(st, 1), Get(st, 2), Get(st, 3), Get(st, 4), Get(st, 5));
                    st.Clear();
                    break;
                case 30: // vhcurveto
                    Curve(s, 0, Get(st, 0), Get(st, 1), Get(st, 2), Get(st, 3), 0);
                    st.Clear();
                    break;
                case 31: // hvcurveto
                    Curve(s, Get(st, 0), 0, Get(st, 1), Get(st, 2), 0, Get(st, 3));
                    st.Clear();
                    break;
                case 9: // closepath
                    s.Path.Close();
                    st.Clear();
                    break;
                case 10: // callsubr
                {
                    if (st.Count == 0) break;
                    int index = (int)st[^1];
                    st.RemoveAt(st.Count - 1);
                    if (index >= 0 && index < _subrs.Count) Execute(_subrs[index], s, level + 1, depth);
                    break;
                }
                case 11: // return
                    return;
                case 14: // endchar
                    s.Path.Close();
                    s.Ended = true;
                    return;
                case 1 or 3: // hstem, vstem
                    st.Clear();
                    break;
                case 12:
                    Escape(code[p++], s, depth);
                    break;
                default:
                    st.Clear();
                    break;
            }
        }
    }

    private static double Get(List<double> stack, int index) => index < stack.Count ? stack[index] : 0;

    private static void MoveRelative(State s, double dx, double dy)
    {
        s.X += dx;
        s.Y += dy;
        if (s.Flex) s.FlexPoints.Add((s.X, s.Y));
        else s.Path.MoveTo(s.X, s.Y);
    }

    private static void Line(State s, double x, double y)
    {
        s.X = x;
        s.Y = y;
        s.Path.LineTo(x, y);
    }

    private static void Curve(State s, double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        double x1 = s.X + dx1, y1 = s.Y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
        s.X = x2 + dx3;
        s.Y = y2 + dy3;
        s.Path.CurveTo(x1, y1, x2, y2, s.X, s.Y);
    }

    private void Escape(int op, State s, int depth)
    {
        var st = s.Stack;
        switch (op)
        {
            case 6: // seac: asb adx ady bchar achar
                if (st.Count >= 5)
                {
                    double asb = st[0], adx = st[1], ady = st[2];
                    string? baseName = CffStandardStrings.StandardEncoding((int)st[3]), accentName = CffStandardStrings.StandardEncoding((int)st[4]);
                    if (baseName != null && GetOutline(baseName, depth + 1) is { } basePath) CffFont.Append(s.Path, basePath, 0, 0);
                    if (accentName != null && GetOutline(accentName, depth + 1) is { } accentPath)
                        CffFont.Append(s.Path, accentPath, s.SideBearingX - asb + adx, ady);
                }
                st.Clear();
                s.Ended = true;
                break;
            case 7: // sbw
                if (st.Count >= 4) { s.SideBearingX = st[0]; s.X = st[0]; s.Y = st[1]; }
                st.Clear();
                break;
            case 12: // div
                if (st.Count >= 2)
                {
                    double b = st[^1], a = st[^2];
                    st.RemoveRange(st.Count - 2, 2);
                    st.Add(b != 0 ? a / b : 0);
                }
                break;
            case 16: // callothersubr: arguments, count, number
            {
                if (st.Count < 2) { st.Clear(); break; }
                int number = (int)st[^1], count = (int)st[^2];
                st.RemoveRange(st.Count - 2, 2);
                count = Math.Clamp(count, 0, st.Count);
                var args = st.GetRange(st.Count - count, count);
                st.RemoveRange(st.Count - count, count);
                switch (number)
                {
                    case 1: // start flex
                        s.Flex = true;
                        s.FlexPoints.Clear();
                        break;
                    case 0: // end flex: the reference point and six control points form two curves
                        s.Flex = false;
                        if (s.FlexPoints.Count >= 7)
                        {
                            var f = s.FlexPoints;
                            s.Path.CurveTo(f[1].X, f[1].Y, f[2].X, f[2].Y, f[3].X, f[3].Y);
                            s.Path.CurveTo(f[4].X, f[4].Y, f[5].X, f[5].Y, f[6].X, f[6].Y);
                            (s.X, s.Y) = f[6];
                        }
                        // the end point is returned to the interpreter (two pops, then setcurrentpoint)
                        s.PsStack.Push(s.Y);
                        s.PsStack.Push(s.X);
                        break;
                    case 3: // hint replacement: pop returns the subroutine number 3
                        s.PsStack.Push(3);
                        break;
                    default:
                        for (int i = args.Count - 1; i >= 0; i--) s.PsStack.Push(args[i]);
                        break;
                }
                break;
            }
            case 17: // pop
                st.Add(s.PsStack.Count > 0 ? s.PsStack.Pop() : 0);
                break;
            case 33: // setcurrentpoint
                if (st.Count >= 2) { s.X = st[0]; s.Y = st[1]; }
                st.Clear();
                break;
            default: // dotsection, vstem3, hstem3
                st.Clear();
                break;
        }
    }
}
