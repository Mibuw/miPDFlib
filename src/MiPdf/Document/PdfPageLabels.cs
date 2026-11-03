using System.Text;
using MiPdf.Objects;

namespace MiPdf;

/// <summary>Numbering style of a page label range (/S, ISO 32000-1, 12.4.2).</summary>
public enum PdfPageLabelStyle
{
    /// <summary>No number, only the prefix.</summary>
    None,
    /// <summary>Decimal numbers: 1, 2, 3.</summary>
    Decimal,
    /// <summary>Upper-case Roman numerals: I, II, III.</summary>
    UpperRoman,
    /// <summary>Lower-case Roman numerals: i, ii, iii.</summary>
    LowerRoman,
    /// <summary>Upper-case letters: A to Z, then AA to ZZ, and so on.</summary>
    UpperLetters,
    /// <summary>Lower-case letters: a to z, then aa to zz, and so on.</summary>
    LowerLetters,
}

/// <summary>A page label range: the pages from <see cref="StartPage"/> up to the start of the next range.</summary>
/// <param name="StartPage">First page of the range (1-based).</param>
/// <param name="Style">Numbering style.</param>
/// <param name="Prefix">Text in front of the number; <c>null</c> for none.</param>
/// <param name="StartNumber">Number of the first page of the range (at least 1).</param>
public sealed record PdfPageLabelRange(int StartPage, PdfPageLabelStyle Style, string? Prefix = null, int StartNumber = 1);

/// <summary>
/// The page labels of a document (/PageLabels in the catalog, ISO 32000-1, 12.4.2): logical page numbers such as
/// "i, ii, iii" for a preface followed by "1, 2, 3". Viewers show them instead of the physical page number.
/// </summary>
/// <remarks>
/// Labels belong to page positions: page operations (<see cref="PdfDocument.RemovePage"/>,
/// <see cref="PdfDocument.InsertPage(int, PdfRectangle?)"/>, imports and merges) keep the ranges as they are and do not
/// shift them.
/// </remarks>
public sealed class PdfPageLabels : IReadOnlyList<PdfPageLabelRange>
{
    private static readonly PdfName Key = PdfName.Get("PageLabels");
    private readonly PdfDocument _document;

    internal PdfPageLabels(PdfDocument document) => _document = document;

    /// <summary>Number of ranges. The ranges are read from the number tree (flat /Nums arrays and /Kids) and ordered by
    /// their first page.</summary>
    public int Count => Read().Count;

    /// <summary>The range at <paramref name="index"/> (ordered by first page).</summary>
    public PdfPageLabelRange this[int index] => Read()[index];

    /// <summary>Adds a range that starts at <paramref name="startPage"/> (1-based) and lasts until the next range;
    /// an existing range at the same page is replaced.</summary>
    public void Add(int startPage, PdfPageLabelStyle style, string? prefix = null, int startNumber = 1) =>
        Add(new PdfPageLabelRange(startPage, style, prefix, startNumber));

    /// <summary>Adds <paramref name="range"/>; an existing range at the same page is replaced.</summary>
    public void Add(PdfPageLabelRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentOutOfRangeException.ThrowIfLessThan(range.StartPage, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(range.StartNumber, 1);
        var ranges = Read().Where(r => r.StartPage != range.StartPage).Append(range).ToList();
        Write(ranges);
    }

    /// <summary>Removes the range that starts at <paramref name="startPage"/>. Returns false if there is none.</summary>
    public bool Remove(int startPage)
    {
        var ranges = Read().ToList();
        if (ranges.RemoveAll(r => r.StartPage == startPage) == 0) return false;
        Write(ranges);
        return true;
    }

    /// <summary>Removes all page labels.</summary>
    public void Clear() => _document.Catalog.Remove(Key);

    /// <summary>
    /// The label of page <paramref name="pageNumber"/> (1-based). Pages before the first range, and all pages of a
    /// document without page labels, get their decimal page number.
    /// </summary>
    public string GetLabel(int pageNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        var range = Read().LastOrDefault(r => r.StartPage <= pageNumber);
        if (range == null) return pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        long number = (long)range.StartNumber + pageNumber - range.StartPage;
        return (range.Prefix ?? "") + Format(number, range.Style);
    }

    /// <inheritdoc/>
    public IEnumerator<PdfPageLabelRange> GetEnumerator() => Read().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private List<PdfPageLabelRange> Read()
    {
        var result = new Dictionary<int, PdfPageLabelRange>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        void Walk(PdfDictionary node, int depth)
        {
            if (depth > 32 || !visited.Add(node)) return;
            if (node.GetArray(PdfName.Get("Nums")) is { } nums)
                for (int i = 0; i + 1 < nums.Count; i += 2)
                    if (nums[i].Resolve() is PdfNumber { IntValue: >= 0 and < int.MaxValue } index && nums[i + 1].Resolve() is PdfDictionary label)
                        result[index.IntValue + 1] = new PdfPageLabelRange(index.IntValue + 1, ParseStyle(label.GetName(PdfName.S)),
                            label.GetText(PdfName.Get("P")), Math.Max(1, label.GetInt(PdfName.Get("St"), 1)));
            if (node.GetArray(PdfName.Kids) is { } kids)
                foreach (var kid in kids)
                    if (kid.Resolve() is PdfDictionary k) Walk(k, depth + 1);
        }
        if (_document.Catalog.GetDictionary(Key) is { } root) Walk(root, 0);
        return result.Values.OrderBy(r => r.StartPage).ToList();
    }

    private void Write(List<PdfPageLabelRange> ranges)
    {
        if (ranges.Count == 0)
        {
            Clear();
            return;
        }
        var nums = new PdfArray();
        foreach (var range in ranges.OrderBy(r => r.StartPage))
        {
            var label = new PdfDictionary();
            if (StyleName(range.Style) is { } style) label[PdfName.S] = PdfName.Get(style);
            if (!string.IsNullOrEmpty(range.Prefix)) label[PdfName.Get("P")] = new PdfString(range.Prefix);
            if (range.StartNumber != 1) label.Set(PdfName.Get("St"), range.StartNumber);
            nums.Add(new PdfNumber(range.StartPage - 1));
            nums.Add(label);
        }
        _document.Catalog[Key] = new PdfDictionary { [PdfName.Get("Nums")] = nums };
    }

    private static PdfPageLabelStyle ParseStyle(PdfName? name) => name?.Value switch
    {
        "D" => PdfPageLabelStyle.Decimal,
        "R" => PdfPageLabelStyle.UpperRoman,
        "r" => PdfPageLabelStyle.LowerRoman,
        "A" => PdfPageLabelStyle.UpperLetters,
        "a" => PdfPageLabelStyle.LowerLetters,
        _ => PdfPageLabelStyle.None,
    };

    private static string? StyleName(PdfPageLabelStyle style) => style switch
    {
        PdfPageLabelStyle.Decimal => "D",
        PdfPageLabelStyle.UpperRoman => "R",
        PdfPageLabelStyle.LowerRoman => "r",
        PdfPageLabelStyle.UpperLetters => "A",
        PdfPageLabelStyle.LowerLetters => "a",
        _ => null,
    };

    private static string Format(long number, PdfPageLabelStyle style) => style switch
    {
        PdfPageLabelStyle.Decimal => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PdfPageLabelStyle.UpperRoman => Roman(number),
        PdfPageLabelStyle.LowerRoman => Roman(number).ToLowerInvariant(),
        PdfPageLabelStyle.UpperLetters => Letters(number),
        PdfPageLabelStyle.LowerLetters => Letters(number).ToLowerInvariant(),
        _ => "",
    };

    private static string Roman(long number)
    {
        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var sb = new StringBuilder();
        // more than 100 thousands is not a useful page label; the number is shown in decimal then
        if (number > 100_000) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < values.Length; i++)
            for (; number >= values[i]; number -= values[i]) sb.Append(symbols[i]);
        return sb.ToString();
    }

    // A..Z, then AA..ZZ, AAA..ZZZ (the letter repeated, ISO 32000-1, Table 159)
    private static string Letters(long number)
    {
        long repeat = (number - 1) / 26 + 1;
        if (repeat > 1000) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new string((char)('A' + (number - 1) % 26), (int)repeat);
    }
}

public sealed partial class PdfDocument
{
    private PdfPageLabels? _pageLabels;

    /// <summary>The page labels (logical page numbers shown by viewers).</summary>
    public PdfPageLabels PageLabels => _pageLabels ??= new PdfPageLabels(this);

    /// <summary>The label of page <paramref name="pageNumber"/> (1-based), see <see cref="PdfPageLabels.GetLabel"/>.</summary>
    public string GetPageLabel(int pageNumber) => PageLabels.GetLabel(pageNumber);
}
