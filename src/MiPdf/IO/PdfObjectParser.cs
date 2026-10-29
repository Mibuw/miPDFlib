using MiPdf.Objects;

namespace MiPdf.IO;

/// <summary>Parses PDF objects from a token stream (ISO 32000-2, 7.3).</summary>
internal sealed class PdfObjectParser
{
    private const int MaxDepth = 512;
    private readonly Func<int, int, PdfReference> _referenceFactory;

    public PdfObjectParser(PdfLexer lexer, Func<int, int, PdfReference>? referenceFactory = null)
    {
        Lexer = lexer;
        _referenceFactory = referenceFactory ?? ((n, g) => new PdfReference(n, g, null));
    }

    public PdfLexer Lexer { get; }

    /// <summary>Parses the next object. Returns null at end of data or when a non-object keyword is found.</summary>
    public PdfObject? ParseObject()
    {
        if (!Lexer.Next()) return null;
        return ParseCurrent(0);
    }

    /// <summary>Converts the current token (already read) into an object.</summary>
    public PdfObject? ParseCurrent(int depth)
    {
        if (depth > MaxDepth) throw new PdfParseException("Objects nested too deeply", Lexer.TokenStart);
        switch (Lexer.Kind)
        {
            case TokenKind.Integer:
                return ParseIntegerOrReference();
            case TokenKind.Real:
                return new PdfNumber(Lexer.RealValue);
            case TokenKind.String:
                return new PdfString(Lexer.Bytes);
            case TokenKind.HexString:
                return new PdfString(Lexer.Bytes, hex: true);
            case TokenKind.Name:
                return PdfName.Get(Lexer.Text);
            case TokenKind.ArrayStart:
                return ParseArray(depth);
            case TokenKind.DictStart:
                return ParseDictionary(depth);
            case TokenKind.Keyword:
                return Lexer.Text switch
                {
                    "true" => PdfBoolean.True,
                    "false" => PdfBoolean.False,
                    "null" => PdfNull.Instance,
                    _ => null,
                };
            default:
                return null;
        }
    }

    private PdfObject ParseIntegerOrReference()
    {
        long first = Lexer.IntValue;
        int save = Lexer.Position;
        if (first >= 0 && first <= int.MaxValue && Lexer.Next() && Lexer.Kind == TokenKind.Integer)
        {
            long gen = Lexer.IntValue;
            if (gen >= 0 && gen <= 65535 && Lexer.Next() && Lexer.IsKeyword("R"))
                return _referenceFactory((int)first, (int)gen);
        }
        Lexer.Position = save;
        return new PdfNumber(first);
    }

    private PdfArray ParseArray(int depth)
    {
        var array = new PdfArray();
        while (Lexer.Next())
        {
            if (Lexer.Kind == TokenKind.ArrayEnd) return array;
            if (Lexer.Kind == TokenKind.DictEnd) return array; // malformed, be tolerant
            if (Lexer.Kind == TokenKind.Keyword && IsStructuralKeyword(Lexer.Text))
            {
                Lexer.Position = Lexer.TokenStart;
                return array;
            }
            var item = ParseCurrent(depth + 1);
            if (item != null) array.Add(item);
        }
        return array;
    }

    private PdfDictionary ParseDictionary(int depth)
    {
        var dict = new PdfDictionary();
        while (Lexer.Next())
        {
            if (Lexer.Kind == TokenKind.DictEnd) return dict;
            if (Lexer.Kind == TokenKind.Keyword && IsStructuralKeyword(Lexer.Text))
            {
                Lexer.Position = Lexer.TokenStart; // missing ">>": stop before stream/endobj
                return dict;
            }
            if (Lexer.Kind != TokenKind.Name) continue; // skip garbage
            var key = PdfName.Get(Lexer.Text);
            if (!Lexer.Next()) break;
            if (Lexer.Kind == TokenKind.DictEnd)
            {
                return dict; // key without value
            }
            if (Lexer.Kind == TokenKind.Keyword && IsStructuralKeyword(Lexer.Text))
            {
                Lexer.Position = Lexer.TokenStart;
                return dict;
            }
            var value = ParseCurrent(depth + 1);
            if (value != null && value is not PdfNull) dict[key] = value; // null value == absent key
        }
        return dict;
    }

    private static bool IsStructuralKeyword(string keyword) =>
        keyword is "stream" or "endobj" or "endstream" or "obj" or "xref" or "trailer" or "startxref";
}
