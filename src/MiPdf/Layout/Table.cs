using MiPdf.Fonts;
using MiPdf.Graphics;

namespace MiPdf.Layout;

public enum VerticalAlignment
{
    Top,
    Middle,
    Bottom,
}

/// <summary>A table cell. Contains block elements (paragraphs, images, nested tables).</summary>
public sealed class Cell
{
    private readonly List<LayoutElement> _content = new();

    public Cell(int colSpan = 1, int rowSpan = 1)
    {
        ColSpan = Math.Max(1, colSpan);
        RowSpan = Math.Max(1, rowSpan);
    }

    public Cell(string text, int colSpan = 1, int rowSpan = 1) : this(colSpan, rowSpan) => Add(text);

    public IReadOnlyList<LayoutElement> Content => _content;

    public int ColSpan { get; }

    /// <summary>Number of rows the cell spans.</summary>
    public int RowSpan { get; }

    /// <summary>Overrides the table's default padding.</summary>
    public Thickness? Padding { get; set; }

    public PdfColor? Background { get; set; }

    /// <summary>Overrides the table's default border width (0 = no border).</summary>
    public double? BorderWidth { get; set; }

    public PdfColor? BorderColor { get; set; }

    /// <summary>Alignment applied to text added with <see cref="Add(string)"/>.</summary>
    public TextAlignment? TextAlignment { get; set; }

    public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Top;

    /// <summary>Font for text added with <see cref="Add(string)"/> (defaults to the table's font).</summary>
    public PdfFont? Font { get; set; }

    public double? FontSize { get; set; }

    public PdfColor? TextColor { get; set; }

    internal string? PlainText { get; private set; }

    /// <summary>Adds text formatted with the cell/table defaults.</summary>
    public Cell Add(string text)
    {
        PlainText = (PlainText == null ? "" : PlainText + "\n") + text;
        return this;
    }

    public Cell Add(LayoutElement element)
    {
        _content.Add(element);
        return this;
    }
}

/// <summary>
/// A table with relative column widths. Rows are created automatically from the added cells (honoring column and
/// row spans). Header rows are repeated on every page; rows taller than a page are split across pages.
/// </summary>
public sealed class Table : LayoutElement
{
    private readonly double[] _columnWidths;
    private readonly List<(Cell Cell, bool Header)> _cells = new();

    /// <summary>Creates a table with relative column widths, e.g. <c>new Table(1, 3, 1)</c>.</summary>
    public Table(params double[] relativeColumnWidths)
    {
        if (relativeColumnWidths.Length == 0) throw new ArgumentException("At least one column is required.");
        _columnWidths = relativeColumnWidths;
    }

    /// <summary>Creates a table with <paramref name="columns"/> equally wide columns.</summary>
    public static Table WithColumns(int columns) => new(Enumerable.Repeat(1.0, columns).ToArray());

    public int ColumnCount => _columnWidths.Length;

    /// <summary>Table width as percentage of the available width.</summary>
    public double WidthPercent { get; set; } = 100;

    public PdfFont Font { get; set; } = PdfStandardFont.Helvetica;

    public PdfFont HeaderFont { get; set; } = PdfStandardFont.HelveticaBold;

    public double FontSize { get; set; } = 9;

    public Thickness CellPadding { get; set; } = new(4, 3);

    public double BorderWidth { get; set; } = 0.5;

    public PdfColor BorderColor { get; set; } = PdfColor.Black;

    public PdfColor? HeaderBackground { get; set; } = PdfColor.Gray(0.9);

    /// <summary>Background for every second body row (zebra striping).</summary>
    public PdfColor? AlternateRowBackground { get; set; }

    public bool RepeatHeader { get; set; } = true;

    public Cell AddHeaderCell(string text) => AddHeaderCell(new Cell(text));

    public Cell AddHeaderCell(Cell cell)
    {
        _cells.Add((cell, true));
        return cell;
    }

    public Cell AddCell(string text) => AddCell(new Cell(text));

    public Cell AddCell(LayoutElement element) => AddCell(new Cell().Add(element));

    public Cell AddCell(Cell cell)
    {
        _cells.Add((cell, false));
        return cell;
    }

    /// <summary>
    /// Split rows that are taller than a whole page across pages (default true). Rows that merely do not fit on the
    /// current page are moved to the next page; rows connected by row spans are kept together.
    /// </summary>
    public bool SplitRowsAcrossPages { get; set; } = true;

    internal override LayoutBox Prepare(double width)
    {
        double tableWidth = width * Math.Clamp(WidthPercent, 1, 100) / 100;
        double total = _columnWidths.Sum();
        var colWidths = _columnWidths.Select(w => tableWidth * w / total).ToArray();
        double ColumnX(int col) => colWidths.Take(col).Sum();

        // place the cells on the grid (honoring column and row spans)
        var occupied = new HashSet<(int, int)>();
        var placed = new List<PreparedCell>();
        int row = 0, column = 0;
        foreach (var (cell, isHeaderCell) in _cells)
        {
            while (column >= ColumnCount || occupied.Contains((row, column)))
            {
                column++;
                if (column >= ColumnCount)
                {
                    row++;
                    column = 0;
                }
            }
            int span = 1;
            while (span < cell.ColSpan && column + span < ColumnCount && !occupied.Contains((row, column + span))) span++;
            for (int r = 0; r < cell.RowSpan; r++)
                for (int c = 0; c < span; c++) occupied.Add((row + r, column + c));
            placed.Add(new PreparedCell(cell, isHeaderCell, row, column, span, cell.RowSpan) { X = ColumnX(column), Width = colWidths.Skip(column).Take(span).Sum() });
            column += span;
        }
        int rowCount = placed.Count == 0 ? 0 : placed.Max(c => c.Row) + 1;
        foreach (var c in placed) c.RowSpan = Math.Min(c.RowSpan, rowCount - c.Row);

        // header rows: leading rows that contain only header cells
        int headerRows = 0;
        while (headerRows < rowCount && placed.Where(c => c.Row == headerRows).All(c => c.Header) && placed.Any(c => c.Row == headerRows)) headerRows++;
        foreach (var c in placed.Where(c => c.Row < headerRows)) c.RowSpan = Math.Min(c.RowSpan, headerRows - c.Row);

        // content and heights
        var rows = Enumerable.Range(0, rowCount).Select(r => new PreparedRow { Index = r, IsHeader = r < headerRows }).ToList();
        foreach (var c in placed)
        {
            c.Padding = c.Cell.Padding ?? CellPadding;
            double innerWidth = Math.Max(1, c.Width - c.Padding.Horizontal);
            if (c.Cell.PlainText != null)
            {
                var p = new Paragraph(c.Cell.PlainText, c.Cell.Font ?? (c.Header ? HeaderFont : Font), c.Cell.FontSize ?? FontSize)
                {
                    Alignment = c.Cell.TextAlignment ?? Graphics.TextAlignment.Left,
                    Color = c.Cell.TextColor ?? PdfColor.Black,
                    TagIntoParent = true,
                };
                c.Boxes.Add(p.Prepare(innerWidth));
            }
            foreach (var element in c.Cell.Content) c.Boxes.Add(element.Prepare(innerWidth));
            rows[c.Row].Cells.Add(c);
            if (c.RowSpan == 1) rows[c.Row].Height = Math.Max(rows[c.Row].Height, c.ContentHeight + c.Padding.Vertical);
        }
        foreach (var c in placed.Where(c => c.RowSpan > 1))
        {
            double spanned = rows.Skip(c.Row).Take(c.RowSpan).Sum(r => r.Height);
            double needed = c.ContentHeight + c.Padding.Vertical;
            if (needed > spanned) rows[c.Row + c.RowSpan - 1].Height += needed - spanned;
        }
        int bodyIndex = 0;
        foreach (var r in rows.Skip(headerRows)) r.BodyIndex = bodyIndex++;

        // groups of rows connected by row spans are kept on one page
        var header = new List<RowGroup>();
        var body = new List<RowGroup>();
        for (int r = 0; r < rowCount;)
        {
            int end = r;
            for (int i = r; i <= end; i++)
                foreach (var c in rows[i].Cells) end = Math.Max(end, c.Row + c.RowSpan - 1);
            var group = new RowGroup(rows.GetRange(r, end - r + 1));
            (r < headerRows ? header : body).Add(group);
            r = end + 1;
        }
        return new TableBox(this, header, body, SpacingBefore, SpacingAfter);
    }

    private sealed class PreparedCell(Cell cell, bool header, int row, int col, int colSpan, int rowSpan)
    {
        public Cell Cell { get; } = cell;
        public bool Header { get; } = header;
        public int Row { get; } = row;
        public int Col { get; } = col;
        public int ColSpan { get; } = colSpan;
        public int RowSpan { get; set; } = rowSpan;
        public double X { get; init; }
        public double Width { get; init; }
        public Thickness Padding { get; set; }
        public List<LayoutBox> Boxes { get; set; } = new();
        public double ContentHeight => Boxes.Sum(b => b.Height);
        public Tagging.PdfStructureElement? Element { get; set; }

        public PreparedCell Continuation(List<LayoutBox> boxes) =>
            new(Cell, Header, Row, Col, ColSpan, 1) { X = X, Width = Width, Padding = Padding, Boxes = boxes, Element = Element };
    }

    private sealed class PreparedRow
    {
        public int Index;
        public List<PreparedCell> Cells { get; init; } = new();
        public double Height;
        public bool IsHeader;
        public int BodyIndex;
        public Tagging.PdfStructureElement? Element;
    }

    private sealed class RowGroup(List<PreparedRow> rows)
    {
        public List<PreparedRow> Rows { get; } = rows;
        public double Height => Rows.Sum(r => r.Height);
        public bool Splittable => Rows.Count == 1 && Rows[0].Cells.All(c => c.RowSpan == 1);
    }

    private sealed class TableBox : LayoutBox
    {
        private readonly Table _table;
        private readonly List<RowGroup> _header;
        private readonly List<RowGroup> _groups;
        private readonly double _before;
        private readonly double _after;
        private readonly bool _drawHeader;
        private readonly bool _headerIsRepeat;
        private readonly Tagging.PdfStructureElement? _element;

        public TableBox(Table table, List<RowGroup> header, List<RowGroup> groups, double before, double after, bool drawHeader = true,
            bool headerIsRepeat = false, Tagging.PdfStructureElement? element = null)
        {
            _table = table;
            _header = header;
            _groups = groups;
            _before = before;
            _after = after;
            _drawHeader = drawHeader;
            _headerIsRepeat = headerIsRepeat;
            _element = element;
        }

        private double HeaderHeight => _drawHeader ? _header.Sum(g => g.Height) : 0;

        public override double Height => _before + HeaderHeight + _groups.Sum(g => g.Height) + _after;

        public override LayoutBox? Render(PdfCanvas canvas, double x, double top, double availableHeight, bool force, out double usedHeight)
        {
            double used = _before;
            double firstGroup = _groups.Count > 0 ? _groups[0].Height : 0;
            // do not start a table on a page where not even the header and the first row fit (unless the page is empty)
            if (!force && used + HeaderHeight + firstGroup > availableHeight)
            {
                usedHeight = 0;
                return this;
            }
            var element = _element;
            if (canvas.StructureParent is { } parent) element ??= parent.AddChild(_table.StructureType ?? Tagging.StructureType.Table);
            if (_drawHeader)
            {
                foreach (var group in _header)
                {
                    RenderGroup(canvas, group, x, top - used, _headerIsRepeat ? null : element, _headerIsRepeat);
                    used += group.Height;
                }
            }
            bool renderedBody = false;
            for (int i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];
                double remaining = availableHeight - used;
                if (group.Height <= remaining + 0.001)
                {
                    RenderGroup(canvas, group, x, top - used, element, false);
                    used += group.Height;
                    renderedBody = true;
                    continue;
                }
                bool emptyPage = force && !renderedBody;
                if (!emptyPage)
                {
                    usedHeight = used;
                    return Continue(i, null, element);
                }
                if (group.Splittable && _table.SplitRowsAcrossPages && remaining > 0)
                {
                    var rest = RenderPartialRow(canvas, group.Rows[0], x, top - used, remaining, element);
                    used += remaining;
                    usedHeight = used;
                    return Continue(i + 1, rest, element);
                }
                // cannot be split: draw it anyway to guarantee progress
                RenderGroup(canvas, group, x, top - used, element, false);
                used += group.Height;
                renderedBody = true;
            }
            usedHeight = used + _after;
            return null;
        }

        private TableBox Continue(int nextGroup, RowGroup? continuation, Tagging.PdfStructureElement? element)
        {
            var groups = _groups.GetRange(nextGroup, _groups.Count - nextGroup);
            if (continuation != null) groups.Insert(0, continuation);
            return new TableBox(_table, _header, groups, 0, _after, _table.RepeatHeader, true, element);
        }

        /// <summary>Renders the part of a single row that fits; returns a group with the remaining cell content.</summary>
        private RowGroup RenderPartialRow(PdfCanvas canvas, PreparedRow row, double x, double top, double height, Tagging.PdfStructureElement? table)
        {
            var continuation = new PreparedRow { Index = row.Index, IsHeader = row.IsHeader, BodyIndex = row.BodyIndex, Element = row.Element };
            var savedParent = canvas.StructureParent;
            var tr = row.Element ??= table?.AddChild(Tagging.StructureType.TableRow);
            continuation.Element = tr;
            foreach (var cell in row.Cells)
            {
                DrawCellFrame(canvas, cell, row, x, top, height, artifact: false);
                BeginCell(canvas, cell, tr);
                double y = top - cell.Padding.Top, available = height - cell.Padding.Vertical;
                var restBoxes = new List<LayoutBox>();
                bool full = false;
                foreach (var box in cell.Boxes)
                {
                    if (full)
                    {
                        restBoxes.Add(box);
                        continue;
                    }
                    var rest = box.Render(canvas, x + cell.X + cell.Padding.Left, y, Math.Max(0, available), restBoxes.Count == 0 && y == top - cell.Padding.Top, out double boxUsed);
                    y -= boxUsed;
                    available -= boxUsed;
                    if (rest != null)
                    {
                        restBoxes.Add(rest);
                        full = true;
                    }
                }
                canvas.StructureParent = savedParent;
                var next = cell.Continuation(restBoxes);
                continuation.Cells.Add(next);
                continuation.Height = Math.Max(continuation.Height, next.ContentHeight + next.Padding.Vertical);
            }
            return new RowGroup(new List<PreparedRow> { continuation });
        }

        private void RenderGroup(PdfCanvas canvas, RowGroup group, double x, double top, Tagging.PdfStructureElement? table, bool artifact)
        {
            bool tagged = canvas.IsTagged;
            var savedParent = canvas.StructureParent;
            if (artifact && tagged) canvas.BeginArtifact(Tagging.ArtifactType.Pagination);
            double y = top;
            for (int r = 0; r < group.Rows.Count; r++)
            {
                var row = group.Rows[r];
                var tr = artifact ? null : row.Element ??= table?.AddChild(Tagging.StructureType.TableRow);
                foreach (var cell in row.Cells)
                {
                    double height = group.Rows.Skip(r).Take(cell.RowSpan).Sum(x2 => x2.Height);
                    DrawCellFrame(canvas, cell, row, x, y, height, artifact);
                    canvas.StructureParent = null;
                    if (tr != null) BeginCell(canvas, cell, tr);
                    double free = height - cell.Padding.Vertical - cell.ContentHeight;
                    double offset = cell.Cell.VerticalAlignment switch
                    {
                        VerticalAlignment.Middle => free / 2,
                        VerticalAlignment.Bottom => free,
                        _ => 0,
                    };
                    double cy = y - cell.Padding.Top - Math.Max(0, offset);
                    foreach (var box in cell.Boxes)
                    {
                        box.RenderAll(canvas, x + cell.X + cell.Padding.Left, cy);
                        cy -= box.Height;
                    }
                    canvas.StructureParent = savedParent;
                }
                y -= row.Height;
            }
            if (artifact && tagged) canvas.EndArtifact();
            canvas.StructureParent = savedParent;
        }

        private void BeginCell(PdfCanvas canvas, PreparedCell cell, Tagging.PdfStructureElement? tr)
        {
            canvas.StructureParent = null;
            if (tr == null) return;
            if (cell.Element == null)
            {
                var td = tr.AddChild(cell.Header ? Tagging.StructureType.TableHeaderCell : Tagging.StructureType.TableDataCell);
                if (cell.Header) td.SetAttribute("Table", "Scope", Objects.PdfName.Get("Column"));
                if (cell.ColSpan > 1) td.SetAttribute("Table", "ColSpan", new Objects.PdfNumber(cell.ColSpan));
                if (cell.RowSpan > 1) td.SetAttribute("Table", "RowSpan", new Objects.PdfNumber(cell.RowSpan));
                cell.Element = td;
            }
            canvas.StructureParent = cell.Element;
        }

        private void DrawCellFrame(PdfCanvas canvas, PreparedCell cell, PreparedRow row, double x, double top, double height, bool artifact)
        {
            bool tagged = canvas.IsTagged && !artifact;
            var background = cell.Cell.Background ?? (cell.Header ? _table.HeaderBackground
                : _table.AlternateRowBackground != null && row.BodyIndex % 2 == 1 ? _table.AlternateRowBackground : null);
            double border = cell.Cell.BorderWidth ?? _table.BorderWidth;
            if (background == null && border <= 0) return;
            if (tagged) canvas.BeginArtifact(Tagging.ArtifactType.Layout);
            double cx = x + cell.X, bottom = top - height;
            if (background is { } bg) canvas.DrawRectangle(cx, bottom, cell.Width, height, fill: bg);
            if (border > 0) canvas.DrawRectangle(cx, bottom, cell.Width, height, cell.Cell.BorderColor ?? _table.BorderColor, lineWidth: border);
            if (tagged) canvas.EndArtifact();
        }
    }
}
