using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf.Content;

/// <summary>An operator with its operands from a content stream.</summary>
public sealed class ContentOperation
{
    internal ContentOperation(string op, List<PdfObject> operands)
    {
        Operator = op;
        Operands = operands;
    }

    public string Operator { get; }

    public IReadOnlyList<PdfObject> Operands { get; }

    /// <summary>For inline images (operator "BI"): the image dictionary and data.</summary>
    public PdfStream? InlineImage { get; internal set; }

    public override string ToString() => string.Join(' ', Operands.Select(o => o.ToString())) + " " + Operator;
}

/// <summary>Parses content streams into operations (ISO 32000-2, 7.8.2).</summary>
public static class ContentParser
{
    public static IEnumerable<ContentOperation> Parse(byte[] content)
    {
        var lexer = new PdfLexer(content);
        var parser = new PdfObjectParser(lexer);
        var operands = new List<PdfObject>();
        while (lexer.Next())
        {
            if (lexer.Kind == TokenKind.Keyword)
            {
                string op = lexer.Text;
                if (op is "true" or "false" or "null")
                {
                    operands.Add(parser.ParseCurrent(0)!);
                    continue;
                }
                var operation = new ContentOperation(op, operands);
                if (op == "BI") operation.InlineImage = ReadInlineImage(lexer, parser);
                yield return operation;
                operands = new List<PdfObject>();
                continue;
            }
            if (lexer.Kind is TokenKind.ArrayEnd or TokenKind.DictEnd or TokenKind.BraceOpen or TokenKind.BraceClose) continue;
            PdfObject? operand;
            try
            {
                operand = parser.ParseCurrent(0);
            }
            catch (PdfParseException)
            {
                yield break;
            }
            if (operand != null) operands.Add(operand);
        }
    }

    /// <summary>Reads "key value ... ID data EI" after the BI operator.</summary>
    private static PdfStream ReadInlineImage(PdfLexer lexer, PdfObjectParser parser)
    {
        var dict = new PdfDictionary();
        while (lexer.Next())
        {
            if (lexer.Kind == TokenKind.Keyword && lexer.Text == "ID") break;
            if (lexer.Kind != TokenKind.Name) continue;
            var key = PdfName.Get(lexer.Text);
            var value = parser.ParseObject();
            if (value != null) dict[key] = value;
        }
        var data = lexer.Data;
        int start = lexer.Position;
        if (start < lexer.End && PdfLexer.IsWhitespace(data[start])) start++;
        int end = start;
        // search "EI" surrounded by whitespace (the data itself may contain these bytes)
        int p = start;
        while (true)
        {
            int idx = lexer.IndexOf("EI"u8, p);
            if (idx < 0)
            {
                end = lexer.End;
                lexer.Position = lexer.End;
                break;
            }
            bool before = idx > start && PdfLexer.IsWhitespace(data[idx - 1]);
            bool after = idx + 2 >= lexer.End || PdfLexer.IsWhitespace(data[idx + 2]) || PdfLexer.IsDelimiter(data[idx + 2]);
            if (before && after)
            {
                end = idx - 1;
                lexer.Position = idx + 2;
                break;
            }
            p = idx + 2;
        }
        return new PdfStream(dict, data.AsSpan(start, Math.Max(0, end - start)).ToArray());
    }
}
