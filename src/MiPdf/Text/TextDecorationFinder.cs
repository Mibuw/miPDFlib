using MiPdf.Content;
using MiPdf.Objects;

namespace MiPdf.Text;

/// <summary>
/// Finds the decorations and links of words (<see cref="TextWord.Decorations"/>, <see cref="TextWord.Link"/>): lines
/// drawn under or through them, text markup annotations (Underline, Squiggly, StrikeOut, Highlight) and link
/// annotations with a URI over them.
/// </summary>
internal sealed class TextDecorationFinder
{
    private readonly List<PdfRule> _rules;
    private readonly List<(PdfRectangle Area, TextDecorations Kind)> _markups = new();
    private readonly List<(PdfRectangle Area, string Uri)> _links = new();

    public TextDecorationFinder(PdfPage page, IReadOnlyList<PdfRule> rules)
    {
        // drawn lines thin enough to be text decorations (edges of filled areas are backgrounds)
        _rules = rules.Where(r => r.IsHorizontal && !r.IsEdgeOfFill).ToList();
        foreach (var annotation in page.Annotations)
        {
            if ((annotation.Flags & AnnotationFlags.Hidden) != 0) continue;
            var kind = annotation.Subtype switch
            {
                "Underline" or "Squiggly" => TextDecorations.Underline,
                "StrikeOut" => TextDecorations.StrikeOut,
                "Highlight" => TextDecorations.Highlight,
                _ => TextDecorations.None,
            };
            if (kind != TextDecorations.None)
                foreach (var area in Areas(annotation.Dictionary)) _markups.Add((area, kind));
            else if (annotation.Subtype == "Link" && annotation.Uri is { Length: > 0 } uri)
                foreach (var area in Areas(annotation.Dictionary)) _links.Add((area, uri));
        }
    }

    public bool IsEmpty => _rules.Count == 0 && _markups.Count == 0 && _links.Count == 0;

    /// <summary>The areas of an annotation: its quadrilaterals (/QuadPoints), or its rectangle.</summary>
    private static IEnumerable<PdfRectangle> Areas(PdfDictionary annotation)
    {
        if (annotation.GetArray(PdfName.Get("QuadPoints")) is { Count: >= 8 } quads)
        {
            var values = quads.Select(q => q.Resolve() is PdfNumber n ? n.Value : double.NaN).ToList();
            for (int i = 0; i + 8 <= values.Count; i += 8)
            {
                var xs = new[] { values[i], values[i + 2], values[i + 4], values[i + 6] };
                var ys = new[] { values[i + 1], values[i + 3], values[i + 5], values[i + 7] };
                if (xs.Concat(ys).All(double.IsFinite)) yield return new PdfRectangle(xs.Min(), ys.Min(), xs.Max(), ys.Max());
            }
            yield break;
        }
        if (annotation.GetRectangle(PdfName.Get("Rect")) is { } rect) yield return rect;
    }

    /// <summary>
    /// The lines of a page with the decorations and links of their words (a line without any is returned as it is).
    /// Drawn lines count only for horizontal text (<c>Horizontal</c>).
    /// </summary>
    public List<TextLineInfo> Decorate(IReadOnlyList<(TextLineInfo Line, bool Horizontal)> lines)
    {
        var drawn = new TextDecorations[lines.Count][];
        for (int i = 0; i < lines.Count; i++)
            drawn[i] = lines[i].Horizontal && _rules.Count > 0 ? Drawn(lines, i) : new TextDecorations[lines[i].Line.Words.Count];
        var result = new List<TextLineInfo>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Line;
            List<TextWord>? words = null;
            for (int w = 0; w < line.Words.Count; w++)
            {
                var word = line.Words[w];
                var decorations = Markups(word) | drawn[i][w];
                string? link = LinkOf(word);
                if (decorations == TextDecorations.None && link == null) continue;
                words ??= line.Words.ToList();
                words[w] = word with { Decorations = decorations, Link = link };
            }
            result.Add(words == null ? line : line with { Words = words });
        }
        return result;
    }

    private static (double X, double Y) Middle(TextWord word) => ((word.Bounds.Left + word.Bounds.Right) / 2, (word.Bounds.Bottom + word.Bounds.Top) / 2);

    private static bool Contains(PdfRectangle area, (double X, double Y) p, double tolerance) =>
        p.X >= area.Left - tolerance && p.X <= area.Right + tolerance && p.Y >= area.Bottom - tolerance && p.Y <= area.Top + tolerance;

    private TextDecorations Markups(TextWord word)
    {
        var result = TextDecorations.None;
        var middle = Middle(word);
        foreach (var (area, kind) in _markups)
            if (Contains(area, middle, 0.1 * word.FontSize)) result |= kind;
        return result;
    }

    private string? LinkOf(TextWord word)
    {
        var middle = Middle(word);
        foreach (var (area, uri) in _links)
            if (Contains(area, middle, 0.1 * word.FontSize)) return uri;
        return null;
    }

    /// <summary>
    /// The lines drawn under or through the words of a line: a rule at most 0.15 em thick, from 0.3 em below the baseline
    /// to the baseline (underline) or at the height of the lower-case letters (strike-out), whose length the words it
    /// crosses cover almost entirely – a table rule, the edge of a form box or a line of a chart is no decoration. A
    /// rule with other text right above or below it (a fraction bar) does not strike out.
    /// </summary>
    private TextDecorations[] Drawn(IReadOnlyList<(TextLineInfo Line, bool Horizontal)> lines, int index)
    {
        var line = lines[index].Line;
        var result = new TextDecorations[line.Words.Count];
        double size = line.Words.Count > 0 ? line.Words.Max(w => w.FontSize) : 0;
        if (size <= 0) return result;
        foreach (var rule in _rules)
        {
            double y = rule.Start.Y;
            if (y < line.Bounds.Bottom - 0.2 * size || y > line.Bounds.Top) continue;
            // the words the rule runs along, each for most of its width
            int first = -1, last = -1;
            var kinds = new List<(int Word, TextDecorations Kind)>();
            for (int w = 0; w < line.Words.Count; w++)
            {
                var word = line.Words[w];
                double wordSize = word.FontSize;
                if (wordSize <= 0 || rule.Thickness > 0.15 * wordSize) continue;
                double overlap = Math.Min(rule.End.X, word.Bounds.Right) - Math.Max(rule.Start.X, word.Bounds.Left);
                if (overlap < 0.6 * word.Bounds.Width) continue;
                // the word box reaches 0.2 em below the baseline
                double d = (y - (word.Bounds.Bottom + 0.2 * wordSize)) / wordSize;
                var kind = d is >= -0.3 and <= 0.02 ? TextDecorations.Underline : d is >= 0.12 and <= 0.5 ? TextDecorations.StrikeOut : TextDecorations.None;
                if (kind == TextDecorations.None) continue;
                kinds.Add((w, kind));
                if (first < 0) first = w;
                last = w;
            }
            if (kinds.Count == 0) continue;
            double covered = line.Words[last].Bounds.Right - line.Words[first].Bounds.Left;
            if (covered < 0.85 * (rule.End.X - rule.Start.X)) continue;
            bool fraction = kinds.Any(k => k.Kind == TextDecorations.StrikeOut) && TextNear(lines, index, rule, 0.3 * size);
            foreach (var (w, kind) in kinds)
                if (!(kind == TextDecorations.StrikeOut && fraction)) result[w] |= kind;
        }
        return result;
    }

    /// <summary>True if a word of another line comes within <paramref name="distance"/> above or below the rule.</summary>
    private static bool TextNear(IReadOnlyList<(TextLineInfo Line, bool Horizontal)> lines, int index, PdfRule rule, double distance)
    {
        double y = rule.Start.Y;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i == index) continue;
            var bounds = lines[i].Line.Bounds;
            if (bounds.Bottom > y + distance || bounds.Top < y - distance || bounds.Right < rule.Start.X || bounds.Left > rule.End.X) continue;
            foreach (var word in lines[i].Line.Words)
                if (word.Bounds.Right > rule.Start.X && word.Bounds.Left < rule.End.X && word.Bounds.Bottom < y + distance && word.Bounds.Top > y - distance)
                    return true;
        }
        return false;
    }
}
