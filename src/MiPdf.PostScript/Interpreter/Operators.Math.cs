namespace MiPdf.PostScript.Interpreter;

/// <summary>Operand stack, arithmetic, relational, boolean and bitwise operators (PLRM 8.1).</summary>
internal sealed partial class PsInterpreter
{
    private int _randomSeed = 1;

    private void RegisterStackOperators()
    {
        Op("pop", ip => ip.Pop());
        Op("exch", ip =>
        {
            var b = ip.Pop();
            var a = ip.Pop();
            ip.Push(b);
            ip.Push(a);
        });
        Op("dup", ip => ip.Push(ip.Ops.Peek()));
        Op("index", ip =>
        {
            long n = ip.PopInt();
            if (n < 0 || n > int.MaxValue) throw new PsError("rangecheck");
            if (n >= ip.Ops.Count) throw new PsError("rangecheck");
            ip.Push(ip.Ops.Peek((int)n));
        });
        Op("roll", ip =>
        {
            long j = ip.PopInt();
            long n = ip.PopInt();
            if (n < 0) throw new PsError("rangecheck");
            if (n > ip.Ops.Count) throw new PsError("stackunderflow");
            if (n == 0) return;
            int count = (int)n;
            int shift = (int)(((j % count) + count) % count);
            if (shift == 0) return;
            var items = new PsObject[count];
            for (int i = count - 1; i >= 0; i--) items[i] = ip.Pop();
            for (int i = 0; i < count; i++) ip.Push(items[((i - shift) % count + count) % count]);
        });
        Op("clear", ip => ip.Ops.Clear());
        Op("count", ip => ip.Push(ip.Ops.Count));
        Op("mark", ip => ip.Push(PsObject.Mark));
        Op("[", ip => ip.Push(PsObject.Mark));
        Op("<<", ip => ip.Push(PsObject.Mark));
        Op("cleartomark", ip =>
        {
            int n = ip.Ops.CountToMark();
            for (int i = 0; i <= n; i++) ip.Pop();
        });
        Op("counttomark", ip => ip.Push(ip.Ops.CountToMark()));
        Op("]", ip =>
        {
            int n = ip.Ops.CountToMark();
            var items = new PsObject[n];
            for (int i = n - 1; i >= 0; i--) items[i] = ip.Pop();
            ip.Pop(); // the mark
            ip.Push(ip.NewArray(items));
        });
        Op(">>", ip =>
        {
            int n = ip.Ops.CountToMark();
            if (n % 2 != 0) throw new PsError("rangecheck");
            var items = new PsObject[n];
            for (int i = n - 1; i >= 0; i--) items[i] = ip.Pop();
            ip.Pop();
            var dict = ip.NewDict(n / 2);
            for (int i = 0; i < n; i += 2) ip.DictPut(dict, items[i], items[i + 1]);
            ip.Push(PsObject.Dictionary(dict));
        });
    }

    private void RegisterMathOperators()
    {
        Op("add", ip => ip.Arithmetic((a, b) => a + b, (a, b) => checked(a + b)));
        Op("sub", ip => ip.Arithmetic((a, b) => a - b, (a, b) => checked(a - b)));
        Op("mul", ip => ip.Arithmetic((a, b) => a * b, (a, b) => checked(a * b)));
        Op("div", ip =>
        {
            float b = (float)ip.PopReal();
            float a = (float)ip.PopReal();
            if (b == 0) throw new PsError("undefinedresult");
            ip.PushReal(a / b);
        });
        Op("idiv", ip =>
        {
            long b = ip.PopInt();
            long a = ip.PopInt();
            if (b == 0) throw new PsError("undefinedresult");
            ip.Push(b == -1 ? unchecked(-a) : a / b);
        });
        Op("mod", ip =>
        {
            long b = ip.PopInt();
            long a = ip.PopInt();
            if (b == 0) throw new PsError("undefinedresult");
            ip.Push(b == -1 ? 0 : a % b);
        });
        // Ghostscript extensions used by some programs: the larger or smaller of two numbers (keeping its type)
        foreach (string name in new[] { "max", ".max", "min", ".min" })
        {
            bool larger = name.EndsWith("max", StringComparison.Ordinal);
            Op(name, ip =>
            {
                var b = ip.PopNumber();
                var a = ip.PopNumber();
                ip.Push(larger ? (b.Number > a.Number ? b : a) : (b.Number < a.Number ? b : a));
            });
        }
        Op("abs", ip =>
        {
            var a = ip.PopNumber();
            if (a.Type == PsType.Integer)
            {
                if (a.Int == long.MinValue) ip.PushReal(-(double)a.Int);
                else ip.Push(Math.Abs(a.Int));
            }
            else ip.PushReal(Math.Abs(a.Real));
        });
        Op("neg", ip =>
        {
            var a = ip.PopNumber();
            if (a.Type == PsType.Integer)
            {
                if (a.Int == long.MinValue) ip.PushReal(-(double)a.Int);
                else ip.Push(-a.Int);
            }
            else ip.PushReal(-a.Real);
        });
        Op("ceiling", ip => ip.Rounding(MathF.Ceiling));
        Op("floor", ip => ip.Rounding(MathF.Floor));
        Op("round", ip => ip.Rounding(x => MathF.Floor(x + 0.5f)));
        Op("truncate", ip => ip.Rounding(MathF.Truncate));
        Op("sqrt", ip =>
        {
            double a = ip.PopReal();
            if (a < 0) throw new PsError("rangecheck");
            ip.PushReal(Math.Sqrt((float)a));
        });
        Op("atan", ip =>
        {
            double den = (float)ip.PopReal();
            double num = (float)ip.PopReal();
            if (num == 0 && den == 0) throw new PsError("undefinedresult");
            double angle = Math.Atan2(num, den) * 180 / Math.PI;
            if (angle < 0) angle += 360;
            ip.PushReal(angle);
        });
        Op("cos", ip => ip.PushReal(Math.Cos((float)ip.PopReal() * Math.PI / 180)));
        Op("sin", ip => ip.PushReal(Math.Sin((float)ip.PopReal() * Math.PI / 180)));
        Op("exp", ip =>
        {
            double exponent = (float)ip.PopReal();
            double b = (float)ip.PopReal();
            if (b < 0 && exponent != Math.Floor(exponent)) throw new PsError("undefinedresult");
            if (b == 0 && exponent < 0) throw new PsError("undefinedresult");
            ip.PushReal(Math.Pow(b, exponent));
        });
        Op("ln", ip =>
        {
            double a = (float)ip.PopReal();
            if (a <= 0) throw new PsError("rangecheck");
            ip.PushReal(Math.Log(a));
        });
        Op("log", ip =>
        {
            double a = (float)ip.PopReal();
            if (a <= 0) throw new PsError("rangecheck");
            ip.PushReal(Math.Log10(a));
        });
        // the minimal standard generator of Park and Miller
        Op("rand", ip =>
        {
            ip._randomSeed = (int)(ip._randomSeed * 16807L % int.MaxValue);
            ip.Push(ip._randomSeed);
        });
        Op("srand", ip =>
        {
            // as Ghostscript: a nonzero state for all inputs
            long seed = ip.PopInt();
            if (seed < 1) seed = -(seed % 0x7ffffffe) + 1;
            else if (seed > 0x7ffffffe) seed = 0x7ffffffe;
            ip._randomSeed = (int)seed;
        });
        Op("rrand", ip => ip.Push(ip._randomSeed));

        Op("eq", ip => ip.Push(PsObject.Equal(ip.Pop(), ip.Pop())));
        Op("ne", ip => ip.Push(!PsObject.Equal(ip.Pop(), ip.Pop())));
        Op("ge", ip => ip.Push(ip.Compare() >= 0));
        Op("gt", ip => ip.Push(ip.Compare() > 0));
        Op("le", ip => ip.Push(ip.Compare() <= 0));
        Op("lt", ip => ip.Push(ip.Compare() < 0));
        Op("and", ip => ip.Logical((a, b) => a & b));
        Op("or", ip => ip.Logical((a, b) => a | b));
        Op("xor", ip => ip.Logical((a, b) => a ^ b));
        Op("not", ip =>
        {
            var a = ip.Pop();
            if (a.Type == PsType.Boolean) ip.Push(!a.Bool);
            else if (a.Type == PsType.Integer) ip.Push(~a.Int);
            else throw new PsError("typecheck");
        });
        Op("bitshift", ip =>
        {
            long shift = ip.PopInt();
            long a = ip.PopInt();
            if (shift >= 64 || shift <= -64) ip.Push(shift > 0 || a >= 0 ? 0 : -1);
            else ip.Push(shift >= 0 ? a << (int)shift : a >> (int)-shift);
        });
    }

    /// <summary>
    /// Binary arithmetic: integers stay integers unless the result overflows (then it is a real), otherwise reals in
    /// single precision.
    /// </summary>
    private void Arithmetic(Func<float, float, float> real, Func<long, long, long> integer)
    {
        var b = PopNumber();
        var a = PopNumber();
        if (a.Type == PsType.Integer && b.Type == PsType.Integer)
        {
            try
            {
                Push(integer(a.Int, b.Int));
                return;
            }
            catch (OverflowException)
            {
                PushReal(real((float)a.Int, (float)b.Int));
                return;
            }
        }
        PushReal(real((float)a.Number, (float)b.Number));
    }

    private void Rounding(Func<float, float> f)
    {
        var a = PopNumber();
        if (a.Type == PsType.Integer) Push(a);
        else PushReal(f(a.Real));
    }

    /// <summary>Compares two numbers or two strings (byte by byte).</summary>
    private int Compare()
    {
        var b = Pop();
        var a = Pop();
        if (a.IsNumber && b.IsNumber)
        {
            if (a.Type == PsType.Integer && b.Type == PsType.Integer) return a.Int.CompareTo(b.Int);
            return ((float)a.Number).CompareTo((float)b.Number);
        }
        if (a.Type == PsType.String && b.Type == PsType.String)
        {
            if (!a.CanRead || !b.CanRead) throw new PsError("invalidaccess");
            return a.AsString.Span.SequenceCompareTo(b.AsString.Span);
        }
        throw new PsError("typecheck");
    }

    private void Logical(Func<long, long, long> f)
    {
        var b = Pop();
        var a = Pop();
        if (a.Type == PsType.Boolean && b.Type == PsType.Boolean) Push(f(a.Int, b.Int) != 0);
        else if (a.Type == PsType.Integer && b.Type == PsType.Integer) Push(f(a.Int, b.Int));
        else throw new PsError("typecheck");
    }
}
