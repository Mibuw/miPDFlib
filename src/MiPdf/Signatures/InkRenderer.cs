using MiPdf.Graphics;

namespace MiPdf.Signatures;

/// <summary>A point of a handwritten stroke. <see cref="Pressure"/> ranges from 0 to 1.</summary>
public readonly record struct InkPoint(double X, double Y, double Pressure = 1);

/// <summary>
/// Draws handwritten signatures (ink strokes, e.g. from a signature pad) as vector graphics with pressure-dependent
/// line width. Use it in <see cref="PdfSignatureAppearance.Draw"/> or on any canvas.
/// </summary>
public static class InkRenderer
{
    /// <param name="canvas">Target canvas.</param>
    /// <param name="strokes">Strokes; each stroke is a sequence of points.</param>
    /// <param name="color">Ink color (default dark blue).</param>
    /// <param name="minWidth">Line width at pressure 0 (points).</param>
    /// <param name="maxWidth">Line width at pressure 1 (points).</param>
    /// <param name="topLeftHeight">
    /// If set, the coordinates have a top-left origin (y pointing down, as on screens) within an area of this height;
    /// they are flipped into PDF coordinates.
    /// </param>
    public static void Draw(PdfCanvas canvas, IEnumerable<IReadOnlyList<InkPoint>> strokes, PdfColor? color = null,
        double minWidth = 0.35, double maxWidth = 2.4, double? topLeftHeight = null)
    {
        var ink = color ?? PdfColor.Rgb(0, 0, 139 / 255.0);
        canvas.SaveState();
        if (topLeftHeight is { } height) canvas.Transform(1, 0, 0, -1, 0, height);
        canvas.SetStrokeColor(ink).SetFillColor(ink).SetLineCap(LineCap.Round).SetLineJoin(LineJoin.Round);
        double Width(double pressure) => minWidth + (maxWidth - minWidth) * Math.Clamp(pressure, 0, 1);
        foreach (var stroke in strokes)
        {
            if (stroke.Count == 0) continue;
            if (stroke.Count == 1)
            {
                var p = stroke[0];
                canvas.Circle(p.X, p.Y, Width(p.Pressure) / 2).Fill();
                continue;
            }
            double current = -1;
            for (int i = 1; i < stroke.Count; i++)
            {
                var a = stroke[i - 1];
                var b = stroke[i];
                double w = Math.Round(Width((a.Pressure + b.Pressure) / 2), 2);
                if (w != current)
                {
                    canvas.SetLineWidth(w);
                    current = w;
                }
                canvas.MoveTo(a.X, a.Y).LineTo(b.X, b.Y).Stroke();
            }
        }
        canvas.RestoreState();
    }
}
