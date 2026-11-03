using System.Text;
using MiPdf.Fonts;
using MiPdf.Images;
using MiPdf.Objects;

namespace MiPdf.Graphics;

/// <summary>Shape of the ends of open stroked lines (PDF line cap style, operator J).</summary>
public enum LineCap
{
    /// <summary>The stroke is squared off at the endpoint.</summary>
    Butt = 0,
    /// <summary>A semicircle with the line width as diameter is drawn around the endpoint.</summary>
    Round = 1,
    /// <summary>The stroke extends half the line width beyond the endpoint and is squared off.</summary>
    Square = 2,
}

/// <summary>Shape of the corners of stroked paths (PDF line join style, operator j).</summary>
public enum LineJoin
{
    /// <summary>Sharp corners, beveled when the miter limit is exceeded.</summary>
    Miter = 0,
    /// <summary>Rounded corners.</summary>
    Round = 1,
    /// <summary>Corners cut off flat.</summary>
    Bevel = 2,
}

/// <summary>Horizontal alignment of text.</summary>
public enum TextAlignment
{
    /// <summary>Aligned to the left edge.</summary>
    Left,
    /// <summary>Centered.</summary>
    Center,
    /// <summary>Aligned to the right edge.</summary>
    Right,
    /// <summary>
    /// Justified to both edges, except the last line of a paragraph (layout engine and
    /// <see cref="PdfCanvas.DrawTextBox"/>); single-line methods such as <see cref="PdfCanvas.DrawText"/> treat it like
    /// <see cref="Left"/>. Form fields do not support it.
    /// </summary>
    Justify,
}

/// <summary>How glyphs are painted (PDF text rendering mode, operator Tr).</summary>
public enum TextRenderingMode
{
    /// <summary>Fill the glyph outlines (normal text).</summary>
    Fill = 0,
    /// <summary>Stroke the glyph outlines.</summary>
    Stroke = 1,
    /// <summary>Fill, then stroke the glyph outlines.</summary>
    FillStroke = 2,
    /// <summary>Neither fill nor stroke: invisible but extractable text (e.g. an OCR layer).</summary>
    Invisible = 3,
    /// <summary>Fill and add the glyphs to the clipping path.</summary>
    FillClip = 4,
    /// <summary>Stroke and add the glyphs to the clipping path.</summary>
    StrokeClip = 5,
    /// <summary>Fill, stroke and add the glyphs to the clipping path.</summary>
    FillStrokeClip = 6,
    /// <summary>Only add the glyphs to the clipping path.</summary>
    Clip = 7,
}

/// <summary>
/// Writes page or XObject content: paths, colors, text, images. Coordinates are in PDF user space
/// (points, origin at the bottom-left of the page). The content is stored when the canvas is disposed
/// (or <see cref="Flush"/> is called).
/// </summary>
/// <example>
/// <code>
/// using (var canvas = page.GetCanvas())
/// {
///     canvas.DrawText("Hello", PdfStandardFont.Helvetica, 12, 72, 770);
///     canvas.SetStrokeColor(PdfColor.Red).Rectangle(72, 700, 200, 50).Stroke();
/// }
/// </code>
/// </example>
public sealed class PdfCanvas : IDisposable
{
    private readonly StringBuilder _content = new();
    private readonly PdfPage? _page;
    private readonly PdfFormXObject? _xobject;
    private readonly bool _background;
    private FontResource? _currentFont;
    private PdfName? _currentFontName;
    private double _currentFontSize;
    private readonly Stack<(FontResource?, PdfName?, double)> _savedFonts = new();
    private bool _flushed;

    internal PdfCanvas(PdfPage page, bool background)
    {
        _page = page;
        _background = background;
        Target = page;
    }

    internal PdfCanvas(PdfFormXObject xobject)
    {
        _xobject = xobject;
        Target = xobject;
    }

    /// <summary>The page or XObject this canvas draws into.</summary>
    public IResourceContainer Target { get; }

    /// <summary>The document of <see cref="Target"/>.</summary>
    public PdfDocument Document => Target.Document;

    // ------------------------------------------------------------------ low level

    /// <summary>Writes raw content stream operators.</summary>
    public PdfCanvas WriteRaw(string operators)
    {
        _content.Append(operators);
        if (operators.Length > 0 && operators[^1] != '\n') _content.Append('\n');
        return this;
    }

    private PdfCanvas Op(string op, params double[] operands)
    {
        foreach (var d in operands) _content.Append(PdfNumber.FormatReal(d)).Append(' ');
        _content.Append(op).Append('\n');
        return this;
    }

    // ------------------------------------------------------------------ graphics state

    /// <summary>Saves the current graphics state (q). Pair every call with <see cref="RestoreState"/>.</summary>
    public PdfCanvas SaveState()
    {
        _savedFonts.Push((_currentFont, _currentFontName, _currentFontSize));
        return WriteRaw("q");
    }

    /// <summary>Restores the graphics state saved by the matching <see cref="SaveState"/> (Q), including the current
    /// font.</summary>
    public PdfCanvas RestoreState()
    {
        if (_savedFonts.TryPop(out var saved)) (_currentFont, _currentFontName, _currentFontSize) = saved;
        else (_currentFont, _currentFontName) = (null, null);
        return WriteRaw("Q");
    }

    /// <summary>Concatenates a transformation matrix (cm).</summary>
    public PdfCanvas Transform(double a, double b, double c, double d, double e, double f) => Op("cm", a, b, c, d, e, f);

    /// <summary>Moves the origin of the coordinate system by (<paramref name="x"/>, <paramref name="y"/>) points (cm).</summary>
    public PdfCanvas Translate(double x, double y) => Transform(1, 0, 0, 1, x, y);

    /// <summary>Scales the coordinate system by <paramref name="sx"/> horizontally and <paramref name="sy"/> vertically (cm).</summary>
    public PdfCanvas Scale(double sx, double sy) => Transform(sx, 0, 0, sy, 0, 0);

    /// <summary>Rotates counter-clockwise by <paramref name="degrees"/> around (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    public PdfCanvas Rotate(double degrees, double cx = 0, double cy = 0)
    {
        double rad = degrees * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        if (cx != 0 || cy != 0) Translate(cx, cy);
        Transform(cos, sin, -sin, cos, 0, 0);
        if (cx != 0 || cy != 0) Translate(-cx, -cy);
        return this;
    }

    /// <summary>Sets the line width in points for stroking (w).</summary>
    public PdfCanvas SetLineWidth(double width) => Op("w", width);

    /// <summary>Sets the line cap style (J).</summary>
    public PdfCanvas SetLineCap(LineCap cap) => Op("J", (int)cap);

    /// <summary>Sets the line join style (j).</summary>
    public PdfCanvas SetLineJoin(LineJoin join) => Op("j", (int)join);

    /// <summary>Sets the miter limit (M): the maximum ratio of miter length to line width before a miter join is beveled.</summary>
    public PdfCanvas SetMiterLimit(double limit) => Op("M", limit);

    /// <summary>
    /// Sets a dash pattern (d) starting at its beginning: every argument is a length in points, alternating dash and
    /// gap. <c>SetDashPattern(3, 2)</c> draws 3 on / 2 off. To start elsewhere in the pattern use
    /// <see cref="SetDashPattern(double[], double)"/>.
    /// </summary>
    public PdfCanvas SetDashPattern(params double[] pattern) => SetDashPattern(pattern, 0);

    /// <summary>
    /// Sets a dash pattern (d) with a phase: <paramref name="pattern"/> holds the alternating dash and gap lengths in
    /// points; <paramref name="phase"/> is the distance into the pattern at which the dashing starts. Unlike
    /// <c>SetDashPattern(3, 2)</c>, <c>SetDashPattern(new[] { 3.0 }, 2)</c> is the pattern [3] (3 on / 3 off) started
    /// 2 points into it.
    /// </summary>
    public PdfCanvas SetDashPattern(double[] pattern, double phase)
    {
        _content.Append('[').Append(string.Join(' ', pattern.Select(PdfNumber.FormatReal))).Append("] ")
            .Append(PdfNumber.FormatReal(phase)).Append(" d\n");
        return this;
    }

    /// <summary>Resets the dash pattern to a solid line.</summary>
    public PdfCanvas SetSolidLine() => WriteRaw("[] 0 d");

    /// <summary>Sets the color for filling paths and text (g, rg or k).</summary>
    public PdfCanvas SetFillColor(PdfColor color) => Op(color.GetOperator(false), color.Components);

    /// <summary>Sets the color for stroking paths (G, RG or K).</summary>
    public PdfCanvas SetStrokeColor(PdfColor color) => Op(color.GetOperator(true), color.Components);

    /// <summary>Sets constant opacity (0 = transparent, 1 = opaque) for fill and stroke operations.</summary>
    public PdfCanvas SetOpacity(double fillOpacity, double? strokeOpacity = null, string? blendMode = null)
    {
        var gs = Document.GetGraphicsState(fillOpacity, strokeOpacity ?? fillOpacity, blendMode);
        var name = Target.AddResource(PdfName.ExtGState, gs, "GS");
        return Name(name).WriteRaw("gs");
    }

    private PdfCanvas Name(PdfName name)
    {
        var ms = new MemoryStream();
        IO.PdfObjectSerializer.WriteName(name, new IO.PdfOutput(ms));
        _content.Append(Encoding.Latin1.GetString(ms.ToArray())).Append(' ');
        return this;
    }

    // ------------------------------------------------------------------ paths

    /// <summary>Begins a new subpath at (<paramref name="x"/>, <paramref name="y"/>) (m).</summary>
    public PdfCanvas MoveTo(double x, double y) => Op("m", x, y);

    /// <summary>Appends a straight line from the current point to (<paramref name="x"/>, <paramref name="y"/>) (l).</summary>
    public PdfCanvas LineTo(double x, double y) => Op("l", x, y);

    /// <summary>
    /// Appends a cubic Bezier curve from the current point to (<paramref name="x3"/>, <paramref name="y3"/>)
    /// with the control points (<paramref name="x1"/>, <paramref name="y1"/>) and (<paramref name="x2"/>, <paramref name="y2"/>) (c).
    /// </summary>
    public PdfCanvas CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) => Op("c", x1, y1, x2, y2, x3, y3);

    /// <summary>Closes the current subpath with a straight line to its start point (h).</summary>
    public PdfCanvas ClosePath() => WriteRaw("h");

    /// <summary>Appends a rectangle with lower-left corner (<paramref name="x"/>, <paramref name="y"/>) to the path (re).</summary>
    public PdfCanvas Rectangle(double x, double y, double width, double height) => Op("re", x, y, width, height);

    /// <summary>Appends the rectangle <paramref name="r"/> to the path (re).</summary>
    public PdfCanvas Rectangle(PdfRectangle r) => Rectangle(r.Left, r.Bottom, r.Width, r.Height);

    /// <summary>
    /// Appends a closed rectangle with rounded corners to the path. The <paramref name="radius"/> is limited to half the
    /// smaller side; a radius of 0 or less gives a plain rectangle.
    /// </summary>
    public PdfCanvas RoundedRectangle(double x, double y, double width, double height, double radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        if (radius <= 0) return Rectangle(x, y, width, height);
        const double k = 0.5523; // bezier approximation of a quarter circle
        double kr = radius * k;
        MoveTo(x + radius, y);
        LineTo(x + width - radius, y);
        CurveTo(x + width - radius + kr, y, x + width, y + radius - kr, x + width, y + radius);
        LineTo(x + width, y + height - radius);
        CurveTo(x + width, y + height - radius + kr, x + width - radius + kr, y + height, x + width - radius, y + height);
        LineTo(x + radius, y + height);
        CurveTo(x + radius - kr, y + height, x, y + height - radius + kr, x, y + height - radius);
        LineTo(x, y + radius);
        CurveTo(x, y + radius - kr, x + radius - kr, y, x + radius, y);
        return ClosePath();
    }

    /// <summary>Adds an ellipse inscribed in the given rectangle.</summary>
    public PdfCanvas Ellipse(double x, double y, double width, double height)
    {
        const double k = 0.5523;
        double rx = width / 2, ry = height / 2, cx = x + rx, cy = y + ry, kx = rx * k, ky = ry * k;
        MoveTo(cx + rx, cy);
        CurveTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
        CurveTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
        CurveTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
        CurveTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
        return ClosePath();
    }

    /// <summary>Appends a closed circle around (<paramref name="centerX"/>, <paramref name="centerY"/>) to the path.</summary>
    public PdfCanvas Circle(double centerX, double centerY, double radius) =>
        Ellipse(centerX - radius, centerY - radius, 2 * radius, 2 * radius);

    /// <summary>Strokes the current path with the stroke color and ends it (S).</summary>
    public PdfCanvas Stroke() => WriteRaw("S");

    /// <summary>Fills the current path with the fill color and ends it (f, or f* with the even-odd rule).</summary>
    public PdfCanvas Fill(bool evenOdd = false) => WriteRaw(evenOdd ? "f*" : "f");

    /// <summary>Fills, then strokes the current path and ends it (B, or B* with the even-odd rule).</summary>
    public PdfCanvas FillAndStroke(bool evenOdd = false) => WriteRaw(evenOdd ? "B*" : "B");

    /// <summary>Intersects the clipping path with the current path and ends the path.</summary>
    public PdfCanvas Clip(bool evenOdd = false) => WriteRaw(evenOdd ? "W* n" : "W n");

    /// <summary>Ends the current path without painting it (n).</summary>
    public PdfCanvas EndPath() => WriteRaw("n");

    // ------------------------------------------------------------------ convenience shapes

    /// <summary>
    /// Draws a straight line from (<paramref name="x1"/>, <paramref name="y1"/>) to (<paramref name="x2"/>, <paramref name="y2"/>)
    /// with the given color (default: the current stroke color) and width in points. The graphics state is restored afterwards.
    /// </summary>
    public PdfCanvas DrawLine(double x1, double y1, double x2, double y2, PdfColor? color = null, double lineWidth = 1)
    {
        SaveState();
        if (color is { } c) SetStrokeColor(c);
        SetLineWidth(lineWidth).MoveTo(x1, y1).LineTo(x2, y2).Stroke();
        return RestoreState();
    }

    /// <summary>Draws a rectangle with an optional border and/or fill.</summary>
    public PdfCanvas DrawRectangle(double x, double y, double width, double height, PdfColor? stroke = null, PdfColor? fill = null, double lineWidth = 1)
    {
        if (stroke == null && fill == null) stroke = PdfColor.Black;
        SaveState();
        if (fill is { } f) SetFillColor(f);
        if (stroke is { } s) SetStrokeColor(s).SetLineWidth(lineWidth);
        Rectangle(x, y, width, height);
        if (stroke != null && fill != null) FillAndStroke();
        else if (fill != null) Fill();
        else Stroke();
        return RestoreState();
    }

    // ------------------------------------------------------------------ text

    /// <summary>Begins a text object (BT). Text operators are only valid between this and <see cref="EndText"/>.</summary>
    public PdfCanvas BeginText() => WriteRaw("BT");

    /// <summary>Ends the text object started with <see cref="BeginText"/> (ET).</summary>
    public PdfCanvas EndText() => WriteRaw("ET");

    /// <summary>Selects the font (Tf). Fonts are registered as page resources automatically.</summary>
    public PdfCanvas SetFont(PdfFont font, double size)
    {
        _currentFont = Document.GetFontResource(font);
        _currentFontName = Target.AddResource(PdfName.Font, _currentFont.Reference, "F");
        _currentFontSize = size;
        Name(_currentFontName);
        return Op("Tf", size);
    }

    /// <summary>Sets the text matrix so that the next text starts at (x, y).</summary>
    public PdfCanvas SetTextPosition(double x, double y) => Op("Tm", 1, 0, 0, 1, x, y);

    /// <summary>Sets the text matrix and the text line matrix to [a b c d e f] (Tm).</summary>
    public PdfCanvas SetTextMatrix(double a, double b, double c, double d, double e, double f) => Op("Tm", a, b, c, d, e, f);

    /// <summary>Moves to the start of the next line offset by (tx, ty) (Td).</summary>
    public PdfCanvas MoveText(double tx, double ty) => Op("Td", tx, ty);

    /// <summary>Sets the leading (TL): the distance between baselines used by <see cref="NewLine"/>, in unscaled text space units.</summary>
    public PdfCanvas SetLeading(double leading) => Op("TL", leading);

    /// <summary>Moves to the start of the next line, using the current leading (T*).</summary>
    public PdfCanvas NewLine() => WriteRaw("T*");

    /// <summary>Sets extra spacing added after every character (Tc), in unscaled text space units.</summary>
    public PdfCanvas SetCharacterSpacing(double spacing) => Op("Tc", spacing);

    /// <summary>Word spacing (Tw). Note: only effective for single-byte fonts (standard fonts).</summary>
    public PdfCanvas SetWordSpacing(double spacing) => Op("Tw", spacing);

    /// <summary>Sets the horizontal scaling of glyphs in percent (Tz); 100 is normal width.</summary>
    public PdfCanvas SetHorizontalScaling(double percent) => Op("Tz", percent);

    /// <summary>Sets the text rise (Ts): a vertical baseline shift, positive upwards (e.g. for superscripts).</summary>
    public PdfCanvas SetTextRise(double rise) => Op("Ts", rise);

    /// <summary>Sets how glyphs are painted (Tr), e.g. <see cref="TextRenderingMode.Invisible"/> for an OCR text layer.</summary>
    public PdfCanvas SetTextRenderingMode(TextRenderingMode mode) => Op("Tr", (int)mode);

    /// <summary>Shows text with the current font (Tj).</summary>
    public PdfCanvas ShowText(string text)
    {
        if (_currentFont == null) throw new InvalidOperationException("SetFont must be called before ShowText.");
        AppendString(_currentFont.Encode(text), _currentFont.IsMultiByte);
        _content.Append(" Tj\n");
        return this;
    }

    /// <summary>
    /// Shows text where each space is widened by <paramref name="extraWordSpace"/> points (TJ). Works with all fonts.
    /// With <paramref name="kerning"/> the font's pair kerning is applied.
    /// </summary>
    public PdfCanvas ShowTextWithWordSpacing(string text, double extraWordSpace, bool kerning = false)
    {
        if (_currentFont == null) throw new InvalidOperationException("SetFont must be called before ShowText.");
        var font = _currentFont.Font;
        bool kern = kerning && font.HasKerning;
        if ((extraWordSpace == 0 || _currentFontSize == 0) && !kern) return ShowText(text);
        double spaceAdjust = _currentFontSize == 0 ? 0 : -extraWordSpace * 1000 / _currentFontSize;
        _content.Append('[');
        var segment = new StringBuilder();
        void Flush()
        {
            if (segment.Length == 0) return;
            AppendString(_currentFont!.Encode(segment.ToString()), _currentFont.IsMultiByte);
            segment.Clear();
        }
        int previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            double adjust = 0;
            if (kern && previous >= 0 && previous != ' ' && rune.Value != ' ') adjust -= font.GetKerning(previous, rune.Value);
            if (previous == ' ') adjust += spaceAdjust;
            if (Math.Abs(adjust) > 0.01)
            {
                Flush();
                _content.Append(' ').Append(PdfNumber.FormatReal(Math.Round(adjust, 2))).Append(' ');
            }
            segment.Append(rune.ToString());
            previous = rune.Value;
        }
        Flush();
        _content.Append("] TJ\n");
        return this;
    }

    private void AppendString(byte[] bytes, bool hex)
    {
        if (hex)
        {
            _content.Append('<').Append(Convert.ToHexString(bytes)).Append('>');
            return;
        }
        _content.Append('(');
        foreach (byte b in bytes)
        {
            switch (b)
            {
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    _content.Append('\\').Append((char)b);
                    break;
                case < 32 or > 126:
                    _content.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    _content.Append((char)b);
                    break;
            }
        }
        _content.Append(')');
    }

    /// <summary>
    /// Draws a single line of text. (<paramref name="x"/>, <paramref name="y"/>) is the baseline point;
    /// the alignment determines whether x is the left edge, center or right edge of the text.
    /// </summary>
    public PdfCanvas DrawText(string text, PdfFont font, double fontSize, double x, double y,
        PdfColor? color = null, TextAlignment alignment = TextAlignment.Left, bool kerning = false)
    {
        double width = font.GetTextWidth(text, fontSize, kerning);
        double startX = alignment switch
        {
            TextAlignment.Center => x - width / 2,
            TextAlignment.Right => x - width,
            _ => x,
        };
        SaveState();
        if (color is { } c) SetFillColor(c);
        BeginText().SetFont(font, fontSize).SetTextPosition(startX, y).ShowTextWithWordSpacing(text, 0, kerning).EndText();
        return RestoreState();
    }

    /// <summary>
    /// Draws text wrapped into <paramref name="box"/> with horizontal and vertical alignment. Line breaks ("\n") are
    /// honored; text that does not fit is clipped at the box. <see cref="TextAlignment.Justify"/> stretches every line
    /// except the last one of a paragraph to the width of the box. Returns the canvas.
    /// </summary>
    public PdfCanvas DrawTextBox(string text, PdfFont font, double fontSize, PdfRectangle box, TextAlignment alignment = TextAlignment.Left,
        Layout.VerticalAlignment verticalAlignment = Layout.VerticalAlignment.Top, PdfColor? color = null, double lineSpacing = 1.15)
    {
        var lines = Layout.TextLayout.BreakLines(new Layout.Paragraph(text, font, fontSize), box.Width);
        double lineHeight = font.GetLineHeight(fontSize) * lineSpacing;
        double total = lines.Count * lineHeight;
        double offset = verticalAlignment switch
        {
            Layout.VerticalAlignment.Middle => Math.Max(0, (box.Height - total) / 2),
            Layout.VerticalAlignment.Bottom => Math.Max(0, box.Height - total),
            _ => 0,
        };
        double halfLeading = (lineHeight - font.GetLineHeight(fontSize)) / 2;
        double baseline = box.Top - offset - halfLeading - font.GetAscent(fontSize);
        SaveState().Rectangle(box).Clip();
        if (color is { } c) SetFillColor(c);
        for (int i = 0; i < lines.Count; i++)
        {
            string line = string.Concat(lines[i].Pieces.Select(p => p.Text)).TrimEnd();
            if (i > 0 && baseline + font.GetDescent(fontSize) < box.Bottom - 0.01) break; // only complete lines
            double width = font.GetTextWidth(line, fontSize);
            double x = alignment switch
            {
                TextAlignment.Center => box.Left + (box.Width - width) / 2,
                TextAlignment.Right => box.Right - width,
                _ => box.Left,
            };
            int spaces = alignment == TextAlignment.Justify && !lines[i].EndsParagraph ? line.Count(ch => ch == ' ') : 0;
            double extraPerSpace = spaces > 0 ? (box.Width - width) / spaces : 0;
            BeginText().SetFont(font, fontSize).SetTextPosition(x, baseline).ShowTextWithWordSpacing(line, extraPerSpace).EndText();
            baseline -= lineHeight;
        }
        return RestoreState();
    }

    // ------------------------------------------------------------------ images and XObjects

    /// <summary>Draws an image into the rectangle (x, y, width, height).</summary>
    public PdfCanvas DrawImage(PdfImage image, double x, double y, double width, double height)
    {
        var reference = Document.GetImageReference(image);
        var name = Target.AddResource(PdfName.XObject, reference, "Im");
        SaveState().Transform(width, 0, 0, height, x, y);
        Name(name).WriteRaw("Do");
        return RestoreState();
    }

    /// <summary>Draws an image scaled to fit into <paramref name="box"/>, keeping its aspect ratio and centering it.</summary>
    public PdfCanvas DrawImage(PdfImage image, PdfRectangle box)
    {
        double scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
        double w = image.Width * scale, h = image.Height * scale;
        return DrawImage(image, box.Left + (box.Width - w) / 2, box.Bottom + (box.Height - h) / 2, w, h);
    }

    /// <summary>Draws a form XObject at (x, y), optionally scaled to the given size.</summary>
    public PdfCanvas DrawXObject(PdfFormXObject xobject, double x, double y, double? width = null, double? height = null)
    {
        var name = Target.AddResource(PdfName.XObject, xobject.Reference, "Fm");
        var bbox = xobject.BBox;
        double sx = width.HasValue && bbox.Width != 0 ? width.Value / bbox.Width : 1;
        double sy = height.HasValue && bbox.Height != 0 ? height.Value / bbox.Height : 1;
        SaveState().Transform(sx, 0, 0, sy, x - bbox.Left * sx, y - bbox.Bottom * sy);
        Name(name).WriteRaw("Do");
        return RestoreState();
    }

    // ------------------------------------------------------------------ tagged content

    /// <summary>True if this is a page canvas of a document with tagging enabled (see <see cref="PdfDocument.EnableTagging"/>).</summary>
    public bool IsTagged => _page != null && Document.StructureTree != null;

    /// <summary>The structure element that layout elements attach their tags to (set by <see cref="Layout.LayoutDocument"/>).</summary>
    public Tagging.PdfStructureElement? StructureParent { get; set; }

    /// <summary>
    /// Starts a marked-content sequence that belongs to <paramref name="element"/> (BDC with MCID).
    /// Everything drawn until <see cref="EndTag"/> becomes the content of that element.
    /// </summary>
    public PdfCanvas BeginTag(Tagging.PdfStructureElement element)
    {
        if (_page == null) throw new InvalidOperationException("Tagged content is only supported on page canvases.");
        var tree = Document.StructureTree ?? throw new InvalidOperationException("Call PdfDocument.EnableTagging() first.");
        int mcid = tree.AddMarkedContent(element, _page);
        Name(PdfName.Get(element.Type));
        _content.Append("<</MCID ").Append(mcid).Append(">> BDC\n");
        return this;
    }

    /// <summary>Ends the marked-content sequence started with <see cref="BeginTag"/> (EMC).</summary>
    public PdfCanvas EndTag() => WriteRaw("EMC");

    /// <summary>Draws the content produced by <paramref name="draw"/> as content of <paramref name="element"/>.</summary>
    public PdfCanvas Tag(Tagging.PdfStructureElement element, Action<PdfCanvas> draw)
    {
        BeginTag(element);
        draw(this);
        return EndTag();
    }

    /// <summary>
    /// Starts an artifact: content that is not part of the document's logical content (page numbers, running heads,
    /// decorative lines, backgrounds). Screen readers skip artifacts.
    /// </summary>
    public PdfCanvas BeginArtifact(Tagging.ArtifactType type = Tagging.ArtifactType.None)
    {
        if (type == Tagging.ArtifactType.None) return WriteRaw("/Artifact BMC");
        return WriteRaw($"/Artifact <</Type /{type}>> BDC");
    }

    /// <summary>Ends the artifact started with <see cref="BeginArtifact"/> (EMC).</summary>
    public PdfCanvas EndArtifact() => WriteRaw("EMC");

    /// <summary>Draws the content produced by <paramref name="draw"/> as an artifact.</summary>
    public PdfCanvas Artifact(Action<PdfCanvas> draw, Tagging.ArtifactType type = Tagging.ArtifactType.None)
    {
        BeginArtifact(type);
        draw(this);
        return EndArtifact();
    }

    // ------------------------------------------------------------------ output

    /// <summary>Returns the content written so far.</summary>
    public byte[] GetContentBytes() => Encoding.Latin1.GetBytes(_content.ToString());

    /// <summary>Stores the content in the page/XObject. Further drawing is not possible afterwards.</summary>
    public void Flush()
    {
        if (_flushed) return;
        _flushed = true;
        var bytes = GetContentBytes();
        if (_page != null)
        {
            if (bytes.Length == 0) return;
            if (_background) _page.PrependContent(bytes);
            else _page.AppendContent(bytes);
        }
        else if (_xobject != null)
        {
            var existing = _xobject.GetContent();
            _xobject.SetContent(existing.Length == 0 ? bytes : [.. existing, (byte)'\n', .. bytes]);
        }
    }

    /// <summary>Stores the content by calling <see cref="Flush"/>.</summary>
    public void Dispose() => Flush();
}
