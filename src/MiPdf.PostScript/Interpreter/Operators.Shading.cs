using System.Buffers.Binary;
using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// Smooth shading (PLRM 4.9.3, LanguageLevel 3): <c>shfill</c> and the shadings of shading patterns, written as PDF
/// shadings. Functions (types 0, 2 and 3) become PDF functions. Colors of other spaces than DeviceGray, DeviceRGB and
/// DeviceCMYK are converted to device colors as everywhere else: through a sampled function for function-based,
/// axial and radial shadings and parametric meshes, vertex by vertex for the other meshes. Mesh data is decoded and
/// written again (arrays, packed strings and files alike).
/// </summary>
internal sealed partial class PsInterpreter
{
    private const int MaxMeshVertices = 4_000_000;

    private readonly Dictionary<PsDict, (KeyValuePair<PsObject, PsObject>[] Entries, PdfObject? Shading)> _shadingCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfObject, PdfReference> _shadingReferences = new(ReferenceEqualityComparer.Instance);

    private void RegisterShadingOperators()
    {
        Op("shfill", ip =>
        {
            var dict = ip.PopDict();
            var shading = ip.ShadingFor(dict);
            if (shading == null || ip.Device is not { Document: { } document } device) return;
            if (!ip._shadingReferences.TryGetValue(shading, out var reference))
            {
                reference = document.AddObject(shading);
                if (ip._shadingReferences.Count < 10_000) ip._shadingReferences[shading] = reference;
            }
            device.PaintShading(ip.GState, reference, ip.GState.Ctm);
        });
    }

    /// <summary>The PDF shading of a shading dictionary; the same dictionary with the same entries gives the same object.</summary>
    private PdfObject? ShadingFor(PsDict dict)
    {
        var entries = dict.Map.ToArray();
        if (_shadingCache.TryGetValue(dict, out var cached) && SameEntries(cached.Entries, entries)) return cached.Shading;
        var shading = ConvertShading(dict);
        if (_shadingCache.Count < 1000) _shadingCache[dict] = (entries, shading);
        return shading;
    }

    private static bool SameEntries(KeyValuePair<PsObject, PsObject>[] a, KeyValuePair<PsObject, PsObject>[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!PsKeyComparer.Instance.Equals(a[i].Key, b[i].Key) || !PsKeyComparer.Instance.Equals(a[i].Value, b[i].Value))
                return false;
        return true;
    }

    // ------------------------------------------------------------------ shading dictionaries

    /// <summary>A shading dictionary as PDF shading (direct object); null if it paints nothing.</summary>
    private PdfObject? ConvertShading(PsDict d)
    {
        var typeObject = DictValue(d, "ShadingType");
        if (typeObject.Type == PsType.Null) throw new PsError("undefined");
        if (typeObject.Type != PsType.Integer) throw new PsError("typecheck");
        if (typeObject.Int is < 1 or > 7) throw new PsError("rangecheck");
        int type = (int)typeObject.Int;
        var spaceObject = DictValue(d, "ColorSpace");
        if (spaceObject.Type == PsType.Null) throw new PsError("undefined");
        var space = ParseColorSpace(spaceObject, 0);
        if (space.Family == PsColorFamily.Pattern) throw new PsError("typecheck");
        var functionObject = DictValue(d, "Function");
        bool hasFunction = functionObject.Type != PsType.Null;
        if (type <= 3 && !hasFunction) throw new PsError("undefined");
        if (hasFunction && space.Family == PsColorFamily.Indexed) throw new PsError("typecheck");

        var colors = new ShadingColors(this, space);
        if (!colors.Paints) return null;

        PdfDictionary result = type <= 3 ? new PdfDictionary() : new PdfStream();
        result[PdfName.Get("ShadingType")] = new PdfNumber(type);
        var background = DictValue(d, "Background");
        if (background.Type != PsType.Null)
        {
            var values = NumberArray(background, -1);
            if (values.Length != space.Components) throw new PsError("rangecheck");
            if (colors.Convert(values) is { } converted) result[PdfName.Get("Background")] = Numbers(converted);
        }
        var bbox = DictValue(d, "BBox");
        if (bbox.Type != PsType.Null) result[PdfName.BBox] = Numbers(NumberArray(bbox, 4));
        if (DictValue(d, "AntiAlias") is { Type: PsType.Boolean } antiAlias) result[PdfName.Get("AntiAlias")] = antiAlias.Bool ? PdfBoolean.True : PdfBoolean.False;

        PdfObject? function = null;
        PdfFunction? evaluator = null;
        if (hasFunction)
        {
            function = ConvertFunction(functionObject, 0);
            evaluator = PdfFunction.LoadCombined(function) ?? throw new PsError("rangecheck");
            if (evaluator.Outputs != space.Components || evaluator.Inputs != (type == 1 ? 2 : 1)) throw new PsError("rangecheck");
        }

        switch (type)
        {
            case 1:
            {
                var domain = OptionalNumbers(d, "Domain", 4) ?? new double[] { 0, 1, 0, 1 };
                result[PdfName.Get("Domain")] = Numbers(domain);
                if (DictValue(d, "Matrix") is { Type: not PsType.Null } matrix) result[PdfName.Get("Matrix")] = PsPatternPaint.ToArray(ToMatrix(matrix));
                result[PdfName.Get("Function")] = colors.Native ? function! : colors.Sampled(evaluator!, domain);
                break;
            }
            case 2:
            case 3:
            {
                var coordsObject = DictValue(d, "Coords");
                if (coordsObject.Type == PsType.Null) throw new PsError("rangecheck");
                var coords = NumberArray(coordsObject, type == 2 ? 4 : 6);
                if (type == 3 && (coords[2] < 0 || coords[5] < 0)) throw new PsError("rangecheck");
                var domain = OptionalNumbers(d, "Domain", 2) ?? new double[] { 0, 1 };
                result[PdfName.Get("Coords")] = Numbers(coords);
                result[PdfName.Get("Domain")] = Numbers(domain);
                var extend = DictValue(d, "Extend");
                if (extend.Type != PsType.Null)
                {
                    if (!extend.IsArray) throw new PsError("typecheck");
                    if (extend.AsArray.Length != 2) throw new PsError("rangecheck");
                    if (extend.AsArray[0].Type != PsType.Boolean || extend.AsArray[1].Type != PsType.Boolean) throw new PsError("typecheck");
                    result[PdfName.Get("Extend")] = new PdfArray(extend.AsArray[0].Bool ? PdfBoolean.True : PdfBoolean.False,
                        extend.AsArray[1].Bool ? PdfBoolean.True : PdfBoolean.False);
                }
                result[PdfName.Get("Function")] = colors.Native ? function! : colors.Sampled(evaluator!, domain);
                break;
            }
            default:
                if (!ConvertMesh(d, type, space, colors, function, evaluator, (PdfStream)result)) return null;
                break;
        }
        result[PdfName.ColorSpace] = PdfName.Get(colors.Family.ToString());
        return result;
    }

    private double[]? OptionalNumbers(PsDict d, string key, int count)
    {
        var v = DictValue(d, key);
        return v.Type == PsType.Null ? null : NumberArray(v, count);
    }

    private static PdfArray Numbers(IEnumerable<double> values) => new(values.Select(v => (PdfObject)new PdfNumber(v)));

    // ------------------------------------------------------------------ colors

    /// <summary>
    /// The colors of a shading in PDF: those of a device space as they are, the others converted to the device colors
    /// they paint (the family is that of the first converted color).
    /// </summary>
    private sealed class ShadingColors
    {
        private readonly PsInterpreter _ip;
        private readonly PsColorSpace _space;

        public ShadingColors(PsInterpreter ip, PsColorSpace space)
        {
            _ip = ip;
            _space = space;
            Native = space.Family is PsColorFamily.DeviceGray or PsColorFamily.DeviceRGB or PsColorFamily.DeviceCMYK;
            if (Native) Family = space.Family;
            else if (ip.ToDevice(space, space.InitialColor(), 0) is { } first) Family = first.Family;
            else Paints = false;
        }

        public bool Native { get; }

        /// <summary>False for colors that paint nothing (Separation /None).</summary>
        public bool Paints { get; } = true;

        public PsColorFamily Family { get; }

        public int Components => Family switch { PsColorFamily.DeviceGray => 1, PsColorFamily.DeviceRGB => 3, _ => 4 };

        /// <summary>A color of the shading's space as device components; null if it paints nothing.</summary>
        public double[]? Convert(double[] values)
        {
            if (Native) return values.Select(v => Math.Clamp(v, 0, 1)).ToArray();
            if (_ip.ToDevice(_space, values, 0) is not { } c) return null;
            if (c.Family == Family)
                return Family switch { PsColorFamily.DeviceGray => new[] { c.C0 }, PsColorFamily.DeviceRGB => new[] { c.C0, c.C1, c.C2 }, _ => new[] { c.C0, c.C1, c.C2, c.C3 } };
            switch (Family)
            {
                case PsColorFamily.DeviceGray:
                    return new[] { c.ToGray() };
                case PsColorFamily.DeviceRGB:
                {
                    var (r, g, b) = c.ToRgb();
                    return new[] { r, g, b };
                }
                default:
                {
                    var (cc, m, y, k) = c.ToCmyk();
                    return new[] { cc, m, y, k };
                }
            }
        }

        /// <summary>
        /// A sampled function (16-bit samples) giving the device color of a function of the shading's space: 256 samples
        /// for one input, 64 x 64 for two.
        /// </summary>
        public PdfStream Sampled(PdfFunction function, double[] domain)
        {
            int inputs = domain.Length / 2, size = inputs == 1 ? 256 : 64, n = Components;
            int count = inputs == 1 ? size : size * size;
            var data = new byte[count * n * 2];
            var x = new double[inputs];
            var values = new double[function.Outputs];
            for (int i = 0; i < count; i++)
            {
                x[0] = domain[0] + (domain[1] - domain[0]) * (i % size) / (size - 1);
                if (inputs == 2) x[1] = domain[2] + (domain[3] - domain[2]) * (i / size) / (size - 1);
                function.Evaluate(x, values);
                var color = Convert(values) ?? new double[n];
                for (int c = 0; c < n; c++)
                    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan((i * n + c) * 2), (ushort)Math.Round(Math.Clamp(color[c], 0, 1) * 65535));
            }
            return new PdfStream(data)
            {
                [PdfName.Get("FunctionType")] = new PdfNumber(0),
                [PdfName.Get("Domain")] = Numbers(domain),
                [PdfName.Get("Range")] = Numbers(Enumerable.Range(0, 2 * n).Select(i => (double)(i % 2))),
                [PdfName.Get("Size")] = Numbers(Enumerable.Repeat((double)size, inputs)),
                [PdfName.Get("BitsPerSample")] = new PdfNumber(16),
            };
        }
    }

    // ------------------------------------------------------------------ functions

    private static readonly int[] SampleBits = { 1, 2, 4, 8, 12, 16, 24, 32 };

    /// <summary>A function dictionary (types 0, 2, 3) or an array of them as PDF function.</summary>
    private PdfObject ConvertFunction(in PsObject f, int depth)
    {
        if (depth > 8) throw new PsError("limitcheck");
        if (f.IsArray && !f.IsProcedure)
        {
            var parts = new PdfArray();
            foreach (var item in f.AsArray.Span) parts.Add(ConvertFunction(item, depth + 1));
            return parts;
        }
        if (f.Type != PsType.Dictionary) throw new PsError("typecheck");
        var d = f.AsDict;
        var typeObject = DictValue(d, "FunctionType");
        if (typeObject.Type != PsType.Integer) throw new PsError(typeObject.Type == PsType.Null ? "undefined" : "typecheck");
        int type = (int)typeObject.Int;
        if (type is not (0 or 2 or 3)) throw new PsError("rangecheck");
        var domainObject = DictValue(d, "Domain");
        if (domainObject.Type == PsType.Null) throw new PsError("rangecheck");
        var domain = NumberArray(domainObject, -1);
        if (domain.Length < 2 || domain.Length % 2 != 0 || (type != 0 && domain.Length != 2)) throw new PsError("rangecheck");
        for (int i = 0; i < domain.Length; i += 2)
            if (domain[i] > domain[i + 1]) throw new PsError("rangecheck");
        var range = OptionalNumbers(d, "Range", -1);
        if (range != null && (range.Length < 2 || range.Length % 2 != 0)) throw new PsError("rangecheck");

        PdfDictionary result;
        switch (type)
        {
            case 0:
            {
                if (range == null) throw new PsError("rangecheck");
                var sizeObject = DictValue(d, "Size");
                if (sizeObject.Type == PsType.Null) throw new PsError("rangecheck");
                var size = NumberArray(sizeObject, domain.Length / 2);
                if (size.Any(s => s < 1 || s != Math.Floor(s))) throw new PsError("rangecheck");
                var bitsObject = DictValue(d, "BitsPerSample");
                if (bitsObject.Type != PsType.Integer || !SampleBits.Contains((int)bitsObject.Int)) throw new PsError("rangecheck");
                int bits = (int)bitsObject.Int;
                double samples = size.Aggregate(1.0, (a, b) => a * b) * (range.Length / 2);
                if (samples * bits / 8 > 1 << 26) throw new PsError("limitcheck");
                var data = new byte[(int)Math.Ceiling(samples * bits / 8)];
                var source = DictValue(d, "DataSource");
                if (source.Type == PsType.String) source.AsString.Span[..Math.Min(data.Length, source.AsString.Length)].CopyTo(data);
                else if (source.Type == PsType.File)
                {
                    for (int got = 0, n; got < data.Length && (n = source.AsFile.Read(data.AsSpan(got))) > 0;) got += n;
                }
                else throw new PsError(source.Type == PsType.Null ? "undefined" : "typecheck");
                var stream = new PdfStream(data);
                stream[PdfName.Get("Size")] = Numbers(size);
                stream[PdfName.Get("BitsPerSample")] = new PdfNumber(bits);
                if (DictValue(d, "Order") is { Type: PsType.Integer } order) stream[PdfName.Get("Order")] = new PdfNumber(order.Int);
                if (OptionalNumbers(d, "Encode", -1) is { } encode) stream[PdfName.Get("Encode")] = Numbers(encode);
                if (OptionalNumbers(d, "Decode", -1) is { } decode) stream[PdfName.Get("Decode")] = Numbers(decode);
                result = stream;
                break;
            }
            case 2:
            {
                var n = DictValue(d, "N");
                if (!n.IsNumber) throw new PsError("rangecheck");
                result = new PdfDictionary { [PdfName.Get("N")] = new PdfNumber(n.Number) };
                if (OptionalNumbers(d, "C0", -1) is { } c0) result[PdfName.Get("C0")] = Numbers(c0);
                if (OptionalNumbers(d, "C1", -1) is { } c1) result[PdfName.Get("C1")] = Numbers(c1);
                break;
            }
            default:
            {
                var functions = DictValue(d, "Functions");
                if (!functions.IsArray || functions.AsArray.Length == 0) throw new PsError("rangecheck");
                int k = functions.AsArray.Length;
                var bounds = OptionalNumbers(d, "Bounds", -1) ?? throw new PsError("rangecheck");
                var encode = OptionalNumbers(d, "Encode", -1) ?? throw new PsError("rangecheck");
                if (bounds.Length != k - 1 || encode.Length != 2 * k) throw new PsError("rangecheck");
                var parts = new PdfArray();
                foreach (var item in functions.AsArray.Span)
                {
                    if (item.Type != PsType.Dictionary) throw new PsError("typecheck");
                    parts.Add(ConvertFunction(item, depth + 1));
                }
                result = new PdfDictionary
                {
                    [PdfName.Get("Functions")] = parts,
                    [PdfName.Get("Bounds")] = Numbers(bounds),
                    [PdfName.Get("Encode")] = Numbers(encode),
                };
                break;
            }
        }
        result[PdfName.Get("FunctionType")] = new PdfNumber(type);
        result[PdfName.Get("Domain")] = Numbers(domain);
        if (range != null) result[PdfName.Get("Range")] = Numbers(range);
        if (PdfFunction.Load(result) == null) throw new PsError("rangecheck");
        return result;
    }

    // ------------------------------------------------------------------ meshes

    private static readonly int[] CoordinateBits = { 1, 2, 4, 8, 12, 16, 24, 32 };
    private static readonly int[] ComponentBits = { 1, 2, 4, 8, 12, 16 };

    /// <summary>Reads mesh data: numbers from an array, or packed values with their decode ranges.</summary>
    private sealed class MeshReader
    {
        private readonly PsArray? _array;
        private readonly byte[] _data = Array.Empty<byte>();
        private int _index;
        private long _bit;
        private readonly int _flagBits, _coordinateBits, _componentBits;
        private readonly double[] _decode;

        public MeshReader(PsArray array)
        {
            _array = array;
            _decode = Array.Empty<double>();
        }

        public MeshReader(byte[] data, int flagBits, int coordinateBits, int componentBits, double[] decode)
        {
            _data = data;
            (_flagBits, _coordinateBits, _componentBits, _decode) = (flagBits, coordinateBits, componentBits, decode);
        }

        /// <summary>True if <paramref name="flags"/> flags, <paramref name="points"/> points and <paramref name="values"/> values follow.</summary>
        public bool Has(int flags, int points, int values)
        {
            if (_array != null) return _index + flags + 2 * points + values <= _array.Length;
            long bits = (long)flags * _flagBits + 2L * points * _coordinateBits + (long)values * _componentBits;
            return _bit + bits <= (long)_data.Length * 8;
        }

        public int Flag() => _array != null ? (int)Number() : (int)Read(_flagBits);

        public (double X, double Y) Point() => (Value(_coordinateBits, 0), Value(_coordinateBits, 1));

        /// <summary>The <paramref name="i"/>-th color value (or the parameter t).</summary>
        public double Component(int i) => Value(_componentBits, 2 + i);

        /// <summary>Packed data: the next vertex (or patch) starts at a byte boundary.</summary>
        public void Align() => _bit = (_bit + 7) & ~7L;

        private double Value(int bits, int pair)
        {
            if (_array != null) return Number();
            ulong raw = Read(bits);
            double max = bits >= 32 ? uint.MaxValue : (1UL << bits) - 1;
            return 2 * pair + 1 < _decode.Length ? _decode[2 * pair] + raw * (_decode[2 * pair + 1] - _decode[2 * pair]) / max : raw;
        }

        private double Number()
        {
            var o = _array![_index++];
            if (!o.IsNumber) throw new PsError("typecheck");
            return o.Number;
        }

        private ulong Read(int bits)
        {
            ulong v = 0;
            for (int i = 0; i < bits; i++, _bit++) v = (v << 1) | (uint)((_data[_bit >> 3] >> (7 - (int)(_bit & 7))) & 1);
            return v;
        }
    }

    /// <summary>A vertex of a mesh: flag (or -1), position and values (colors or t).</summary>
    private readonly record struct MeshVertex(int Flag, double X, double Y, double[] Values);

    /// <summary>Decodes the mesh data, converts the colors and writes the mesh as PDF stream; false if it is empty.</summary>
    private bool ConvertMesh(PsDict d, int type, PsColorSpace space, ShadingColors colors, PdfObject? function, PdfFunction? evaluator, PdfStream result)
    {
        bool hasFunction = function != null;
        int nv = hasFunction ? 1 : space.Components;
        int verticesPerRow = 0;
        if (type == 5)
        {
            var vpr = DictValue(d, "VerticesPerRow");
            if (vpr.Type == PsType.Null) throw new PsError("undefined");
            if (vpr.Type != PsType.Integer) throw new PsError("typecheck");
            if (vpr.Int < 2) throw new PsError("rangecheck");
            verticesPerRow = (int)Math.Min(vpr.Int, 1_000_000);
        }
        var source = DictValue(d, "DataSource");
        MeshReader reader;
        if (source.IsArray && !source.IsProcedure) reader = new MeshReader(source.AsArray);
        else
        {
            int MeshBits(string key, int[] allowed)
            {
                var v = DictValue(d, key);
                if (v.Type == PsType.Null) throw new PsError("undefined");
                if (v.Type != PsType.Integer) throw new PsError("typecheck");
                if (!allowed.Contains((int)v.Int)) throw new PsError("rangecheck");
                return (int)v.Int;
            }
            int coordinateBits = MeshBits("BitsPerCoordinate", CoordinateBits);
            int componentBits = MeshBits("BitsPerComponent", ComponentBits);
            int flagBits = type == 5 ? 0 : MeshBits("BitsPerFlag", new[] { 2, 4, 8 });
            double[] decode;
            if (DictValue(d, "Decode") is { Type: not PsType.Null } decodeObject)
            {
                decode = NumberArray(decodeObject, -1);
                if (decode.Length < 4 + 2 * nv) throw new PsError("rangecheck");
            }
            else
            {
                // no decode array: coordinates as they are, values over their range
                double coordinateMax = coordinateBits >= 32 ? uint.MaxValue : (1UL << coordinateBits) - 1;
                decode = new[] { 0, coordinateMax, 0, coordinateMax }.Concat(Enumerable.Range(0, nv).SelectMany(_ => new[] { 0.0, 1.0 })).ToArray();
            }
            byte[] data = source.Type switch
            {
                PsType.String => source.AsString.ToArray(),
                PsType.File => ReadAll(source.AsFile),
                PsType.Null => throw new PsError("undefined"),
                _ => throw new PsError("typecheck"),
            };
            reader = new MeshReader(data, flagBits, coordinateBits, componentBits, decode);
        }

        // decode: vertices in the order of the data (patches as their points and colors)
        var vertices = new List<MeshVertex>();
        var patches = new List<(int Flag, (double X, double Y)[] Points, double[][] Colors)>();
        double[] Values()
        {
            var values = new double[nv];
            for (int i = 0; i < nv; i++) values[i] = reader.Component(i);
            return values;
        }
        switch (type)
        {
            case 4:
                while (reader.Has(1, 1, nv) && vertices.Count < MaxMeshVertices)
                {
                    int flag = reader.Flag();
                    var (x, y) = reader.Point();
                    vertices.Add(new MeshVertex(flag, x, y, Values()));
                    reader.Align();
                }
                break;
            case 5:
                while (reader.Has(0, verticesPerRow, verticesPerRow * nv) && vertices.Count < MaxMeshVertices)
                    for (int i = 0; i < verticesPerRow; i++)
                    {
                        var (x, y) = reader.Point();
                        vertices.Add(new MeshVertex(-1, x, y, Values()));
                        reader.Align();
                    }
                break;
            default:
            {
                int points = type == 6 ? 12 : 16;
                while (reader.Has(1, 0, 0) && patches.Count < MaxMeshVertices / 16)
                {
                    int flag = reader.Flag();
                    int newPoints = flag == 0 ? points : points - 4, newColors = flag == 0 ? 4 : 2;
                    if (!reader.Has(0, newPoints, newColors * nv)) break;
                    var p = new (double X, double Y)[newPoints];
                    for (int i = 0; i < newPoints; i++) p[i] = reader.Point();
                    var c = new double[newColors][];
                    for (int i = 0; i < newColors; i++) c[i] = Values();
                    reader.Align();
                    // a patch that continues a previous one needs that one
                    if (flag is < 0 or > 3 || (flag != 0 && patches.Count == 0)) continue;
                    patches.Add((flag, p, c));
                }
                break;
            }
        }
        if (type == 5) vertices.RemoveRange(vertices.Count - vertices.Count % verticesPerRow, vertices.Count % verticesPerRow);
        if (vertices.Count == 0 && patches.Count == 0) return false;

        // the values written: t as it is (with the function), otherwise the converted colors
        int outValues = hasFunction ? 1 : colors.Components;
        double[] Out(double[] values) => hasFunction ? values : colors.Convert(values) ?? new double[outValues];
        var allPoints = vertices.Select(v => (v.X, v.Y)).Concat(patches.SelectMany(p => p.Points)).ToList();
        var outVertexValues = vertices.Select(v => Out(v.Values)).ToList();
        var outPatchValues = patches.Select(p => p.Colors.Select(Out).ToArray()).ToList();
        var allValues = outVertexValues.Concat(outPatchValues.SelectMany(c => c)).ToList();

        (double Min, double Max) Span(IEnumerable<double> values)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (double v in values)
            {
                if (!double.IsFinite(v)) continue;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
            if (min > max) (min, max) = (0, 1);
            return max > min ? (min, max) : (min, min + 1);
        }
        var xs = Span(allPoints.Select(p => p.X));
        var ys = Span(allPoints.Select(p => p.Y));
        var decodeOut = new List<double> { xs.Min, xs.Max, ys.Min, ys.Max };
        var ranges = new (double Min, double Max)[outValues];
        for (int i = 0; i < outValues; i++)
        {
            ranges[i] = hasFunction ? Span(allValues.Select(v => v[i])) : (0, 1);
            decodeOut.Add(ranges[i].Min);
            decodeOut.Add(ranges[i].Max);
        }

        // 8-bit flags, 32-bit coordinates and 16-bit values: every vertex and patch ends on a byte boundary
        var output = new MemoryStream();
        var buffer = new byte[4];
        void Coordinate(double v, (double Min, double Max) r)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)Math.Round(Math.Clamp((v - r.Min) / (r.Max - r.Min), 0, 1) * uint.MaxValue));
            output.Write(buffer, 0, 4);
        }
        void Values16(double[] values)
        {
            for (int i = 0; i < outValues; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)Math.Round(Math.Clamp((values[i] - ranges[i].Min) / (ranges[i].Max - ranges[i].Min), 0, 1) * 65535));
                output.Write(buffer, 0, 2);
            }
        }
        for (int i = 0; i < vertices.Count; i++)
        {
            if (type == 4) output.WriteByte((byte)Math.Clamp(vertices[i].Flag, 0, 2));
            Coordinate(vertices[i].X, xs);
            Coordinate(vertices[i].Y, ys);
            Values16(outVertexValues[i]);
        }
        for (int i = 0; i < patches.Count; i++)
        {
            output.WriteByte((byte)patches[i].Flag);
            foreach (var (x, y) in patches[i].Points)
            {
                Coordinate(x, xs);
                Coordinate(y, ys);
            }
            foreach (var c in outPatchValues[i]) Values16(c);
        }

        result.SetBytes(output.ToArray());
        result[PdfName.Get("BitsPerCoordinate")] = new PdfNumber(32);
        result[PdfName.BitsPerComponent] = new PdfNumber(16);
        if (type != 5) result[PdfName.Get("BitsPerFlag")] = new PdfNumber(8);
        else result[PdfName.Get("VerticesPerRow")] = new PdfNumber(verticesPerRow);
        result[PdfName.Decode] = Numbers(decodeOut);
        if (hasFunction) result[PdfName.Get("Function")] = colors.Native ? function! : colors.Sampled(evaluator!, new[] { ranges[0].Min, ranges[0].Max });
        return true;
    }

    /// <summary>All remaining data of a file (up to 256 MB).</summary>
    private byte[] ReadAll(PsFile file)
    {
        var result = new MemoryStream();
        var buffer = new byte[65536];
        int n;
        while ((n = file.Read(buffer)) > 0)
        {
            result.Write(buffer, 0, n);
            if (result.Length > Limits.MaxImageBytes) throw new PsError("limitcheck");
        }
        return result.ToArray();
    }
}
