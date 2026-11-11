namespace MiPdf.Text;

/// <summary>
/// Reading order of pages set in columns (<see cref="TextExtractionOptions.DetectColumns"/>): the lines are cut at
/// wide gaps into fragments, and the fragments are split recursively at the whitespace between them – at vertical
/// channels into columns (read left to right), otherwise at horizontal gaps into bands (read top to bottom), e.g. a
/// headline above the columns, a header or a footer.
/// <para>
/// A vertical channel separates columns only if at least two of its sides hold running text (lines of several words
/// that fill their side), or one side does and the lines of the sides do not share baselines (a side note). The
/// columns of a table, an invoice's description next to its amounts, bullets and line numbers are therefore not split
/// apart: their rows stay lines, as without column detection.
/// </para>
/// </summary>
internal static class ColumnLayout
{
    private const int MaxDepth = 64;

    private sealed class Fragment
    {
        public required List<(TextGlyph Glyph, bool SpaceBefore)> Items;
        public double X0, X1;       // along the baseline
        public double Base;         // across the baseline (larger = higher on the page)
        public double Size;
        public int Words;

        public double Top => Base + 0.8 * Size;

        public double Bottom => Base - 0.2 * Size;
    }

    /// <summary>Puts the lines of one text direction into reading order; lines may be cut into fragments.</summary>
    public static List<List<(TextGlyph Glyph, bool SpaceBefore)>> Order(List<List<(TextGlyph Glyph, bool SpaceBefore)>> lines)
    {
        var result = new List<List<(TextGlyph, bool)>>();
        // lines are grouped by direction already; each direction is laid out on its own
        foreach (var group in lines.GroupBy(l => l[0].Glyph.Orientation))
        {
            var fragments = group.SelectMany(Cut).ToList();
            foreach (var leaf in Segment(fragments, 0))
                result.AddRange(Lines(leaf));
        }
        return result;
    }

    /// <summary>Cuts a line where the gap between two glyphs is wider than about one em.</summary>
    private static IEnumerable<Fragment> Cut(List<(TextGlyph Glyph, bool SpaceBefore)> line)
    {
        var current = new List<(TextGlyph, bool)>();
        TextGlyph? previous = null;
        foreach (var (g, space) in line)
        {
            if (previous != null && g.ParallelStart - previous.ParallelEnd > Math.Max(g.FontSize, previous.FontSize))
            {
                yield return Make(current);
                current = new List<(TextGlyph, bool)>();
                current.Add((g, false));
            }
            else current.Add((g, current.Count > 0 && space));
            previous = g;
        }
        if (current.Count > 0) yield return Make(current);
    }

    private static Fragment Make(List<(TextGlyph Glyph, bool SpaceBefore)> items)
    {
        int words = 0;
        bool inWord = false;
        foreach (var (g, space) in items)
        {
            if (space) inWord = false;
            foreach (char c in g.Text)
            {
                if (char.IsWhiteSpace(c)) inWord = false;
                else if (!inWord)
                {
                    inWord = true;
                    words++;
                }
            }
        }
        return new Fragment
        {
            Items = items,
            X0 = items.Min(e => Math.Min(e.Glyph.ParallelStart, e.Glyph.ParallelEnd)),
            X1 = items.Max(e => Math.Max(e.Glyph.ParallelStart, e.Glyph.ParallelEnd)),
            Base = items[0].Glyph.Perpendicular,
            Size = items.Max(e => e.Glyph.FontSize),
            Words = words,
        };
    }

    /// <summary>
    /// Splits a region at its whitespace; returns the leaf regions in reading order. Column channels may be crossed by
    /// a few fragments that span the columns (a running header, a title, a caption across the page, a footer): they
    /// divide the region into sections, and each section is read column by column.
    /// </summary>
    private static List<List<Fragment>> Segment(List<Fragment> region, int depth)
    {
        if (region.Count <= 1 || depth > MaxDepth) return new List<List<Fragment>> { region };
        if (ColumnChannels(region) is { } channels) return ReadColumns(region, channels, depth);

        // no columns across the whole region (e.g. a block above them that spans them): split it into bands
        var bands = HorizontalParts(region, 0.5 * Median(region.Select(f => f.Size)));
        return bands.Count > 1 ? Bands(bands, depth) : new List<List<Fragment>> { region };
    }

    /// <summary>
    /// Reads bands top to bottom, joining a band set in columns with the following bands that fit into its columns, and
    /// a small band before the columns that fits into them (a heading over one column).
    /// </summary>
    private static List<List<Fragment>> Bands(List<List<Fragment>> bands, int depth)
    {
        var result = new List<List<Fragment>>();
        int i = 0;
        while (i < bands.Count)
        {
            var section = new List<Fragment>(bands[i]);
            var sectionChannels = ColumnChannels(section);
            int j = i + 1;
            for (; j < bands.Count; j++)
            {
                var next = bands[j];
                if (sectionChannels != null && !next.Any(f => Crosses(f, sectionChannels)))
                    section.AddRange(next);
                else if (sectionChannels == null && ColumnChannels(next) is { } nextChannels
                         && !section.Any(f => Crosses(f, nextChannels)) && section.Count < next.Count)
                {
                    // a small band before the columns that fits into them (a heading over one column)
                    section.AddRange(next);
                    sectionChannels = nextChannels;
                }
                else break;
            }
            result.AddRange(sectionChannels != null && j > i + 1 ? ReadColumns(section, sectionChannels, depth) : Segment(section, depth + 1));
            i = j;
        }
        return result;
    }

    /// <summary>A channel between columns: its core and the free strip around it (from the end of the lines on its left).</summary>
    private readonly record struct Channel(double Left, double Right, double StripLeft, double StripRight);

    /// <summary>
    /// True for a fragment spanning a channel: over its core, or starting in the free strip and running into the next
    /// column (e.g. a centered author line). The ragged ends of lines in the strip do not count.
    /// </summary>
    private static bool Crosses(Fragment f, List<Channel> channels) => channels.Any(c =>
        (f.X0 < c.Right && f.X1 > c.Left) || (f.X0 > c.StripLeft + 1 && f.X0 < c.StripRight - 1 && f.X1 > c.StripRight + 1));

    /// <summary>
    /// The channels between the columns of a region, or null if it is not set in columns. Spanning fragments are few
    /// (header, title, captions, footer); a strip crossed by many lines lies inside a column.
    /// </summary>
    private static List<Channel>? ColumnChannels(List<Fragment> region)
    {
        if (region.Count < 2) return null;
        double size = Median(region.Select(f => f.Size));
        var channels = Channels(region, Math.Max(4, 0.9 * size))
            .Where(c => region.Count(f => Crosses(f, new List<Channel> { c })) <= Math.Max(3, region.Count / 20)).ToList();
        if (channels.Count == 0) return null;
        return AreColumns(Parts(region.Where(f => !Crosses(f, channels)).ToList(), channels)) ? channels : null;
    }

    /// <summary>
    /// Reads a region column by column. Fragments spanning the channels divide it into sections, top to bottom; each
    /// section is read column by column.
    /// </summary>
    private static List<List<Fragment>> ReadColumns(List<Fragment> region, List<Channel> channels, int depth)
    {
        var result = new List<List<Fragment>>();
        // a small band at the top or bottom, apart from the columns by a wide gap (a header, a footer, a page number),
        // is read on its own like a spanning fragment, even if it lies within one column
        var apart = new HashSet<Fragment>();
        var wide = HorizontalParts(region, 2 * Median(region.Select(f => f.Size)));
        if (wide.Count > 1)
        {
            if (wide[0].Count <= 3) apart.UnionWith(wide[0]);
            if (wide[^1].Count <= 3) apart.UnionWith(wide[^1]);
        }
        bool Divides(Fragment f) => apart.Contains(f) || Crosses(f, channels);
        var remaining = region.Where(f => !Divides(f)).ToList();
        foreach (var span in region.Where(Divides).OrderByDescending(f => f.Base))
        {
            AddSection(remaining.Where(f => f.Base > span.Base).ToList());
            remaining = remaining.Where(f => f.Base <= span.Base).ToList();
            result.Add(new List<Fragment> { span });
        }
        AddSection(remaining);
        return result;

        void AddSection(List<Fragment> section)
        {
            foreach (var part in Parts(section, channels))
                if (part.Count > 0) result.AddRange(Segment(part, depth + 1));
        }
    }

    /// <summary>
    /// The vertical channels of a region: strips at least <paramref name="minGap"/> wide that are free of text except
    /// for a few fragments spanning across, from a profile of the text height over the x axis (in half points). A strip is
    /// free where the text height is at most a fifth of the typical one; the channel is its core, where only the
    /// spanning fragments are.
    /// </summary>
    private static List<Channel> Channels(List<Fragment> region, double minGap)
    {
        double left = region.Min(f => f.X0), right = region.Max(f => f.X1);
        int bins = (int)Math.Ceiling((right - left) * 2) + 1;
        if (bins < 3 || bins > 100_000) return new List<Channel>();
        var height = new double[bins];
        foreach (var f in region)
        {
            double h = f.Top - f.Bottom;
            int from = (int)Math.Floor((f.X0 - left) * 2), to = Math.Min(bins - 1, (int)Math.Ceiling((f.X1 - left) * 2));
            for (int i = from; i <= to; i++) height[i] += h;
        }
        // free: at most a fifth of the typical text height over a point (a column holds only its share of the text)
        var covered = height.Where(h => h > 0).OrderBy(h => h).ToList();
        double limit = covered.Count == 0 ? 0 : covered[covered.Count / 2] / 5;
        var channels = new List<Channel>();
        int start = -1;
        for (int i = 0; i <= bins; i++)
        {
            bool free = i < bins && height[i] <= limit;
            if (free && start < 0) start = i;
            else if (!free && start >= 0)
            {
                {
                    // the channel is the core of the strip where only the spanning fragments are: around it, the
                    // ragged ends of lines thin out the text without being a gap
                    double min = double.MaxValue;
                    for (int k = start; k < i; k++) min = Math.Min(min, height[k]);
                    int bestStart = -1, bestLength = 0;
                    for (int k = start; k < i;)
                    {
                        if (height[k] > min + 1e-9) { k++; continue; }
                        int run = k;
                        while (run < i && height[run] <= min + 1e-9) run++;
                        if (run - k > bestLength) (bestStart, bestLength) = (k, run - k);
                        k = run;
                    }
                    // inner channels only: text on both sides of the core (however little, e.g. side notes)
                    bool before = false, after = false;
                    for (int k = 0; k < bestStart && !before; k++) before = height[k] > min + 1e-9;
                    for (int k = bestStart + bestLength; k < bins && !after; k++) after = height[k] > min + 1e-9;
                    if (before && after && bestLength / 2.0 >= minGap)
                        channels.Add(new Channel(left + bestStart / 2.0, left + (bestStart + bestLength) / 2.0, left + start / 2.0, left + i / 2.0));
                }
                start = -1;
            }
        }
        return channels;
    }

    /// <summary>The fragments between the channels, left to right (a fragment belongs to the part its middle is in).</summary>
    private static List<List<Fragment>> Parts(List<Fragment> fragments, List<Channel> channels)
    {
        var parts = Enumerable.Range(0, channels.Count + 1).Select(_ => new List<Fragment>()).ToList();
        foreach (var f in fragments)
        {
            double middle = (f.X0 + f.X1) / 2;
            int index = channels.Count(c => middle >= c.Right);
            parts[index].Add(f);
        }
        return parts;
    }

    /// <summary>The bands of a region between horizontal gaps at least <paramref name="minGap"/> high, top to bottom.</summary>
    private static List<List<Fragment>> HorizontalParts(List<Fragment> region, double minGap)
    {
        var sorted = region.OrderByDescending(f => f.Top).ToList();
        var bands = new List<List<Fragment>> { new() { sorted[0] } };
        double bottom = sorted[0].Bottom;
        foreach (var f in sorted.Skip(1))
        {
            if (bottom - f.Top >= minGap) bands.Add(new List<Fragment>());
            bands[^1].Add(f);
            bottom = Math.Min(bottom, f.Bottom);
        }
        return bands;
    }

    /// <summary>True if the parts beside each other are columns of text (see the class remarks).</summary>
    private static bool AreColumns(List<List<Fragment>> parts)
    {
        int text = parts.Count(IsRunningText);
        if (text >= 2) return true;
        if (text == 0) return false;
        // one side of running text: a side note if its neighbours' lines are not on the same baselines
        var main = parts.First(IsRunningText);
        var baselines = main.Select(f => f.Base).ToList();
        var others = parts.Where(p => !ReferenceEquals(p, main)).SelectMany(p => p).ToList();
        int aligned = others.Count(f => baselines.Any(b => Math.Abs(b - f.Base) < 0.3 * f.Size));
        return aligned < 0.5 * others.Count;
    }

    /// <summary>
    /// Running text: at least three lines of three words or more that fill most of the part's width, holding at least
    /// 40 % of its words (labels of a figure next to the text do not count against it; a table column has few such lines).
    /// </summary>
    private static bool IsRunningText(List<Fragment> part)
    {
        if (part.Count < 3) return false;
        double width = part.Max(f => f.X1) - part.Min(f => f.X0);
        if (width <= 0) return false;
        var full = part.Where(f => f.Words >= 3 && f.X1 - f.X0 >= 0.6 * width).ToList();
        return full.Count >= 3 && full.Sum(f => f.Words) >= 0.4 * part.Sum(f => f.Words);
    }

    /// <summary>The lines of a leaf region: fragments on the same baseline joined again, top to bottom.</summary>
    private static IEnumerable<List<(TextGlyph Glyph, bool SpaceBefore)>> Lines(List<Fragment> leaf)
    {
        var rows = new List<List<Fragment>>();
        foreach (var f in leaf.OrderByDescending(f => f.Base).ThenBy(f => f.X0))
        {
            if (rows.Count > 0 && Math.Abs(rows[^1][0].Base - f.Base) <= Math.Max(1, 0.4 * f.Size)) rows[^1].Add(f);
            else rows.Add(new List<Fragment> { f });
        }
        foreach (var row in rows)
        {
            var line = new List<(TextGlyph, bool)>();
            foreach (var f in row.OrderBy(f => f.X0))
                for (int i = 0; i < f.Items.Count; i++)
                    line.Add((f.Items[i].Glyph, i == 0 ? line.Count > 0 : f.Items[i].SpaceBefore));
            yield return line;
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
