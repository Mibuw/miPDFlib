using MiPdf.Fonts;
using MiPdf.Graphics;

namespace MiPdf.Layout;

/// <summary>A piece of text with uniform formatting inside a <see cref="Paragraph"/>.</summary>
public sealed class TextRun
{
    /// <summary>Creates a run; formatting values that are null are inherited from the paragraph.</summary>
    public TextRun(string text, PdfFont? font = null, double? fontSize = null, PdfColor? color = null)
    {
        Text = text ?? "";
        Font = font;
        FontSize = fontSize;
        Color = color;
    }

    /// <summary>The text; line breaks ("\n") are honored.</summary>
    public string Text { get; set; }

    /// <summary>Font; inherits the paragraph's font if null.</summary>
    public PdfFont? Font { get; set; }

    /// <summary>Font size in points; inherits the paragraph's font size if null.</summary>
    public double? FontSize { get; set; }

    /// <summary>Text color; inherits the paragraph's color if null.</summary>
    public PdfColor? Color { get; set; }

    /// <summary>Draw a line below the text (position and thickness from the font metrics).</summary>
    public bool Underline { get; set; }

    /// <summary>Draw a line through the text.</summary>
    public bool Strikethrough { get; set; }

    /// <summary>
    /// Web address the text links to ("https://...", "mailto:..."), or null. A link annotation is placed over the text
    /// on every line it covers, with the text as its description. In tagged documents (PDF/UA) the text and the
    /// annotations form a <c>Link</c> structure element inside the paragraph.
    /// </summary>
    public string? Uri { get; set; }
}

/// <summary>
/// A block of text that is wrapped to the available width. Can contain several <see cref="TextRun"/>s with
/// different fonts, sizes and colors. Line breaks ("\n") are honored. Splits across pages line by line.
/// </summary>
public sealed class Paragraph : LayoutElement
{
    private readonly List<TextRun> _runs = new();

    /// <summary>Creates an empty paragraph; add text with <see cref="Add(string)"/>.</summary>
    public Paragraph() { }

    /// <summary>Creates a paragraph with <paramref name="text"/>; a null font or size keeps the default (Helvetica, 10 pt).</summary>
    public Paragraph(string text, PdfFont? font = null, double? fontSize = null)
    {
        if (font != null) Font = font;
        if (fontSize.HasValue) FontSize = fontSize.Value;
        Add(text);
    }

    /// <summary>The text runs in the order they were added.</summary>
    public IReadOnlyList<TextRun> Runs => _runs;

    /// <summary>Default font for runs without their own font (default Helvetica).</summary>
    public PdfFont Font { get; set; } = PdfStandardFont.Helvetica;

    /// <summary>Default font size in points for runs without their own size (default 10).</summary>
    public double FontSize { get; set; } = 10;

    /// <summary>Default text color for runs without their own color (default black).</summary>
    public PdfColor Color { get; set; } = PdfColor.Black;

    /// <summary>Horizontal alignment of the lines (default <see cref="TextAlignment.Left"/>).</summary>
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;

    /// <summary>Line height as a multiple of the font's natural line height (default 1.15).</summary>
    public double LineSpacing { get; set; } = 1.15;

    /// <summary>Additional indent of the first line in points, relative to <see cref="LeftIndent"/> (default 0).</summary>
    public double FirstLineIndent { get; set; }

    /// <summary>Left indent of all lines in points (default 0).</summary>
    public double LeftIndent { get; set; }

    /// <summary>Right indent of all lines in points (default 0).</summary>
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

    /// <summary>Adds a formatted text run; returns the paragraph.</summary>
    public Paragraph Add(TextRun run)
    {
        _runs.Add(run);
        return this;
    }

    /// <summary>Adds text with its own font (and optionally size and color); returns the paragraph.</summary>
    public Paragraph Add(string text, PdfFont font, double? fontSize = null, PdfColor? color = null) =>
        Add(new TextRun(text, font, fontSize, color));

    /// <summary>Adds a link: blue, underlined text that opens <paramref name="uri"/> (see <see cref="TextRun.Uri"/>).</summary>
    public Paragraph AddLink(string text, string uri)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        return Add(new TextRun(text, color: PdfColor.Blue) { Uri = uri, Underline = true });
    }

    // fluent setters
    /// <summary>Sets <see cref="Font"/> and, if given, <see cref="FontSize"/>; returns the paragraph.</summary>
    public Paragraph SetFont(PdfFont font, double? size = null)
    {
        Font = font;
        if (size.HasValue) FontSize = size.Value;
        return this;
    }

    /// <summary>Sets <see cref="FontSize"/> in points; returns the paragraph.</summary>
    public Paragraph SetFontSize(double size)
    {
        FontSize = size;
        return this;
    }

    /// <summary>Sets <see cref="Color"/>; returns the paragraph.</summary>
    public Paragraph SetColor(PdfColor color)
    {
        Color = color;
        return this;
    }

    /// <summary>Sets <see cref="Alignment"/>; returns the paragraph.</summary>
    public Paragraph SetAlignment(TextAlignment alignment)
    {
        Alignment = alignment;
        return this;
    }

    /// <summary>Sets the space before and after the paragraph in points; returns the paragraph.</summary>
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

        public int LineCount => _lines.Count;

        public double SpacingBefore => _before;

        /// <summary>The structure element the rendered lines were tagged with (tagged documents).</summary>
        public Tagging.PdfStructureElement? Element { get; private set; }

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
            Element = tag;
            var links = _lines.Take(count).Any(l => l.Pieces.Any(piece => piece.Run.Uri != null)) ? new LinkWriter(canvas, tag) : null;
            double y = top - _before;
            for (int i = 0; i < count; i++)
            {
                TextLayout.RenderLine(canvas, _p, _lines[i], x, y, _width, links);
                y -= _lines[i].Height;
            }
            if (tag != null) canvas.EndTag();
            links?.AddAnnotations();

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
