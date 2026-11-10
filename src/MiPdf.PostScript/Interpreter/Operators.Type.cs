using System.Globalization;
using System.Text;

namespace MiPdf.PostScript.Interpreter;

/// <summary>Type, attribute and conversion operators (PLRM 8.1).</summary>
internal sealed partial class PsInterpreter
{
    private static readonly Dictionary<PsType, string> TypeNames = new()
    {
        [PsType.Null] = "nulltype", [PsType.Integer] = "integertype", [PsType.Real] = "realtype",
        [PsType.Boolean] = "booleantype", [PsType.Name] = "nametype", [PsType.String] = "stringtype",
        [PsType.Array] = "arraytype", [PsType.PackedArray] = "packedarraytype", [PsType.Dictionary] = "dicttype",
        [PsType.Operator] = "operatortype", [PsType.File] = "filetype", [PsType.Mark] = "marktype",
        [PsType.Save] = "savetype", [PsType.FontId] = "fonttype", [PsType.GState] = "gstatetype",
    };

    private void RegisterTypeOperators()
    {
        Op("type", ip => ip.Push(PsObject.Name(ip.Name(TypeNames[ip.Pop().Type]), executable: true)));
        Op("cvlit", ip => ip.Push(ip.Pop().AsLiteral()));
        Op("cvx", ip => ip.Push(ip.Pop().AsExecutable()));
        Op("xcheck", ip => ip.Push(ip.Pop().IsExecutable));
        Op("executeonly", ip => ip.Push(ip.Restrict(ip.Pop(), PsAttributes.ExecuteOnly, PsDictAccess.ExecuteOnly, dict: false)));
        Op("noaccess", ip => ip.Push(ip.Restrict(ip.Pop(), PsAttributes.NoAccess, PsDictAccess.NoAccess, dict: true)));
        Op("readonly", ip => ip.Push(ip.Restrict(ip.Pop(), PsAttributes.ReadOnly, PsDictAccess.ReadOnly, dict: true)));
        Op("rcheck", ip =>
        {
            var o = ip.Pop();
            if (!HasAccess(o)) throw new PsError("typecheck");
            ip.Push(o.CanRead);
        });
        Op("wcheck", ip =>
        {
            var o = ip.Pop();
            if (!HasAccess(o)) throw new PsError("typecheck");
            ip.Push(o.CanWrite);
        });
        Op("cvi", ip =>
        {
            var o = ip.Pop();
            double v = o.Type switch
            {
                PsType.Integer => o.Int,
                PsType.Real => o.Real,
                PsType.String => ip.ParseNumberString(o).Number,
                _ => throw new PsError("typecheck"),
            };
            if (o.Type == PsType.Integer)
            {
                ip.Push(o.Int);
                return;
            }
            v = Math.Truncate(v);
            if (v >= 9.2233720368547758e18 || v < -9.2233720368547758e18) throw new PsError("rangecheck");
            ip.Push((long)v);
        });
        Op("cvr", ip =>
        {
            var o = ip.Pop();
            double v = o.Type switch
            {
                PsType.Integer or PsType.Real => o.Number,
                PsType.String => ip.ParseNumberString(o).Number,
                _ => throw new PsError("typecheck"),
            };
            ip.PushReal(v);
        });
        Op("cvn", ip =>
        {
            var s = ip.PopString();
            ip.Push(PsObject.Name(ip.Name(s.AsString.Text), s.IsExecutable));
        });
        Op("cvs", ip =>
        {
            var target = ip.PopString(write: true);
            var o = ip.Pop();
            ip.Push(ip.CopyInto(target, Formatter.Text(o)));
        });
        Op("cvrs", ip =>
        {
            var target = ip.PopString(write: true);
            long radix = ip.PopInt();
            var n = ip.PopNumber();
            if (radix < 2 || radix > 36) throw new PsError("rangecheck");
            byte[] text;
            if (radix == 10) text = Formatter.Text(n);
            else
            {
                double v = n.Type == PsType.Integer ? n.Int : Math.Truncate(n.Real);
                if (n.Type == PsType.Real && (v >= 9.2233720368547758e18 || v < -9.2233720368547758e18)) throw new PsError("rangecheck");
                ulong u = unchecked((ulong)(n.Type == PsType.Integer ? n.Int : (long)v));
                var sb = new StringBuilder();
                do
                {
                    int digit = (int)(u % (ulong)radix);
                    sb.Insert(0, (char)(digit < 10 ? '0' + digit : 'A' + digit - 10));
                    u /= (ulong)radix;
                } while (u != 0);
                text = Encoding.ASCII.GetBytes(sb.ToString());
            }
            ip.Push(ip.CopyInto(target, text));
        });
        Op("bind", ip => ip.Bind(ip.Ops.Peek(), new HashSet<PsArrayStorage>(), 0));
    }

    private static bool HasAccess(in PsObject o) => o.Type is PsType.Array or PsType.PackedArray or PsType.String or PsType.File or PsType.Dictionary;

    /// <summary>Reduces the access of an object (it can only be reduced).</summary>
    private PsObject Restrict(PsObject o, PsAttributes attribute, PsDictAccess access, bool dict)
    {
        if (o.Type == PsType.Dictionary)
        {
            if (!dict) throw new PsError("typecheck");
            var d = o.AsDict;
            if (d.Access < access)
            {
                BeforeChange(d);
                d.Access = access;
            }
            return o;
        }
        if (o.Type is not (PsType.Array or PsType.PackedArray or PsType.String or PsType.File)) throw new PsError("typecheck");
        var current = o.Attributes & (PsAttributes.ReadOnly | PsAttributes.ExecuteOnly | PsAttributes.NoAccess);
        // the strongest restriction wins
        if (current >= attribute) return o;
        return o.WithAttributes((o.Attributes & PsAttributes.Executable) | attribute);
    }

    /// <summary>A number from a string (the whole string must be one number token).</summary>
    private PsObject ParseNumberString(in PsObject s)
    {
        if (!s.CanRead) throw new PsError("invalidaccess");
        string text = s.AsString.Text.Trim('\0', '\t', '\n', '\f', '\r', ' ');
        return PsScanner.ParseNumber(text) ?? throw new PsError("typecheck");
    }

    /// <summary>Copies bytes to the start of a string and returns that part (rangecheck if it does not fit).</summary>
    private PsObject CopyInto(in PsObject target, ReadOnlySpan<byte> bytes)
    {
        var s = target.AsString;
        if (bytes.Length > s.Length) throw new PsError("rangecheck");
        bytes.CopyTo(s.Span);
        return PsObject.String(s.Interval(0, bytes.Length), target.Attributes);
    }

    /// <summary>
    /// <c>bind</c>: replaces executable names whose current value is an operator by the operator, in the procedure and in
    /// nested (writable) procedures.
    /// </summary>
    private void Bind(in PsObject proc, HashSet<PsArrayStorage> visited, int depth)
    {
        if (!proc.IsArray) throw new PsError("typecheck");
        var array = proc.AsArray;
        if (depth > 1000 || !visited.Add(array.Storage)) return;
        bool writable = proc.CanWrite;
        for (int i = 0; i < array.Length; i++)
        {
            var item = array[i];
            CountOperation();
            if (item.Type == PsType.Name && item.IsExecutable)
            {
                if (writable && Lookup(item.AsName) is { Type: PsType.Operator } value)
                {
                    BeforeChange(array.Storage);
                    array.Storage.Items[array.Offset + i] = value;
                }
            }
            else if (item.IsProcedure && item.CanRead)
            {
                Bind(item, visited, depth + 1);
            }
        }
    }

    internal static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
