using System.Globalization;
using MiPdf.Objects;

namespace MiPdf.Graphics;

/// <summary>
/// PDF functions (ISO 32000-2, 7.10): sampled (type 0), exponential interpolation (type 2), stitching (type 3) and
/// PostScript calculator (type 4) functions. Inputs are clipped to the domain, outputs to the range.
/// </summary>
internal abstract class PdfFunction
{
    private const int MaxDepth = 8;

    protected double[] Domain = { 0, 1 };
    protected double[]? Range;

    public int Inputs => Domain.Length / 2;

    public abstract int Outputs { get; }

    /// <summary>Evaluates the function; <paramref name="output"/> must have <see cref="Outputs"/> elements.</summary>
    public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> x = stackalloc double[Math.Max(1, Inputs)];
        for (int i = 0; i < Inputs; i++)
        {
            double v = i < input.Length ? input[i] : 0;
            x[i] = Math.Clamp(double.IsFinite(v) ? v : Domain[2 * i], Domain[2 * i], Domain[2 * i + 1]);
        }
        Compute(x[..Inputs], output);
        if (Range != null)
            for (int j = 0; j < output.Length && 2 * j + 1 < Range.Length; j++)
                output[j] = Math.Clamp(double.IsFinite(output[j]) ? output[j] : Range[2 * j], Range[2 * j], Range[2 * j + 1]);
    }

    protected abstract void Compute(ReadOnlySpan<double> x, Span<double> output);

    /// <summary>Reads a function (dictionary or stream); null if it is missing or invalid.</summary>
    public static PdfFunction? Load(PdfObject? value, int depth = 0)
    {
        if (depth > MaxDepth || value?.Resolve() is not PdfDictionary dict) return null;
        try
        {
            PdfFunction? function = dict.GetInt(PdfName.Get("FunctionType"), -1) switch
            {
                0 when dict is PdfStream stream => Sampled.Create(stream),
                2 => Exponential.Create(dict),
                3 => Stitching.Create(dict, depth),
                4 when dict is PdfStream stream => Calculator.Create(stream),
                _ => null,
            };
            if (function == null) return null;
            function.Domain = Numbers(dict.GetArray(PdfName.Get("Domain"))) is { Length: >= 2 } domain ? domain[..(domain.Length & ~1)] : function.Domain;
            function.Range = Numbers(dict.GetArray(PdfName.Get("Range"))) is { Length: >= 2 } range ? range : null;
            return function.Validate() ? function : null;
        }
        catch (Exception ex) when (ex is PdfException or ArgumentException or IndexOutOfRangeException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>A function from an array of single-output functions (one per component) or a single function.</summary>
    public static PdfFunction? LoadCombined(PdfObject? value)
    {
        if (value?.Resolve() is PdfArray array)
        {
            var parts = array.Select(item => Load(item)).ToArray();
            if (parts.Length == 0 || parts.Any(p => p == null)) return null;
            return parts.Length == 1 ? parts[0] : new Combined(parts!);
        }
        return Load(value);
    }

    protected virtual bool Validate() => Inputs >= 1 && Outputs >= 1 && Outputs <= 32;

    protected static double[] Numbers(PdfArray? array) =>
        array?.Select(v => (v.Resolve() as PdfNumber)?.Value ?? 0).ToArray() ?? Array.Empty<double>();

    private static double Interpolate(double x, double x0, double x1, double y0, double y1) =>
        x1 == x0 ? y0 : y0 + (x - x0) * (y1 - y0) / (x1 - x0);

    // ------------------------------------------------------------------ type 0

    private sealed class Sampled : PdfFunction
    {
        private int[] _size = Array.Empty<int>();
        private double[] _encode = Array.Empty<double>(), _decode = Array.Empty<double>();
        private double[] _samples = Array.Empty<double>(); // normalized 0..1
        private int _outputs;

        public override int Outputs => _outputs;

        public static Sampled? Create(PdfStream stream)
        {
            var f = new Sampled();
            f._size = Numbers(stream.GetArray(PdfName.Get("Size"))).Select(v => (int)v).ToArray();
            int bps = stream.GetInt(PdfName.Get("BitsPerSample"));
            var range = Numbers(stream.GetArray(PdfName.Get("Range")));
            f._outputs = range.Length / 2;
            if (f._size.Length == 0 || f._size.Any(s => s < 1) || f._outputs < 1 || bps is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32)) return null;
            long count = f._outputs;
            foreach (int s in f._size)
            {
                count *= s;
                if (count > 16_000_000) return null;
            }
            f._encode = Numbers(stream.GetArray(PdfName.Get("Encode")));
            if (f._encode.Length < 2 * f._size.Length)
                f._encode = f._size.SelectMany(s => new double[] { 0, s - 1 }).ToArray();
            f._decode = Numbers(stream.GetArray(PdfName.Decode));
            if (f._decode.Length < 2 * f._outputs) f._decode = range;
            var data = stream.GetBytes();
            f._samples = new double[count];
            double max = Math.Pow(2, bps) - 1;
            long bit = 0;
            for (long i = 0; i < count; i++)
            {
                ulong v = 0;
                for (int b = 0; b < bps; b++, bit++)
                {
                    long index = bit >> 3;
                    int value = index < data.Length ? (data[index] >> (7 - (int)(bit & 7))) & 1 : 0;
                    v = (v << 1) | (uint)value;
                }
                f._samples[i] = v / max;
            }
            return f;
        }

        protected override bool Validate() => base.Validate() && _size.Length == Inputs && Inputs <= 8;

        protected override void Compute(ReadOnlySpan<double> x, Span<double> output)
        {
            int m = Inputs;
            Span<int> lower = stackalloc int[m];
            Span<double> fraction = stackalloc double[m];
            for (int i = 0; i < m; i++)
            {
                double e = Interpolate(x[i], Domain[2 * i], Domain[2 * i + 1], _encode[2 * i], _encode[2 * i + 1]);
                e = Math.Clamp(e, 0, _size[i] - 1);
                int lo = Math.Min((int)Math.Floor(e), Math.Max(0, _size[i] - 2));
                lower[i] = lo;
                fraction[i] = _size[i] == 1 ? 0 : e - lo;
            }
            // multilinear interpolation over the 2^m corners
            for (int j = 0; j < _outputs; j++)
            {
                double sum = 0;
                for (int corner = 0; corner < 1 << m; corner++)
                {
                    double weight = 1;
                    long index = 0, stride = 1;
                    for (int i = 0; i < m; i++)
                    {
                        bool high = (corner >> i & 1) == 1;
                        if (high && _size[i] == 1)
                        {
                            weight = 0;
                            break;
                        }
                        weight *= high ? fraction[i] : 1 - fraction[i];
                        index += (lower[i] + (high ? 1 : 0)) * stride;
                        stride *= _size[i];
                    }
                    if (weight > 0) sum += weight * _samples[index * _outputs + j];
                }
                output[j] = _decode[2 * j] + sum * (_decode[2 * j + 1] - _decode[2 * j]);
            }
        }
    }

    // ------------------------------------------------------------------ type 2

    private sealed class Exponential : PdfFunction
    {
        private double[] _c0 = { 0 }, _c1 = { 1 };
        private double _n = 1;

        public override int Outputs => _c0.Length;

        public static Exponential Create(PdfDictionary dict)
        {
            var f = new Exponential();
            if (Numbers(dict.GetArray(PdfName.Get("C0"))) is { Length: > 0 } c0) f._c0 = c0;
            if (Numbers(dict.GetArray(PdfName.Get("C1"))) is { Length: > 0 } c1) f._c1 = c1;
            if (f._c1.Length != f._c0.Length) f._c1 = f._c1.Concat(new double[Math.Max(0, f._c0.Length - f._c1.Length)]).Take(f._c0.Length).ToArray();
            f._n = dict.GetDouble(PdfName.N, 1);
            return f;
        }

        protected override void Compute(ReadOnlySpan<double> x, Span<double> output)
        {
            double p = _n == 1 ? x[0] : Math.Pow(x[0], _n);
            if (!double.IsFinite(p)) p = 0;
            for (int j = 0; j < _c0.Length; j++) output[j] = _c0[j] + p * (_c1[j] - _c0[j]);
        }
    }

    // ------------------------------------------------------------------ type 3

    private sealed class Stitching : PdfFunction
    {
        private PdfFunction[] _functions = Array.Empty<PdfFunction>();
        private double[] _bounds = Array.Empty<double>(), _encode = Array.Empty<double>();

        public override int Outputs => _functions.Length > 0 ? _functions[0].Outputs : 0;

        public static Stitching? Create(PdfDictionary dict, int depth)
        {
            var f = new Stitching();
            var functions = dict.GetArray(PdfName.Get("Functions"));
            if (functions == null || functions.Count == 0) return null;
            var loaded = functions.Select(item => Load(item, depth + 1)).ToArray();
            if (loaded.Any(l => l == null || l.Inputs != 1)) return null;
            f._functions = loaded!;
            f._bounds = Numbers(dict.GetArray(PdfName.Get("Bounds")));
            f._encode = Numbers(dict.GetArray(PdfName.Get("Encode")));
            if (f._bounds.Length != f._functions.Length - 1 || f._encode.Length < 2 * f._functions.Length) return null;
            return f;
        }

        protected override bool Validate() => base.Validate() && Inputs == 1 && _functions.All(g => g.Outputs == Outputs);

        protected override void Compute(ReadOnlySpan<double> x, Span<double> output)
        {
            double v = x[0];
            int k = 0;
            while (k < _bounds.Length && v >= _bounds[k]) k++;
            double low = k == 0 ? Domain[0] : _bounds[k - 1], high = k == _bounds.Length ? Domain[1] : _bounds[k];
            double e = Interpolate(v, low, high, _encode[2 * k], _encode[2 * k + 1]);
            _functions[k].Evaluate(stackalloc double[] { e }, output);
        }
    }

    // ------------------------------------------------------------------ arrays of functions

    private sealed class Combined : PdfFunction
    {
        private readonly PdfFunction[] _parts;

        public Combined(PdfFunction[] parts)
        {
            _parts = parts;
            Domain = parts[0].Domain;
        }

        public override int Outputs => _parts.Sum(p => p.Outputs);

        protected override void Compute(ReadOnlySpan<double> x, Span<double> output)
        {
            int offset = 0;
            foreach (var part in _parts)
            {
                part.Evaluate(x, output.Slice(offset, part.Outputs));
                offset += part.Outputs;
            }
        }
    }

    // ------------------------------------------------------------------ type 4

    /// <summary>PostScript calculator functions: a compiled program of operators with nested if/ifelse blocks.</summary>
    private sealed class Calculator : PdfFunction
    {
        private const int MaxStack = 100;
        private const int MaxSteps = 100_000;

        private abstract record Instruction;
        private sealed record Push(double Value) : Instruction;
        private sealed record Operator(string Name) : Instruction;
        private sealed record If(Instruction[] Then, Instruction[]? Else) : Instruction;

        private Instruction[] _program = Array.Empty<Instruction>();
        private int _outputs;

        public override int Outputs => _outputs;

        public static Calculator? Create(PdfStream stream)
        {
            var range = Numbers(stream.GetArray(PdfName.Get("Range")));
            var text = System.Text.Encoding.Latin1.GetString(stream.GetBytes());
            var tokens = Tokenize(text);
            int pos = 0;
            while (pos < tokens.Count && tokens[pos] != "{") pos++;
            if (pos >= tokens.Count) return null;
            pos++;
            var program = Parse(tokens, ref pos, 0);
            return program == null ? null : new Calculator { _program = program, _outputs = range.Length / 2 };
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) i++;
                else if (c == '%')
                    while (i < text.Length && text[i] is not ('\r' or '\n')) i++;
                else if (c is '{' or '}')
                {
                    tokens.Add(c.ToString());
                    i++;
                }
                else
                {
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '%')) i++;
                    tokens.Add(text[start..i]);
                }
            }
            return tokens;
        }

        private static Instruction[]? Parse(List<string> tokens, ref int pos, int depth)
        {
            if (depth > 32) return null;
            var list = new List<Instruction>();
            var blocks = new List<Instruction[]>();
            while (pos < tokens.Count)
            {
                string token = tokens[pos++];
                switch (token)
                {
                    case "}":
                        if (blocks.Count > 0) return null;
                        return list.ToArray();
                    case "{":
                        var block = Parse(tokens, ref pos, depth + 1);
                        if (block == null) return null;
                        blocks.Add(block);
                        break;
                    case "if":
                        if (blocks.Count != 1) return null;
                        list.Add(new If(blocks[0], null));
                        blocks.Clear();
                        break;
                    case "ifelse":
                        if (blocks.Count != 2) return null;
                        list.Add(new If(blocks[0], blocks[1]));
                        blocks.Clear();
                        break;
                    default:
                        if (blocks.Count > 0) return null;
                        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) list.Add(new Push(number));
                        else if (token == "true") list.Add(new Push(1));
                        else if (token == "false") list.Add(new Push(0));
                        else list.Add(new Operator(token));
                        break;
                }
            }
            return null;
        }

        protected override void Compute(ReadOnlySpan<double> x, Span<double> output)
        {
            var stack = new double[MaxStack];
            int sp = 0;
            foreach (double v in x) stack[sp++] = v;
            int steps = 0;
            try
            {
                Run(_program, stack, ref sp, ref steps);
            }
            catch (InvalidOperationException)
            {
                // stack under- or overflow, unknown operator: the outputs keep what is on the stack
            }
            // the results are the topmost Outputs values
            for (int j = 0; j < output.Length; j++)
            {
                int index = sp - output.Length + j;
                output[j] = index >= 0 && index < sp ? stack[index] : 0;
            }
        }

        private static void Run(Instruction[] program, double[] s, ref int sp, ref int steps)
        {
            foreach (var instruction in program)
            {
                if (++steps > MaxSteps) throw new InvalidOperationException();
                switch (instruction)
                {
                    case Push push:
                        if (sp >= s.Length) throw new InvalidOperationException();
                        s[sp++] = push.Value;
                        break;
                    case If branch:
                        if (sp < 1) throw new InvalidOperationException();
                        bool condition = s[--sp] != 0;
                        if (condition) Run(branch.Then, s, ref sp, ref steps);
                        else if (branch.Else != null) Run(branch.Else, s, ref sp, ref steps);
                        break;
                    case Operator op:
                        Execute(op.Name, s, ref sp);
                        break;
                }
            }
        }

        private static void Execute(string name, double[] s, ref int sp)
        {
            void Need(int n, int sp2)
            {
                if (sp2 < n) throw new InvalidOperationException();
            }
            static long L(double v) => (long)Math.Truncate(v);
            switch (name)
            {
                // one operand
                case "abs" or "neg" or "ceiling" or "floor" or "round" or "truncate" or "sqrt" or "sin" or "cos" or "ln" or "log" or "cvi" or "cvr" or "not":
                {
                    Need(1, sp);
                    double a = s[sp - 1];
                    s[sp - 1] = name switch
                    {
                        "abs" => Math.Abs(a),
                        "neg" => -a,
                        "ceiling" => Math.Ceiling(a),
                        "floor" => Math.Floor(a),
                        "round" => Math.Floor(a + 0.5),
                        "truncate" => Math.Truncate(a),
                        "sqrt" => Math.Sqrt(Math.Max(0, a)),
                        "sin" => Math.Sin(a * Math.PI / 180),
                        "cos" => Math.Cos(a * Math.PI / 180),
                        "ln" => a > 0 ? Math.Log(a) : 0,
                        "log" => a > 0 ? Math.Log10(a) : 0,
                        "cvi" => Math.Truncate(a),
                        "cvr" => a,
                        _ => a == 0 ? 1 : a == 1 ? 0 : ~L(a), // not: boolean or bitwise
                    };
                    break;
                }
                // two operands
                case "add" or "sub" or "mul" or "div" or "idiv" or "mod" or "exp" or "atan" or "and" or "or" or "xor" or "bitshift"
                    or "eq" or "ne" or "gt" or "ge" or "lt" or "le":
                {
                    Need(2, sp);
                    double a = s[sp - 2], b = s[sp - 1];
                    sp--;
                    s[sp - 1] = name switch
                    {
                        "add" => a + b,
                        "sub" => a - b,
                        "mul" => a * b,
                        "div" => b == 0 ? 0 : a / b,
                        "idiv" => L(b) == 0 ? 0 : L(a) / L(b),
                        "mod" => L(b) == 0 ? 0 : L(a) % L(b),
                        "exp" => Math.Pow(a, b) is var p && double.IsFinite(p) ? p : 0,
                        "atan" => (Math.Atan2(a, b) * 180 / Math.PI + 360) % 360,
                        "and" => L(a) & L(b),
                        "or" => L(a) | L(b),
                        "xor" => L(a) ^ L(b),
                        "bitshift" => L(b) >= 0 ? L(a) << (int)Math.Min(63, L(b)) : L(a) >> (int)Math.Min(63, -L(b)),
                        "eq" => a == b ? 1 : 0,
                        "ne" => a != b ? 1 : 0,
                        "gt" => a > b ? 1 : 0,
                        "ge" => a >= b ? 1 : 0,
                        "lt" => a < b ? 1 : 0,
                        _ => a <= b ? 1 : 0,
                    };
                    break;
                }
                case "dup":
                    Need(1, sp);
                    if (sp >= s.Length) throw new InvalidOperationException();
                    s[sp] = s[sp - 1];
                    sp++;
                    break;
                case "pop":
                    Need(1, sp);
                    sp--;
                    break;
                case "exch":
                    Need(2, sp);
                    (s[sp - 1], s[sp - 2]) = (s[sp - 2], s[sp - 1]);
                    break;
                case "copy":
                {
                    Need(1, sp);
                    int n = (int)s[--sp];
                    if (n < 0 || n > sp || sp + n > s.Length) throw new InvalidOperationException();
                    Array.Copy(s, sp - n, s, sp, n);
                    sp += n;
                    break;
                }
                case "index":
                {
                    Need(1, sp);
                    int n = (int)s[sp - 1];
                    if (n < 0 || n >= sp - 1) throw new InvalidOperationException();
                    s[sp - 1] = s[sp - 2 - n];
                    break;
                }
                case "roll":
                {
                    Need(2, sp);
                    int j = (int)s[--sp], n = (int)s[--sp];
                    if (n < 0 || n > sp) throw new InvalidOperationException();
                    if (n == 0) break;
                    j = ((j % n) + n) % n;
                    var part = s.AsSpan(sp - n, n).ToArray();
                    for (int i = 0; i < n; i++) s[sp - n + (i + j) % n] = part[i];
                    break;
                }
                default:
                    throw new InvalidOperationException();
            }
        }
    }
}
