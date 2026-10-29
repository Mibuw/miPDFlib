using MiPdf.Fonts;
using MiPdf.Graphics;

namespace MiPdf.Layout;

/// <summary>A piece of text with uniform formatting inside a <see cref="Paragraph"/>.</summary>
public sealed class TextRun
{
    public TextRun(string text, PdfFont? font = null, double? fontSize = null, PdfColor? color = null)
    {
        Text = text ?? "";
        Font = font;
        FontSize = fontSize;
        Color = color;
    }

    public string Text { get; set; }

    /// <summary>Font; inherits the paragraph's font if null.</summary>
    public PdfFont? Font { get; set; }

    public double? FontSize { get; set; }

    public PdfColor? Color { get; set; }

    public bool Underline { get; set; }

    public bool Strikethrough { get; set; }
}

/// <summary>
/// A block of text that is wrapped to the available width. Can contain several <see cref="TextRun"/>s with
/// different fonts, sizes and colors. Line breaks ("\n") are honored. Splits across pages line by line.
/// </summary>
public sealed class Paragraph : LayoutElement
{
    private readonly List<TextRun> _runs = new();

    public Paragraph() { }

    public Paragraph(string text, PdfFont? font = null, double? fontSize = null)
    {
        if (font != null) Font = font;
        if (fontSize.HasValue) FontSize = fontSize.Value;
        Add(text);
    }

    public IReadOnlyList<TextRun> Runs => _runs;

    public PdfFont Font { get; set; } = PdfStandardFont.Helvetica;

    public double FontSize { get; set; } = 10;

    public PdfColor Color { get; set; } = PdfColor.Black;

    public TextAlignment Alignment { get; set; } = TextAlignment.Left;

    /// <summary>Line height as a multiple of the font's natural line height (default 1.15).</summary>
    public double LineSpacing { get; set; } = 1.15;

    public double FirstLineIndent { get; set; }

    public double LeftIndent { get; set; }

    public double RightIndent { get; set; }

    /// <summary>Apply the font's pair kerning (TrueType/OpenType fonts; default true).</summary>
    public bool Kerning { get; set; } = true;

    /// <summary>Hyphenation for long words (default: <see cref="TextLayout.DefaultHyphenator"/>). Soft hyphens (U+00AD) are always honored.</summary>
    public IHyphenator? Hyphenator { get; set; }

    /// <summary>Tag the text directly as content of the parent element (used for plain-text table cells).</summary>
    internal bool TagIntoParent { get; set; }

    /// <summary>Makes the paragraph a heading (H1-H6) in tagged documents; returns the paragraph.</summary>
    public Paragraph AsHeading(int level)
    {
        StructureType = Tagging.StructureType.Heading(level);
        return this;
    }

    /// <summary>Adds text using the paragraph's formatting.</summary>
    public Paragraph Add(string text)
    {
        _runs.Add(new TextRun(text));
        return this;
    }

    public Paragraph Add(TextRun run)
    {
        _runs.Add(run);
        return this;
    }

    public Paragraph Add(string text, PdfFont font, double? fontSize = null, PdfColor? color = null) =>
        Add(new TextRun(text, font, fontSize, color));

    // fluent setters
    public Paragraph SetFont(PdfFont font, double? size = null)
    {
        Font = font;
        if (size.HasValue) FontSize = size.Value;
        return this;
    }

    public Paragraph SetFontSize(double size)
    {
        FontSize = size;
        return this;
    }

    public Paragraph SetColor(PdfColor color)
    {
        Color = color;
        return this;
    }

    public Paragraph SetAlignment(TextAlignment alignment)
    {
        Alignment = alignment;
        return this;
    }

    public Paragraph SetSpacing(double before, double after)
    {
        SpacingBefore = before;
        SpacingAfter = after;
        return this;
    }

    internal override LayoutBox Prepare(double width)
    {
        var lines = TextLayout.BreakLines(this, width);
        return new ParagraphBox(this, lines, width, SpacingBefore, SpacingAfter);
    }

    internal sealed class ParagraphBox : LayoutBox
    {
        private readonly Paragraph _p;
        private readonly List<TextLine> _lines;
        private readonly double _width;
        private readonly double _before;
        private readonly double _after;

        private readonly Tagging.PdfStructureElement? _element;

        public ParagraphBox(Paragraph p, List<TextLine> lines, double width, double before, double after, Tagging.PdfStructureElement? element = null)
        {
            _p = p;
            _lines = lines;
            _width = width;
            _before = before;
            _after = after;
            _element = element;
        }

        public override double Height => _before + _lines.Sum(l => l.Height) + _after;

        public override double? FirstBaseline => _lines.Count == 0 ? null
            : _before + (_lines[0].Height - (_lines[0].Ascent - _lines[0].Descent)) / 2 + _lines[0].Ascent;

        public override LayoutBox? Render(PdfCanvas canvas, double x, double top, double availableHeight, bool force, out double usedHeight)
        {
            double used = _before;
            int count = 0;
            for (; count < _lines.Count; count++)
            {
                if (used + _lines[count].Height > availableHeight + 0.001 && !(force && count == 0)) break;
                used += _lines[count].Height;
            }

            var element = _element;
            Tagging.PdfStructureElement? tag = null;
            if (count > 0 && canvas.StructureParent is { } parent)
            {
                if (_p.TagIntoParent) tag = parent;
                else tag = element ??= parent.AddChild(_p.StructureType ?? Tagging.StructureType.Paragraph);
            }
            if (tag != null) canvas.BeginTag(tag);
            double y = top - _before;
            for (int i = 0; i < count; i++)
            {
                TextLayout.RenderLine(canvas, _p, _lines[i], x, y, _width);
                y -= _lines[i].Height;
            }
            if (tag != null) canvas.EndTag();

            if (count < _lines.Count)
            {
                usedHeight = used;
                return new ParagraphBox(_p, _lines.GetRange(count, _lines.Count - count), _width, 0, _after, element);
            }
            usedHeight = Math.Min(used + _after, Math.Max(availableHeight, used));
            return null;
        }
    }
}
