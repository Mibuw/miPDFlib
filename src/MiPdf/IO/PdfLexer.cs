using System.Globalization;
using System.Text;

namespace MiPdf.IO;

internal enum TokenKind
{
    Eof,
    Integer,
    Real,
    String,
    HexString,
    Name,
    Keyword,
    ArrayStart,
    ArrayEnd,
    DictStart,
    DictEnd,
    BraceOpen,
    BraceClose,
}

/// <summary>Tokenizer for PDF files and content streams (ISO 32000-2, 7.2).</summary>
internal sealed class PdfLexer
{
    private readonly byte[] _data;
    private readonly int _end;

    public PdfLexer(byte[] data, int start = 0, int length = -1)
    {
        _data = data;
        Position = start;
        _end = length < 0 ? data.Length : Math.Min(data.Length, start + length);
    }

    public byte[] Data => _data;

    public int Position { get; set; }

    public int End => _end;

    public TokenKind Kind { get; private set; }

    public int TokenStart { get; private set; }

    public long IntValue { get; private set; }

    public double RealValue { get; private set; }

    /// <summary>Name (without slash) or keyword text.</summary>
    public string Text { get; private set; } = "";

    /// <summary>Bytes of string tokens.</summary>
    public byte[] Bytes { get; private set; } = Array.Empty<byte>();

    public static bool IsWhitespace(int c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(int c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    public static bool IsRegular(int c) => !IsWhitespace(c) && !IsDelimiter(c);

    public bool IsKeyword(string keyword) => Kind == TokenKind.Keyword && Text == keyword;

    public int PeekByte() => Position < _end ? _data[Position] : -1;

    public void SkipWhitespaceAndComments()
    {
        while (Position < _end)
        {
            byte c = _data[Position];
            if (IsWhitespace(c)) Position++;
            else if (c == '%')
            {
                while (Position < _end && _data[Position] != '\n' && _data[Position] != '\r') Position++;
            }
            else break;
        }
    }

    /// <summary>Reads the next token. Returns false at end of data.</summary>
    public bool Next()
    {
        SkipWhitespaceAndComments();
        TokenStart = Position;
        if (Position >= _end)
        {
            Kind = TokenKind.Eof;
            return false;
        }
        byte c = _data[Position];
        switch (c)
        {
            case (byte)'[':
                Position++;
                Kind = TokenKind.ArrayStart;
                return true;
            case (byte)']':
                Position++;
                Kind = TokenKind.ArrayEnd;
                return true;
            case (byte)'{':
                Position++;
                Kind = TokenKind.BraceOpen;
                return true;
            case (byte)'}':
                Position++;
                Kind = TokenKind.BraceClose;
                return true;
            case (byte)'(':
                Position++;
                ReadLiteralString();
                return true;
            case (byte)'/':
                Position++;
                ReadName();
                return true;
            case (byte)'<':
                if (Position + 1 < _end && _data[Position + 1] == '<')
                {
                    Position += 2;
                    Kind = TokenKind.DictStart;
                    return true;
                }
                Position++;
                ReadHexString();
                return true;
            case (byte)'>':
                Position++;
                if (Position < _end && _data[Position] == '>') Position++;
                Kind = TokenKind.DictEnd;
                return true;
            case (byte)')':
                Position++; // stray delimiter, treat as keyword
                Kind = TokenKind.Keyword;
                Text = ")";
                return true;
        }
        if (c is (byte)'+' or (byte)'-' or (byte)'.' || (c >= '0' && c <= '9'))
        {
            if (ReadNumber()) return true;
        }
        ReadKeyword();
        return true;
    }

    private bool ReadNumber()
    {
        int start = Position;
        int p = start;
        while (p < _end && _data[p] is (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9') p++;
        if (p < _end && IsRegular(_data[p])) return false; // e.g. "1abc": not a number

        // Tolerant parsing: leading signs, digits with at most one dot; garbage after that is ignored
        // (real-world files contain things like "--5" or "0.00-5").
        int i = start;
        bool negative = false;
        while (i < p && _data[i] is (byte)'+' or (byte)'-')
        {
            if (_data[i] == '-') negative = true;
            i++;
        }
        long intPart = 0;
        double fraction = 0, scale = 1;
        bool isReal = false, overflow = false;
        for (; i < p; i++)
        {
            byte c = _data[i];
            if (c == '.')
            {
                if (isReal) break;
                isReal = true;
            }
            else if (c >= '0' && c <= '9')
            {
                if (isReal)
                {
                    scale /= 10;
                    fraction += (c - '0') * scale;
                }
                else if (intPart > (long.MaxValue - 9) / 10) overflow = true;
                else intPart = intPart * 10 + (c - '0');
            }
            else break;
        }
        Position = p;
        if (isReal || overflow)
        {
            double d = intPart + fraction;
            if (overflow && !double.TryParse(Encoding.ASCII.GetString(_data, start, p - start).TrimStart('+', '-').Split('.')[0],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                d = double.MaxValue;
            if (negative) d = -d;
            Kind = TokenKind.Real;
            RealValue = d;
            IntValue = (long)d;
        }
        else
        {
            Kind = TokenKind.Integer;
            IntValue = negative ? -intPart : intPart;
            RealValue = IntValue;
        }
        return true;
    }

    private void ReadKeyword()
    {
        int start = Position;
        while (Position < _end && IsRegular(_data[Position])) Position++;
        if (Position == start) Position++; // unexpected delimiter; consume it
        Kind = TokenKind.Keyword;
        Text = Encoding.Latin1.GetString(_data, start, Position - start);
    }

    private void ReadName()
    {
        var buffer = new List<byte>(16);
        while (Position < _end && IsRegular(_data[Position]))
        {
            byte c = _data[Position];
            if (c == '#' && Position + 2 < _end && AsciiHex(_data[Position + 1]) >= 0 && AsciiHex(_data[Position + 2]) >= 0)
            {
                buffer.Add((byte)((AsciiHex(_data[Position + 1]) << 4) | AsciiHex(_data[Position + 2])));
                Position += 3;
                continue;
            }
            buffer.Add(c);
            Position++;
        }
        Kind = TokenKind.Name;
        Text = DecodeName(buffer.ToArray());
    }

    internal static string DecodeName(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            if (b >= 0x80)
            {
                try
                {
                    return new UTF8Encoding(false, true).GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    return Encoding.Latin1.GetString(bytes);
                }
            }
        }
        return Encoding.ASCII.GetString(bytes);
    }

    private static int AsciiHex(int c) => Filters.AsciiHexFilter.HexValue(c);

    private void ReadHexString()
    {
        var buffer = new List<byte>(32);
        int high = -1;
        while (Position < _end)
        {
            byte c = _data[Position++];
            if (c == '>') break;
            int v = AsciiHex(c);
            if (v < 0) continue;
            if (high < 0) high = v;
            else
            {
                buffer.Add((byte)((high << 4) | v));
                high = -1;
            }
        }
        if (high >= 0) buffer.Add((byte)(high << 4));
        Kind = TokenKind.HexString;
        Bytes = buffer.ToArray();
    }

    private void ReadLiteralString()
    {
        var buffer = new List<byte>(32);
        int depth = 1;
        while (Position < _end)
        {
            byte c = _data[Position++];
            switch (c)
            {
                case (byte)'(':
                    depth++;
                    buffer.Add(c);
                    break;
                case (byte)')':
                    if (--depth == 0)
                    {
                        Kind = TokenKind.String;
                        Bytes = buffer.ToArray();
                        return;
                    }
                    buffer.Add(c);
                    break;
                case (byte)'\r':
                    buffer.Add((byte)'\n');
                    if (Position < _end && _data[Position] == '\n') Position++;
                    break;
                case (byte)'\\':
                    if (Position >= _end) break;
                    byte e = _data[Position++];
                    switch (e)
                    {
                        case (byte)'n': buffer.Add((byte)'\n'); break;
                        case (byte)'r': buffer.Add((byte)'\r'); break;
                        case (byte)'t': buffer.Add((byte)'\t'); break;
                        case (byte)'b': buffer.Add((byte)'\b'); break;
                        case (byte)'f': buffer.Add((byte)'\f'); break;
                        case (byte)'\r':
                            if (Position < _end && _data[Position] == '\n') Position++;
                            break;
                        case (byte)'\n':
                            break;
                        default:
                            if (e >= '0' && e <= '7')
                            {
                                int v = e - '0';
                                for (int k = 0; k < 2 && Position < _end && _data[Position] >= '0' && _data[Position] <= '7'; k++)
                                    v = v * 8 + (_data[Position++] - '0');
                                buffer.Add((byte)v);
                            }
                            else buffer.Add(e); // \( \) \\ and unknown escapes
                            break;
                    }
                    break;
                default:
                    buffer.Add(c);
                    break;
            }
        }
        Kind = TokenKind.String; // unterminated string: return what we have
        Bytes = buffer.ToArray();
    }

    /// <summary>Searches forward for <paramref name="pattern"/> starting at <paramref name="from"/>.</summary>
    public int IndexOf(ReadOnlySpan<byte> pattern, int from)
    {
        if (from >= _end) return -1;
        int idx = _data.AsSpan(from, _end - from).IndexOf(pattern);
        return idx < 0 ? -1 : from + idx;
    }
}
