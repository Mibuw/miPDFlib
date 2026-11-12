using MiPdf.Content;

namespace MiPdf.Text;

/// <summary>A cell of a <see cref="LayoutTable"/>.</summary>
public sealed class LayoutTableCell
{
    internal LayoutTableCell(string text, PdfRectangle bounds, int row, int column, int rowSpan, int columnSpan)
    {
        Text = text;
        Bounds = bounds;
        Row = row;
        Column = column;
        RowSpan = rowSpan;
        ColumnSpan = columnSpan;
    }

    /// <summary>The text of the cell, its lines separated by '\n' (empty for an empty cell).</summary>
    public string Text { get; }

    /// <summary>The bounds of the cell (of its rules, or of its text in a table without rules).</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>The row of the cell, 0 for the top row.</summary>
    public int Row { get; }

    /// <summary>The column of the cell, 0 for the left column.</summary>
    public int Column { get; }

    /// <summary>The number of rows the cell spans.</summary>
    public int RowSpan { get; }

    /// <summary>The number of columns the cell spans.</summary>
    public int ColumnSpan { get; }

    /// <inheritdoc/>
    public override string ToString() => $"[{Row},{Column}] {Text}";
}

/// <summary>
/// The cells of a table found by the layout analysis: from its rules (a grid of lines, also the edges of cell
/// backgrounds), or, for a table without rules, from the gaps that all its rows share.
/// </summary>
public sealed class LayoutTable
{
    internal LayoutTable(int rowCount, int columnCount, IReadOnlyList<LayoutTableCell> cells, bool hasRules)
    {
        RowCount = rowCount;
        ColumnCount = columnCount;
        Cells = cells;
        HasRules = hasRules;
    }

    /// <summary>The number of rows.</summary>
    public int RowCount { get; }

    /// <summary>The number of columns.</summary>
    public int ColumnCount { get; }

    /// <summary>The cells, row by row, left to right; a cell spanning several rows or columns is listed once.</summary>
    public IReadOnlyList<LayoutTableCell> Cells { get; }

    /// <summary>True if the cells were found from the table's rules.</summary>
    public bool HasRules { get; }

    /// <summary>The cell covering a row and column (also a spanning cell), or null if there is none.</summary>
    public LayoutTableCell? this[int row, int column] =>
        Cells.FirstOrDefault(c => row >= c.Row && row < c.Row + c.RowSpan && column >= c.Column && column < c.Column + c.ColumnSpan);
}

/// <summary>Finds tables from the rules of a page, and the columns of tables without rules.</summary>
internal static class TableDetector
{
    private const double Tolerance = 2;

    /// <summary>A grid of rules: column boundaries left to right, row boundaries top to bottom, and the cells (grid cells joined where a rule is missing).</summary>
    internal sealed class Grid
    {
        public required double[] Xs;
        public required double[] Ys;
        public required int[,] Cell;   // per grid cell the index of the (joined) cell it belongs to

        public PdfRectangle Bounds => new(Xs[0], Ys[^1], Xs[^1], Ys[0]);

        public bool Contains(double x, double y) =>
            x >= Xs[0] - Tolerance && x <= Xs[^1] + Tolerance && y >= Ys[^1] - Tolerance && y <= Ys[0] + Tolerance;
    }

    /// <summary>
    /// The grids among the rules: rules that cross form groups, and a group with at least two rows and two columns of
    /// grid cells is a grid (a single frame or a row of boxes is not).
    /// </summary>
    public static List<Grid> FindGrids(IReadOnlyList<PdfRule> rules)
    {
        var h = rules.Where(r => r.IsHorizontal && r.Length >= 5).ToList();
        var v = rules.Where(r => !r.IsHorizontal && r.Length >= 5).ToList();
        int n = h.Count + v.Count;
        // vector drawings made of many thousands of segments (plots, maps) are not searched
        if ((long)h.Count * v.Count > 25_000_000) return new List<Grid>();
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < h.Count; i++)
            for (int j = 0; j < v.Count; j++)
                if (Crosses(h[i], v[j])) parent[Find(i)] = Find(h.Count + j);

        var grids = new List<Grid>();
        foreach (var group in Enumerable.Range(0, n).GroupBy(Find))
        {
            var hs = group.Where(i => i < h.Count).Select(i => h[i]).ToList();
            var vs = group.Where(i => i >= h.Count).Select(i => v[i - h.Count]).ToList();
            var xs = Cluster(vs.Select(r => r.Start.X)).ToArray();
            var ys = Cluster(hs.Select(r => r.Start.Y)).OrderByDescending(y => y).ToArray();
            if (xs.Length < 3 || ys.Length < 3) continue;
            int rows = ys.Length - 1, cols = xs.Length - 1;
            if (rows * cols > 20_000 || hs.Count + vs.Count > 5_000) continue;
            // grid cells are joined where the rule between them is missing
            var id = Enumerable.Range(0, rows * cols).ToArray();
            int Root(int i) => id[i] == i ? i : id[i] = Root(id[i]);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    double midY = (ys[r] + ys[r + 1]) / 2, midX = (xs[c] + xs[c + 1]) / 2;
                    if (c + 1 < cols && !vs.Any(x => Math.Abs(x.Start.X - xs[c + 1]) <= Tolerance && x.Start.Y <= midY + Tolerance && x.End.Y >= midY - Tolerance))
                        id[Root(r * cols + c)] = Root(r * cols + c + 1);
                    if (r + 1 < rows && !hs.Any(y => Math.Abs(y.Start.Y - ys[r + 1]) <= Tolerance && y.Start.X <= midX + Tolerance && y.End.X >= midX - Tolerance))
                        id[Root(r * cols + c)] = Root((r + 1) * cols + c);
                }
            var cell = new int[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++) cell[r, c] = Root(r * cols + c);
            grids.Add(new Grid { Xs = xs, Ys = ys, Cell = cell });
        }
        // a grid inside another (a table in a framed area) is kept; the outer one is dropped if it holds only that one
        return grids.Where(g => !grids.Any(o => !ReferenceEquals(o, g) && Inside(o, g) && Distinct(g) <= 1)).ToList();

        static bool Inside(Grid inner, Grid outer) =>
            inner.Xs[0] >= outer.Xs[0] - Tolerance && inner.Xs[^1] <= outer.Xs[^1] + Tolerance
            && inner.Ys[0] <= outer.Ys[0] + Tolerance && inner.Ys[^1] >= outer.Ys[^1] - Tolerance;
        static int Distinct(Grid g) => g.Cell.Cast<int>().Distinct().Count();
    }

    private static bool Crosses(PdfRule h, PdfRule v) =>
        v.Start.X >= h.Start.X - Tolerance && v.Start.X <= h.End.X + Tolerance && h.Start.Y >= v.Start.Y - Tolerance && h.Start.Y <= v.End.Y + Tolerance;

    /// <summary>Positions within the tolerance merged (their mean), ascending.</summary>
    private static List<double> Cluster(IEnumerable<double> values)
    {
        var result = new List<double>();
        var group = new List<double>();
        foreach (var x in values.OrderBy(x => x))
        {
            if (group.Count > 0 && x - group[^1] > Tolerance)
            {
                result.Add(group.Average());
                group.Clear();
            }
            group.Add(x);
        }
        if (group.Count > 0) result.Add(group.Average());
        return result;
    }

    /// <summary>
    /// The table of a grid with the words placed into its cells (by the middle of each word). Where rules are missing,
    /// grid cells are joined; they are divided again where the text shows rows and columns: a grid row whose lines carry
    /// text in several cells each (data rows without rules between them) is split into rows at its lines, and a cell
    /// joined across columns is split at a column boundary that a clear gap runs along in all its lines (data rows of a
    /// table whose rules divide only the head).
    /// </summary>
    public static LayoutTable FromGrid(Grid grid, IEnumerable<TextLineInfo> lines)
    {
        int cols = grid.Xs.Length - 1;
        var words = new List<(TextLineInfo Line, TextWord Word, int Column, double Y)>();
        foreach (var line in lines)
            foreach (var word in line.Words)
            {
                double x = (word.Bounds.Left + word.Bounds.Right) / 2, y = (word.Bounds.Bottom + word.Bounds.Top) / 2;
                int c = Array.FindLastIndex(grid.Xs, b => b <= x);
                if (c < 0 || c >= cols || y > grid.Ys[0] || y < grid.Ys[^1]) continue;
                words.Add((line, word, c, y));
            }
        var (ys, cell) = SplitRows(grid, words);
        int rows = ys.Length - 1;
        var placed = words.Select(w => (Row: Math.Clamp(Array.FindLastIndex(ys, b => b >= w.Y), 0, rows - 1), w.Column, w.Line, w.Word)).ToList();

        int next = cell.Cast<int>().Max() + 1;
        foreach (var id in cell.Cast<int>().Distinct().ToList())
        {
            int first = cols, last = -1;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (cell[r, c] == id) { first = Math.Min(first, c); last = Math.Max(last, c); }
            if (last <= first) continue;
            // left to right: after a split, the part right of the boundary is checked on at the next boundaries
            int current = id;
            for (int j = first + 1; j <= last; j++)
            {
                var inCell = placed.Where(p => cell[p.Row, p.Column] == current).ToList();
                if (!GapAt(inCell, grid.Xs[j])) continue;
                int part = next++;
                for (int r = 0; r < rows; r++)
                    for (int c = j; c < cols; c++)
                        if (cell[r, c] == current) cell[r, c] = part;
                current = part;
            }
        }

        var byId = placed.GroupBy(p => cell[p.Row, p.Column]).ToDictionary(g => g.Key, g => g.Select(p => (p.Line, p.Word)).ToList());
        var cells = new List<LayoutTableCell>();
        var done = new HashSet<int>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int id = cell[r, c];
                if (!done.Add(id)) continue;
                int rowSpan = 1, colSpan = 1;
                while (r + rowSpan < rows && cell[r + rowSpan, c] == id) rowSpan++;
                while (c + colSpan < cols && cell[r, c + colSpan] == id) colSpan++;
                var bounds = new PdfRectangle(grid.Xs[c], ys[r + rowSpan], grid.Xs[c + colSpan], ys[r]);
                cells.Add(new LayoutTableCell(byId.TryGetValue(id, out var list) ? Text(list) : "", bounds, r, c, rowSpan, colSpan));
            }
        return new LayoutTable(rows, cols, cells, hasRules: true);
    }

    /// <summary>
    /// The row boundaries and cells of a grid after splitting the grid rows that hold several data rows. The words of
    /// the cells lying in one grid row alone are grouped into lines; each line with text in two cells or more begins a
    /// row, a line with text in one cell only continues the row above (a wrapped description). A grid row is split only
    /// if this gives two rows or more and no line beginning a row holds running text (more than five words) in two
    /// cells, as two wrapped paragraphs side by side would.
    /// </summary>
    private static (double[] Ys, int[,] Cell) SplitRows(Grid grid, List<(TextLineInfo Line, TextWord Word, int Column, double Y)> words)
    {
        int rows = grid.Ys.Length - 1, cols = grid.Xs.Length - 1, size = rows * cols;
        var ys = new List<double> { grid.Ys[0] };
        var cellRows = new List<int[]>();
        for (int r = 0; r < rows; r++)
        {
            bool Own(int c) => (r == 0 || grid.Cell[r - 1, c] != grid.Cell[r, c]) && (r == rows - 1 || grid.Cell[r + 1, c] != grid.Cell[r, c]);
            var band = words.Where(w => w.Y <= grid.Ys[r] && w.Y > grid.Ys[r + 1] && Own(w.Column)).OrderByDescending(w => w.Y).ToList();
            var slots = new List<List<(TextLineInfo Line, TextWord Word, int Column, double Y)>>();
            foreach (var w in band)
            {
                if (slots.Count == 0 || slots[^1][0].Y - w.Y > 0.5 * w.Word.FontSize) slots.Add(new());
                slots[^1].Add(w);
            }
            var starts = new List<int>();
            for (int s = 0; s < slots.Count; s++)
            {
                var pieces = slots[s].GroupBy(w => grid.Cell[r, w.Column]).ToList();
                if (pieces.Count < 2) continue;
                if (pieces.Count(p => p.Count() > 5) >= 2) { starts.Clear(); break; }
                starts.Add(s);
            }
            var bounds = new List<double>();
            for (int i = 1; i < starts.Count; i++)
                bounds.Add((slots[starts[i] - 1].Min(w => w.Y) + slots[starts[i]].Max(w => w.Y)) / 2);
            for (int part = 0; part <= bounds.Count; part++)
            {
                var row = new int[cols];
                for (int c = 0; c < cols; c++) row[c] = Own(c) ? grid.Cell[r, c] + part * size : grid.Cell[r, c];
                cellRows.Add(row);
                ys.Add(part < bounds.Count ? bounds[part] : grid.Ys[r + 1]);
            }
        }
        var cell = new int[cellRows.Count, cols];
        for (int r = 0; r < cellRows.Count; r++)
            for (int c = 0; c < cols; c++) cell[r, c] = cellRows[r][c];
        return (ys.ToArray(), cell);
    }

    /// <summary>
    /// True if a column boundary runs through a clear gap in the words of a cell: no word crosses it, words lie on both
    /// sides of it, and in each line with words on both sides they stand at least 1 em apart there.
    /// </summary>
    private static bool GapAt(List<(int Row, int Column, TextLineInfo Line, TextWord Word)> words, double x)
    {
        bool both = false;
        foreach (var line in words.GroupBy(w => w.Line))
        {
            var left = line.Where(w => w.Word.Bounds.Right <= x + 0.5).ToList();
            var right = line.Where(w => w.Word.Bounds.Left >= x - 0.5).ToList();
            if (left.Count + right.Count < line.Count()) return false;   // a word crosses the boundary
            if (left.Count == 0 || right.Count == 0) continue;
            var a = left.MaxBy(w => w.Word.Bounds.Right).Word;
            var b = right.MinBy(w => w.Word.Bounds.Left).Word;
            if (b.Bounds.Left - a.Bounds.Right < Math.Max(a.FontSize, b.FontSize)) return false;
            both = true;
        }
        return both;
    }

    /// <summary>
    /// True if the cells of a grid hold a table rather than the fields of a form or the frames of a page layout: most
    /// cells with text do not span columns, and most rows with text have text in two cells or more (or all cells are
    /// single cells and a head row has text in each column, as in a table left empty to be filled in). A cell with text
    /// spanning rows and columns over a quarter of the table or more is a frame of a page layout, cells holding running
    /// text (more than 60 words on average) do not make a table either.
    /// </summary>
    public static bool IsTable(LayoutTable table)
    {
        var text = table.Cells.Where(c => c.Text.Length > 0).ToList();
        if (text.Count < 2) return false;
        if (text.Sum(c => c.Text.Split(' ', '\n').Length) > 60 * text.Count) return false;
        if (text.Count(c => c.ColumnSpan == 1) < 0.5 * text.Count) return false;
        double area = Area(table.Cells);
        if (text.Any(c => c.RowSpan >= 2 && c.ColumnSpan >= 2 && Area(new[] { c }) >= 0.25 * area)) return false;
        var rows = text.GroupBy(c => c.Row).ToList();
        if (rows.Count < 2) return false;
        int several = rows.Count(g => g.Count() >= 2);
        if (several >= 0.5 * rows.Count) return true;
        return table.ColumnCount >= 3 && rows.Any(g => g.Count() == table.ColumnCount)
            && text.Count(c => c.ColumnSpan == 1 && c.RowSpan == 1) >= 0.9 * text.Count;

        static double Area(IEnumerable<LayoutTableCell> cells) => cells.Sum(c => c.Bounds.Width * c.Bounds.Height);
    }

    /// <summary>The words of a cell as text: in the order of their lines, a line break where the line changes.</summary>
    private static string Text(List<(TextLineInfo Line, TextWord Word)> words)
    {
        var sb = new System.Text.StringBuilder();
        TextLineInfo? line = null;
        foreach (var (l, w) in words)
        {
            if (line != null) sb.Append(ReferenceEquals(l, line) ? ' ' : '\n');
            sb.Append(w.Text);
            line = l;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The table of rows without rules: each row's cells are its words split at gaps of 1.5 em or more; the columns are
    /// the stretches the cells of all rows cover, between the gaps they all leave free. A cell covering several columns
    /// spans them.
    /// </summary>
    public static LayoutTable FromRows(IReadOnlyList<TextLineInfo> rows)
    {
        var rowCells = rows.Select(Cells).ToList();
        // the covered stretches of the x axis, from the rows with the most cells (a head cell spanning two columns does
        // not join them)
        int most = rowCells.Max(r => r.Count);
        var spans = rowCells.Where(r => r.Count == most).SelectMany(r => r).Select(c => (c.Bounds.Left, c.Bounds.Right)).OrderBy(s => s.Left).ToList();
        var columns = new List<(double Left, double Right)>();
        foreach (var (left, right) in spans)
        {
            if (columns.Count > 0 && left <= columns[^1].Right + Tolerance) columns[^1] = (columns[^1].Left, Math.Max(columns[^1].Right, right));
            else columns.Add((left, right));
        }
        var cells = new List<LayoutTableCell>();
        for (int r = 0; r < rowCells.Count; r++)
        {
            // pieces of a row do not share columns: a piece starting within the columns of the one before it ends that
            // one's span; one starting at the same column starts after it, or, with no column left, is part of it
            var placed = new List<(string Text, PdfRectangle Bounds, int First, int Last)>();
            foreach (var (text, bounds) in rowCells[r])
            {
                int first = columns.FindIndex(c => c.Right >= bounds.Left - Tolerance);
                int last = columns.FindLastIndex(c => c.Left <= bounds.Right + Tolerance);
                if (first < 0) first = 0;
                if (last < first) last = first;
                if (placed.Count > 0 && first <= placed[^1].Last)
                {
                    var previous = placed[^1];
                    if (first > previous.First) placed[^1] = previous with { Last = first - 1 };
                    else if (last > previous.Last) first = previous.Last + 1;
                }
                if (placed.Count > 0 && first <= placed[^1].Last)
                {
                    var (t, b, f, l) = placed[^1];
                    placed[^1] = (t + " " + text, PdfTextExtractor.Union(new[] { b, bounds }), f, Math.Max(l, last));
                }
                else placed.Add((text, bounds, first, last));
            }
            foreach (var (text, bounds, first, last) in placed)
                cells.Add(new LayoutTableCell(text, bounds, r, first, 1, last - first + 1));
        }
        return new LayoutTable(rowCells.Count, Math.Max(1, columns.Count), cells, hasRules: false);
    }

    /// <summary>The cells of a line: its words split at gaps of 1.5 em or more.</summary>
    private static List<(string Text, PdfRectangle Bounds)> Cells(TextLineInfo line)
    {
        var cells = new List<(string Text, PdfRectangle Bounds)>();
        TextWord? previous = null;
        foreach (var word in line.Words)
        {
            if (previous != null && word.Bounds.Left - previous.Bounds.Right < 1.5 * Math.Max(word.FontSize, previous.FontSize))
            {
                var (text, b) = cells[^1];
                cells[^1] = (text + " " + word.Text, new PdfRectangle(b.Left, Math.Min(b.Bottom, word.Bounds.Bottom), word.Bounds.Right, Math.Max(b.Top, word.Bounds.Top)));
            }
            else cells.Add((word.Text, word.Bounds));
            previous = word;
        }
        return cells;
    }
}
