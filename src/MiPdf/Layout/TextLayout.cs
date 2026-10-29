using MiPdf.Fonts;
using MiPdf.Graphics;

namespace MiPdf.Layout;

/// <summary>A piece of a line: text with uniform formatting.</summary>
internal sealed class TextPiece
{
    public required string Text;
    public required PdfFont Font;
    public required double FontSize;
    public required PdfColor Color;
    public required TextRun Run;
    public double Width;
    public bool IsSpace;

    /// <summary>Positions in <see cref="Text"/> where the word may be hyphenated (soft hyphens).</summary>
    public List<int>? SoftHyphens;
}

/// <summary>A laid-out line of a paragraph.</summary>
internal sealed class TextLine
{
    public List<TextPiece> Pieces { get; } = new();

    /// <summary>Width without trailing spaces.</summary>
    public double Width;

    public double Ascent;

    public double Descent;

    public double Height;

    public double Indent;

    /// <summary>True for the last line of a paragraph or a line ended by "\n" (not justified).</summary>
    public bool EndsParagraph;
}

/// <summary>Line breaking and rendering of paragraphs. Also usable for simple multi-line text.</summary>
public static class TextLayout
{
    private const char SoftHyphen = '\u00AD';

    /// <summary>Hyphenator used by paragraphs without their own <see cref="Paragraph.Hyphenator"/> (default none).</summary>
    public static IHyphenator? DefaultHyphenator { get; set; }

    /// <summary>Breaks plain text into lines that fit into <paramref name="width"/> points.</summary>
    public static IReadOnlyList<string> BreakLines(string text, PdfFont font, double fontSize, double width)
    {
        var p = new Paragraph(text, font, fontSize);
        return BreakLines(p, width).Select(l => string.Concat(l.Pieces.Select(x => x.Text)).TrimEnd()).ToList();
    }

    /// <summary>Measures the height of plain text wrapped to <paramref name="width"/>.</summary>
    public static double MeasureHeight(string text, PdfFont font, double fontSize, double width, double lineSpacing = 1.15)
    {
        var p = new Paragraph(text, font, fontSize) { LineSpacing = lineSpacing };
        return BreakLines(p, width).Sum(l => l.Height);
    }

    internal static List<TextLine> BreakLines(Paragraph p, double totalWidth)
    {
        var lines = new List<TextLine>();
        double available = Math.Max(1, totalWidth - p.LeftIndent - p.RightIndent);
        var current = NewLine(p, p.FirstLineIndent);

        void Finish(bool endsParagraph)
        {
            // trailing spaces do not count for width
            current.EndsParagraph = endsParagraph;
            Measure(p, current);
            lines.Add(current);
            current = NewLine(p, 0);
        }

        foreach (var run in p.Runs)
        {
            var font = run.Font ?? p.Font;
            double size = run.FontSize ?? p.FontSize;
            var color = run.Color ?? p.Color;
            foreach (var token in Tokenize(run.Text))
            {
                if (token == "\n")
                {
                    Finish(true);
                    continue;
                }
                var piece = new TextPiece { Text = token, Font = font, FontSize = size, Color = color, Run = run, IsSpace = token == " " };
                if (token.Contains(SoftHyphen))
                {
                    piece.SoftHyphens = new List<int>();
                    var clean = new System.Text.StringBuilder();
                    foreach (char ch in token)
                    {
                        if (ch == SoftHyphen) piece.SoftHyphens.Add(clean.Length);
                        else clean.Append(ch);
                    }
                    piece.Text = clean.ToString();
                }
                piece.Width = font.GetTextWidth(piece.Text, size, p.Kerning);
                double lineWidth = available - current.Indent;
                if (piece.IsSpace)
                {
                    if (current.Pieces.Count > 0) current.Pieces.Add(piece);
                    continue;
                }
                double used = TotalWidth(current); // including the space before the word
                if (used + piece.Width <= lineWidth + 0.001)
                {
                    current.Pieces.Add(piece);
                    continue;
                }
                if (current.Pieces.Count > 0 && ContentWidth(current) > 0)
                {
                    if (TryHyphenate(p, piece, lineWidth - used, out var head))
                    {
                        current.Pieces.Add(head);
                        piece.Width = font.GetTextWidth(piece.Text, size, p.Kerning);
                    }
                    Finish(false);
                    lineWidth = available - current.Indent;
                }
                // word longer than a line: break by characters
                while (piece.Width > lineWidth && piece.Text.Length > 1)
                {
                    int fit = FitCharacters(piece.Text, font, size, lineWidth);
                    var head = piece.Text[..fit];
                    current.Pieces.Add(new TextPiece { Text = head, Font = font, FontSize = size, Color = color, Run = run, Width = font.GetTextWidth(head, size) });
                    Finish(false);
                    lineWidth = available - current.Indent;
                    piece.Text = piece.Text[fit..];
                    piece.Width = font.GetTextWidth(piece.Text, size);
                }
                current.Pieces.Add(piece);
            }
        }
        if (current.Pieces.Count > 0 || lines.Count == 0) Finish(true);
        else if (lines.Count > 0) lines[^1].EndsParagraph = true;
        return lines;
    }

    private static TextLine NewLine(Paragraph p, double indent) => new() { Indent = indent };

    private static double TotalWidth(TextLine line) => line.Pieces.Sum(x => x.Width);

    /// <summary>
    /// Splits <paramref name="piece"/> at the last possible break (soft hyphen, hard hyphen, hyphenator) so that the
    /// head plus hyphen fits into <paramref name="space"/>. The piece keeps the tail.
    /// </summary>
    private static bool TryHyphenate(Paragraph p, TextPiece piece, double space, out TextPiece head)
    {
        head = null!;
        var text = piece.Text;
        var candidates = new SortedDictionary<int, bool>(); // position -> insert a hyphen
        foreach (int pos in piece.SoftHyphens ?? new List<int>()) candidates[pos] = true;
        for (int i = 1; i < text.Length - 1; i++)
            if (text[i] == '-' && char.IsLetter(text[i - 1])) candidates[i + 1] = false;
        if ((p.Hyphenator ?? DefaultHyphenator) is { } hyphenator)
        {
            // hyphenate the letters only (leading/trailing punctuation stays attached)
            int start = 0, end = text.Length;
            while (start < end && !char.IsLetter(text[start])) start++;
            while (end > start && !char.IsLetter(text[end - 1])) end--;
            if (end - start >= 4 && text[start..end].All(char.IsLetter))
                foreach (int pos in hyphenator.GetBreakPoints(text[start..end])) candidates.TryAdd(start + pos, true);
        }
        foreach (var (pos, hyphen) in candidates.Reverse())
        {
            if (pos < 2 || pos > text.Length - 2) continue;
            string headText = text[..pos] + (hyphen ? "-" : "");
            double width = piece.Font.GetTextWidth(headText, piece.FontSize, p.Kerning);
            if (width > space + 0.001) continue;
            head = new TextPiece { Text = headText, Font = piece.Font, FontSize = piece.FontSize, Color = piece.Color, Run = piece.Run, Width = width };
            piece.Text = text[pos..];
            piece.SoftHyphens = piece.SoftHyphens?.Where(x => x > pos).Select(x => x - pos).ToList();
            return true;
        }
        return false;
    }

    private static double ContentWidth(TextLine line)
    {
        double w = 0;
        int last = line.Pieces.Count - 1;
        while (last >= 0 && line.Pieces[last].IsSpace) last--;
        for (int i = 0; i <= last; i++) w += line.Pieces[i].Width;
        return w;
    }

    private static void Measure(Paragraph p, TextLine line)
    {
        line.Width = ContentWidth(line);
        double ascent = 0, descent = 0, natural = 0;
        if (line.Pieces.Count == 0)
        {
            ascent = p.Font.GetAscent(p.FontSize);
            descent = p.Font.GetDescent(p.FontSize);
            natural = p.Font.GetLineHeight(p.FontSize);
        }
        foreach (var piece in line.Pieces)
        {
            ascent = Math.Max(ascent, piece.Font.GetAscent(piece.FontSize));
            descent = Math.Min(descent, piece.Font.GetDescent(piece.FontSize));
            natural = Math.Max(natural, piece.Font.GetLineHeight(piece.FontSize));
        }
        line.Ascent = ascent;
        line.Descent = descent;
        line.Height = Math.Max(natural, ascent - descent) * p.LineSpacing;
    }

    private static int FitCharacters(string text, PdfFont font, double size, double width)
    {
        int count = 0;
        double w = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            double cw = font.GetCharWidth(rune.Value) * size / 1000;
            if (w + cw > width && count > 0) break;
            w += cw;
            count += rune.Utf16SequenceLength;
        }
        return Math.Max(1, count);
    }

    /// <summary>Splits text into words, single spaces and line breaks.</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is ' ' or '\n' or '\t' or '\r')
            {
                if (i > start) yield return text[start..i];
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                yield return c is '\n' or '\r' ? "\n" : " ";
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text[start..];
    }

    internal static void RenderLine(PdfCanvas canvas, Paragraph p, TextLine line, double x, double top, double totalWidth)
    {
        double available = totalWidth - p.LeftIndent - p.RightIndent - line.Indent;
        double halfLeading = (line.Height - (line.Ascent - line.Descent)) / 2;
        double baseline = top - halfLeading - line.Ascent;

        int last = line.Pieces.Count - 1;
        while (last >= 0 && line.Pieces[last].IsSpace) last--;
        if (last < 0) return;

        double startX = x + p.LeftIndent + line.Indent;
        double extraPerSpace = 0;
        switch (p.Alignment)
        {
            case TextAlignment.Center:
                startX += (available - line.Width) / 2;
                break;
            case TextAlignment.Right:
                startX += available - line.Width;
                break;
            case TextAlignment.Justify when !line.EndsParagraph:
                int spaces = line.Pieces.Take(last + 1).Count(pc => pc.IsSpace);
                if (spaces > 0) extraPerSpace = (available - line.Width) / spaces;
                break;
        }

        // group consecutive pieces with identical formatting
        double cursor = startX;
        int i = 0;
        while (i <= last)
        {
            var first = line.Pieces[i];
            int j = i;
            var text = new System.Text.StringBuilder();
            double width = 0;
            int spaceCount = 0;
            while (j <= last && SameFormat(line.Pieces[j], first))
            {
                text.Append(line.Pieces[j].Text);
                width += line.Pieces[j].Width;
                if (line.Pieces[j].IsSpace) spaceCount++;
                j++;
            }
            canvas.SaveState().SetFillColor(first.Color).BeginText().SetFont(first.Font, first.FontSize)
                .SetTextPosition(cursor, baseline).ShowTextWithWordSpacing(text.ToString(), extraPerSpace, p.Kerning).EndText();
            double segmentWidth = width + spaceCount * extraPerSpace;
            if (first.Run.Underline || first.Run.Strikethrough)
            {
                canvas.SetStrokeColor(first.Color).SetLineWidth(Math.Max(0.5, first.Font.UnderlineThickness * first.FontSize / 1000));
                if (first.Run.Underline)
                {
                    double uy = baseline + first.Font.UnderlinePosition * first.FontSize / 1000;
                    canvas.MoveTo(cursor, uy).LineTo(cursor + segmentWidth, uy).Stroke();
                }
                if (first.Run.Strikethrough)
                {
                    double sy = baseline + first.Font.CapHeight * first.FontSize / 1000 * 0.4;
                    canvas.MoveTo(cursor, sy).LineTo(cursor + segmentWidth, sy).Stroke();
                }
            }
            canvas.RestoreState();
            cursor += segmentWidth;
            i = j;
        }
    }

    private static bool SameFormat(TextPiece a, TextPiece b) =>
        ReferenceEquals(a.Run, b.Run) || (ReferenceEquals(a.Font, b.Font) && a.FontSize == b.FontSize && a.Color == b.Color &&
                                          a.Run.Underline == b.Run.Underline && a.Run.Strikethrough == b.Run.Strikethrough);
}
