using MiPdf.Content;
using MiPdf.Graphics;
using MiPdf.Objects;
using System.Text.RegularExpressions;

namespace MiPdf.Text;

/// <summary>What a <see cref="LayoutBlock"/> is.</summary>
public enum LayoutBlockKind
{
    /// <summary>Running text.</summary>
    Paragraph,

    /// <summary>A heading; its <see cref="LayoutBlock.Level"/> ranks it (1 is the most prominent).</summary>
    Heading,

    /// <summary>An item of a bulleted or numbered list.</summary>
    ListItem,

    /// <summary>Rows of cells (each line a row).</summary>
    Table,

    /// <summary>The caption of a figure or table ("Figure 2. …", "Tabelle 1: …").</summary>
    Caption,

    /// <summary>A running header at the top of the page.</summary>
    Header,

    /// <summary>A running footer or page number at the bottom of the page.</summary>
    Footer,

    /// <summary>Text outside the flow of the page, such as a stamp running up the margin.</summary>
    Other,
}

/// <summary>A block of text on a page: a paragraph, heading, list item, table, caption, header or footer.</summary>
public sealed class LayoutBlock
{
    internal LayoutBlock(IReadOnlyList<TextLineInfo> lines, double fontSize, bool isBold)
    {
        Lines = lines;
        FontSize = fontSize;
        IsBold = isBold;
        Bounds = PdfTextExtractor.Union(lines.Select(l => l.Bounds));
    }

    /// <summary>What the block is.</summary>
    public LayoutBlockKind Kind { get; internal set; }

    /// <summary>The level of a heading (1 for the largest headings of the page or document), 0 for other blocks.</summary>
    public int Level { get; internal set; }

    /// <summary>The bounds of the block in user space.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>The lines of the block in reading order; the rows of a table.</summary>
    public IReadOnlyList<TextLineInfo> Lines { get; }

    /// <summary>The text of the block, its lines separated by '\n'.</summary>
    public string Text => string.Join('\n', Lines.Select(l => l.Text));

    /// <summary>The size most of the block's characters appear in, in points.</summary>
    public double FontSize { get; }

    /// <summary>True if most of the block's characters are bold.</summary>
    public bool IsBold { get; }

    /// <summary>The cells of a <see cref="LayoutBlockKind.Table"/>; null for other blocks.</summary>
    public LayoutTable? Table { get; internal set; }

    /// <inheritdoc/>
    public override string ToString() => $"{Kind}{(Level > 0 ? " " + Level : "")}: {(Text.Length > 60 ? Text[..60] + "…" : Text)}";
}

/// <summary>The blocks of a page in reading order.</summary>
public sealed class PageLayout
{
    internal PageLayout(int pageNumber, PdfRectangle pageBox, IReadOnlyList<LayoutBlock> blocks, double bodyFontSize)
    {
        PageNumber = pageNumber;
        PageBox = pageBox;
        Blocks = blocks;
        BodyFontSize = bodyFontSize;
    }

    /// <summary>The number of the page (1-based).</summary>
    public int PageNumber { get; }

    /// <summary>The visible area of the page (its crop box) in user space; exports measure from its top left corner.</summary>
    public PdfRectangle PageBox { get; }

    /// <summary>The blocks in reading order (columns one after the other, see <see cref="TextExtractionOptions.DetectColumns"/>).</summary>
    public IReadOnlyList<LayoutBlock> Blocks { get; }

    /// <summary>The font size of the running text (the size most characters have), in points.</summary>
    public double BodyFontSize { get; }
}

/// <summary>
/// Layout analysis: the text of a page as blocks in reading order – paragraphs, headings with their levels, list
/// items, tables, captions, running headers and footers. The blocks are found within the columns and sections of
/// <see cref="TextExtractionOptions.DetectColumns"/>, at gaps between lines, changes of font size or weight, list
/// markers, indented first lines and rows of cells; headings are told by size and weight against the running text.
/// Analysing a whole document also finds headers and footers repeated on its pages and ranks headings across it.
/// </summary>
public static class PdfLayoutAnalyzer
{
    /// <summary>Analyses one page.</summary>
    public static PageLayout Analyze(PdfPage page, TextExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        var (lines, grids) = Collect(page, options);
        double body = BodySize(lines);
        var blocks = Blocks(lines, grids, body);
        var layout = new PageLayout(page.Document.Pages.ToList().IndexOf(page) + 1, page.CropBox, blocks.Select(b => b.Block).ToList(), body);
        ClassifyMargins(new[] { (layout, page, blocks) }, singlePage: true);
        RankHeadings(new[] { layout });
        return layout;
    }

    /// <summary>
    /// Analyses all pages of a document: headers and footers are also recognised by their repetition on the pages, and
    /// heading levels are ranked across the document.
    /// </summary>
    public static IReadOnlyList<PageLayout> Analyze(PdfDocument document, TextExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var pages = document.Pages.ToList();
        var collected = pages.Select(p => Collect(p, options)).ToList();
        double body = BodySize(collected.SelectMany(c => c.Lines).ToList());
        var analysed = new List<(PageLayout Layout, PdfPage Page, List<(LayoutBlock Block, List<Line> Lines)> Blocks)>();
        for (int i = 0; i < pages.Count; i++)
        {
            var blocks = Blocks(collected[i].Lines, collected[i].Grids, body);
            analysed.Add((new PageLayout(i + 1, pages[i].CropBox, blocks.Select(b => b.Block).ToList(), body), pages[i], blocks));
        }
        ClassifyMargins(analysed, singlePage: pages.Count == 1);
        RankHeadings(analysed.Select(a => a.Layout).ToList());
        return analysed.Select(a => a.Layout).ToList();
    }

    // ------------------------------------------------------------------ lines

    /// <summary>A line with what the analysis needs: size, weight, extent, cells, region.</summary>
    private sealed class Line
    {
        public required TextLineInfo Info;
        public required int Region;
        public double Size, Base, X0, X1;
        public bool Bold;
        public int Orientation;     // direction of the baseline in degrees
        public bool Rotated;        // running in another direction than most of the page's text (e.g. a stamp in the margin)
        public int Cells;           // pieces separated by gaps of 1.5 em or more
        public int Characters;

        public double Top => Info.Bounds.Top;

        public double Bottom => Info.Bounds.Bottom;

        public string Text => Info.Text;
    }

    private static (List<Line> Lines, List<TableDetector.Grid> Grids) Collect(PdfPage page, TextExtractionOptions? options)
    {
        options ??= new TextExtractionOptions();
        using var diagnostics = DiagnosticContext.Enter(page);
        try
        {
            var glyphs = new GlyphCollector(options).Collect(page);
            var raw = LineBuilder.BuildGlyphLines(glyphs, sortByPosition: true);
            var ordered = options.DetectColumns ? ColumnLayout.OrderWithRegions(raw) : raw.Select(l => (Line: l, Region: 0)).ToList();
            // the grids of rules; a line running through several grids, or into one and out, is split at their edges
            var rules = new RuleCollector(page.CropBox);
            rules.Process(page.GetContentBytes(), page.GetInheritable(PdfName.Resources) as PdfDictionary, Matrix.Identity, 0);
            var grids = TableDetector.FindGrids(RuleCollector.Join(rules.Rules));
            if (grids.Count > 0)
            {
                // only grids holding a table (not the fields of a form or the frames of a page layout)
                var infos = ordered.Select(l => LineBuilder.ToLineInfo(l.Line)).OfType<TextLineInfo>().ToList();
                grids = grids.Where(g => TableDetector.IsTable(TableDetector.FromGrid(g, infos))).ToList();
            }
            if (grids.Count > 0) ordered = ordered.SelectMany(l => SplitAtGrids(l.Line, grids).Select(piece => (piece, l.Region))).ToList();
            var result = new List<Line>();
            foreach (var (glyphLine, region) in ordered)
            {
                if (LineBuilder.ToLineInfo(glyphLine) is not { } info) continue;
                var visible = glyphLine.Where(e => !string.IsNullOrWhiteSpace(e.Glyph.Text)).Select(e => e.Glyph).ToList();
                if (visible.Count == 0) continue;
                int chars = visible.Sum(g => g.Text.Length);
                int cells = 1;
                for (int i = 1; i < visible.Count; i++)
                    if (visible[i].ParallelStart - visible[i - 1].ParallelEnd >= 1.5 * Math.Max(visible[i].FontSize, visible[i - 1].FontSize)) cells++;
                result.Add(new Line
                {
                    Info = info,
                    Region = region,
                    // the size most characters have (superscripts and footnote marks do not count)
                    Size = visible.GroupBy(g => Math.Round(g.FontSize * 2) / 2).MaxBy(g => g.Sum(x => x.Text.Length))!.Key,
                    Bold = visible.Where(g => g.Bold).Sum(g => g.Text.Length) >= 0.6 * chars,
                    Orientation = visible[0].Orientation,
                    Base = visible[0].Perpendicular,
                    X0 = info.Bounds.Left,
                    X1 = info.Bounds.Right,
                    Cells = cells,
                    Characters = chars,
                });
            }
            // the direction most characters run in; lines in another direction are outside the flow
            if (result.Count > 0)
            {
                int main = result.GroupBy(l => l.Orientation).MaxBy(g => g.Sum(l => l.Characters))!.Key;
                foreach (var line in result) line.Rotated = line.Orientation != main;
            }
            return (result, grids);
        }
        catch (Exception ex) when (PdfException.IsUnexpectedInputError(ex))
        {
            throw new PdfException("Layout analysis failed (damaged content): " + ex.Message, ex);
        }
    }

    /// <summary>A line cut where its glyphs pass from one grid into another or out of a grid (by the middle of each glyph).</summary>
    private static IEnumerable<List<(TextGlyph Glyph, bool SpaceBefore)>> SplitAtGrids(List<(TextGlyph Glyph, bool SpaceBefore)> line, List<TableDetector.Grid> grids)
    {
        int GridOf(TextGlyph g)
        {
            double x = (g.Bounds.Left + g.Bounds.Right) / 2, y = (g.Bounds.Bottom + g.Bounds.Top) / 2;
            return grids.FindIndex(grid => grid.Contains(x, y));
        }
        var piece = new List<(TextGlyph Glyph, bool SpaceBefore)>();
        int current = int.MinValue;
        foreach (var item in line)
        {
            // spaces go with the piece they follow
            int grid = string.IsNullOrWhiteSpace(item.Glyph.Text) && current != int.MinValue ? current : GridOf(item.Glyph);
            if (piece.Count > 0 && grid != current)
            {
                yield return piece;
                piece = new List<(TextGlyph Glyph, bool SpaceBefore)>();
            }
            piece.Add(piece.Count == 0 ? (item.Glyph, false) : item);
            current = grid;
        }
        if (piece.Count > 0) yield return piece;
    }

    /// <summary>The size of the running text: the size most characters have.</summary>
    private static double BodySize(List<Line> lines) =>
        lines.Count == 0 ? 0 : lines.GroupBy(l => l.Size).MaxBy(g => g.Sum(l => l.Characters))!.Key;

    // ------------------------------------------------------------------ blocks

    private static readonly Regex ListMarker = new(
        @"^(?:[•·▪◦‣∙●○■□–—\-\*](?:\s|$)|\(?(?:\d{1,3}|[a-z]|[ivxlc]{1,5})[.)](?:\s|$)|\d{1,3}(?:\.\d{1,3})+\s)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex Bullet = new(@"^[•·▪◦‣∙●○■□–—\-\*](?:\s|$)", RegexOptions.CultureInvariant);

    private static readonly Regex NumberedTitle = new(@"^(?:[A-Z]|\d{1,2}(?:\.\d{1,2})*)\.?\s+\p{Lu}", RegexOptions.CultureInvariant);

    private static readonly Regex CaptionStart = new(
        @"^(?:Fig\.?|Figure|Table|Tab\.|Abbildung|Abb\.|Tabelle|Listing|Algorithm)\s*[A-Z]?\d+[.:\s]",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits the lines (in reading order) into blocks and gives each a first kind (headers, footers and heading levels
    /// follow). A block does not go from one column to another; within a column it may span the sections the column
    /// detection split it into (e.g. the rows of a table).
    /// </summary>
    private static List<(LayoutBlock Block, List<Line> Lines)> Blocks(List<Line> lines, List<TableDetector.Grid> grids, double body)
    {
        var result = new List<(LayoutBlock, List<Line>)>();
        // the lines in each grid of rules that holds a table
        var inGrid = new Dictionary<Line, TableDetector.Grid>();
        foreach (var grid in grids)
            foreach (var line in lines.Where(l => !l.Rotated && grid.Contains((l.X0 + l.X1) / 2, (l.Top + l.Bottom) / 2)))
                inGrid.TryAdd(line, grid);
        var tablesDone = new HashSet<TableDetector.Grid>();
        // the usual distance between the baselines of consecutive lines of one size, one under the other
        var leading = lines.Zip(lines.Skip(1))
            .Where(p => Math.Abs(p.First.Size - p.Second.Size) < 0.5 && Below(p.First, p.Second))
            .GroupBy(p => p.First.Size)
            .ToDictionary(g => g.Key, g => { var d = g.Select(p => p.First.Base - p.Second.Base).OrderBy(x => x).ToList(); return d[d.Count / 2]; });
        // the left and right edges of each region, for indented first lines and full lines
        var left = lines.GroupBy(l => l.Region).ToDictionary(g => g.Key, g => g.Min(l => l.X0));
        var right = lines.GroupBy(l => l.Region).ToDictionary(g => g.Key, g => g.Max(l => l.X1));

        var current = new List<Line>();
        foreach (var line in lines)
        {
            if (inGrid.TryGetValue(line, out var grid))
            {
                // the table of a grid: one block where its first line comes in the reading order
                if (!tablesDone.Add(grid)) continue;
                Flush();
                var rows = lines.Where(l => inGrid.TryGetValue(l, out var g) && ReferenceEquals(g, grid)).OrderByDescending(l => l.Top).ThenBy(l => l.X0).ToList();
                var block = MakeBlock(rows, body);
                block.Kind = LayoutBlockKind.Table;
                block.Table = TableDetector.FromGrid(grid, rows.Select(l => l.Info));
                result.Add((block, rows));
                continue;
            }
            if (current.Count > 0 && (line.Region != current[^1].Region && !Below(current[^1], line)
                                      || StartsBlock(current, line, left[line.Region], right[current[^1].Region], leading.GetValueOrDefault(line.Size))))
                Flush();
            current.Add(line);
        }
        Flush();
        return result;

        void Flush()
        {
            if (current.Count == 0) return;
            result.Add((MakeBlock(current, body), current));
            current = new List<Line>();
        }
    }

    /// <summary>True if <paramref name="line"/> stands under <paramref name="previous"/> in the same column (overlapping it, not far below).</summary>
    private static bool Below(Line previous, Line line)
    {
        double distance = previous.Base - line.Base;
        double overlap = Math.Min(previous.X1, line.X1) - Math.Max(previous.X0, line.X0);
        return distance > 0 && distance <= 3 * Math.Max(previous.Size, line.Size) && overlap > 0.3 * Math.Min(previous.X1 - previous.X0, line.X1 - line.X0);
    }

    /// <summary>True if <paramref name="line"/> begins a new block after the lines of <paramref name="block"/>.</summary>
    private static bool StartsBlock(List<Line> block, Line line, double left, double right, double leading)
    {
        var previous = block[^1];
        double size = Math.Max(line.Size, previous.Size);
        double distance = previous.Base - line.Base;
        if (distance <= 0) return true;  // going back up, e.g. after a side note
        // rows of cells and running text do not share a block; table rows may stand further apart than lines of text,
        // and a bold head row belongs to the table
        bool rowLine = line.Cells >= 3, rowPrevious = previous.Cells >= 3;
        if (rowLine != rowPrevious) return true;
        if (rowLine) return distance > 3 * size || Math.Abs(line.Size - previous.Size) > 0.15 * size;
        // a change of size (a heading and the text after it)
        if (Math.Abs(line.Size - previous.Size) > 0.15 * size) return true;
        // a change of weight, unless a bold beginning of a paragraph runs on (its line is full, the next follows closely)
        if (line.Bold != previous.Bold)
        {
            bool runsOn = previous.Bold && previous.X1 >= right - 2 * size && (leading > 0 ? distance <= 1.35 * leading : distance <= 1.6 * size);
            if (!runsOn) return true;
        }
        // a gap wider than the usual distance of the lines
        if (leading > 0 ? distance > 1.35 * leading : distance > 1.6 * size) return true;
        // a list item begins with its marker; its further lines are indented
        if (ListMarker.IsMatch(line.Text)) return true;
        bool inList = ListMarker.IsMatch(block[0].Text);
        // an indented first line begins a paragraph (outside lists, where the further lines are indented)
        // (a centered line is shorter on the right as well)
        if (!inList && line.X0 - left > 0.8 * line.Size && previous.X0 - left < 0.3 * line.Size && line.X1 >= right - 1.5 * line.Size) return true;
        // a list ends where a line returns to the marker's indent without a marker
        if (inList && block.Count > 0 && line.X0 <= block[0].X0 + 0.3 * line.Size) return true;
        return false;
    }

    private static LayoutBlock MakeBlock(List<Line> lines, double body)
    {
        double size = lines.GroupBy(l => l.Size).MaxBy(g => g.Sum(l => l.Characters))!.Key;
        bool bold = lines.Where(l => l.Bold).Sum(l => l.Characters) >= 0.6 * lines.Sum(l => l.Characters);
        var block = new LayoutBlock(lines.Select(l => l.Info).ToList(), size, bold);
        string first = lines[0].Text;
        int chars = lines.Sum(l => l.Characters);
        if (lines.All(l => l.Rotated)) block.Kind = LayoutBlockKind.Other;
        else if (lines.Count >= 2 && lines.All(l => l.Cells >= 3))
        {
            block.Kind = LayoutBlockKind.Table;
            block.Table = TableDetector.FromRows(lines.Select(l => l.Info).ToList());
        }
        else if (CaptionStart.IsMatch(first)) block.Kind = LayoutBlockKind.Caption;
        // a bullet makes a list item even in bold; a numbered heading ("2.1 Method") is told from a numbered item by its size or weight
        else if (Bullet.IsMatch(first)) block.Kind = LayoutBlockKind.ListItem;
        else if (IsHeading(lines, size, bold, body, chars)) block.Kind = LayoutBlockKind.Heading;
        else if (ListMarker.IsMatch(first)) block.Kind = LayoutBlockKind.ListItem;
        else block.Kind = LayoutBlockKind.Paragraph;
        return block;
    }

    /// <summary>
    /// A heading: at most three lines and 200 characters, not ending like a sentence, and set larger than the running
    /// text, or in bold where the running text is not.
    /// </summary>
    private static bool IsHeading(List<Line> lines, double size, bool bold, double body, int chars)
    {
        if (lines.Count > 3 || chars > 200 || body <= 0 || lines.Any(l => l.Rotated)) return false;
        string last = lines[^1].Text.TrimEnd();
        if (last.EndsWith('.') && !Regex.IsMatch(last, @"^\d+(\.\d+)*\.$") || last.EndsWith(',') || last.EndsWith(';')) return false;
        if (!lines[0].Text.Any(char.IsLetter)) return false;
        if (size >= 1.15 * body || (bold && size >= 0.95 * body && lines.Count <= 2)) return true;
        // a numbered section title set only a little larger ("1 Introduction", "2.3 Results")
        return lines.Count == 1 && size >= 1.05 * body && NumberedTitle.IsMatch(lines[0].Text);
    }

    // ------------------------------------------------------------------ headers, footers, heading levels

    private static readonly Regex PageNumber = new(
        @"^(?:[-–—]\s*)?(?:(?:page|seite|p\.|s\.)\s*)?\d{1,4}(?:\s*(?:/|of|von|de|sur)\s*\d{1,4})?(?:\s*[-–—])?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Headers and footers: blocks in the top or bottom tenth of the page that are a page number, or (in a document)
    /// whose text, with the digits left out, recurs at the same end of at least two pages and of 40 % of them.
    /// </summary>
    private static void ClassifyMargins(IReadOnlyList<(PageLayout Layout, PdfPage Page, List<(LayoutBlock Block, List<Line> Lines)> Blocks)> pages, bool singlePage)
    {
        static string Key(string text) => Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"\d+", "#"), @"\s+", " ").Trim();

        var candidates = new List<(LayoutBlock Block, bool Top, string Key)>();
        foreach (var (layout, page, blocks) in pages)
        {
            var box = page.CropBox;
            double margin = 0.1 * box.Height;
            var topmost = blocks.Count == 0 ? null : blocks.MaxBy(b => b.Block.Bounds.Top).Block;
            var bottommost = blocks.Count == 0 ? null : blocks.MinBy(b => b.Block.Bounds.Bottom).Block;
            foreach (var (block, lines) in blocks)
            {
                if (lines.Count > 3) continue;
                bool top = block.Bounds.Bottom >= box.Top - margin, bottom = block.Bounds.Top <= box.Bottom + margin;
                if (!top && !bottom) continue;
                var kind = top ? LayoutBlockKind.Header : LayoutBlockKind.Footer;
                if (PageNumber.IsMatch(block.Text.Trim())) block.Kind = kind;
                // the outermost line of the page, set smaller than the running text (also on a single page)
                else if (lines.Count == 1 && block.FontSize < 0.9 * layout.BodyFontSize && ReferenceEquals(block, top ? topmost : bottommost))
                    block.Kind = kind;
                else candidates.Add((block, top, Key(block.Text)));
            }
        }
        if (singlePage) return;
        int needed = Math.Max(2, (int)Math.Ceiling(0.4 * pages.Count));
        foreach (var group in candidates.GroupBy(c => (c.Top, c.Key)))
        {
            if (group.Count() < needed) continue;
            foreach (var (block, top, _) in group)
            {
                block.Kind = top ? LayoutBlockKind.Header : LayoutBlockKind.Footer;
                block.Level = 0;
            }
        }
    }

    /// <summary>Heading levels: the distinct sizes of the headings, largest first; bold headings at the size of the running text come last.</summary>
    private static void RankHeadings(IReadOnlyList<PageLayout> pages)
    {
        var headings = pages.SelectMany(p => p.Blocks).Where(b => b.Kind == LayoutBlockKind.Heading).ToList();
        var sizes = headings.Select(b => b.FontSize).Distinct().OrderByDescending(s => s).ToList();
        foreach (var heading in headings) heading.Level = sizes.IndexOf(heading.FontSize) + 1;
    }
}
