using MiPdf.Fonts;
using MiPdf.Graphics;

namespace MiPdf.Layout;

/// <summary>Label style of a <see cref="ListElement"/>.</summary>
public enum ListStyle
{
    /// <summary>• (bullet)</summary>
    Bullet,

    /// <summary>– (en dash)</summary>
    Dash,

    /// <summary>1. 2. 3.</summary>
    Decimal,

    /// <summary>a) b) c)</summary>
    LowerAlpha,

    /// <summary>A. B. C.</summary>
    UpperAlpha,

    /// <summary>i. ii. iii.</summary>
    LowerRoman,

    /// <summary>I. II. III.</summary>
    UpperRoman,

    /// <summary>No label.</summary>
    None,
}

/// <summary>An item of a <see cref="ListElement"/>; contains paragraphs, nested lists, tables, images.</summary>
public sealed class ListItem
{
    private readonly List<LayoutElement> _content = new();

    public IReadOnlyList<LayoutElement> Content => _content;

    /// <summary>Overrides the generated label (e.g. "★" or "§ 3").</summary>
    public string? Label { get; set; }

    public ListItem Add(LayoutElement element)
    {
        _content.Add(element);
        return this;
    }
}

/// <summary>
/// An ordered or unordered list. Items may contain any layout elements including nested lists; items are split
/// across pages. In tagged documents the list is tagged as L / LI / Lbl / LBody.
/// </summary>
/// <example>
/// <code>
/// var list = new ListElement(ListStyle.Decimal) { Font = font };
/// list.AddItem("First item");
/// list.AddItem("Second item").Add(new ListElement(ListStyle.Dash).AddItems("Sub-item a", "Sub-item b"));
/// layout.Add(list);
/// </code>
/// </example>
public sealed class ListElement : LayoutElement
{
    private readonly List<ListItem> _items = new();

    public ListElement(ListStyle style = ListStyle.Bullet) => Style = style;

    public IReadOnlyList<ListItem> Items => _items;

    public ListStyle Style { get; set; }

    /// <summary>Number of the first item for numbered styles (default 1).</summary>
    public int StartNumber { get; set; } = 1;

    /// <summary>Custom label text per item number (overrides <see cref="Style"/>).</summary>
    public Func<int, string>? LabelFormatter { get; set; }

    /// <summary>Font of the labels and of items added as text.</summary>
    public PdfFont Font { get; set; } = PdfStandardFont.Helvetica;

    public double FontSize { get; set; } = 10;

    public PdfColor Color { get; set; } = PdfColor.Black;

    /// <summary>Width reserved for the labels in points (default 18).</summary>
    public double LabelWidth { get; set; } = 18;

    /// <summary>Vertical space between items in points (default 2).</summary>
    public double ItemSpacing { get; set; } = 2;

    /// <summary>Adds an item with text formatted with the list's font.</summary>
    public ListItem AddItem(string text)
    {
        var item = new ListItem().Add(new Paragraph(text, Font, FontSize) { Color = Color });
        _items.Add(item);
        return item;
    }

    /// <summary>Adds an item with the given content.</summary>
    public ListItem AddItem(params LayoutElement[] content)
    {
        var item = new ListItem();
        foreach (var element in content) item.Add(element);
        _items.Add(item);
        return item;
    }

    /// <summary>Adds several text items; returns the list (fluent).</summary>
    public ListElement AddItems(params string[] texts)
    {
        foreach (var text in texts) AddItem(text);
        return this;
    }

    /// <summary>The label text of the item with the given 0-based index.</summary>
    public string GetLabel(int index)
    {
        if (_items.Count > index && _items[index].Label is { } custom) return custom;
        int number = StartNumber + index;
        if (LabelFormatter != null) return LabelFormatter(number);
        return Style switch
        {
            ListStyle.Bullet => "•",
            ListStyle.Dash => "–",
            ListStyle.Decimal => number + ".",
            ListStyle.LowerAlpha => Alpha(number).ToLowerInvariant() + ")",
            ListStyle.UpperAlpha => Alpha(number) + ".",
            ListStyle.LowerRoman => Roman(number).ToLowerInvariant() + ".",
            ListStyle.UpperRoman => Roman(number) + ".",
            _ => "",
        };
    }

    private static string Alpha(int n)
    {
        var s = "";
        for (n = Math.Max(1, n); n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private static string Roman(int n)
    {
        if (n <= 0 || n >= 4000) return n.ToString();
        var values = new[] { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        var symbols = new[] { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < values.Length; i++)
            while (n >= values[i])
            {
                sb.Append(symbols[i]);
                n -= values[i];
            }
        return sb.ToString();
    }

    internal override LayoutBox Prepare(double width)
    {
        double contentWidth = Math.Max(1, width - LabelWidth);
        var items = new List<PreparedItem>();
        for (int i = 0; i < _items.Count; i++)
            items.Add(new PreparedItem(GetLabel(i), _items[i].Content.Select(e => e.Prepare(contentWidth)).ToList()));
        return new ListBox(this, items, SpacingBefore, SpacingAfter);
    }

    private sealed class PreparedItem(string label, List<LayoutBox> boxes)
    {
        public string Label { get; } = label;
        public List<LayoutBox> Boxes { get; } = boxes;
        public bool LabelDrawn { get; set; }
        public Tagging.PdfStructureElement? ListItemElement { get; set; }
        public Tagging.PdfStructureElement? BodyElement { get; set; }
    }

    private sealed class ListBox : LayoutBox
    {
        private readonly ListElement _list;
        private readonly List<PreparedItem> _items;
        private readonly double _before;
        private readonly double _after;
        private Tagging.PdfStructureElement? _element;

        public ListBox(ListElement list, List<PreparedItem> items, double before, double after, Tagging.PdfStructureElement? element = null)
        {
            _list = list;
            _items = items;
            _before = before;
            _after = after;
            _element = element;
        }

        public override double Height => _before + _items.Sum(i => i.Boxes.Sum(b => b.Height)) + Math.Max(0, _items.Count - 1) * _list.ItemSpacing + _after;

        public override double? FirstBaseline =>
            _items.Count > 0 && _items[0].Boxes.Count > 0 ? _before + (_items[0].Boxes[0].FirstBaseline ?? _list.Font.GetAscent(_list.FontSize)) : null;

        public override LayoutBox? Render(PdfCanvas canvas, double x, double top, double availableHeight, bool force, out double usedHeight)
        {
            double used = _before;
            bool renderedAny = false;
            var parent = canvas.StructureParent;
            if (parent != null) _element ??= parent.AddChild(_list.StructureType ?? Tagging.StructureType.List);
            try
            {
                for (int index = 0; index < _items.Count; index++)
                {
                    var item = _items[index];
                    if (index > 0) used += _list.ItemSpacing;
                    if (_element != null)
                    {
                        item.ListItemElement ??= _element.AddChild(Tagging.StructureType.ListItem);
                        item.BodyElement ??= item.ListItemElement.AddChild(Tagging.StructureType.ListBody);
                    }
                    while (item.Boxes.Count > 0)
                    {
                        var box = item.Boxes[0];
                        double y = top - used;
                        canvas.StructureParent = item.BodyElement ?? parent;
                        var rest = box.Render(canvas, x + _list.LabelWidth, y, Math.Max(0, availableHeight - used), force && !renderedAny, out double boxUsed);
                        canvas.StructureParent = parent;
                        if (boxUsed > 0 || rest == null)
                        {
                            renderedAny = true;
                            if (!item.LabelDrawn)
                            {
                                // the label sits on the baseline of the first line of the item
                                DrawLabel(canvas, item, x, y - (box.FirstBaseline ?? _list.Font.GetAscent(_list.FontSize)));
                                item.LabelDrawn = true;
                            }
                        }
                        used += boxUsed;
                        if (rest == null)
                        {
                            item.Boxes.RemoveAt(0);
                            continue;
                        }
                        item.Boxes[0] = rest; // page is full: continue this item on the next page
                        usedHeight = renderedAny ? used : 0;
                        return new ListBox(_list, _items.GetRange(index, _items.Count - index), renderedAny ? 0 : _before, _after, _element);
                    }
                }
            }
            finally
            {
                canvas.StructureParent = parent;
            }
            usedHeight = Math.Min(used + _after, Math.Max(availableHeight, used));
            return null;
        }

        private void DrawLabel(PdfCanvas canvas, PreparedItem item, double x, double baseline)
        {
            if (item.Label.Length == 0) return;
            if (item.ListItemElement is { } li)
            {
                var label = li.AddChild(Tagging.StructureType.Label);
                // Lbl must precede LBody in the structure (reading order)
                var kids = li.Dictionary.GetArray(Objects.PdfName.K);
                if (kids is { Count: >= 2 })
                {
                    var last = kids[kids.Count - 1];
                    kids.RemoveAt(kids.Count - 1);
                    kids.Insert(0, last);
                }
                canvas.BeginTag(label);
            }
            else if (canvas.IsTagged) canvas.BeginArtifact();
            canvas.DrawText(item.Label, _list.Font, _list.FontSize, x + _list.LabelWidth - 4, baseline, _list.Color, TextAlignment.Right);
            if (item.ListItemElement != null || canvas.IsTagged) canvas.EndTag();
        }
    }
}
