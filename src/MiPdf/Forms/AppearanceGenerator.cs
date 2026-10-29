using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.IO;
using MiPdf.Layout;
using MiPdf.Objects;

namespace MiPdf.Forms;

/// <summary>Parsed default appearance string (/DA), e.g. "/Helv 10 Tf 0 0 1 rg".</summary>
internal sealed record DefaultAppearance(string FontName, double FontSize, PdfColor Color)
{
    public static DefaultAppearance Parse(string da)
    {
        string font = "Helv";
        double size = 0;
        var color = PdfColor.Black;
        var operands = new List<double>();
        string? lastName = null;
        var lexer = new PdfLexer(System.Text.Encoding.Latin1.GetBytes(da));
        while (lexer.Next())
        {
            switch (lexer.Kind)
            {
                case TokenKind.Name:
                    lastName = lexer.Text;
                    break;
                case TokenKind.Integer:
                case TokenKind.Real:
                    operands.Add(lexer.RealValue);
                    break;
                case TokenKind.Keyword:
                    switch (lexer.Text)
                    {
                        case "Tf":
                            if (lastName != null) font = lastName;
                            if (operands.Count > 0) size = operands[^1];
                            break;
                        case "g" when operands.Count >= 1:
                            color = PdfColor.Gray(operands[^1]);
                            break;
                        case "rg" when operands.Count >= 3:
                            color = PdfColor.Rgb(operands[^3], operands[^2], operands[^1]);
                            break;
                        case "k" when operands.Count >= 4:
                            color = PdfColor.Cmyk(operands[^4], operands[^3], operands[^2], operands[^1]);
                            break;
                    }
                    operands.Clear();
                    break;
            }
        }
        return new DefaultAppearance(font, size, color);
    }
}

/// <summary>Generates appearance streams for form fields.</summary>
internal static class AppearanceGenerator
{
    private const double Padding = 2;

    private static PdfColor? ReadColor(PdfDictionary? mk, string key)
    {
        if (mk?.GetArray(PdfName.Get(key)) is not { } a) return null;
        return a.Count switch
        {
            1 => PdfColor.Gray(a.GetDouble(0)),
            3 => PdfColor.Rgb(a.GetDouble(0), a.GetDouble(1), a.GetDouble(2)),
            4 => PdfColor.Cmyk(a.GetDouble(0), a.GetDouble(1), a.GetDouble(2), a.GetDouble(3)),
            _ => null,
        };
    }

    /// <summary>Selects the font for a field: explicit font, form font, standard font named in /DA or /DR, Helvetica.</summary>
    private static PdfFont ResolveFont(PdfFormField field, string daFont)
    {
        if (field.AppearanceFont != null) return field.AppearanceFont;
        if (field.Form.AppearanceFont != null) return field.Form.AppearanceFont;
        string? baseFont = field.Form.Dictionary.GetDictionary(PdfName.DR)?.GetDictionary(PdfName.Font)?.GetDictionary(PdfName.Get(daFont))?.GetName(PdfName.BaseFont)?.Value;
        foreach (var candidate in new[] { baseFont, daFont })
        {
            switch (candidate)
            {
                case "Helv": return PdfStandardFont.Helvetica;
                case "HeBo": return PdfStandardFont.HelveticaBold;
                case "TiRo": return PdfStandardFont.TimesRoman;
                case "TiBo": return PdfStandardFont.TimesBold;
                case "Cour": return PdfStandardFont.Courier;
                case "CoBo": return PdfStandardFont.CourierBold;
                case "ZaDb": return PdfStandardFont.ZapfDingbats;
                case "Symb": return PdfStandardFont.Symbol;
            }
            if (candidate != null && StandardFontMetrics.IsStandardFont(candidate)) return PdfStandardFont.FromName(candidate);
        }
        return PdfStandardFont.Helvetica;
    }

    /// <summary>Creates the appearance XObject honoring the widget rotation (/MK /R). Returns layout width/height.</summary>
    private static PdfFormXObject CreateAppearance(PdfFormField field, PdfWidget widget, out double width, out double height)
    {
        var rect = widget.Rect;
        int rotation = ((widget.Dictionary.GetDictionary(PdfName.MK)?.GetInt(PdfName.Get("R")) ?? 0) % 360 + 360) % 360;
        bool swap = rotation is 90 or 270;
        width = swap ? rect.Height : rect.Width;
        height = swap ? rect.Width : rect.Height;
        var xobject = new PdfFormXObject(field.Form.Document, new PdfRectangle(0, 0, width, height));
        xobject.Matrix = rotation switch
        {
            90 => new double[] { 0, 1, -1, 0, 0, 0 },
            180 => new double[] { -1, 0, 0, -1, 0, 0 },
            270 => new double[] { 0, -1, 1, 0, 0, 0 },
            _ => null,
        };
        return xobject;
    }

    private static void DrawBackgroundAndBorder(PdfCanvas canvas, PdfDictionary widget, double w, double h)
    {
        var mk = widget.GetDictionary(PdfName.MK);
        var bg = ReadColor(mk, "BG");
        var bc = ReadColor(mk, "BC");
        double borderWidth = widget.GetDictionary(PdfName.BS)?.GetDouble(PdfName.W, 1) ?? 1;
        if (bg is { } background) canvas.DrawRectangle(0, 0, w, h, fill: background);
        if (bc is { } border && borderWidth > 0)
            canvas.DrawRectangle(borderWidth / 2, borderWidth / 2, w - borderWidth, h - borderWidth, border, lineWidth: borderWidth);
    }

    private static void SetNormalAppearance(PdfWidget widget, PdfFormXObject xobject)
    {
        var ap = new PdfDictionary { [PdfName.N] = xobject.Reference };
        widget.Dictionary[PdfName.AP] = ap;
    }

    public static void TextField(PdfTextField field, PdfWidget widget)
    {
        var da = DefaultAppearance.Parse(field.DefaultAppearance);
        var font = ResolveFont(field, da.FontName);
        string text = field.Value ?? "";
        if (field.Password) text = new string('*', text.Length);

        var xobject = CreateAppearance(field, widget, out double w, out double h);
        using (var canvas = xobject.GetCanvas())
        {
            DrawBackgroundAndBorder(canvas, widget.Dictionary, w, h);
            canvas.WriteRaw("/Tx BMC").SaveState().Rectangle(1, 1, w - 2, h - 2).Clip();
            canvas.SetFillColor(da.Color);
            double innerWidth = w - 2 * Padding;
            if (field.Multiline)
            {
                double size = da.FontSize > 0 ? da.FontSize : AutoSizeMultiline(text, font, innerWidth, h - 2 * Padding);
                var lines = TextLayout.BreakLines(text, font, size, innerWidth);
                double lineHeight = font.GetLineHeight(size);
                double y = h - Padding - font.GetAscent(size);
                foreach (var line in lines)
                {
                    DrawLine(canvas, line, font, size, field.Alignment, innerWidth, y);
                    y -= lineHeight;
                }
            }
            else
            {
                double size = da.FontSize > 0 ? da.FontSize : AutoSizeSingleLine(text, font, innerWidth, h - 2 * Padding);
                double textHeight = (font.Ascent - font.Descent) * size / 1000;
                double baseline = (h - textHeight) / 2 - font.GetDescent(size);
                int maxLen = field.MaxLength;
                if (field.Comb && maxLen > 0)
                {
                    double cell = w / maxLen;
                    int i = 0;
                    foreach (var rune in text.EnumerateRunes())
                    {
                        var s = rune.ToString();
                        double cw = font.GetTextWidth(s, size);
                        canvas.BeginText().SetFont(font, size).SetTextPosition(i * cell + (cell - cw) / 2, baseline).ShowText(s).EndText();
                        if (++i >= maxLen) break;
                    }
                }
                else DrawLine(canvas, text, font, size, field.Alignment, innerWidth, baseline);
            }
            canvas.RestoreState().WriteRaw("EMC");
        }
        SetNormalAppearance(widget, xobject);
    }

    private static void DrawLine(PdfCanvas canvas, string line, PdfFont font, double size, TextAlignment alignment, double innerWidth, double baseline)
    {
        double tw = font.GetTextWidth(line, size);
        double x = alignment switch
        {
            TextAlignment.Center => Padding + (innerWidth - tw) / 2,
            TextAlignment.Right => Padding + innerWidth - tw,
            _ => Padding,
        };
        canvas.BeginText().SetFont(font, size).SetTextPosition(x, baseline).ShowText(line).EndText();
    }

    private static double AutoSizeSingleLine(string text, PdfFont font, double width, double height)
    {
        double size = height * 1000 / Math.Max(1, font.Ascent - font.Descent);
        double tw = font.GetTextWidth(text, 1);
        if (tw > 0) size = Math.Min(size, width / tw);
        return Math.Clamp(size, 4, Math.Max(4, Math.Min(size, 24)));
    }

    private static double AutoSizeMultiline(string text, PdfFont font, double width, double height)
    {
        for (double size = 12; size > 4; size -= 0.5)
        {
            int lines = TextLayout.BreakLines(text, font, size, width).Count;
            if (lines * font.GetLineHeight(size) <= height) return size;
        }
        return 4;
    }

    public static void ChoiceField(PdfChoiceField field, PdfWidget widget)
    {
        var da = DefaultAppearance.Parse(field.DefaultAppearance);
        var font = ResolveFont(field, da.FontName);
        var options = field.Options;
        var selected = field.SelectedValues;
        var xobject = CreateAppearance(field, widget, out double w, out double h);
        using (var canvas = xobject.GetCanvas())
        {
            DrawBackgroundAndBorder(canvas, widget.Dictionary, w, h);
            canvas.WriteRaw("/Tx BMC").SaveState().Rectangle(1, 1, w - 2, h - 2).Clip();
            if (field.IsComboBox)
            {
                string display = selected.Count == 0 ? "" :
                    options.FirstOrDefault(o => o.Export == selected[0]).Display ?? selected[0];
                if (string.IsNullOrEmpty(display) && selected.Count > 0) display = selected[0];
                double size = da.FontSize > 0 ? da.FontSize : AutoSizeSingleLine(display, font, w - 2 * Padding, h - 2 * Padding);
                double baseline = (h - (font.Ascent - font.Descent) * size / 1000) / 2 - font.GetDescent(size);
                canvas.SetFillColor(da.Color);
                DrawLine(canvas, display, font, size, TextAlignment.Left, w - 2 * Padding, baseline);
            }
            else
            {
                double size = da.FontSize > 0 ? da.FontSize : 10;
                double lineHeight = font.GetLineHeight(size);
                double top = h - Padding;
                foreach (var (export, display) in options)
                {
                    if (top - lineHeight < 0) break;
                    if (selected.Contains(export))
                        canvas.DrawRectangle(1, top - lineHeight, w - 2, lineHeight, fill: PdfColor.Rgb(0.6, 0.75, 0.85));
                    canvas.SetFillColor(da.Color);
                    DrawLine(canvas, display, font, size, TextAlignment.Left, w - 2 * Padding, top - font.GetAscent(size));
                    top -= lineHeight;
                }
            }
            canvas.RestoreState().WriteRaw("EMC");
        }
        SetNormalAppearance(widget, xobject);
    }

    /// <summary>Creates "on" and "Off" appearances for a check box or radio button widget.</summary>
    public static void CheckBoxStates(PdfAcroForm form, PdfDictionary widget, string onState, char symbol)
    {
        var rect = widget.GetRectangle(PdfName.Rect) ?? new PdfRectangle(0, 0, 12, 12);
        double w = rect.Width, h = rect.Height;
        var font = PdfStandardFont.ZapfDingbats;
        PdfFormXObject Make(bool on)
        {
            var x = new PdfFormXObject(form.Document, new PdfRectangle(0, 0, w, h));
            using var canvas = x.GetCanvas();
            DrawBackgroundAndBorder(canvas, widget, w, h);
            if (on && DrawSymbol(canvas, symbol, w, h)) return x;
            if (on)
            {
                double size = Math.Min(w, h) * 0.8;
                string s = symbol.ToString();
                double tw = font.GetTextWidth(s, size);
                double baseline = (h - font.CapHeight * size / 1000) / 2;
                canvas.SetFillColor(PdfColor.Black).BeginText().SetFont(font, size).SetTextPosition((w - tw) / 2, baseline).ShowText(s).EndText();
            }
            return x;
        }
        var normal = new PdfDictionary();
        normal[PdfName.Get(onState)] = Make(true).Reference;
        normal[PdfName.Off] = Make(false).Reference;
        widget[PdfName.AP] = new PdfDictionary { [PdfName.N] = normal };
        widget[PdfName.AS] = PdfName.Off;
    }

    /// <summary>
    /// Draws the common ZapfDingbats check box symbols as vector graphics, so that no (non-embedded) font is needed
    /// (required for PDF/A and PDF/UA). Returns false for other symbols.
    /// </summary>
    private static bool DrawSymbol(PdfCanvas canvas, char symbol, double w, double h)
    {
        double s = Math.Min(w, h) * 0.7, x0 = (w - s) / 2, y0 = (h - s) / 2;
        double X(double fx) => x0 + fx * s;
        double Y(double fy) => y0 + fy * s;
        canvas.SaveState().SetFillColor(PdfColor.Black).SetStrokeColor(PdfColor.Black);
        switch (symbol)
        {
            case '4': // check mark
                canvas.SetLineWidth(s * 0.14).SetLineCap(LineCap.Round).SetLineJoin(LineJoin.Round)
                    .MoveTo(X(0.08), Y(0.5)).LineTo(X(0.38), Y(0.15)).LineTo(X(0.92), Y(0.85)).Stroke();
                break;
            case '8': // cross
                canvas.SetLineWidth(s * 0.14).SetLineCap(LineCap.Round)
                    .MoveTo(X(0.12), Y(0.12)).LineTo(X(0.88), Y(0.88)).MoveTo(X(0.12), Y(0.88)).LineTo(X(0.88), Y(0.12)).Stroke();
                break;
            case 'l': // filled circle (radio buttons)
                canvas.Circle(w / 2, h / 2, s * 0.35).Fill();
                break;
            case 'n': // square
                canvas.Rectangle(X(0.2), Y(0.2), s * 0.6, s * 0.6).Fill();
                break;
            case 'u': // diamond
                canvas.MoveTo(X(0.5), Y(0.05)).LineTo(X(0.95), Y(0.5)).LineTo(X(0.5), Y(0.95)).LineTo(X(0.05), Y(0.5)).ClosePath().Fill();
                break;
            default:
                canvas.RestoreState();
                return false;
        }
        canvas.RestoreState();
        return true;
    }
}
