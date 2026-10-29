using MiPdf.Fonts;
using MiPdf.Graphics;

namespace MiPdf.Barcodes;

/// <summary>
/// Base class of all barcodes. Barcodes are drawn as vector graphics (one filled path), so they stay sharp
/// at every zoom level and print resolution.
/// </summary>
public abstract class Barcode
{
    private protected Barcode(string text) => Text = text;

    /// <summary>The encoded data.</summary>
    public string Text { get; }

    /// <summary>Color of the bars/modules (default black).</summary>
    public PdfColor Color { get; set; } = PdfColor.Black;

    /// <summary>Optional background fill (including the quiet zone).</summary>
    public PdfColor? Background { get; set; }

    /// <summary>Width of the light margin on each side, in modules. Scanners need it; the default follows the symbology.</summary>
    public int QuietZone { get; set; }

    /// <summary>Total width in modules including the quiet zones.</summary>
    public abstract int TotalWidthModules { get; }

    /// <summary>Total height in modules including the quiet zones (2D codes), or 0 for linear barcodes.</summary>
    public abstract int TotalHeightModules { get; }

    /// <summary>
    /// Draws the barcode into the rectangle with the bottom-left corner (<paramref name="x"/>, <paramref name="y"/>).
    /// The quiet zone is part of the rectangle. 2D codes keep square modules and are centered.
    /// </summary>
    public abstract void Draw(PdfCanvas canvas, double x, double y, double width, double height);

    /// <summary>Draws the barcode with a fixed module (narrow bar) size in points; returns the size used.</summary>
    public PdfRectangle DrawWithModuleSize(PdfCanvas canvas, double x, double y, double moduleSize, double? height = null)
    {
        double w = TotalWidthModules * moduleSize;
        double h = height ?? (TotalHeightModules > 0 ? TotalHeightModules * moduleSize : w * 0.3);
        Draw(canvas, x, y, w, h);
        return new PdfRectangle(x, y, x + w, y + h);
    }

    private protected void FillBackground(PdfCanvas canvas, double x, double y, double width, double height)
    {
        if (Background is { } bg) canvas.SetFillColor(bg).Rectangle(x, y, width, height).Fill();
    }
}

/// <summary>A two-dimensional barcode (QR Code, Data Matrix): a grid of dark and light modules.</summary>
public class MatrixBarcode : Barcode
{
    private readonly bool[,] _modules;

    private protected MatrixBarcode(string text, bool[,] modules, int quietZone) : base(text)
    {
        _modules = modules;
        QuietZone = quietZone;
    }

    /// <summary>Number of module rows (without quiet zone).</summary>
    public int Rows => _modules.GetLength(0);

    /// <summary>Number of module columns (without quiet zone).</summary>
    public int Columns => _modules.GetLength(1);

    /// <summary>True if the module at (row, column) is dark. Row 0 is the top row.</summary>
    public bool this[int row, int column] => _modules[row, column];

    public override int TotalWidthModules => Columns + 2 * QuietZone;

    public override int TotalHeightModules => Rows + 2 * QuietZone;

    public override void Draw(PdfCanvas canvas, double x, double y, double width, double height)
    {
        double module = Math.Min(width / TotalWidthModules, height / TotalHeightModules);
        double w = module * TotalWidthModules, h = module * TotalHeightModules;
        x += (width - w) / 2;
        y += (height - h) / 2;
        canvas.SaveState();
        FillBackground(canvas, x, y, w, h);
        canvas.SetFillColor(Color);
        double left = x + QuietZone * module, top = y + h - QuietZone * module;
        bool any = false;
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                if (!_modules[r, c]) continue;
                int start = c;
                while (c + 1 < Columns && _modules[r, c + 1]) c++;
                canvas.Rectangle(left + start * module, top - (r + 1) * module, (c - start + 1) * module, module);
                any = true;
            }
        }
        if (any) canvas.Fill();
        canvas.RestoreState();
    }

    /// <summary>Returns the modules as text ('#' dark, ' ' light), e.g. for console output and tests.</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++) sb.Append(_modules[r, c] ? '#' : ' ');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>A one-dimensional barcode (Code 128, EAN, Code 39, ...): a sequence of bars and spaces.</summary>
public class LinearBarcode : Barcode
{
    private readonly bool[] _modules;
    private readonly List<(string Text, int Start, int End)> _textGroups;
    private readonly HashSet<int> _guardModules;

    private protected LinearBarcode(string text, bool[] modules, int quietZone, string? humanReadable = null,
        List<(string, int, int)>? textGroups = null, HashSet<int>? guardModules = null) : base(text)
    {
        _modules = modules;
        QuietZone = quietZone;
        HumanReadableText = humanReadable ?? text;
        _textGroups = textGroups ?? new List<(string, int, int)> { (HumanReadableText, 0, modules.Length) };
        _guardModules = guardModules ?? new HashSet<int>();
    }

    /// <summary>Bars (true) and spaces (false), one entry per module.</summary>
    public IReadOnlyList<bool> Modules => _modules;

    public override int TotalWidthModules => _modules.Length + 2 * QuietZone;

    public override int TotalHeightModules => 0;

    /// <summary>The text printed below the bars.</summary>
    public string HumanReadableText { get; }

    /// <summary>Print the human-readable text below the bars (default true).</summary>
    public bool ShowText { get; set; } = true;

    public PdfFont Font { get; set; } = PdfStandardFont.Helvetica;

    /// <summary>Font size of the human-readable text; default: proportional to the module width.</summary>
    public double? FontSize { get; set; }

    public override void Draw(PdfCanvas canvas, double x, double y, double width, double height)
    {
        double module = width / TotalWidthModules;
        double fontSize = FontSize ?? Math.Max(4, module * 9);
        bool text = ShowText && HumanReadableText.Length > 0;
        double textHeight = text ? fontSize * 1.1 : 0;
        double barBottom = y + textHeight, guardBottom = y + textHeight / 2;
        canvas.SaveState();
        FillBackground(canvas, x, y, width, height);
        canvas.SetFillColor(Color);
        double left = x + QuietZone * module;
        bool any = false;
        for (int i = 0; i < _modules.Length; i++)
        {
            if (!_modules[i]) continue;
            int start = i;
            bool guard = _guardModules.Contains(i);
            while (i + 1 < _modules.Length && _modules[i + 1] && _guardModules.Contains(i + 1) == guard) i++;
            double bottom = guard ? guardBottom : barBottom;
            canvas.Rectangle(left + start * module, bottom, (i - start + 1) * module, y + height - bottom);
            any = true;
        }
        if (any) canvas.Fill();
        if (text)
        {
            double baseline = y + fontSize * 0.2;
            foreach (var (groupText, start, end) in _textGroups)
                canvas.DrawText(groupText, Font, fontSize, left + (start + end) / 2.0 * module, baseline, Color, TextAlignment.Center);
        }
        canvas.RestoreState();
    }

    /// <summary>Returns the modules as text ('|' bar, ' ' space).</summary>
    public override string ToString() => new(_modules.Select(m => m ? '|' : ' ').ToArray());

    /// <summary>Appends bars/spaces with the given widths (in modules), starting with a bar.</summary>
    private protected static void AppendWidths(List<bool> modules, IEnumerable<int> widths, bool startWithBar = true)
    {
        bool bar = startWithBar;
        foreach (int w in widths)
        {
            for (int i = 0; i < w; i++) modules.Add(bar);
            bar = !bar;
        }
    }
}

/// <summary>Drawing barcodes on a canvas.</summary>
public static class BarcodeCanvasExtensions
{
    /// <summary>Draws <paramref name="barcode"/> into the rectangle (x, y, width, height); the quiet zone is included.</summary>
    public static PdfCanvas DrawBarcode(this PdfCanvas canvas, Barcode barcode, double x, double y, double width, double height)
    {
        barcode.Draw(canvas, x, y, width, height);
        return canvas;
    }

    /// <summary>Draws a 2D code as a square of <paramref name="size"/> points.</summary>
    public static PdfCanvas DrawBarcode(this PdfCanvas canvas, MatrixBarcode barcode, double x, double y, double size) =>
        canvas.DrawBarcode(barcode, x, y, size, size);
}
