using MiPdf.Graphics;
using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>Graphics state, coordinate system, path construction and painting operators (PLRM 8.1).</summary>
internal sealed partial class PsInterpreter
{
    /// <summary>The current graphics state.</summary>
    public PsGState GState = new();

    /// <summary>Graphics states saved by gsave (Save marks the ones saved by <c>save</c>).</summary>
    private readonly List<(PsGState State, PsSave? Save)> _gstack = new();

    /// <summary>The current output device (null: nothing is painted, e.g. before a device is set up).</summary>
    public PsDevice? Device
    {
        get => GState.Device;
        set => GState.Device = value;
    }

    /// <summary>Sets up the graphics state for a device (PLRM initgraphics).</summary>
    public void InitGraphics(PsDevice? device)
    {
        GState.Device = device;
        GState.Ctm = device?.DefaultMatrix ?? Matrix.Identity;
        GState.Path = new PsPath();
        GState.Clip = null;
        GState.ColorSpace = PsColorSpace.Gray;
        GState.Color = new double[] { 0 };
        GState.Pattern = PsObject.Null;
        GState.LineWidth = 1;
        GState.LineCap = GState.LineJoin = 0;
        GState.MiterLimit = 10;
        GState.Dash = Array.Empty<double>();
        GState.DashOffset = 0;
        GState.StrokeAdjust = false;
        GState.Overprint = false;
    }

    private void OnSave(PsSave save) => _gstack.Add((GState.Clone(), save));

    private void OnRestore(PsSave save)
    {
        int i = _gstack.FindLastIndex(e => ReferenceEquals(e.Save, save));
        if (i < 0) return;
        GState = _gstack[i].State;
        _gstack.RemoveRange(i, _gstack.Count - i);
    }

    private void RegisterGraphicsOperators()
    {
        SaveHook = OnSave;
        RestoreHook = OnRestore;

        // ---------------------------------------------------------------- graphics state
        Op("gsave", ip =>
        {
            if (ip._gstack.Count >= 10_000) throw new PsError("limitcheck");
            ip._gstack.Add((ip.GState.Clone(), null));
        });
        Op("grestore", ip =>
        {
            if (ip._gstack.Count == 0) return;
            var (state, save) = ip._gstack[^1];
            // a state saved by save is restored but stays on the stack
            if (save != null) ip.GState = state.Clone();
            else
            {
                ip.GState = state;
                ip._gstack.RemoveAt(ip._gstack.Count - 1);
            }
        });
        Op("grestoreall", ip =>
        {
            int i = ip._gstack.FindLastIndex(e => e.Save != null);
            if (i < 0)
            {
                if (ip._gstack.Count == 0) return;
                ip.GState = ip._gstack[0].State;
                ip._gstack.Clear();
                return;
            }
            ip.GState = ip._gstack[i].State.Clone();
            ip._gstack.RemoveRange(i + 1, ip._gstack.Count - i - 1);
        });
        Op("initgraphics", ip => ip.InitGraphics(ip.Device));
        Op("gstate", ip => ip.Push(PsObject.Other(PsType.GState, new PsGStateObject(ip.GState.Clone()))));
        Op("currentgstate", ip =>
        {
            var o = ip.Pop();
            if (o.Type != PsType.GState) throw new PsError("typecheck");
            ((PsGStateObject)o.Ref!).State = ip.GState.Clone();
            ip.Push(o);
        });
        Op("setgstate", ip =>
        {
            var o = ip.Pop();
            if (o.Type != PsType.GState) throw new PsError("typecheck");
            ip.GState = ((PsGStateObject)o.Ref!).State.Clone();
        });
        Op("setlinewidth", ip => ip.GState.LineWidth = Math.Abs(ip.PopReal()));
        Op("currentlinewidth", ip => ip.PushReal(ip.GState.LineWidth));
        Op("setlinecap", ip => ip.GState.LineCap = ip.PopRange(0, 2));
        Op("currentlinecap", ip => ip.Push(ip.GState.LineCap));
        Op("setlinejoin", ip => ip.GState.LineJoin = ip.PopRange(0, 2));
        Op("currentlinejoin", ip => ip.Push(ip.GState.LineJoin));
        Op("setmiterlimit", ip =>
        {
            double limit = ip.PopReal();
            if (limit < 1) throw new PsError("rangecheck");
            ip.GState.MiterLimit = limit;
        });
        Op("currentmiterlimit", ip => ip.PushReal(ip.GState.MiterLimit));
        Op("setstrokeadjust", ip => ip.GState.StrokeAdjust = ip.PopBool());
        Op("currentstrokeadjust", ip => ip.Push(ip.GState.StrokeAdjust));
        Op("setdash", ip =>
        {
            double offset = ip.PopReal();
            var array = ip.PopArray();
            var dash = new double[array.AsArray.Length];
            bool nonzero = false;
            for (int i = 0; i < dash.Length; i++)
            {
                var item = array.AsArray[i];
                if (!item.IsNumber) throw new PsError("typecheck");
                if (item.Number < 0) throw new PsError("rangecheck");
                dash[i] = item.Number;
                nonzero |= dash[i] > 0;
            }
            if (dash.Length > 0 && !nonzero) throw new PsError("rangecheck");
            ip.GState.Dash = dash;
            ip.GState.DashOffset = offset;
        });
        Op("currentdash", ip =>
        {
            ip.Push(ip.NewArray(ip.GState.Dash.Select(d => PsObject.RealValue((float)d)).ToArray()));
            ip.PushReal(ip.GState.DashOffset);
        });
        Op("setflat", ip => ip.GState.Flatness = Math.Clamp(ip.PopReal(), 0.2, 100));
        Op("currentflat", ip => ip.PushReal(ip.GState.Flatness));
        Op("setoverprint", ip => ip.GState.Overprint = ip.PopBool());
        Op("currentoverprint", ip => ip.Push(ip.GState.Overprint));
        Op("setsmoothness", ip => ip.GState.Smoothness = Math.Clamp(ip.PopReal(), 0, 1));
        Op("currentsmoothness", ip => ip.PushReal(ip.GState.Smoothness));

        // device-dependent parameters: kept, but without effect on PDF output
        DeviceParameter("transfer", s => s.Transfer, (s, v) => s.Transfer = v);
        DeviceParameter("blackgeneration", s => s.BlackGeneration, (s, v) => s.BlackGeneration = v);
        DeviceParameter("undercolorremoval", s => s.UnderColorRemoval, (s, v) => s.UnderColorRemoval = v);
        DeviceParameter("colorrendering", s => s.ColorRendering, (s, v) => s.ColorRendering = v);
        DeviceParameter("halftone", s => s.Halftone, (s, v) => s.Halftone = v);
        Op("setcolortransfer", ip =>
        {
            var items = new PsObject[4];
            for (int i = 3; i >= 0; i--) items[i] = ip.Pop();
            ip.GState.ColorTransfer = ip.NewArray(items);
        });
        Op("currentcolortransfer", ip =>
        {
            for (int i = 0; i < 4; i++)
                ip.Push(ip.GState.ColorTransfer.IsArray && ip.GState.ColorTransfer.AsArray.Length == 4
                    ? ip.GState.ColorTransfer.AsArray[i] : ip.NewArray(Array.Empty<PsObject>()).AsExecutable());
        });
        Op("setscreen", ip =>
        {
            ip.Pop();
            ip.PopReal();
            ip.PopReal();
        });
        Op("currentscreen", ip =>
        {
            ip.PushReal(60);
            ip.PushReal(45);
            ip.Push(ip.NewArray(new[] { PsObject.Name(ip.Name("pop"), executable: true), PsObject.Name(ip.Name("pop"), executable: true), PsObject.RealValue(0) }).AsExecutable());
        });
        Op("setcolorscreen", ip =>
        {
            for (int i = 0; i < 12; i++) ip.Pop();
        });
        Op("currentcolorscreen", ip =>
        {
            for (int i = 0; i < 4; i++)
            {
                ip.PushReal(60);
                ip.PushReal(45);
                ip.Push(ip.NewArray(new[] { PsObject.Name(ip.Name("pop"), executable: true), PsObject.Name(ip.Name("pop"), executable: true), PsObject.RealValue(0) }).AsExecutable());
            }
        });
        Op("sethalftonephase", ip =>
        {
            ip.PopInt();
            ip.PopInt();
        });
        Op("currenthalftonephase", ip =>
        {
            ip.Push(0);
            ip.Push(0);
        });

        // ---------------------------------------------------------------- coordinate system
        Op("matrix", ip => ip.Push(ip.MatrixObject(Matrix.Identity)));
        Op("initmatrix", ip => ip.GState.Ctm = ip.Device?.DefaultMatrix ?? Matrix.Identity);
        Op("identmatrix", ip => ip.Push(ip.FillMatrix(ip.PopMatrixArray(), Matrix.Identity)));
        Op("defaultmatrix", ip => ip.Push(ip.FillMatrix(ip.PopMatrixArray(), ip.Device?.DefaultMatrix ?? Matrix.Identity)));
        Op("currentmatrix", ip => ip.Push(ip.FillMatrix(ip.PopMatrixArray(), ip.GState.Ctm)));
        Op("setmatrix", ip => ip.GState.Ctm = ip.PopMatrix());
        Op("translate", ip => ip.Transformation(() =>
        {
            double ty = ip.PopReal(), tx = ip.PopReal();
            return Matrix.Translate(tx, ty);
        }));
        Op("scale", ip => ip.Transformation(() =>
        {
            double sy = ip.PopReal(), sx = ip.PopReal();
            return Matrix.Scale(sx, sy);
        }));
        Op("rotate", ip => ip.Transformation(() => Matrix.Rotate(ip.PopReal())));
        Op("concat", ip => ip.GState.Ctm = ip.PopMatrix().Multiply(ip.GState.Ctm));
        Op("concatmatrix", ip =>
        {
            var target = ip.PopMatrixArray();
            var m2 = ip.PopMatrix();
            var m1 = ip.PopMatrix();
            ip.Push(ip.FillMatrix(target, m1.Multiply(m2)));
        });
        Op("invertmatrix", ip =>
        {
            var target = ip.PopMatrixArray();
            if (!ip.PopMatrix().TryInvert(out var inverse)) throw new PsError("undefinedresult");
            ip.Push(ip.FillMatrix(target, inverse));
        });
        Op("transform", ip => ip.TransformPoint((m, x, y) => m.Transform(x, y), inverse: false));
        Op("dtransform", ip => ip.TransformPoint((m, x, y) => m.TransformDistance(x, y), inverse: false));
        Op("itransform", ip => ip.TransformPoint((m, x, y) => m.Transform(x, y), inverse: true));
        Op("idtransform", ip => ip.TransformPoint((m, x, y) => m.TransformDistance(x, y), inverse: true));

        // ---------------------------------------------------------------- path construction
        Op("newpath", ip => ip.GState.Path = new PsPath());
        Op("currentpoint", ip =>
        {
            var path = ip.GState.Path;
            if (!path.HasCurrentPoint) throw new PsError("nocurrentpoint");
            if (!ip.GState.Ctm.TryInvert(out var inverse)) throw new PsError("undefinedresult");
            var (x, y) = inverse.Transform(path.CurrentX, path.CurrentY);
            ip.PushReal(x);
            ip.PushReal(y);
        });
        Op("moveto", ip =>
        {
            var (x, y) = ip.PopPoint();
            ip.GState.Path.MoveTo(x, y);
        });
        Op("rmoveto", ip =>
        {
            var (dx, dy) = ip.PopDistance();
            var path = ip.RequirePoint();
            path.MoveTo(path.CurrentX + dx, path.CurrentY + dy);
        });
        Op("lineto", ip =>
        {
            var (x, y) = ip.PopPoint();
            ip.RequirePoint().LineTo(x, y);
        });
        Op("rlineto", ip =>
        {
            var (dx, dy) = ip.PopDistance();
            var path = ip.RequirePoint();
            path.LineTo(path.CurrentX + dx, path.CurrentY + dy);
        });
        Op("curveto", ip =>
        {
            var (x3, y3) = ip.PopPoint();
            var (x2, y2) = ip.PopPoint();
            var (x1, y1) = ip.PopPoint();
            ip.RequirePoint().CurveTo(x1, y1, x2, y2, x3, y3);
        });
        Op("rcurveto", ip =>
        {
            var (dx3, dy3) = ip.PopDistance();
            var (dx2, dy2) = ip.PopDistance();
            var (dx1, dy1) = ip.PopDistance();
            var path = ip.RequirePoint();
            double x = path.CurrentX, y = path.CurrentY;
            path.CurveTo(x + dx1, y + dy1, x + dx2, y + dy2, x + dx3, y + dy3);
        });
        Op("arc", ip => ip.Arc(clockwise: false));
        Op("arcn", ip => ip.Arc(clockwise: true));
        Op("arct", ip => ip.ArcTangent(push: false));
        Op("arcto", ip => ip.ArcTangent(push: true));
        Op("closepath", ip => ip.GState.Path.ClosePath());
        Op("flattenpath", ip => ip.GState.Path = ip.GState.Path.Flatten(ip.GState.Flatness));
        Op("reversepath", ip => ip.GState.Path = ip.GState.Path.Reverse());
        Op("strokepath", ip => ip.GState.Path = PsStroker.Outline(ip.GState));
        Op("clippath", ip =>
        {
            var path = new PsPath();
            if (ip.GState.Clip != null)
            {
                foreach (var s in ip.GState.Clip.Path)
                {
                    switch (s.Kind)
                    {
                        case PsSegmentKind.MoveTo: path.MoveTo(s.X3, s.Y3); break;
                        case PsSegmentKind.LineTo: path.LineTo(s.X3, s.Y3); break;
                        case PsSegmentKind.CurveTo: path.CurveTo(s.X1, s.Y1, s.X2, s.Y2, s.X3, s.Y3); break;
                        default: path.ClosePath(); break;
                    }
                }
            }
            else
            {
                // the whole page
                double w = (ip.Device?.Width ?? 612) * PsContentWriter.DeviceScale, h = (ip.Device?.Height ?? 792) * PsContentWriter.DeviceScale;
                path.MoveTo(0, 0);
                path.LineTo(w, 0);
                path.LineTo(w, h);
                path.LineTo(0, h);
                path.ClosePath();
            }
            ip.GState.Path = path;
        });
        Op("pathbbox", ip =>
        {
            var bounds = ip.GState.Path.Bounds() ?? throw new PsError("nocurrentpoint");
            if (!ip.GState.Ctm.TryInvert(out var inverse)) throw new PsError("undefinedresult");
            var corners = new[]
            {
                inverse.Transform(bounds.X0, bounds.Y0), inverse.Transform(bounds.X1, bounds.Y0),
                inverse.Transform(bounds.X0, bounds.Y1), inverse.Transform(bounds.X1, bounds.Y1),
            };
            ip.PushReal(corners.Min(c => c.X));
            ip.PushReal(corners.Min(c => c.Y));
            ip.PushReal(corners.Max(c => c.X));
            ip.PushReal(corners.Max(c => c.Y));
        });
        Op("pathforall", ip =>
        {
            var close = ip.PopProc();
            var curve = ip.PopProc();
            var line = ip.PopProc();
            var move = ip.PopProc();
            if (!ip.GState.Ctm.TryInvert(out var inverse)) throw new PsError("undefinedresult");
            ip.PushFrame(new PathForall(ip.GState.Path.Segments.ToArray(), inverse, move, line, curve, close));
        });

        // ---------------------------------------------------------------- clipping
        Op("initclip", ip => ip.GState.Clip = null);
        Op("clip", ip => ip.Clip(evenOdd: false));
        Op("eoclip", ip => ip.Clip(evenOdd: true));
        Op("rectclip", ip =>
        {
            var path = ip.RectanglePath();
            ip.GState.Clip = new PsClip(path.Segments.ToArray(), false, ip.GState.Clip);
            ip.GState.Path = new PsPath();
        });
        Op("clipsave", ip => ip.GState.ClipStack.Add(ip.GState.Clip));
        Op("cliprestore", ip =>
        {
            var stack = ip.GState.ClipStack;
            if (stack.Count == 0) return;
            ip.GState.Clip = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        });

        // ---------------------------------------------------------------- painting
        Op("fill", ip => ip.Fill(evenOdd: false));
        Op("eofill", ip => ip.Fill(evenOdd: true));
        Op("stroke", ip => ip.Stroke());
        Op("rectfill", ip =>
        {
            var saved = ip.GState.Path;
            ip.GState.Path = ip.RectanglePath();
            ip.Fill(evenOdd: false);
            ip.GState.Path = saved;
        });
        Op("rectstroke", ip =>
        {
            // an optional matrix after the rectangles applies to the stroke (a rectangle array has a multiple of
            // four numbers, so an array of six is the matrix)
            Matrix? extra = ip.Ops.Count > 1 && ip.Ops.Peek().IsArray && ip.Ops.Peek().AsArray.Length == 6 ? ip.PopMatrix() : null;
            var saved = ip.GState.Path;
            var savedCtm = ip.GState.Ctm;
            ip.GState.Path = ip.RectanglePath();
            if (extra is { } m) ip.GState.Ctm = m.Multiply(ip.GState.Ctm);
            ip.Stroke();
            ip.GState.Ctm = savedCtm;
            ip.GState.Path = saved;
        });
        Op("erasepage", ip =>
        {
            if (!ip.Encapsulated) ip.Device?.ErasePage();
        });
        Op("showpage", ip =>
        {
            if (!ip.Encapsulated) ip.ShowPage(erase: true);
        });
        Op("copypage", ip =>
        {
            if (!ip.Encapsulated) ip.ShowPage(erase: false);
        });
        // ---------------------------------------------------------------- user paths (LanguageLevel 2)
        Op("setbbox", ip =>
        {
            for (int i = 0; i < 4; i++) ip.PopReal();
        });
        Op("ucache", _ => { });
        Op("uappend", ip => ip.AppendUserPath(ip.Pop()));
        Op("ufill", ip => ip.PaintUserPath(() => ip.Fill(evenOdd: false)));
        Op("ueofill", ip => ip.PaintUserPath(() => ip.Fill(evenOdd: true)));
        Op("ustroke", ip =>
        {
            // an optional matrix after the user path applies to the stroke
            Matrix? extra = ip.Ops.Count > 1 && ip.Ops.Peek().IsArray && ip.Ops.Peek().AsArray.Length == 6 && ip.Ops.Peek(1).IsArray
                ? ip.PopMatrix() : null;
            ip.PaintUserPath(() =>
            {
                if (extra is { } m) ip.GState.Ctm = m.Multiply(ip.GState.Ctm);
                ip.Stroke();
            });
        });
        Op("ustrokepath", ip =>
        {
            Matrix? extra = ip.Ops.Count > 1 && ip.Ops.Peek().IsArray && ip.Ops.Peek().AsArray.Length == 6 && ip.Ops.Peek(1).IsArray
                ? ip.PopMatrix() : null;
            var userPath = ip.Pop();
            var savedCtm = ip.GState.Ctm;
            ip.GState.Path = new PsPath();
            ip.AppendUserPath(userPath);
            if (extra is { } m) ip.GState.Ctm = m.Multiply(ip.GState.Ctm);
            ip.GState.Path = PsStroker.Outline(ip.GState);
            ip.GState.Ctm = savedCtm;
        });
        Op("upath", ip =>
        {
            ip.PopBool(); // ucache flag
            if (!ip.GState.Ctm.TryInvert(out var inverse)) throw new PsError("undefinedresult");
            var items = new List<PsObject>();
            var bounds = ip.GState.Path.Bounds() ?? (0, 0, 0, 0);
            var corners = new[] { inverse.Transform(bounds.X0, bounds.Y0), inverse.Transform(bounds.X1, bounds.Y1), inverse.Transform(bounds.X0, bounds.Y1), inverse.Transform(bounds.X1, bounds.Y0) };
            foreach (double v in new[] { corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y) }) items.Add(PsObject.RealValue((float)v));
            items.Add(PsObject.Name(ip.Name("setbbox"), executable: true));
            foreach (var s in ip.GState.Path.Segments)
            {
                void Point(double x, double y)
                {
                    var (ux, uy) = inverse.Transform(x, y);
                    items.Add(PsObject.RealValue((float)ux));
                    items.Add(PsObject.RealValue((float)uy));
                }
                switch (s.Kind)
                {
                    case PsSegmentKind.MoveTo: Point(s.X3, s.Y3); items.Add(PsObject.Name(ip.Name("moveto"), true)); break;
                    case PsSegmentKind.LineTo: Point(s.X3, s.Y3); items.Add(PsObject.Name(ip.Name("lineto"), true)); break;
                    case PsSegmentKind.CurveTo:
                        Point(s.X1, s.Y1);
                        Point(s.X2, s.Y2);
                        Point(s.X3, s.Y3);
                        items.Add(PsObject.Name(ip.Name("curveto"), true));
                        break;
                    default: items.Add(PsObject.Name(ip.Name("closepath"), true)); break;
                }
            }
            ip.Push(ip.NewArray(items.ToArray()).AsExecutable());
        });

        Op("nulldevice", ip =>
        {
            ip.Device = new PsNullDevice();
            ip.GState.Ctm = Matrix.Identity;
            ip.GState.Clip = null;
        });
    }

    private void DeviceParameter(string name, Func<PsGState, PsObject> get, Action<PsGState, PsObject> set)
    {
        Op("set" + name, ip => set(ip.GState, ip.Pop()));
        Op("current" + name, ip =>
        {
            var value = get(ip.GState);
            ip.Push(value.Type == PsType.Null && name is "transfer" or "blackgeneration" or "undercolorremoval"
                ? ip.NewArray(Array.Empty<PsObject>()).AsExecutable()
                : value.Type == PsType.Null ? PsObject.Dictionary(ip.NewDict(0)) : value);
        });
    }

    private int PopRange(int min, int max)
    {
        long v = PopInt();
        if (v < min || v > max) throw new PsError("rangecheck");
        return (int)v;
    }

    // ---------------------------------------------------------------- matrices

    private PsObject MatrixObject(Matrix m) => NewArray(new[]
    {
        PsObject.RealValue((float)m.A), PsObject.RealValue((float)m.B), PsObject.RealValue((float)m.C),
        PsObject.RealValue((float)m.D), PsObject.RealValue((float)m.E), PsObject.RealValue((float)m.F),
    });

    /// <summary>A matrix operand: an array of six numbers.</summary>
    public Matrix PopMatrix()
    {
        var a = PopArray();
        return ToMatrix(a);
    }

    public static Matrix ToMatrix(in PsObject a)
    {
        if (!a.IsArray) throw new PsError("typecheck");
        var items = a.AsArray;
        if (items.Length != 6) throw new PsError("rangecheck");
        var v = new double[6];
        for (int i = 0; i < 6; i++)
        {
            if (!items[i].IsNumber) throw new PsError("typecheck");
            v[i] = items[i].Number;
        }
        return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    /// <summary>A writable array of six elements to receive a matrix.</summary>
    private PsObject PopMatrixArray()
    {
        var a = PopArray(write: true);
        if (a.AsArray.Length != 6) throw new PsError("rangecheck");
        return a;
    }

    private PsObject FillMatrix(PsObject target, Matrix m)
    {
        double[] v = { m.A, m.B, m.C, m.D, m.E, m.F };
        for (int i = 0; i < 6; i++) ArrayPut(target, i, PsObject.RealValue((float)v[i]));
        return target;
    }

    /// <summary>translate, scale, rotate: with a matrix operand the matrix is set, otherwise the CTM is changed.</summary>
    private void Transformation(Func<Matrix> make)
    {
        if (Ops.Count > 0 && Ops.Peek().IsArray)
        {
            var target = PopMatrixArray();
            Push(FillMatrix(target, make()));
            return;
        }
        GState.Ctm = make().Multiply(GState.Ctm);
    }

    private void TransformPoint(Func<Matrix, double, double, (double, double)> f, bool inverse)
    {
        var m = Ops.Count > 0 && Ops.Peek().IsArray ? PopMatrix() : GState.Ctm;
        double y = PopReal(), x = PopReal();
        if (inverse && !m.TryInvert(out m)) throw new PsError("undefinedresult");
        var (rx, ry) = f(m, x, y);
        PushReal(rx);
        PushReal(ry);
    }

    /// <summary>A point in user space, transformed to device space.</summary>
    private (double X, double Y) PopPoint()
    {
        double y = PopReal(), x = PopReal();
        return GState.Ctm.Transform(x, y);
    }

    private (double X, double Y) PopDistance()
    {
        double y = PopReal(), x = PopReal();
        return GState.Ctm.TransformDistance(x, y);
    }

    private PsPath RequirePoint() => GState.Path.HasCurrentPoint ? GState.Path : throw new PsError("nocurrentpoint");

    // ---------------------------------------------------------------- arcs and rectangles

    private void Arc(bool clockwise)
    {
        double a2 = PopReal(), a1 = PopReal(), r = PopReal(), y = PopReal(), x = PopReal();
        GState.Path.Arc(GState.Ctm, x, y, r, a1, a2, clockwise);
    }

    /// <summary>
    /// <c>arct</c> and <c>arcto</c>: a line from the current point towards (x1, y1) and an arc of radius r tangent to
    /// both lines (current point, x1 y1) and (x1 y1, x2 y2). arcto also pushes the two tangent points.
    /// </summary>
    private void ArcTangent(bool push)
    {
        double r = PopReal(), y2 = PopReal(), x2 = PopReal(), y1 = PopReal(), x1 = PopReal();
        var path = RequirePoint();
        if (!GState.Ctm.TryInvert(out var inverse)) throw new PsError("undefinedresult");
        var (x0, y0) = inverse.Transform(path.CurrentX, path.CurrentY);
        double ux = x0 - x1, uy = y0 - y1, vx = x2 - x1, vy = y2 - y1;
        double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
        double cross = ux * vy - uy * vx;
        if (lu == 0 || lv == 0 || Math.Abs(cross) < 1e-12 * lu * lv || r == 0)
        {
            // collinear or degenerate: a line to (x1, y1)
            var (dx, dy) = GState.Ctm.Transform(x1, y1);
            path.LineTo(dx, dy);
            if (push)
            {
                PushReal(x1);
                PushReal(y1);
                PushReal(x1);
                PushReal(y1);
            }
            return;
        }
        ux /= lu;
        uy /= lu;
        vx /= lv;
        vy /= lv;
        double cos = ux * vx + uy * vy, angle = Math.Acos(Math.Clamp(cos, -1, 1));
        double distance = r / Math.Tan(angle / 2); // from (x1, y1) to the tangent points
        double t1x = x1 + ux * distance, t1y = y1 + uy * distance;
        double t2x = x1 + vx * distance, t2y = y1 + vy * distance;
        // the center lies on the bisector
        double bx = ux + vx, by = uy + vy, bl = Math.Sqrt(bx * bx + by * by);
        double centerDistance = r / Math.Sin(angle / 2);
        double cx = x1 + bx / bl * centerDistance, cy = y1 + by / bl * centerDistance;
        double a1 = Math.Atan2(t1y - cy, t1x - cx) * 180 / Math.PI, a2 = Math.Atan2(t2y - cy, t2x - cx) * 180 / Math.PI;
        var (sx, sy) = GState.Ctm.Transform(t1x, t1y);
        path.LineTo(sx, sy);
        path.Arc(GState.Ctm, cx, cy, r, a1, a2, clockwise: cross > 0);
        if (push)
        {
            PushReal(t1x);
            PushReal(t1y);
            PushReal(t2x);
            PushReal(t2y);
        }
    }

    /// <summary>The rectangles of rectfill, rectstroke and rectclip: x y w h, or an array of numbers.</summary>
    private PsPath RectanglePath()
    {
        var numbers = new List<double>();
        if (Ops.Count > 0 && (Ops.Peek().IsArray || Ops.Peek().Type == PsType.String))
        {
            var o = Pop();
            if (o.Type == PsType.String) throw new PsError("rangecheck"); // encoded number strings are not supported
            foreach (var item in o.AsArray.Span)
            {
                if (!item.IsNumber) throw new PsError("typecheck");
                numbers.Add(item.Number);
            }
            if (numbers.Count % 4 != 0) throw new PsError("rangecheck");
        }
        else
        {
            double h = PopReal(), w = PopReal(), y = PopReal(), x = PopReal();
            numbers.AddRange(new[] { x, y, w, h });
        }
        var path = new PsPath();
        var m = GState.Ctm;
        for (int i = 0; i < numbers.Count; i += 4)
        {
            double x = numbers[i], y = numbers[i + 1], w = numbers[i + 2], h = numbers[i + 3];
            var p0 = m.Transform(x, y);
            var p1 = m.Transform(x + w, y);
            var p2 = m.Transform(x + w, y + h);
            var p3 = m.Transform(x, y + h);
            path.MoveTo(p0.X, p0.Y);
            path.LineTo(p1.X, p1.Y);
            path.LineTo(p2.X, p2.Y);
            path.LineTo(p3.X, p3.Y);
            path.ClosePath();
        }
        return path;
    }

    // ---------------------------------------------------------------- painting

    private void Clip(bool evenOdd)
    {
        // the path stays the current path
        if (GState.Path.IsEmpty)
        {
            GState.Clip = new PsClip(Array.Empty<PsSegment>(), evenOdd, GState.Clip);
            return;
        }
        GState.Clip = new PsClip(GState.Path.Segments.ToArray(), evenOdd, GState.Clip);
    }

    private void Fill(bool evenOdd)
    {
        if (Device != null && PaintColor() is { } color) Device.Fill(GState, color, evenOdd);
        GState.Path = new PsPath();
    }

    private void Stroke()
    {
        if (Device != null && PaintColor() is { } color) Device.Stroke(GState, color);
        GState.Path = new PsPath();
    }

    /// <summary>
    /// Appends a user path (PLRM 4.6): an array of numbers and the operators setbbox, ucache, moveto, rmoveto, lineto,
    /// rlineto, curveto, rcurveto, arc, arcn, arct and closepath. Encoded user paths (strings) are not supported.
    /// </summary>
    private void AppendUserPath(in PsObject userPath)
    {
        if (!userPath.IsArray) throw new PsError(userPath.Type == PsType.String ? "rangecheck" : "typecheck");
        var operands = new List<double>();
        foreach (var item in userPath.AsArray.Span)
        {
            CountOperation();
            if (item.IsNumber)
            {
                operands.Add(item.Number);
                continue;
            }
            string op = item.Type switch
            {
                PsType.Name => item.AsName.Text,
                PsType.Operator => item.AsOperator.Name.Text,
                _ => throw new PsError("typecheck"),
            };
            if (op is not ("setbbox" or "ucache" or "moveto" or "rmoveto" or "lineto" or "rlineto" or "curveto" or "rcurveto"
                or "arc" or "arcn" or "arct" or "closepath"))
                throw new PsError("typecheck");
            foreach (double v in operands) PushReal(v);
            operands.Clear();
            ((PsOperator)SystemDict.Map[Key(op)].Ref!).Run(this);
        }
    }

    /// <summary>ufill, ueofill, ustroke: the user path replaces the current path temporarily (as within gsave).</summary>
    private void PaintUserPath(Action paint)
    {
        var userPath = Pop();
        var saved = GState;
        GState = saved.Clone();
        GState.Path = new PsPath();
        try
        {
            AppendUserPath(userPath);
            paint();
        }
        finally
        {
            GState = saved;
        }
    }

    /// <summary>pathforall: calls the procedures with the segments in user space.</summary>
    private sealed class PathForall(PsSegment[] segments, Matrix inverse, PsObject move, PsObject line, PsObject curve, PsObject close) : LoopFrame
    {
        private int _index;

        public override bool Next(PsInterpreter ip)
        {
            if (_index >= segments.Length) return false;
            var s = segments[_index++];
            switch (s.Kind)
            {
                case PsSegmentKind.MoveTo:
                    Point(ip, s.X3, s.Y3);
                    ip.PushProcedure(move);
                    break;
                case PsSegmentKind.LineTo:
                    Point(ip, s.X3, s.Y3);
                    ip.PushProcedure(line);
                    break;
                case PsSegmentKind.CurveTo:
                    Point(ip, s.X1, s.Y1);
                    Point(ip, s.X2, s.Y2);
                    Point(ip, s.X3, s.Y3);
                    ip.PushProcedure(curve);
                    break;
                default:
                    ip.PushProcedure(close);
                    break;
            }
            return true;
        }

        private void Point(PsInterpreter ip, double x, double y)
        {
            var (ux, uy) = inverse.Transform(x, y);
            ip.PushReal(ux);
            ip.PushReal(uy);
        }
    }
}
