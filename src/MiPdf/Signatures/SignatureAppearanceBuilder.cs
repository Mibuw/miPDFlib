using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using MiPdf.Forms;
using MiPdf.Graphics;
using MiPdf.Layout;
using MiPdf.Objects;

namespace MiPdf.Signatures;

/// <summary>Builds the visible appearance (/AP /N) of a signature widget.</summary>
internal static class SignatureAppearanceBuilder
{
    private const string DefaultTemplate = "Digitally signed by {name}\nDate: {date}\nReason: {reason}\nLocation: {location}";

    public static void Apply(PdfDocument doc, PdfWidget widget, PdfSignatureOptions options, X509Certificate2? certificate, DateTimeOffset signingTime)
    {
        var appearance = options.Appearance;
        var rect = widget.Rect;
        double w = rect.Width, h = rect.Height;
        var xobject = new PdfFormXObject(doc, new PdfRectangle(0, 0, w, h));
        string text = BuildText(appearance, options, certificate, signingTime);

        using (var canvas = xobject.GetCanvas())
        {
            if (appearance.Background is { } bg) canvas.DrawRectangle(0, 0, w, h, fill: bg);
            if (appearance.Border is { } border) canvas.DrawRectangle(0.5, 0.5, w - 1, h - 1, border, lineWidth: 1);
            const double pad = 3;
            var textBox = new PdfRectangle(pad, pad, w - pad, h - pad);
            if (appearance.Image is { } image)
            {
                switch (appearance.ImagePosition)
                {
                    case SignatureImagePosition.ImageOnly:
                        canvas.DrawImage(image, textBox);
                        text = "";
                        break;
                    case SignatureImagePosition.Background:
                        canvas.SaveState().SetOpacity(0.3);
                        canvas.DrawImage(image, textBox);
                        canvas.RestoreState();
                        break;
                    case SignatureImagePosition.Top:
                        double strip = text.Length > 0 ? h * Math.Clamp(appearance.TextStripRatio, 0, 0.9) : 0;
                        canvas.DrawImage(image, new PdfRectangle(pad, pad + strip, w - pad, h - pad));
                        textBox = new PdfRectangle(pad, pad, w - pad, Math.Max(pad + 1, strip));
                        break;
                    default:
                        var imageBox = new PdfRectangle(pad, pad, w * 0.4, h - pad);
                        canvas.DrawImage(image, imageBox);
                        textBox = new PdfRectangle(w * 0.4 + pad, pad, w - pad, h - pad);
                        break;
                }
            }
            if (appearance.Draw is { } draw)
            {
                canvas.SaveState();
                draw(canvas, w, h);
                canvas.RestoreState();
            }
            if (text.Length > 0) DrawText(canvas, text, appearance, textBox);
        }
        widget.Dictionary[PdfName.AP] = new PdfDictionary { [PdfName.N] = xobject.Reference };
    }

    private static string BuildText(PdfSignatureAppearance appearance, PdfSignatureOptions options, X509Certificate2? certificate, DateTimeOffset signingTime)
    {
        var values = new Dictionary<string, string?>
        {
            ["{name}"] = options.SignerName ?? certificate?.GetNameInfo(X509NameType.SimpleName, false),
            ["{date}"] = signingTime.ToString(appearance.DateFormat, CultureInfo.InvariantCulture),
            ["{reason}"] = options.Reason,
            ["{location}"] = options.Location,
            ["{contact}"] = options.ContactInfo,
        };
        var lines = new List<string>();
        foreach (var line in (appearance.Text ?? DefaultTemplate).Split('\n'))
        {
            var result = line;
            bool empty = false;
            foreach (var (key, value) in values)
            {
                if (!result.Contains(key, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(value)) empty = true;
                result = result.Replace(key, value ?? "", StringComparison.Ordinal);
            }
            if (!empty) lines.Add(result);
        }
        return string.Join('\n', lines);
    }

    private static void DrawText(PdfCanvas canvas, string text, PdfSignatureAppearance appearance, PdfRectangle box)
    {
        var font = appearance.Font;
        double size = appearance.FontSize;
        if (size <= 0)
        {
            size = 14;
            while (size > 4 && TextLayout.MeasureHeight(text, font, size, box.Width, 1.1) > box.Height) size -= 0.5;
        }
        var lines = TextLayout.BreakLines(text, font, size, box.Width);
        double lineHeight = font.GetLineHeight(size) * 1.1;
        double y = box.Top - font.GetAscent(size);
        canvas.SetFillColor(appearance.TextColor);
        foreach (var line in lines)
        {
            if (y < box.Bottom - lineHeight / 2) break;
            canvas.BeginText().SetFont(font, size).SetTextPosition(box.Left, y).ShowText(line).EndText();
            y -= lineHeight;
        }
    }
}
