using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// Color operators (PLRM 4.8). Device colors are written natively; Indexed, Separation and DeviceN colors are reduced
/// to the color of their base or alternate space (tint transforms are evaluated), CIE-based colors are converted to
/// sRGB.
/// </summary>
internal sealed partial class PsInterpreter
{
    private readonly Dictionary<PsColorSpace, Dictionary<string, PsDeviceColor?>> _colorCache = new();

    private void RegisterColorOperators()
    {
        Op("setgray", ip => ip.SetColor(PsColorSpace.Gray, ip.PopReal()));
        Op("setrgbcolor", ip =>
        {
            double b = ip.PopReal(), g = ip.PopReal(), r = ip.PopReal();
            ip.SetColor(PsColorSpace.Rgb, r, g, b);
        });
        Op("setcmykcolor", ip =>
        {
            double k = ip.PopReal(), y = ip.PopReal(), m = ip.PopReal(), c = ip.PopReal();
            ip.SetColor(PsColorSpace.Cmyk, c, m, y, k);
        });
        Op("sethsbcolor", ip =>
        {
            double b = ip.PopReal(), s = ip.PopReal(), h = ip.PopReal();
            var (r, g, bl) = HsbToRgb(Math.Clamp(h, 0, 1), Math.Clamp(s, 0, 1), Math.Clamp(b, 0, 1));
            ip.SetColor(PsColorSpace.Rgb, r, g, bl);
        });
        Op("currentgray", ip => ip.PushReal((ip.CurrentDeviceColor() ?? PsDeviceColor.Black).ToGray()));
        Op("currentrgbcolor", ip =>
        {
            var (r, g, b) = (ip.CurrentDeviceColor() ?? PsDeviceColor.Black).ToRgb();
            ip.PushReal(r);
            ip.PushReal(g);
            ip.PushReal(b);
        });
        Op("currentcmykcolor", ip =>
        {
            var (c, m, y, k) = (ip.CurrentDeviceColor() ?? PsDeviceColor.Black).ToCmyk();
            ip.PushReal(c);
            ip.PushReal(m);
            ip.PushReal(y);
            ip.PushReal(k);
        });
        Op("currenthsbcolor", ip =>
        {
            var (r, g, b) = (ip.CurrentDeviceColor() ?? PsDeviceColor.Black).ToRgb();
            var (h, s, v) = RgbToHsb(r, g, b);
            ip.PushReal(h);
            ip.PushReal(s);
            ip.PushReal(v);
        });
        Op("setcolorspace", ip =>
        {
            var space = ip.ParseColorSpace(ip.Pop(), 0);
            ip.GState.ColorSpace = space;
            ip.GState.Color = space.InitialColor();
            ip.GState.Pattern = PsObject.Null;
        });
        Op("currentcolorspace", ip =>
        {
            var space = ip.GState.ColorSpace;
            ip.Push(space.Definition.IsArray ? space.Definition
                : ip.NewArray(new[] { PsObject.Name(ip.Name(space.Family.ToString())) }));
        });
        Op("setcolor", ip =>
        {
            var space = ip.GState.ColorSpace;
            if (space.Family == PsColorFamily.Pattern)
            {
                var pattern = ip.Pop();
                if (pattern.Type != PsType.Dictionary) throw new PsError("typecheck");
                int n = space.Base?.Components ?? 0;
                var components = new double[n];
                for (int i = n - 1; i >= 0; i--) components[i] = ip.PopReal();
                ip.GState.Color = components;
                ip.GState.Pattern = pattern;
                return;
            }
            var values = new double[space.Components];
            for (int i = values.Length - 1; i >= 0; i--) values[i] = ip.PopReal();
            ip.GState.Color = values;
        });
        Op("currentcolor", ip =>
        {
            foreach (double c in ip.GState.Color) ip.PushReal(c);
            if (ip.GState.ColorSpace.Family == PsColorFamily.Pattern) ip.Push(ip.GState.Pattern);
        });
        Op("setpattern", ip =>
        {
            // pattern colors are painted from stage 5 on; the color space and pattern are already set up
            var pattern = ip.Pop();
            if (pattern.Type != PsType.Dictionary) throw new PsError("typecheck");
            ip.GState.ColorSpace = new PsColorSpace { Family = PsColorFamily.Pattern, Components = 1, Base = null };
            ip.GState.Color = Array.Empty<double>();
            ip.GState.Pattern = pattern;
        });
    }

    private void SetColor(PsColorSpace space, params double[] values)
    {
        GState.ColorSpace = space;
        GState.Color = values;
        GState.Pattern = PsObject.Null;
    }

    // ---------------------------------------------------------------- color spaces

    private PsColorSpace ParseColorSpace(in PsObject o, int depth)
    {
        if (depth > 8) throw new PsError("limitcheck");
        PsName family;
        PsArray? array = null;
        if (o.Type == PsType.Name) family = o.AsName;
        else if (o.IsArray && o.AsArray.Length > 0 && o.AsArray[0].Type == PsType.Name)
        {
            array = o.AsArray;
            family = array[0].AsName;
        }
        else throw new PsError("typecheck");
        PsObject Item(int i) => array != null && i < array.Length ? array[i] : throw new PsError("rangecheck");
        switch (family.Text)
        {
            case "DeviceGray":
            case "G":
                return PsColorSpace.Gray;
            case "DeviceRGB":
            case "RGB":
                return PsColorSpace.Rgb;
            case "DeviceCMYK":
            case "CMYK":
                return PsColorSpace.Cmyk;
            case "Pattern":
                return new PsColorSpace
                {
                    Family = PsColorFamily.Pattern, Components = 1, Definition = o,
                    Base = array != null && array.Length > 1 ? ParseColorSpace(array[1], depth + 1) : null,
                };
            case "Indexed":
            case "I":
            {
                var baseSpace = ParseColorSpace(Item(1), depth + 1);
                var hival = Item(2);
                if (hival.Type != PsType.Integer) throw new PsError("typecheck");
                if (hival.Int < 0 || hival.Int > 4095) throw new PsError("rangecheck");
                var lookup = Item(3);
                if (lookup.Type == PsType.String)
                {
                    if (lookup.AsString.Length < (hival.Int + 1) * baseSpace.Components) throw new PsError("rangecheck");
                }
                else if (!lookup.IsProcedure) throw new PsError("typecheck");
                return new PsColorSpace
                {
                    Family = PsColorFamily.Indexed, Components = 1, Definition = o, Base = baseSpace,
                    HighValue = (int)hival.Int, Lookup = lookup,
                };
            }
            case "Separation":
            {
                var alternate = ParseColorSpace(Item(2), depth + 1);
                var tint = Item(3);
                if (!tint.IsProcedure) throw new PsError("typecheck");
                string name = Item(1).Type == PsType.Name ? Item(1).AsName.Text : Item(1).Type == PsType.String ? Item(1).AsString.Text : "";
                return new PsColorSpace
                {
                    Family = PsColorFamily.Separation, Components = 1, Definition = o, Base = alternate, TintTransform = tint,
                    Lookup = PsObject.Name(Name(name)),
                };
            }
            case "DeviceN":
            {
                var names = Item(1);
                if (!names.IsArray) throw new PsError("typecheck");
                if (names.AsArray.Length == 0 || names.AsArray.Length > 32) throw new PsError("rangecheck");
                var alternate = ParseColorSpace(Item(2), depth + 1);
                var tint = Item(3);
                if (!tint.IsProcedure) throw new PsError("typecheck");
                return new PsColorSpace
                {
                    Family = PsColorFamily.DeviceN, Components = names.AsArray.Length, Definition = o, Base = alternate,
                    TintTransform = tint, Lookup = names,
                };
            }
            case "CIEBasedA":
            case "CIEBasedABC":
            case "CIEBasedDEF":
            case "CIEBasedDEFG":
            {
                var dict = Item(1);
                if (dict.Type != PsType.Dictionary) throw new PsError("typecheck");
                var f = Enum.Parse<PsColorFamily>(family.Text);
                return new PsColorSpace
                {
                    Family = f, Definition = o, Cie = dict.AsDict,
                    Components = f switch { PsColorFamily.CIEBasedA => 1, PsColorFamily.CIEBasedDEFG => 4, _ => 3 },
                };
            }
            case "ICCBased":
            {
                // not a PostScript color space, but accepted by Ghostscript: by the number of components
                var stream = Item(1);
                int n = stream.Type == PsType.Dictionary && stream.AsDict.Map.TryGetValue(Key("N"), out var nv) && nv.Type == PsType.Integer ? (int)nv.Int : 3;
                return n switch { 1 => PsColorSpace.Gray, 4 => PsColorSpace.Cmyk, _ => PsColorSpace.Rgb };
            }
            default:
                throw new PsError("undefined");
        }
    }

    /// <summary>The current color as a device color; null if nothing is painted (Separation /None, patterns for now).</summary>
    public PsDeviceColor? CurrentDeviceColor() => ToDevice(GState.ColorSpace, GState.Color, 0);

    private PsDeviceColor? ToDevice(PsColorSpace space, double[] c, int depth)
    {
        if (depth > 8) return PsDeviceColor.Black;
        double C(int i) => i < c.Length ? c[i] : 0;
        switch (space.Family)
        {
            case PsColorFamily.DeviceGray:
                return PsDeviceColor.Gray(C(0));
            case PsColorFamily.DeviceRGB:
                return PsDeviceColor.Rgb(C(0), C(1), C(2));
            case PsColorFamily.DeviceCMYK:
                return PsDeviceColor.Cmyk(C(0), C(1), C(2), C(3));
            case PsColorFamily.Pattern:
                return null;
        }
        // the other spaces are evaluated (with PostScript procedures) once per color
        if (!_colorCache.TryGetValue(space, out var cache))
        {
            if (_colorCache.Count > 256) _colorCache.Clear();
            _colorCache[space] = cache = new Dictionary<string, PsDeviceColor?>();
        }
        string key = string.Join(",", c.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        if (cache.TryGetValue(key, out var cached)) return cached;
        var result = space.Family switch
        {
            PsColorFamily.Indexed => IndexedToDevice(space, C(0), depth),
            PsColorFamily.Separation when space.Lookup.Type == PsType.Name && space.Lookup.AsName.Text == "None" => null,
            PsColorFamily.Separation or PsColorFamily.DeviceN => ToDevice(space.Base!, Tint(space, c), depth + 1),
            _ => CieToDevice(space, c),
        };
        if (cache.Count < 4096) cache[key] = result;
        return result;
    }

    private PsDeviceColor? IndexedToDevice(PsColorSpace space, double index, int depth)
    {
        int i = (int)Math.Clamp(Math.Round(index), 0, space.HighValue);
        var baseSpace = space.Base!;
        int n = baseSpace.Components;
        double[] components;
        if (space.Lookup.Type == PsType.String)
        {
            var bytes = space.Lookup.AsString;
            components = new double[n];
            for (int k = 0; k < n; k++)
            {
                var (lo, hi) = ComponentRange(baseSpace, k);
                components[k] = lo + (hi - lo) * bytes[i * n + k] / 255.0;
            }
        }
        else components = Numbers(CallProcedure(space.Lookup, new[] { PsObject.Integer(i) }), n);
        return ToDevice(baseSpace, components, depth + 1);
    }

    private double[] Tint(PsColorSpace space, double[] c) =>
        Numbers(CallProcedure(space.TintTransform, c.Select(v => PsObject.RealValue((float)Math.Clamp(v, 0, 1))).ToArray()), space.Base!.Components);

    /// <summary>The last <paramref name="n"/> results of a procedure as numbers.</summary>
    private static double[] Numbers(PsObject[] results, int n)
    {
        var values = new double[n];
        int start = results.Length - n;
        for (int i = 0; i < n; i++)
        {
            int j = start + i;
            values[i] = j >= 0 && results[j].IsNumber ? results[j].Number : 0;
        }
        return values;
    }

    /// <summary>The range of a component (0 to 1 except for CIE-based spaces).</summary>
    private (double Lo, double Hi) ComponentRange(PsColorSpace space, int i)
    {
        if (space.Cie == null) return (0, 1);
        string name = space.Family switch
        {
            PsColorFamily.CIEBasedA => "RangeA",
            PsColorFamily.CIEBasedABC => "RangeABC",
            PsColorFamily.CIEBasedDEF => "RangeDEF",
            _ => "RangeDEFG",
        };
        var range = CieNumbers(space.Cie, name, 2 * space.Components, 0, 1);
        return (range[2 * i], range[2 * i + 1]);
    }

    private double[] CieNumbers(PsDict dict, string key, int count, double even, double odd)
    {
        var result = new double[count];
        for (int i = 0; i < count; i++) result[i] = i % 2 == 0 ? even : odd;
        if (dict.Map.TryGetValue(Key(key), out var v) && v.IsArray && v.AsArray.Length >= count)
            for (int i = 0; i < count; i++)
                if (v.AsArray[i].IsNumber) result[i] = v.AsArray[i].Number;
        return result;
    }

    private double[] CieMatrix(PsDict dict, string key, int count, double[] fallback)
    {
        if (dict.Map.TryGetValue(Key(key), out var v) && v.IsArray && v.AsArray.Length == count)
            return Enumerable.Range(0, count).Select(i => v.AsArray[i].IsNumber ? v.AsArray[i].Number : 0).ToArray();
        return fallback;
    }

    /// <summary>Applies the decode procedures of a CIE dictionary entry (an array of procedures, or one for A).</summary>
    private double Decode(PsDict dict, string key, int index, double value)
    {
        if (!dict.Map.TryGetValue(Key(key), out var procs)) return value;
        var proc = procs.IsProcedure ? procs : procs.IsArray && index < procs.AsArray.Length ? procs.AsArray[index] : PsObject.Null;
        if (!proc.IsProcedure) return value;
        var result = CallProcedure(proc, new[] { PsObject.RealValue((float)value) });
        return result.Length > 0 && result[^1].IsNumber ? result[^1].Number : value;
    }

    /// <summary>A CIE-based color to sRGB (PLRM 4.8.3): decode, matrices, then XYZ adapted from the white point to D65.</summary>
    private PsDeviceColor? CieToDevice(PsColorSpace space, double[] c)
    {
        var d = space.Cie!;
        double C(int i) => i < c.Length ? c[i] : 0;
        if (space.Family is PsColorFamily.CIEBasedDEF or PsColorFamily.CIEBasedDEFG)
        {
            // the lookup tables of DEF and DEFG are not evaluated: the components are taken as RGB or CMYK
            return space.Family == PsColorFamily.CIEBasedDEF ? PsDeviceColor.Rgb(C(0), C(1), C(2)) : PsDeviceColor.Cmyk(C(0), C(1), C(2), C(3));
        }
        double l, m, n;
        if (space.Family == PsColorFamily.CIEBasedA)
        {
            var range = CieNumbers(d, "RangeA", 2, 0, 1);
            double a = Decode(d, "DecodeA", 0, Math.Clamp(C(0), range[0], range[1]));
            var matrix = CieMatrix(d, "MatrixA", 3, new double[] { 1, 1, 1 });
            (l, m, n) = (a * matrix[0], a * matrix[1], a * matrix[2]);
        }
        else
        {
            var range = CieNumbers(d, "RangeABC", 6, 0, 1);
            double a = Decode(d, "DecodeABC", 0, Math.Clamp(C(0), range[0], range[1]));
            double b = Decode(d, "DecodeABC", 1, Math.Clamp(C(1), range[2], range[3]));
            double cc = Decode(d, "DecodeABC", 2, Math.Clamp(C(2), range[4], range[5]));
            var matrix = CieMatrix(d, "MatrixABC", 9, new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 });
            l = a * matrix[0] + b * matrix[3] + cc * matrix[6];
            m = a * matrix[1] + b * matrix[4] + cc * matrix[7];
            n = a * matrix[2] + b * matrix[5] + cc * matrix[8];
        }
        var rangeLmn = CieNumbers(d, "RangeLMN", 6, 0, 1);
        l = Decode(d, "DecodeLMN", 0, Math.Clamp(l, rangeLmn[0], rangeLmn[1]));
        m = Decode(d, "DecodeLMN", 1, Math.Clamp(m, rangeLmn[2], rangeLmn[3]));
        n = Decode(d, "DecodeLMN", 2, Math.Clamp(n, rangeLmn[4], rangeLmn[5]));
        var lmn = CieMatrix(d, "MatrixLMN", 9, new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 });
        double x = l * lmn[0] + m * lmn[3] + n * lmn[6];
        double y = l * lmn[1] + m * lmn[4] + n * lmn[7];
        double z = l * lmn[2] + m * lmn[5] + n * lmn[8];
        var white = CieMatrix(d, "WhitePoint", 3, new[] { 0.9505, 1.0, 1.089 });
        // scaling the white point to D65
        x *= 0.9505 / Math.Max(white[0], 1e-6);
        y *= 1.0 / Math.Max(white[1], 1e-6);
        z *= 1.089 / Math.Max(white[2], 1e-6);
        double r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
        double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        double bl = 0.0557 * x - 0.2040 * y + 1.0570 * z;
        return PsDeviceColor.Rgb(Gamma(r), Gamma(g), Gamma(bl));
    }

    private static double Gamma(double v)
    {
        v = Math.Clamp(v, 0, 1);
        return v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
    }

    private static (double R, double G, double B) HsbToRgb(double h, double s, double v)
    {
        if (s == 0) return (v, v, v);
        double h6 = (h * 6) % 6;
        int i = (int)Math.Floor(h6);
        double f = h6 - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    private static (double H, double S, double B) RgbToHsb(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double s = max == 0 ? 0 : delta / max, h = 0;
        if (delta > 0)
        {
            if (max == r) h = (g - b) / delta;
            else if (max == g) h = 2 + (b - r) / delta;
            else h = 4 + (r - g) / delta;
            h /= 6;
            if (h < 0) h += 1;
        }
        return (h, s, max);
    }
}
