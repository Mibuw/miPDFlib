using System.Globalization;
using System.Text;

namespace MiPdf.PostScript.Interpreter;

/// <summary>Text forms of objects: <c>cvs</c>/<c>=</c> and the syntax form of <c>==</c> (as Ghostscript prints them).</summary>
internal static class Formatter
{
    /// <summary>The form of <c>cvs</c> and <c>=</c>; null for objects without a text form ("--nostringval--").</summary>
    public static byte[] Text(in PsObject o) => o.Type switch
    {
        PsType.Integer => Ascii(o.Int.ToString(CultureInfo.InvariantCulture)),
        PsType.Real => Ascii(Real(o.Real, roundTrip: false)),
        PsType.Boolean => Ascii(o.Bool ? "true" : "false"),
        PsType.String => o.AsString.ToArray(),
        PsType.Name => o.AsName.Bytes,
        PsType.Operator => o.AsOperator.Name.Bytes,
        _ => Ascii("--nostringval--"),
    };

    /// <summary>The syntax form of <c>==</c>.</summary>
    public static string Syntax(in PsObject o, int depth)
    {
        var sb = new StringBuilder();
        AppendSyntax(sb, o, depth);
        return sb.ToString();
    }

    private static void AppendSyntax(StringBuilder sb, in PsObject o, int depth)
    {
        switch (o.Type)
        {
            case PsType.Integer:
                sb.Append(o.Int.ToString(CultureInfo.InvariantCulture));
                break;
            case PsType.Real:
                sb.Append(Real(o.Real, roundTrip: true));
                break;
            case PsType.Boolean:
                sb.Append(o.Bool ? "true" : "false");
                break;
            case PsType.Null:
                sb.Append("null");
                break;
            case PsType.Mark:
                sb.Append("-mark-");
                break;
            case PsType.Name:
                if (!o.IsExecutable) sb.Append('/');
                sb.Append(o.AsName.Text);
                break;
            case PsType.String:
                AppendString(sb, o.AsString.Span);
                break;
            case PsType.Operator:
                sb.Append("--").Append(o.AsOperator.Name.Text).Append("--");
                break;
            case PsType.Array:
            case PsType.PackedArray:
            {
                if ((o.Attributes & (PsAttributes.ExecuteOnly | PsAttributes.NoAccess)) != 0)
                {
                    sb.Append("--nostringval--");
                    break;
                }
                bool proc = o.IsExecutable;
                sb.Append(proc ? '{' : '[');
                if (depth > 100) sb.Append("...");
                else
                {
                    var items = o.AsArray.Span;
                    for (int i = 0; i < items.Length; i++)
                    {
                        if (i > 0) sb.Append(' ');
                        AppendSyntax(sb, items[i], depth + 1);
                        if (sb.Length > 1 << 20)
                        {
                            sb.Append(" ...");
                            break;
                        }
                    }
                }
                sb.Append(proc ? '}' : ']');
                break;
            }
            case PsType.Dictionary:
                sb.Append("-dict-");
                break;
            case PsType.File:
                sb.Append("-file-");
                break;
            case PsType.Save:
                sb.Append("-save-");
                break;
            case PsType.FontId:
                sb.Append("-fontID-");
                break;
            case PsType.GState:
                sb.Append("-gstate-");
                break;
            default:
                sb.Append("--nostringval--");
                break;
        }
    }

    private static void AppendString(StringBuilder sb, ReadOnlySpan<byte> bytes)
    {
        sb.Append('(');
        foreach (byte b in bytes)
        {
            switch (b)
            {
                case (byte)'(': sb.Append("\\("); break;
                case (byte)')': sb.Append("\\)"); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case (byte)'\n': sb.Append("\\n"); break;
                case (byte)'\r': sb.Append("\\r"); break;
                case (byte)'\t': sb.Append("\\t"); break;
                case (byte)'\b': sb.Append("\\b"); break;
                case (byte)'\f': sb.Append("\\f"); break;
                default:
                    if (b < 32 || b >= 127) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    else sb.Append((char)b);
                    break;
            }
        }
        sb.Append(')');
    }

    /// <summary>
    /// A real like C's %g with 6 digits; with <paramref name="roundTrip"/> 9 digits if 6 do not give the same value back.
    /// A ".0" is added when there is neither a point nor an exponent.
    /// </summary>
    public static string Real(float value, bool roundTrip)
    {
        string s = G(value, 6);
        if (roundTrip && float.Parse(s, CultureInfo.InvariantCulture) != value) s = G(value, 9);
        if (s.IndexOf('.') < 0 && s.IndexOf('e') < 0) s += ".0";
        return s;
    }

    private static string G(float value, int precision)
    {
        if (value == 0) return "0";
        return ((double)value).ToString("G" + precision, CultureInfo.InvariantCulture).Replace('E', 'e');
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
}
