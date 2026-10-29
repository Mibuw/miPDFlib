using System.Text;

namespace MiPdf.Layout;

/// <summary>Finds the positions where a word may be hyphenated.</summary>
public interface IHyphenator
{
    /// <summary>Returns the indexes <c>i</c> (1 ≤ i &lt; word.Length) where a hyphen may be inserted before <c>word[i]</c>.</summary>
    IReadOnlyList<int> GetBreakPoints(string word);
}

/// <summary>
/// Liang's hyphenation algorithm (as used by TeX) with pattern files from the hyph-utf8 project, e.g.
/// <c>hyph-de-1996.tex</c> (German), <c>hyph-de-ch-1901.tex</c> (Swiss German) or <c>hyph-en-us.tex</c>.
/// Load patterns with <see cref="FromTex(string, int, int)"/>; the pattern files are not bundled.
/// </summary>
/// <example>
/// <code>
/// var german = PatternHyphenator.FromTex(File.ReadAllText("hyph-de-1996.tex"));
/// TextLayout.DefaultHyphenator = german;           // or per paragraph: paragraph.Hyphenator = german
/// </code>
/// </example>
public sealed class PatternHyphenator : IHyphenator
{
    private readonly Dictionary<string, byte[]> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _exceptions = new(StringComparer.Ordinal);
    private readonly int _maxPatternLength;

    /// <param name="patterns">Patterns such as "hy3ph" or ".ab1c" (digits mark break priorities).</param>
    /// <param name="exceptions">Exception words with explicit hyphens, e.g. "ta-ble".</param>
    /// <param name="leftMin">Minimum number of characters before a break.</param>
    /// <param name="rightMin">Minimum number of characters after a break.</param>
    public PatternHyphenator(IEnumerable<string> patterns, IEnumerable<string>? exceptions = null, int leftMin = 2, int rightMin = 2)
    {
        LeftMin = leftMin;
        RightMin = rightMin;
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0) continue;
            var letters = new StringBuilder();
            var values = new List<byte> { 0 };
            foreach (char c in pattern)
            {
                if (char.IsAsciiDigit(c)) values[^1] = (byte)(c - '0');
                else
                {
                    letters.Append(char.ToLowerInvariant(c));
                    values.Add(0);
                }
            }
            _patterns[letters.ToString()] = values.ToArray();
            _maxPatternLength = Math.Max(_maxPatternLength, letters.Length);
        }
        foreach (var exception in exceptions ?? Array.Empty<string>())
        {
            var word = exception.Trim();
            if (word.Length == 0) continue;
            var points = new List<int>();
            int index = 0;
            foreach (char c in word)
            {
                if (c == '-') points.Add(index);
                else index++;
            }
            _exceptions[word.Replace("-", "").ToLowerInvariant()] = points.ToArray();
        }
    }

    public int LeftMin { get; }

    public int RightMin { get; }

    /// <summary>Loads a TeX pattern file (<c>\patterns{...}</c> and optional <c>\hyphenation{...}</c>).</summary>
    public static PatternHyphenator FromTex(string tex, int leftMin = 2, int rightMin = 2)
    {
        var withoutComments = string.Join('\n', tex.Split('\n').Select(line =>
        {
            int comment = line.IndexOf('%');
            return comment >= 0 ? line[..comment] : line;
        }));
        return new PatternHyphenator(Block(withoutComments, "\\patterns"), Block(withoutComments, "\\hyphenation"), leftMin, rightMin);
    }

    private static IEnumerable<string> Block(string text, string command)
    {
        int start = text.IndexOf(command + "{", StringComparison.Ordinal);
        if (start < 0) return Array.Empty<string>();
        start += command.Length + 1;
        int end = text.IndexOf('}', start);
        if (end < 0) end = text.Length;
        return text[start..end].Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }

    public IReadOnlyList<int> GetBreakPoints(string word)
    {
        if (word.Length < LeftMin + RightMin) return Array.Empty<int>();
        var lower = word.ToLowerInvariant();
        if (_exceptions.TryGetValue(lower, out var fixedPoints)) return fixedPoints;
        string w = "." + lower + ".";
        var points = new byte[w.Length + 1];
        for (int i = 0; i < w.Length; i++)
        {
            for (int length = 1; length <= _maxPatternLength && i + length <= w.Length; length++)
            {
                if (!_patterns.TryGetValue(w.Substring(i, length), out var values)) continue;
                for (int k = 0; k < values.Length; k++)
                    if (values[k] > points[i + k]) points[i + k] = values[k];
            }
        }
        var result = new List<int>();
        for (int i = LeftMin; i <= word.Length - RightMin; i++)
            if (points[i + 1] % 2 == 1) result.Add(i);
        return result;
    }
}
