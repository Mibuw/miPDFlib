using System.Runtime.CompilerServices;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.PostScript.Fonts;
using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>A pattern made by <c>makepattern</c> (PLRM 4.9).</summary>
internal sealed class PsPatternInstance
{
    public PsDict Dict = null!;

    /// <summary>The pattern matrix: pattern space to device space.</summary>
    public Matrix Matrix;

    public int PatternType, PaintType = 1, TilingType;
    public double[] BBox = Array.Empty<double>();
    public double XStep, YStep;
    public PsObject PaintProc;

    /// <summary>The shading of a shading pattern as PDF object (null: it paints nothing).</summary>
    public PdfObject? Shading;

    /// <summary>The graphics state at <c>makepattern</c>, in which the PaintProc runs.</summary>
    public PsGState State = null!;

    public PsPatternPaint? Paint;
    public PdfDocument? Document;
    public bool Preparing;
}

/// <summary>
/// Patterns (PLRM 4.9) and forms (PLRM 4.7). A tiling pattern's PaintProc runs once, when the pattern is first painted
/// with; what it paints becomes the cell of a PDF tiling pattern. Shading patterns become PDF shading patterns. A form
/// becomes a form XObject that is painted again as long as the graphics state it depends on is the same.
/// </summary>
internal sealed partial class PsInterpreter
{
    private readonly ConditionalWeakTable<PsArrayStorage, PsPatternInstance> _patternInstances = new();
    private readonly Dictionary<FormKey, PdfReference> _forms = new();
    private int _captureDepth;

    private readonly record struct FormKey(PsDict Form, PsColorSpace Space, object? Pattern, object? Font, string State);

    private void RegisterPatternOperators()
    {
        Op("makepattern", ip =>
        {
            var matrix = ip.PopMatrix();
            var dict = ip.PopDict();
            ip.Push(PsObject.Dictionary(ip.MakePattern(dict, matrix)));
        });
        Op("execform", ip => ip.ExecForm(ip.PopDict()));
    }

    // ------------------------------------------------------------------ makepattern

    private PsDict MakePattern(PsDict dict, Matrix matrix)
    {
        var instance = new PsPatternInstance { Matrix = matrix.Multiply(GState.Ctm), State = GState.Clone() };
        instance.PatternType = PatternInt(dict, "PatternType", 1, 2);
        if (instance.PatternType == 1)
        {
            instance.PaintType = PatternInt(dict, "PaintType", 1, 2);
            instance.TilingType = PatternInt(dict, "TilingType", 1, 3);
            var bbox = DictValue(dict, "BBox");
            if (bbox.Type == PsType.Null) throw new PsError("undefined");
            instance.BBox = NumberArray(bbox, 4);
            if (instance.BBox[0] == instance.BBox[2] || instance.BBox[1] == instance.BBox[3]) throw new PsError("rangecheck");
            instance.XStep = PatternStep(dict, "XStep");
            instance.YStep = PatternStep(dict, "YStep");
            var proc = DictValue(dict, "PaintProc");
            if (proc.Type == PsType.Null) throw new PsError("undefined");
            if (!proc.IsProcedure) throw new PsError("typecheck");
            instance.PaintProc = proc;
        }
        else
        {
            var shading = DictValue(dict, "Shading");
            if (shading.Type == PsType.Null) throw new PsError("undefined");
            if (shading.Type != PsType.Dictionary) throw new PsError("typecheck");
            instance.Shading = ConvertShading(shading.AsDict);
        }
        // the result is a read-only copy with an Implementation entry (an array, as in Ghostscript)
        var copy = NewDict(dict.Count + 1);
        foreach (var (key, value) in dict.Map) copy.Map[key] = value;
        var implementation = NewArray(new[] { PsObject.Null });
        _patternInstances.AddOrUpdate(implementation.AsArray.Storage, instance);
        copy.Map[Key("Implementation")] = implementation;
        copy.Access = PsDictAccess.ReadOnly;
        instance.Dict = copy;
        return copy;
    }

    /// <summary>A required integer entry in a range (missing or not an integer: undefined, as Ghostscript).</summary>
    private int PatternInt(PsDict dict, string key, int min, int max)
    {
        var v = DictValue(dict, key);
        if (v.Type != PsType.Integer) throw new PsError("undefined");
        if (v.Int < min || v.Int > max) throw new PsError("rangecheck");
        return (int)v.Int;
    }

    private double PatternStep(PsDict dict, string key)
    {
        var v = DictValue(dict, key);
        if (v.Type == PsType.Null) throw new PsError("undefined");
        if (!v.IsNumber) throw new PsError("typecheck");
        if (v.Number == 0 || !double.IsFinite(v.Number)) throw new PsError("rangecheck");
        return v.Number;
    }

    /// <summary>An array of numbers; <paramref name="count"/> elements (or any number when negative).</summary>
    private static double[] NumberArray(in PsObject o, int count)
    {
        if (!o.IsArray) throw new PsError("typecheck");
        var a = o.AsArray;
        if (count >= 0 && a.Length != count) throw new PsError("rangecheck");
        var values = new double[a.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (!a[i].IsNumber) throw new PsError("typecheck");
            values[i] = a[i].Number;
        }
        return values;
    }

    /// <summary>The instance behind a pattern dictionary made by makepattern (null for other objects).</summary>
    private PsPatternInstance? PatternOf(in PsObject pattern)
    {
        if (pattern.Type != PsType.Dictionary) return null;
        return pattern.AsDict.Map.TryGetValue(Key("Implementation"), out var implementation) && implementation.IsArray
            && _patternInstances.TryGetValue(implementation.AsArray.Storage, out var instance) ? instance : null;
    }

    /// <summary><c>setcolor</c> in a Pattern color space: the pattern, after it the components of an uncolored one.</summary>
    private void SetPatternColor()
    {
        var pattern = Pop();
        if (pattern.Type != PsType.Dictionary) throw new PsError("typecheck");
        var instance = PatternOf(pattern) ?? throw new PsError("undefined");
        var components = Array.Empty<double>();
        if (instance.PaintType == 2)
        {
            components = new double[GState.ColorSpace.Base?.Components ?? 0];
            for (int i = components.Length - 1; i >= 0; i--) components[i] = PopReal();
        }
        GState.Color = components;
        GState.Pattern = pattern;
    }

    // ------------------------------------------------------------------ painting with patterns

    /// <summary>
    /// The color painting operators use: the current color, or the pattern (prepared on first use). Null if nothing is
    /// painted (Separation /None, the initial color of a Pattern space). On a device without a document, a pattern
    /// paints black (only the side effects of painting matter there).
    /// </summary>
    public PsDeviceColor? PaintColor()
    {
        if (GState.ColorSpace.Family != PsColorFamily.Pattern) return CurrentDeviceColor();
        if (PatternOf(GState.Pattern) is not { } instance) return null;
        if (Device is not { Document: not null } device) return PsDeviceColor.Black;
        if (PreparePattern(instance, device) is not { } paint) return null;
        if (instance.PaintType == 1) return new PsDeviceColor(PsColorFamily.Pattern, 0, 0, 0, 0) { Pattern = paint };
        if (GState.ColorSpace.Base is not { } baseSpace || ToDevice(baseSpace, GState.Color, 0) is not { } color) return null;
        return color with { Pattern = paint };
    }

    private PsPatternPaint? PreparePattern(PsPatternInstance instance, PsDevice device)
    {
        var document = device.Document!;
        if (instance.Paint != null && instance.Document == document) return instance.Paint;
        if (instance.Preparing) return null; // the pattern paints itself
        instance.Preparing = true;
        try
        {
            var p = instance.Matrix;
            if (instance.PatternType == 2)
            {
                if (instance.Shading == null) return null;
                var shading = document.AddObject(instance.Shading);
                instance.Paint = new PsPatternPaint(colored: true, toContent => document.AddObject(new PdfDictionary
                {
                    [PdfName.Type] = PdfName.Pattern,
                    [PdfName.Get("PatternType")] = new PdfNumber(2),
                    [PdfName.Shading] = shading,
                    [PdfName.Get("Matrix")] = PsPatternPaint.ToArray(p.Multiply(toContent)),
                }));
            }
            else
            {
                if (!p.TryInvert(out var inverse)) return null;
                // the PaintProc paints in device space (the CTM is the pattern matrix); the cell content maps it back
                var resources = new PdfDictionary();
                var writer = new PsContentWriter(new PsDictionaryResources(document, resources), deviceToContent: inverse, unknownStart: true)
                {
                    NoColor = instance.PaintType == 2,
                };
                var state = instance.State.Clone();
                state.Ctm = p;
                Capture(device, writer, state, instance.PaintProc, PsObject.Dictionary(instance.Dict), clip: null);
                var content = writer.ToArray();
                var resourcesReference = document.AddObject(resources);
                var dict = instance.Dict;
                instance.Paint = new PsPatternPaint(colored: instance.PaintType == 1, toContent =>
                {
                    var stream = new PdfStream(content)
                    {
                        [PdfName.Type] = PdfName.Pattern,
                        [PdfName.Get("PatternType")] = new PdfNumber(1),
                        [PdfName.Get("PaintType")] = new PdfNumber(instance.PaintType),
                        [PdfName.Get("TilingType")] = new PdfNumber(instance.TilingType),
                        [PdfName.BBox] = new PdfArray(instance.BBox.Select(v => (PdfObject)new PdfNumber(v))),
                        [PdfName.Get("XStep")] = new PdfNumber(instance.XStep),
                        [PdfName.Get("YStep")] = new PdfNumber(instance.YStep),
                        [PdfName.Get("Matrix")] = PsPatternPaint.ToArray(p.Multiply(toContent)),
                        [PdfName.Resources] = resourcesReference,
                    };
                    return document.AddObject(stream);
                });
            }
            instance.Document = document;
            return instance.Paint;
        }
        finally
        {
            instance.Preparing = false;
        }
    }

    /// <summary>
    /// Runs a PaintProc (with <paramref name="argument"/> on the stack) in <paramref name="state"/>, painting into
    /// <paramref name="writer"/>; the current graphics state is restored afterwards.
    /// </summary>
    private void Capture(PsDevice parent, PsContentWriter writer, PsGState state, PsObject proc, PsObject argument, PsClip? clip)
    {
        if (_captureDepth >= 16) throw new PsError("limitcheck");
        parent.Fonts ??= new PsPdfFonts(parent.Document!, this);
        var saved = GState;
        GState = state;
        GState.Device = new PsCaptureDevice(parent, writer);
        GState.Path = new PsPath();
        GState.Clip = clip;
        _captureDepth++;
        try
        {
            CallProcedure(proc, new[] { argument });
        }
        finally
        {
            _captureDepth--;
            GState = saved;
        }
    }

    // ------------------------------------------------------------------ execform

    private void ExecForm(PsDict form)
    {
        var formType = DictValue(form, "FormType");
        if (formType.Type != PsType.Integer || formType.Int != 1) throw new PsError("undefined");
        var bboxObject = DictValue(form, "BBox");
        var matrixObject = DictValue(form, "Matrix");
        var proc = DictValue(form, "PaintProc");
        if (bboxObject.Type == PsType.Null || matrixObject.Type == PsType.Null || proc.Type == PsType.Null) throw new PsError("undefined");
        var bbox = NumberArray(bboxObject, 4);
        var matrix = ToMatrix(matrixObject);
        if (!proc.IsProcedure) throw new PsError("typecheck");

        var m = matrix.Multiply(GState.Ctm);
        var device = Device;
        var formObject = PsObject.Dictionary(form);
        // the form space BBox as clipping path in device space
        var corners = new[] { m.Transform(bbox[0], bbox[1]), m.Transform(bbox[2], bbox[1]), m.Transform(bbox[2], bbox[3]), m.Transform(bbox[0], bbox[3]) };
        var clipPath = new PsPath();
        clipPath.MoveTo(corners[0].X, corners[0].Y);
        for (int i = 1; i < 4; i++) clipPath.LineTo(corners[i].X, corners[i].Y);
        clipPath.ClosePath();
        var segments = clipPath.Segments.ToArray();

        bool asXObject = device is { Document: not null } && device is not (PsNullDevice or PsPathDevice)
            && !device.Content.NoColor && _captureDepth < 16 && m.TryInvert(out _);
        if (!asXObject)
        {
            // as gsave, concat, clip to the BBox, PaintProc, grestore
            var saved = GState;
            GState = saved.Clone();
            GState.Ctm = m;
            GState.Path = new PsPath();
            GState.Clip = new PsClip(segments, false, GState.Clip);
            try
            {
                CallProcedure(proc, new[] { formObject });
            }
            finally
            {
                GState = saved;
            }
            return;
        }

        var key = new FormKey(form, GState.ColorSpace, GState.Pattern.Ref, GState.Font.Ref, FormState(m));
        if (!_forms.TryGetValue(key, out var reference))
        {
            var document = device!.Document!;
            m.TryInvert(out var inverse);
            var resources = new PdfDictionary();
            var writer = new PsContentWriter(new PsDictionaryResources(document, resources), deviceToContent: inverse, unknownStart: true);
            var state = GState.Clone();
            state.Ctm = m;
            Capture(device, writer, state, proc, formObject, new PsClip(segments, false, null));
            var stream = new PdfStream(writer.ToArray())
            {
                [PdfName.Type] = PdfName.XObject,
                [PdfName.Subtype] = PdfName.Form,
                [PdfName.BBox] = new PdfArray(bbox.Select(v => (PdfObject)new PdfNumber(v))),
                [PdfName.Resources] = resources,
            };
            reference = document.AddObject(stream);
            if (_forms.Count < 10_000) _forms[key] = reference;
        }
        device!.PaintForm(GState, reference, m);
    }

    /// <summary>
    /// The graphics state a form's content depends on, besides its color space, pattern and font: the linear part of
    /// the transformation (the translation is applied when the form is painted), the color and the line parameters.
    /// </summary>
    private string FormState(Matrix m)
    {
        var g = GState;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join("|", new[] { m.A, m.B, m.C, m.D }.Select(v => Math.Round(v, 9).ToString("R", inv)))
            + "|" + string.Join(",", g.Color.Select(v => v.ToString("R", inv)))
            + $"|{g.LineWidth.ToString("R", inv)}|{g.LineCap}|{g.LineJoin}|{g.MiterLimit.ToString("R", inv)}"
            + $"|{string.Join(",", g.Dash.Select(v => v.ToString("R", inv)))}|{g.DashOffset.ToString("R", inv)}"
            + $"|{g.Overprint}|{g.StrokeAdjust}|{g.Flatness.ToString("R", inv)}";
    }
}
