using System.Globalization;
using System.Text;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.PostScript.Graphics;

/// <summary>
/// Writes PostScript painting operations as PDF content. Device space is 1/720 inch (as in Ghostscript's pdfwrite), so
/// the content starts with a scaling to points. Graphics state parameters are written only when painting needs them
/// and only if they changed. Clipping uses one save level that is restarted when the clip gets larger again.
/// </summary>
internal sealed class PsContentWriter
{
    /// <summary>Device units per point.</summary>
    public const double DeviceScale = 10;

    private readonly StringBuilder _sb = new();
    private readonly IResourceContainer? _resources;
    private readonly Dictionary<(bool Overprint, bool StrokeAdjust), PdfName> _extGStates = new();

    // the state written so far
    private PsClip? _clip;
    private PsDeviceColor? _fill, _stroke;
    private double _lineWidth;
    private int _cap, _join;
    private double _miter;
    private string _dash = "";
    private bool _overprint, _strokeAdjust;
    private int _textMode;
    private readonly bool _glyphSpace;
    private readonly Dictionary<PdfReference, PdfName> _fonts = new();

    /// <param name="resources">Where fonts and graphics states are registered (null: the null device).</param>
    /// <param name="glyphSpace">Content of a Type 3 glyph: coordinates are glyph space, without the device scaling.</param>
    public PsContentWriter(IResourceContainer? resources, bool glyphSpace = false)
    {
        _resources = resources;
        _glyphSpace = glyphSpace;
        Begin();
    }

    /// <summary>True when something was painted.</summary>
    public bool HasContent { get; private set; }

    /// <summary>Colors are not written (uncolored Type 3 glyphs, after setcachedevice).</summary>
    public bool NoColor { get; set; }

    private void Begin()
    {
        _sb.Clear();
        _sb.Append(_glyphSpace ? "q\n" : "0.1 0 0 0.1 0 0 cm\nq\n");
        ResetState();
        _clip = null;
        HasContent = false;
    }

    /// <summary>
    /// Shows text: <paramref name="codes"/> in the PDF font, with the text matrix <paramref name="tm"/> (text space to
    /// device space). Mode 0 fills the glyphs, mode 1 strokes them (PaintType 2 fonts).
    /// </summary>
    public void ShowText(PsGState gs, PsDeviceColor color, PdfReference font, Matrix tm, ReadOnlySpan<byte> codes, int mode, double lineWidth)
    {
        if (codes.Length == 0 || _resources == null) return;
        SyncClip(gs.Clip);
        if (mode == 1)
        {
            SyncStroke(color);
            if (_lineWidth != lineWidth)
            {
                Numbers(4, lineWidth);
                _sb.Append("w\n");
                _lineWidth = lineWidth;
            }
        }
        else SyncFill(color);
        SyncExtGState(gs.Overprint, _strokeAdjust);
        if (!_fonts.TryGetValue(font, out var name))
            _fonts[font] = name = _resources.AddResource(PdfName.Font, font, "F");
        _sb.Append("BT /").Append(name.Value).Append(" 1 Tf ");
        if (_textMode != mode)
        {
            _sb.Append(mode).Append(" Tr ");
            _textMode = mode;
        }
        Numbers(6, tm.A, tm.B, tm.C, tm.D, tm.E, tm.F);
        _sb.Append("Tm <").Append(Convert.ToHexString(codes)).Append("> Tj ET\n");
        HasContent = true;
    }

    /// <summary>Discards everything painted so far (<c>erasepage</c>).</summary>
    public void Erase() => Begin();

    /// <summary>The finished content.</summary>
    public byte[] ToArray() => Encoding.ASCII.GetBytes(_sb + "Q\n");

    private void ResetState()
    {
        // the PDF defaults after Q
        _fill = _stroke = PsDeviceColor.Black;
        _lineWidth = 1;
        _cap = _join = 0;
        _miter = 10;
        _dash = "[] 0";
        _overprint = _strokeAdjust = false;
        _textMode = 0;
    }

    public void Fill(PsGState gs, PsDeviceColor color, bool evenOdd)
    {
        if (gs.Path.IsEmpty) return;
        SyncClip(gs.Clip);
        SyncFill(color);
        SyncExtGState(gs.Overprint, _strokeAdjust);
        WritePath(gs.Path.Segments, 2);
        _sb.Append(evenOdd ? "f*\n" : "f\n");
        HasContent = true;
    }

    public void Stroke(PsGState gs, PsDeviceColor color)
    {
        if (gs.Path.IsEmpty) return;
        SyncClip(gs.Clip);
        SyncStroke(color);
        SyncLine(gs);
        SyncExtGState(gs.Overprint, gs.StrokeAdjust);
        var ctm = gs.Ctm;
        if (ctm == Matrix.Identity || !ctm.TryInvert(out var inverse))
        {
            WritePath(gs.Path.Segments, 2);
            _sb.Append("S\n");
        }
        else
        {
            // line width and dashes are in user space: stroke the path in the user space of the stroke
            _sb.Append("q ");
            Numbers(6, ctm.A, ctm.B, ctm.C, ctm.D, ctm.E, ctm.F);
            _sb.Append("cm\n");
            double scale = Math.Max(Math.Max(Math.Abs(ctm.A), Math.Abs(ctm.B)), Math.Max(Math.Abs(ctm.C), Math.Abs(ctm.D)));
            int decimals = Math.Clamp(2 + (int)Math.Ceiling(Math.Log10(Math.Max(scale, 1e-9))), 2, 8);
            WritePath(gs.Path.Transformed(inverse), decimals);
            _sb.Append("S Q\n");
        }
        HasContent = true;
    }

    /// <summary>Makes the written clip equal to <paramref name="clip"/>.</summary>
    private void SyncClip(PsClip? clip)
    {
        if (ReferenceEquals(clip, _clip)) return;
        // the new clip extends the written one: only the inner clips are added
        var chain = new List<PsClip>();
        bool extends = false;
        for (var c = clip; c != null; c = c.Outer)
        {
            if (ReferenceEquals(c, _clip))
            {
                extends = true;
                break;
            }
            chain.Add(c);
        }
        if (!extends && _clip != null)
        {
            _sb.Append("Q q\n");
            ResetState();
        }
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            WritePath(chain[i].Path, 2);
            _sb.Append(chain[i].EvenOdd ? "W* n\n" : "W n\n");
        }
        _clip = clip;
    }

    private void SyncFill(PsDeviceColor color)
    {
        if (_fill == color || NoColor) return;
        WriteColor(color, stroke: false);
        _fill = color;
    }

    private void SyncStroke(PsDeviceColor color)
    {
        if (_stroke == color || NoColor) return;
        WriteColor(color, stroke: true);
        _stroke = color;
    }

    private void WriteColor(PsDeviceColor c, bool stroke)
    {
        switch (c.Family)
        {
            case PsColorFamily.DeviceGray:
                Numbers(4, c.C0);
                _sb.Append(stroke ? "G\n" : "g\n");
                break;
            case PsColorFamily.DeviceRGB:
                Numbers(4, c.C0, c.C1, c.C2);
                _sb.Append(stroke ? "RG\n" : "rg\n");
                break;
            default:
                Numbers(4, c.C0, c.C1, c.C2, c.C3);
                _sb.Append(stroke ? "K\n" : "k\n");
                break;
        }
    }

    private void SyncLine(PsGState gs)
    {
        if (_lineWidth != gs.LineWidth)
        {
            Numbers(4, gs.LineWidth);
            _sb.Append("w\n");
            _lineWidth = gs.LineWidth;
        }
        if (_cap != gs.LineCap)
        {
            _sb.Append(gs.LineCap).Append(" J\n");
            _cap = gs.LineCap;
        }
        if (_join != gs.LineJoin)
        {
            _sb.Append(gs.LineJoin).Append(" j\n");
            _join = gs.LineJoin;
        }
        if (_miter != gs.MiterLimit)
        {
            Numbers(4, gs.MiterLimit);
            _sb.Append("M\n");
            _miter = gs.MiterLimit;
        }
        var dash = new StringBuilder("[");
        for (int i = 0; i < gs.Dash.Length; i++)
        {
            if (i > 0) dash.Append(' ');
            dash.Append(Format(gs.Dash[i], 4));
        }
        dash.Append("] ").Append(Format(gs.DashOffset, 4));
        string d = dash.ToString();
        if (_dash != d)
        {
            _sb.Append(d).Append(" d\n");
            _dash = d;
        }
    }

    private void SyncExtGState(bool overprint, bool strokeAdjust)
    {
        if (_overprint == overprint && _strokeAdjust == strokeAdjust) return;
        if (_resources == null) return; // null device
        if (!_extGStates.TryGetValue((overprint, strokeAdjust), out var name))
        {
            var dict = new PdfDictionary
            {
                [PdfName.Type] = PdfName.ExtGState,
                [PdfName.Get("OP")] = overprint ? PdfBoolean.True : PdfBoolean.False,
                [PdfName.Get("op")] = overprint ? PdfBoolean.True : PdfBoolean.False,
                [PdfName.Get("SA")] = strokeAdjust ? PdfBoolean.True : PdfBoolean.False,
            };
            name = _resources.AddResource(PdfName.ExtGState, _resources.Document.AddObject(dict), "GS");
            _extGStates[(overprint, strokeAdjust)] = name;
        }
        _sb.Append('/').Append(name.Value).Append(" gs\n");
        _overprint = overprint;
        _strokeAdjust = strokeAdjust;
    }

    private void WritePath(IEnumerable<PsSegment> segments, int decimals)
    {
        foreach (var s in segments)
        {
            switch (s.Kind)
            {
                case PsSegmentKind.MoveTo:
                    Numbers(decimals, s.X3, s.Y3);
                    _sb.Append("m\n");
                    break;
                case PsSegmentKind.LineTo:
                    Numbers(decimals, s.X3, s.Y3);
                    _sb.Append("l\n");
                    break;
                case PsSegmentKind.CurveTo:
                    Numbers(decimals, s.X1, s.Y1, s.X2, s.Y2, s.X3, s.Y3);
                    _sb.Append("c\n");
                    break;
                default:
                    _sb.Append("h\n");
                    break;
            }
        }
    }

    private void Numbers(int decimals, params double[] values)
    {
        foreach (double v in values) _sb.Append(Format(v, decimals)).Append(' ');
    }

    private static readonly string[] Formats = Enumerable.Range(0, 10).Select(d => "0." + new string('#', d)).ToArray();

    internal static string Format(double value, int decimals)
    {
        if (!double.IsFinite(value)) return "0";
        string s = Math.Round(value, decimals).ToString(Formats[decimals], CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
