using System.Security.Cryptography;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf;

/// <summary>Text markup annotation types.</summary>
public enum TextMarkupType
{
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
}

/// <summary>Line ending styles of line annotations.</summary>
public enum LineEnding
{
    None,
    OpenArrow,
    ClosedArrow,
    Circle,
    Square,
}

/// <summary>Common properties of new annotations.</summary>
public sealed class AnnotationOptions
{
    /// <summary>Author (/T).</summary>
    public string? Author { get; init; }

    /// <summary>Comment text (/Contents), shown in the comment list and read by screen readers.</summary>
    public string? Contents { get; init; }

    /// <summary>Opacity 0..1 (/CA).</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>Lock the annotation against editing (flag Locked).</summary>
    public bool Locked { get; init; }
}

public sealed partial class PdfPage
{
    // ------------------------------------------------------------------ text markup

    /// <summary>Adds a text markup annotation (highlight, underline, strike-out, squiggly) over the given areas (e.g. from <see cref="FindText(string, StringComparison)"/>).</summary>
    public PdfAnnotation AddTextMarkup(TextMarkupType type, IEnumerable<PdfRectangle> areas, PdfColor? color = null, AnnotationOptions? options = null)
    {
        var rects = areas.ToList();
        if (rects.Count == 0) throw new ArgumentException("At least one area is required.", nameof(areas));
        var c = color ?? (type == TextMarkupType.Highlight ? PdfColor.Rgb(1, 0.92, 0.2) : PdfColor.Rgb(0.85, 0, 0));
        var bounds = Union(rects);
        var quads = new PdfArray();
        foreach (var r in rects)
            foreach (var v in new[] { r.Left, r.Top, r.Right, r.Top, r.Left, r.Bottom, r.Right, r.Bottom }) quads.Add(new PdfNumber(v));
        var dict = NewMarkup(type.ToString(), bounds, c, options);
        dict[PdfName.Get("QuadPoints")] = quads;
        SetAppearance(dict, bounds, canvas =>
        {
            foreach (var r in rects)
            {
                double h = r.Height;
                switch (type)
                {
                    case TextMarkupType.Highlight:
                        canvas.SaveState().SetOpacity(options?.Opacity ?? 1, null, "Multiply").SetFillColor(c).Rectangle(r).Fill().RestoreState();
                        break;
                    case TextMarkupType.Underline:
                        canvas.SetStrokeColor(c).SetLineWidth(Math.Max(0.5, h * 0.07)).MoveTo(r.Left, r.Bottom + h * 0.08).LineTo(r.Right, r.Bottom + h * 0.08).Stroke();
                        break;
                    case TextMarkupType.StrikeOut:
                        canvas.SetStrokeColor(c).SetLineWidth(Math.Max(0.5, h * 0.07)).MoveTo(r.Left, r.Bottom + h * 0.45).LineTo(r.Right, r.Bottom + h * 0.45).Stroke();
                        break;
                    default:
                        double step = Math.Max(1.5, h * 0.12), y = r.Bottom + step;
                        canvas.SetStrokeColor(c).SetLineWidth(Math.Max(0.4, h * 0.05)).MoveTo(r.Left, y);
                        bool up = false;
                        for (double x = r.Left + step; x <= r.Right; x += step, up = !up) canvas.LineTo(x, up ? y : y - step);
                        canvas.Stroke();
                        break;
                }
            }
        }, highlight: type == TextMarkupType.Highlight);
        return AddAnnotation(dict);
    }

    /// <summary>Highlights all occurrences of <paramref name="text"/> on the page; returns the created annotations.</summary>
    public IReadOnlyList<PdfAnnotation> HighlightText(string text, PdfColor? color = null, StringComparison comparison = StringComparison.Ordinal,
        TextMarkupType type = TextMarkupType.Highlight, AnnotationOptions? options = null) =>
        FindText(text, comparison).Select(r => AddTextMarkup(type, new[] { r }, color, options)).ToList();

    // ------------------------------------------------------------------ free text, stamps

    /// <summary>Adds a text box (FreeText annotation) displayed directly on the page.</summary>
    public PdfAnnotation AddFreeText(PdfRectangle rect, string text, PdfFont? font = null, double fontSize = 10, PdfColor? textColor = null,
        PdfColor? border = null, PdfColor? background = null, TextAlignment alignment = TextAlignment.Left, AnnotationOptions? options = null)
    {
        font ??= PdfStandardFont.Helvetica;
        var color = textColor ?? PdfColor.Black;
        var dict = NewMarkup("FreeText", rect, border ?? color, options, contents: text);
        dict[PdfName.DA] = new PdfString($"/Helv {PdfNumber.FormatReal(fontSize)} Tf {string.Join(' ', color.Components.Select(PdfNumber.FormatReal))} {(color.Components.Length == 1 ? "g" : color.Components.Length == 4 ? "k" : "rg")}");
        dict.Set(PdfName.Get("Q"), alignment switch { TextAlignment.Center => 1, TextAlignment.Right => 2, _ => 0 });
        if (border == null) dict[PdfName.BS] = new PdfDictionary { [PdfName.Get("W")] = new PdfNumber(0) };
        SetAppearance(dict, rect, canvas =>
        {
            if (background is { } bg) canvas.DrawRectangle(rect.Left, rect.Bottom, rect.Width, rect.Height, fill: bg);
            if (border is { } b) canvas.DrawRectangle(rect.Left + 0.5, rect.Bottom + 0.5, rect.Width - 1, rect.Height - 1, b, lineWidth: 1);
            var inner = new PdfRectangle(rect.Left + 3, rect.Bottom + 2, rect.Right - 3, rect.Top - 2);
            canvas.DrawTextBox(text, font, fontSize, inner, alignment, Layout.VerticalAlignment.Top, color);
        });
        return AddAnnotation(dict);
    }

    /// <summary>Adds a rubber stamp with text (e.g. "GENEHMIGT", "ENTWURF") framed by a rounded rectangle.</summary>
    /// <remarks>Pass an embedded font (<see cref="PdfTrueTypeFont"/>) for PDF/A or PDF/UA documents.</remarks>
    public PdfAnnotation AddStamp(PdfRectangle rect, string text, PdfColor? color = null, AnnotationOptions? options = null, PdfFont? font = null)
    {
        var c = color ?? PdfColor.Rgb(0.8, 0.1, 0.1);
        var dict = NewMarkup("Stamp", rect, c, options, contents: options?.Contents ?? text);
        dict[PdfName.Name] = PdfName.Get(new string(text.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } n ? n : "Stamp");
        font ??= PdfStandardFont.HelveticaBold;
        SetAppearance(dict, rect, canvas =>
        {
            double line = Math.Max(1, Math.Min(rect.Width, rect.Height) * 0.06);
            canvas.SaveState().SetStrokeColor(c).SetLineWidth(line)
                .RoundedRectangle(rect.Left + line, rect.Bottom + line, rect.Width - 2 * line, rect.Height - 2 * line, line * 3).Stroke().RestoreState();
            double size = Math.Min((rect.Height - 4 * line) * 0.7, (rect.Width - 6 * line) / Math.Max(1, font.GetTextWidth(text, 1)));
            canvas.DrawText(text, font, size, rect.Left + rect.Width / 2, rect.Bottom + (rect.Height - font.CapHeight * size / 1000) / 2, c, TextAlignment.Center);
        });
        return AddAnnotation(dict);
    }

    /// <summary>Adds an image stamp (e.g. a company seal or a scanned signature).</summary>
    public PdfAnnotation AddImageStamp(PdfRectangle rect, Images.PdfImage image, AnnotationOptions? options = null)
    {
        var dict = NewMarkup("Stamp", rect, null, options);
        dict[PdfName.Name] = PdfName.Get("Image");
        SetAppearance(dict, rect, canvas => canvas.DrawImage(image, rect));
        return AddAnnotation(dict);
    }

    // ------------------------------------------------------------------ drawing annotations

    /// <summary>Adds a freehand drawing (Ink annotation).</summary>
    public PdfAnnotation AddInk(IEnumerable<IEnumerable<(double X, double Y)>> strokes, PdfColor? color = null, double lineWidth = 1.5, AnnotationOptions? options = null)
    {
        var paths = strokes.Select(s => s.ToList()).Where(s => s.Count > 0).ToList();
        if (paths.Count == 0) throw new ArgumentException("At least one stroke is required.", nameof(strokes));
        var c = color ?? PdfColor.Rgb(0, 0, 0.55);
        double pad = lineWidth;
        var bounds = new PdfRectangle(paths.Min(p => p.Min(q => q.X)) - pad, paths.Min(p => p.Min(q => q.Y)) - pad,
            paths.Max(p => p.Max(q => q.X)) + pad, paths.Max(p => p.Max(q => q.Y)) + pad);
        var dict = NewMarkup("Ink", bounds, c, options);
        dict[PdfName.Get("InkList")] = new PdfArray(paths.Select(p => (PdfObject)PdfArray.FromNumbers(p.SelectMany(q => new[] { q.X, q.Y }).ToArray())));
        dict[PdfName.BS] = new PdfDictionary { [PdfName.Get("W")] = new PdfNumber(lineWidth) };
        SetAppearance(dict, bounds, canvas =>
        {
            canvas.SetStrokeColor(c).SetFillColor(c).SetLineWidth(lineWidth).SetLineCap(LineCap.Round).SetLineJoin(LineJoin.Round);
            foreach (var p in paths)
            {
                if (p.Count == 1)
                {
                    canvas.Circle(p[0].X, p[0].Y, lineWidth / 2).Fill();
                    continue;
                }
                canvas.MoveTo(p[0].X, p[0].Y);
                for (int i = 1; i < p.Count; i++) canvas.LineTo(p[i].X, p[i].Y);
                canvas.Stroke();
            }
        });
        return AddAnnotation(dict);
    }

    /// <summary>Adds a rectangle (Square annotation).</summary>
    public PdfAnnotation AddRectangleAnnotation(PdfRectangle rect, PdfColor? stroke = null, PdfColor? fill = null, double lineWidth = 1, AnnotationOptions? options = null) =>
        AddShape("Square", rect, stroke, fill, lineWidth, options);

    /// <summary>Adds an ellipse inscribed in <paramref name="rect"/> (Circle annotation).</summary>
    public PdfAnnotation AddEllipseAnnotation(PdfRectangle rect, PdfColor? stroke = null, PdfColor? fill = null, double lineWidth = 1, AnnotationOptions? options = null) =>
        AddShape("Circle", rect, stroke, fill, lineWidth, options);

    private PdfAnnotation AddShape(string subtype, PdfRectangle rect, PdfColor? stroke, PdfColor? fill, double lineWidth, AnnotationOptions? options)
    {
        var s = stroke ?? PdfColor.Rgb(0.85, 0, 0);
        var dict = NewMarkup(subtype, rect, s, options);
        dict[PdfName.BS] = new PdfDictionary { [PdfName.Get("W")] = new PdfNumber(lineWidth) };
        if (fill is { } f) dict[PdfName.Get("IC")] = PdfArray.FromNumbers(f.Components);
        double h = lineWidth / 2;
        SetAppearance(dict, rect, canvas =>
        {
            canvas.SetStrokeColor(s).SetLineWidth(lineWidth);
            if (fill is { } f2) canvas.SetFillColor(f2);
            if (subtype == "Square") canvas.Rectangle(rect.Left + h, rect.Bottom + h, rect.Width - lineWidth, rect.Height - lineWidth);
            else canvas.Ellipse(rect.Left + h, rect.Bottom + h, rect.Width - lineWidth, rect.Height - lineWidth);
            if (fill != null) canvas.FillAndStroke();
            else canvas.Stroke();
        });
        return AddAnnotation(dict);
    }

    /// <summary>Adds a line, optionally with arrow heads (Line annotation).</summary>
    public PdfAnnotation AddLineAnnotation(double x1, double y1, double x2, double y2, PdfColor? color = null, double lineWidth = 1,
        LineEnding start = LineEnding.None, LineEnding end = LineEnding.None, AnnotationOptions? options = null)
    {
        var c = color ?? PdfColor.Rgb(0.85, 0, 0);
        double size = Math.Max(6, lineWidth * 4);
        var bounds = new PdfRectangle(Math.Min(x1, x2) - size, Math.Min(y1, y2) - size, Math.Max(x1, x2) + size, Math.Max(y1, y2) + size);
        var dict = NewMarkup("Line", bounds, c, options);
        dict[PdfName.Get("L")] = PdfArray.FromNumbers(x1, y1, x2, y2);
        dict[PdfName.Get("LE")] = new PdfArray(PdfName.Get(start.ToString()), PdfName.Get(end.ToString()));
        dict[PdfName.BS] = new PdfDictionary { [PdfName.Get("W")] = new PdfNumber(lineWidth) };
        SetAppearance(dict, bounds, canvas =>
        {
            canvas.SetStrokeColor(c).SetFillColor(c).SetLineWidth(lineWidth).SetLineCap(LineCap.Butt).MoveTo(x1, y1).LineTo(x2, y2).Stroke();
            DrawEnding(canvas, start, x1, y1, x1 - x2, y1 - y2, size);
            DrawEnding(canvas, end, x2, y2, x2 - x1, y2 - y1, size);
        });
        return AddAnnotation(dict);
    }

    private static void DrawEnding(PdfCanvas canvas, LineEnding ending, double x, double y, double dx, double dy, double size)
    {
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (ending == LineEnding.None || len == 0) return;
        double ux = dx / len, uy = dy / len;     // pointing outwards
        double bx = x - ux * size, by = y - uy * size;
        double px = -uy * size / 2, py = ux * size / 2;
        switch (ending)
        {
            case LineEnding.OpenArrow:
                canvas.MoveTo(bx + px, by + py).LineTo(x, y).LineTo(bx - px, by - py).Stroke();
                break;
            case LineEnding.ClosedArrow:
                canvas.MoveTo(bx + px, by + py).LineTo(x, y).LineTo(bx - px, by - py).ClosePath().FillAndStroke();
                break;
            case LineEnding.Circle:
                canvas.Circle(x, y, size / 3).FillAndStroke();
                break;
            case LineEnding.Square:
                canvas.Rectangle(x - size / 3, y - size / 3, size * 2 / 3, size * 2 / 3).FillAndStroke();
                break;
        }
    }

    // ------------------------------------------------------------------ file attachment

    /// <summary>Adds a file attachment annotation (paperclip icon) at (x, y) with the embedded file.</summary>
    public PdfAnnotation AddFileAttachmentAnnotation(double x, double y, string fileName, byte[] data, string? mimeType = null,
        string? description = null, AnnotationOptions? options = null)
    {
        var rect = new PdfRectangle(x, y - 20, x + 14, y);
        var dict = NewMarkup("FileAttachment", rect, PdfColor.Rgb(0.2, 0.2, 0.6), options, contents: options?.Contents ?? description ?? fileName);
        dict[PdfName.Name] = PdfName.Get("Paperclip");
        var stream = new PdfStream(data, compress: true);
        stream[PdfName.Type] = PdfName.Get("EmbeddedFile");
        stream[PdfName.Subtype] = PdfName.Get(mimeType ?? "application/octet-stream");
        var parameters = new PdfDictionary();
        parameters.Set(PdfName.Get("Size"), data.Length);
        parameters[PdfName.Get("ModDate")] = new PdfString(PdfDate.Format(DateTimeOffset.Now));
        parameters[PdfName.Get("CheckSum")] = new PdfString(MD5.HashData(data), hex: true);
        stream[PdfName.Get("Params")] = parameters;
        var streamRef = Document.AddObject(stream);
        var spec = new PdfDictionary(PdfName.Get("Filespec"));
        spec[PdfName.F] = new PdfString(System.Text.Encoding.ASCII.GetBytes(new string(fileName.Select(ch => ch is >= ' ' and < (char)127 ? ch : '_').ToArray())));
        spec.SetText(PdfName.Get("UF"), fileName);
        if (description != null) spec.SetText(PdfName.Get("Desc"), description);
        spec[PdfName.Get("AFRelationship")] = PdfName.Get("Supplement");
        spec[PdfName.Get("EF")] = new PdfDictionary { [PdfName.F] = streamRef, [PdfName.Get("UF")] = streamRef };
        var specRef = Document.AddObject(spec);
        dict[PdfName.Get("FS")] = specRef;
        dict[PdfName.Get("AF")] = new PdfArray(specRef);
        SetAppearance(dict, rect, canvas =>
        {
            // paperclip icon
            double cx = rect.Left + 7, w = 3.2;
            canvas.SetStrokeColor(PdfColor.Rgb(0.2, 0.2, 0.6)).SetLineWidth(1.2).SetLineCap(LineCap.Round)
                .MoveTo(cx - w / 2, rect.Bottom + 6).LineTo(cx - w / 2, rect.Top - 4)
                .CurveTo(cx - w / 2, rect.Top - 1, cx + w, rect.Top - 1, cx + w, rect.Top - 4)
                .LineTo(cx + w, rect.Bottom + 4)
                .CurveTo(cx + w, rect.Bottom + 0.5, cx - w * 1.5, rect.Bottom + 0.5, cx - w * 1.5, rect.Bottom + 4)
                .LineTo(cx - w * 1.5, rect.Top - 6).Stroke();
        });
        return AddAnnotation(dict);
    }

    // ------------------------------------------------------------------ flatten

    /// <summary>
    /// Draws the appearance of annotations into the page content and removes them (links and popups are removed
    /// without drawing). Form fields are not affected; use <see cref="Forms.PdfAcroForm.Flatten"/> for them.
    /// </summary>
    public int FlattenAnnotations(Func<PdfAnnotation, bool>? filter = null)
    {
        int count = 0;
        foreach (var annotation in Annotations.ToList())
        {
            if (annotation.Subtype == "Widget" || (filter != null && !filter(annotation))) continue;
            bool hidden = (annotation.Dictionary.GetInt(PdfName.F) & (1 | 2 | 32)) != 0;
            if (!hidden && annotation.Subtype is not ("Popup" or "Link")) Forms.PdfAcroForm.DrawAppearance(this, annotation.Dictionary);
            RemoveAnnotation(annotation);
            count++;
        }
        return count;
    }

    // ------------------------------------------------------------------ helpers

    private PdfDictionary NewMarkup(string subtype, PdfRectangle rect, PdfColor? color, AnnotationOptions? options, string? contents = null)
    {
        var dict = new PdfDictionary();
        dict[PdfName.Subtype] = PdfName.Get(subtype);
        dict[PdfName.Rect] = PdfArray.FromRectangle(rect);
        dict.Set(PdfName.F, options?.Locked == true ? 4 | 128 : 4); // print (+ locked)
        var now = new PdfString(PdfDate.Format(DateTimeOffset.Now));
        dict[PdfName.M] = now;
        dict[PdfName.Get("CreationDate")] = now.Clone();
        dict.SetText(PdfName.Get("NM"), Guid.NewGuid().ToString());
        if (color is { } c) dict[PdfName.C] = PdfArray.FromNumbers(c.Components);
        if (options?.Author != null) dict.SetText(PdfName.T, options.Author);
        var text = options?.Contents ?? contents;
        if (text != null) dict.SetText(PdfName.Contents, text);
        if (options != null && options.Opacity < 1) dict.Set(PdfName.Get("CA"), options.Opacity);
        return dict;
    }

    /// <summary>Creates the normal appearance; <paramref name="draw"/> uses page coordinates.</summary>
    private void SetAppearance(PdfDictionary annotation, PdfRectangle rect, Action<PdfCanvas> draw, bool highlight = false)
    {
        var xobject = new PdfFormXObject(Document, rect);
        using (var canvas = xobject.GetCanvas())
        {
            double opacity = annotation.GetDouble(PdfName.Get("CA"), 1);
            if (opacity < 1 && !highlight) canvas.SetOpacity(opacity);
            draw(canvas);
        }
        annotation[PdfName.AP] = new PdfDictionary { [PdfName.N] = xobject.Reference };
    }

    private static PdfRectangle Union(IEnumerable<PdfRectangle> rects)
    {
        var list = rects.ToList();
        return new PdfRectangle(list.Min(r => r.Left), list.Min(r => r.Bottom), list.Max(r => r.Right), list.Max(r => r.Top));
    }
}
