using System.Globalization;
using System.Text;

namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// The PostScript scanner (PLRM 3.2): numbers (including radix numbers), names, literal and immediately evaluated
/// names, strings with escapes, hexadecimal and ASCII base-85 strings, procedures and the self-delimiting tokens
/// <c>[ ] &lt;&lt; &gt;&gt;</c>.
/// </summary>
internal sealed class PsScanner
{
    private readonly PsInterpreter _ip;
    private readonly List<byte> _buffer = new();

    public PsScanner(PsInterpreter ip) => _ip = ip;

    public static bool IsWhitespace(int c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(int c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    /// <summary>Reads the next token; false at the end of the file.</summary>
    public bool Next(PsFile file, out PsObject token)
    {
        var procedures = new Stack<List<PsObject>>();
        while (true)
        {
            if (!NextSimple(file, out var simple, out int procedureSign))
            {
                if (procedures.Count > 0) throw new PsError("syntaxerror"); // unterminated procedure
                token = default;
                return false;
            }
            if (procedureSign > 0)
            {
                procedures.Push(new List<PsObject>());
                continue;
            }
            if (procedureSign < 0)
            {
                if (procedures.Count == 0) throw new PsError("syntaxerror"); // '}' without '{'
                var items = procedures.Pop();
                var proc = _ip.NewArray(items.ToArray(), _ip.Packing).AsExecutable();
                if (procedures.Count == 0)
                {
                    token = proc;
                    return true;
                }
                procedures.Peek().Add(proc);
                continue;
            }
            if (procedures.Count == 0)
            {
                token = simple;
                return true;
            }
            procedures.Peek().Add(simple);
            _ip.CountOperation(); // long procedures are work, too
        }
    }

    /// <summary>A token other than a procedure; <paramref name="procedureSign"/> is 1 for '{' and -1 for '}'.</summary>
    private bool NextSimple(PsFile file, out PsObject token, out int procedureSign)
    {
        procedureSign = 0;
        token = default;
        int c;
        while (true)
        {
            c = file.ReadByte();
            if (c < 0) return false;
            if (IsWhitespace(c)) continue;
            if (c == '%')
            {
                // comment up to the end of the line
                while (c >= 0 && c != '\n' && c != '\r') c = file.ReadByte();
                if (c < 0) return false;
                continue;
            }
            break;
        }
        switch (c)
        {
            case '{':
                procedureSign = 1;
                return true;
            case '}':
                procedureSign = -1;
                return true;
            case '[':
            case ']':
                token = PsObject.Name(_ip.Name(c == '[' ? "[" : "]"), executable: true);
                return true;
            case '(':
                token = _ip.NewString(ReadString(file));
                return true;
            case ')':
                throw new PsError("syntaxerror");
            case '<':
            {
                int d = file.ReadByte();
                if (d == '<')
                {
                    token = PsObject.Name(_ip.Name("<<"), executable: true);
                    return true;
                }
                if (d == '~')
                {
                    token = _ip.NewString(ReadAscii85(file));
                    return true;
                }
                file.Unread(d);
                token = _ip.NewString(ReadHex(file));
                return true;
            }
            case '>':
            {
                int d = file.ReadByte();
                if (d != '>') throw new PsError("syntaxerror");
                token = PsObject.Name(_ip.Name(">>"), executable: true);
                return true;
            }
            case '/':
            {
                int d = file.ReadByte();
                bool immediate = d == '/';
                if (!immediate) file.Unread(d);
                string text = ReadRegular(file, -1);
                var name = _ip.Name(text);
                if (immediate)
                {
                    // immediately evaluated name: its current value
                    token = _ip.Lookup(name) ?? throw new PsError("undefined", PsObject.Name(name, executable: true));
                    return true;
                }
                token = PsObject.Name(name);
                return true;
            }
        }
        if (c >= 128 && c <= 159) throw new PsError("syntaxerror"); // binary tokens are not supported
        string regular = ReadRegular(file, c);
        token = ParseNumber(regular) ?? PsObject.Name(_ip.Name(regular), executable: true);
        return true;
    }

    /// <summary>Regular characters up to a delimiter or whitespace (a whitespace delimiter is consumed).</summary>
    private string ReadRegular(PsFile file, int first)
    {
        _buffer.Clear();
        if (first >= 0) _buffer.Add((byte)first);
        while (true)
        {
            int c = file.ReadByte();
            if (c < 0) break;
            if (IsWhitespace(c))
            {
                // the terminating whitespace is consumed; CR LF counts as one newline
                if (c == '\r')
                {
                    int d = file.ReadByte();
                    if (d != '\n') file.Unread(d);
                }
                break;
            }
            if (IsDelimiter(c))
            {
                file.Unread(c);
                break;
            }
            _buffer.Add((byte)c);
            if (_buffer.Count > 65535) throw new PsError("limitcheck");
        }
        return PsText.Latin1.GetString(_buffer.ToArray());
    }

    private byte[] ReadString(PsFile file)
    {
        _buffer.Clear();
        int depth = 1;
        while (true)
        {
            int c = file.ReadByte();
            if (c < 0) throw new PsError("syntaxerror");
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) break;
            else if (c == '\\')
            {
                c = file.ReadByte();
                if (c < 0) throw new PsError("syntaxerror");
                switch (c)
                {
                    case 'n': c = '\n'; break;
                    case 'r': c = '\r'; break;
                    case 't': c = '\t'; break;
                    case 'b': c = '\b'; break;
                    case 'f': c = '\f'; break;
                    case '\r':
                    {
                        // line continuation (\ CR LF counts as one)
                        int d = file.ReadByte();
                        if (d != '\n') file.Unread(d);
                        continue;
                    }
                    case '\n':
                        continue;
                    case >= '0' and <= '7':
                    {
                        int value = c - '0';
                        for (int i = 0; i < 2; i++)
                        {
                            int d = file.ReadByte();
                            if (d < '0' || d > '7')
                            {
                                file.Unread(d);
                                break;
                            }
                            value = value * 8 + d - '0';
                        }
                        c = value & 0xFF;
                        break;
                    }
                    // any other character stands for itself (including \ ( and ))
                }
            }
            else if (c == '\r')
            {
                // an end of line in a string is a newline character
                int d = file.ReadByte();
                if (d != '\n') file.Unread(d);
                c = '\n';
            }
            _buffer.Add((byte)c);
            if (_buffer.Count > _ip.Limits.MaxStringLength) throw new PsError("limitcheck");
        }
        return _buffer.ToArray();
    }

    private byte[] ReadHex(PsFile file)
    {
        _buffer.Clear();
        int high = -1;
        while (true)
        {
            int c = file.ReadByte();
            if (c < 0) throw new PsError("syntaxerror");
            if (c == '>') break;
            if (IsWhitespace(c)) continue;
            int digit = PsAsciiHexDecode.HexDigit(c);
            if (digit < 0) throw new PsError("syntaxerror");
            if (high < 0) high = digit;
            else
            {
                _buffer.Add((byte)(high << 4 | digit));
                high = -1;
            }
        }
        if (high >= 0) _buffer.Add((byte)(high << 4));
        if (_buffer.Count > _ip.Limits.MaxStringLength) throw new PsError("limitcheck");
        return _buffer.ToArray();
    }

    private byte[] ReadAscii85(PsFile file)
    {
        var filter = new PsAscii85Decode(file);
        var result = new List<byte>();
        try
        {
            int b;
            while ((b = filter.ReadByte()) >= 0)
            {
                result.Add((byte)b);
                if (result.Count > _ip.Limits.MaxStringLength) throw new PsError("limitcheck");
            }
        }
        catch (PsError e) when (e.Name == "ioerror")
        {
            throw new PsError("syntaxerror");
        }
        return result.ToArray();
    }

    /// <summary>Integer, real or radix number; null if the text is not a number (it is a name then).</summary>
    public static PsObject? ParseNumber(string s)
    {
        if (s.Length == 0) return null;
        int hash = s.IndexOf('#');
        if (hash > 0)
        {
            // radix number: base#digits, the digits are an unsigned value
            if (!int.TryParse(s.AsSpan(0, hash), NumberStyles.None, CultureInfo.InvariantCulture, out int radix)
                || radix < 2 || radix > 36 || hash == s.Length - 1)
                return null;
            ulong value = 0;
            for (int i = hash + 1; i < s.Length; i++)
            {
                int d = char.ToLowerInvariant(s[i]) switch
                {
                    var ch and >= '0' and <= '9' => ch - '0',
                    var ch and >= 'a' and <= 'z' => ch - 'a' + 10,
                    _ => 99,
                };
                if (d >= radix) return null;
                value = unchecked(value * (ulong)radix + (ulong)d);
            }
            return PsObject.Integer(unchecked((long)value));
        }
        int p = 0;
        if (s[0] is '+' or '-') p = 1;
        int digits = 0, fraction = 0;
        bool dot = false, exponent = false;
        for (; p < s.Length; p++)
        {
            char ch = s[p];
            if (ch >= '0' && ch <= '9')
            {
                if (dot) fraction++;
                else digits++;
            }
            else if (ch == '.' && !dot) dot = true;
            else if (ch is 'e' or 'E' && digits + fraction > 0)
            {
                exponent = true;
                p++;
                if (p < s.Length && s[p] is '+' or '-') p++;
                if (p >= s.Length) return null;
                for (; p < s.Length; p++)
                    if (s[p] < '0' || s[p] > '9') return null;
                break;
            }
            else return null;
        }
        if (digits + fraction == 0) return null;
        if (!dot && !exponent)
        {
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return PsObject.Integer(l);
            // too large for an integer: a real
        }
        double d2 = double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        float f = (float)d2;
        if (float.IsInfinity(f)) throw new PsError("limitcheck");
        return PsObject.RealValue(f);
    }
}
