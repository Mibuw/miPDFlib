using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Fonts;

/// <summary>A glyph in glyph space: its advance width and outline.</summary>
internal sealed class GlyphOutline
{
    public double AdvanceX, AdvanceY;
    public readonly List<PsSegment> Segments = new();

    /// <summary>The glyph names of the base and accent of a seac composite (they must be embedded as well).</summary>
    public string? SeacBase, SeacAccent;
}

/// <summary>
/// Type 1 charstrings (Adobe Type 1 Font Format, chapter 6): decryption, the charstring commands with subroutines,
/// flex and hint replacement through the standard OtherSubrs 0-3, and seac accented characters.
/// </summary>
internal static class Type1Charstrings
{
    public const int CharstringKey = 4330;
    public const int EexecKey = 55665;

    /// <summary>Decrypts charstring or eexec data and drops the first <paramref name="skip"/> bytes.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> data, int key, int skip)
    {
        if (skip < 0) return data.ToArray(); // lenIV -1: not encrypted
        var result = new byte[Math.Max(0, data.Length - skip)];
        int r = key;
        for (int i = 0; i < data.Length; i++)
        {
            int c = data[i];
            int p = c ^ (r >> 8);
            r = ((c + r) * 52845 + 22719) & 0xFFFF;
            if (i >= skip) result[i - skip] = (byte)p;
        }
        return result;
    }

    /// <summary>Encrypts data (the inverse of <see cref="Decrypt"/>) after <paramref name="prefix"/> leading bytes.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain, int key, ReadOnlySpan<byte> prefix)
    {
        var result = new byte[prefix.Length + plain.Length];
        int r = key;
        for (int i = 0; i < result.Length; i++)
        {
            int p = i < prefix.Length ? prefix[i] : plain[i - prefix.Length];
            int c = p ^ (r >> 8);
            r = ((c + r) * 52845 + 22719) & 0xFFFF;
            result[i] = (byte)c;
        }
        return result;
    }

    /// <summary>
    /// Interprets a charstring. <paramref name="glyph"/> returns the decrypted charstring of a glyph name (for seac),
    /// <paramref name="subr"/> a decrypted subroutine; both return null if missing.
    /// </summary>
    public static GlyphOutline? Run(byte[] code, Func<int, byte[]?> subr, Func<string, byte[]?> glyph)
    {
        var outline = new GlyphOutline();
        var state = new State(outline);
        try
        {
            Execute(code, state, subr, glyph, 0);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return outline.Segments.Count > 0 ? outline : null;
        }
        if (state.Open) outline.Segments.Add(PsSegment.Close);
        return outline;
    }

    private sealed class State(GlyphOutline outline)
    {
        public readonly GlyphOutline Outline = outline;
        public readonly List<double> Stack = new();
        public readonly Stack<double> PsStack = new();
        public double X, Y, SideBearingX, OriginX, OriginY;
        public bool Open, Flex, Ended, Accent;
        public readonly List<(double X, double Y)> FlexPoints = new();

        public void MoveTo(double x, double y)
        {
            if (Open) Outline.Segments.Add(PsSegment.Close);
            Open = false;
            X = x;
            Y = y;
        }

        private void Start()
        {
            if (Open) return;
            Outline.Segments.Add(PsSegment.Move(X, Y));
            Open = true;
        }

        public void LineTo(double x, double y)
        {
            Start();
            Outline.Segments.Add(PsSegment.Line(x, y));
            X = x;
            Y = y;
        }

        public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            Start();
            Outline.Segments.Add(new PsSegment(PsSegmentKind.CurveTo, x1, y1, x2, y2, x3, y3));
            X = x3;
            Y = y3;
        }
    }

    private static void Execute(byte[] code, State s, Func<int, byte[]?> subr, Func<string, byte[]?> glyph, int depth)
    {
        if (depth > 20) return;
        var stack = s.Stack;
        int p = 0;
        while (p < code.Length && !s.Ended)
        {
            int v = code[p++];
            if (v >= 32)
            {
                if (v <= 246) stack.Add(v - 139);
                else if (v <= 250) stack.Add((v - 247) * 256 + code[p++] + 108);
                else if (v <= 254) stack.Add(-(v - 251) * 256 - code[p++] - 108);
                else
                {
                    stack.Add((code[p] << 24) | (code[p + 1] << 16) | (code[p + 2] << 8) | code[p + 3]);
                    p += 4;
                }
                continue;
            }
            double Arg(int i) => i < stack.Count ? stack[i] : 0;
            switch (v)
            {
                case 13: // hsbw: sbx wx
                    s.SideBearingX = Arg(0);
                    if (!s.Accent)
                    {
                        s.Outline.AdvanceX = Arg(1);
                        s.Outline.AdvanceY = 0;
                    }
                    s.X = s.OriginX + Arg(0);
                    s.Y = s.OriginY;
                    break;
                case 9: // closepath
                    if (s.Open) s.Outline.Segments.Add(PsSegment.Close);
                    s.Open = false;
                    break;
                case 21: // rmoveto
                    if (s.Flex) s.FlexPoints.Add((s.X += Arg(0), s.Y += Arg(1)));
                    else s.MoveTo(s.X + Arg(0), s.Y + Arg(1));
                    break;
                case 22: // hmoveto
                    if (s.Flex) s.FlexPoints.Add((s.X += Arg(0), s.Y));
                    else s.MoveTo(s.X + Arg(0), s.Y);
                    break;
                case 4: // vmoveto
                    if (s.Flex) s.FlexPoints.Add((s.X, s.Y += Arg(0)));
                    else s.MoveTo(s.X, s.Y + Arg(0));
                    break;
                case 5: // rlineto
                    s.LineTo(s.X + Arg(0), s.Y + Arg(1));
                    break;
                case 6: // hlineto
                    s.LineTo(s.X + Arg(0), s.Y);
                    break;
                case 7: // vlineto
                    s.LineTo(s.X, s.Y + Arg(0));
                    break;
                case 8: // rrcurveto
                {
                    double x1 = s.X + Arg(0), y1 = s.Y + Arg(1), x2 = x1 + Arg(2), y2 = y1 + Arg(3);
                    s.CurveTo(x1, y1, x2, y2, x2 + Arg(4), y2 + Arg(5));
                    break;
                }
                case 30: // vhcurveto
                {
                    double x1 = s.X, y1 = s.Y + Arg(0), x2 = x1 + Arg(1), y2 = y1 + Arg(2);
                    s.CurveTo(x1, y1, x2, y2, x2 + Arg(3), y2);
                    break;
                }
                case 31: // hvcurveto
                {
                    double x1 = s.X + Arg(0), y1 = s.Y, x2 = x1 + Arg(1), y2 = y1 + Arg(2);
                    s.CurveTo(x1, y1, x2, y2, x2, y2 + Arg(3));
                    break;
                }
                case 10: // callsubr
                {
                    if (stack.Count == 0) return;
                    int index = (int)stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    var body = subr(index);
                    if (body != null) Execute(body, s, subr, glyph, depth + 1);
                    continue; // the subroutine works on the same stack
                }
                case 11: // return
                    return;
                case 14: // endchar
                    s.Ended = true;
                    break;
                case 1:
                case 3: // hstem, vstem: hints
                    break;
                case 12:
                {
                    int e = code[p++];
                    switch (e)
                    {
                        case 7: // sbw: sbx sby wx wy
                            s.SideBearingX = Arg(0);
                            if (!s.Accent)
                            {
                                s.Outline.AdvanceX = Arg(2);
                                s.Outline.AdvanceY = Arg(3);
                            }
                            s.X = s.OriginX + Arg(0);
                            s.Y = s.OriginY + Arg(1);
                            break;
                        case 6: // seac: asb adx ady bchar achar
                        {
                            double asb = Arg(0), adx = Arg(1), ady = Arg(2);
                            string? baseName = Code((int)Arg(3)), accentName = Code((int)Arg(4));
                            s.Outline.SeacBase = baseName;
                            s.Outline.SeacAccent = accentName;
                            stack.Clear();
                            var baseCode = baseName != null ? glyph(baseName) : null;
                            if (baseCode != null)
                            {
                                var baseState = new State(s.Outline) { OriginX = 0, OriginY = 0 };
                                Execute(baseCode, baseState, subr, glyph, depth + 1);
                                if (baseState.Open) s.Outline.Segments.Add(PsSegment.Close);
                            }
                            var accentCode = accentName != null ? glyph(accentName) : null;
                            if (accentCode != null)
                            {
                                double advance = s.Outline.AdvanceX, advanceY = s.Outline.AdvanceY;
                                var accentState = new State(s.Outline) { OriginX = adx - asb, OriginY = ady, Accent = true };
                                Execute(accentCode, accentState, subr, glyph, depth + 1);
                                if (accentState.Open) s.Outline.Segments.Add(PsSegment.Close);
                                s.Outline.AdvanceX = advance;
                                s.Outline.AdvanceY = advanceY;
                            }
                            s.Ended = true;
                            s.Open = false;
                            return;
                        }
                        case 12: // div
                            if (stack.Count >= 2)
                            {
                                double b = stack[^1], a = stack[^2];
                                stack.RemoveRange(stack.Count - 2, 2);
                                stack.Add(b == 0 ? 0 : a / b);
                            }
                            continue;
                        case 16: // callothersubr: args n othersubr#
                        {
                            if (stack.Count < 2) break;
                            int number = (int)stack[^1], n = (int)stack[^2];
                            stack.RemoveRange(stack.Count - 2, 2);
                            var args = new List<double>();
                            for (int i = 0; i < n && stack.Count > 0; i++)
                            {
                                args.Insert(0, stack[^1]);
                                stack.RemoveAt(stack.Count - 1);
                            }
                            OtherSubr(s, number, args);
                            continue;
                        }
                        case 17: // pop: a value of the PostScript stack (results of othersubrs)
                            stack.Add(s.PsStack.Count > 0 ? s.PsStack.Pop() : 0);
                            continue;
                        case 33: // setcurrentpoint
                            s.X = Arg(0);
                            s.Y = Arg(1);
                            break;
                        // 0 dotsection, 1 vstem3, 2 hstem3: hints
                    }
                    break;
                }
            }
            stack.Clear();
        }
    }

    /// <summary>The standard OtherSubrs: 0 ends flex, 1 starts it, 2 adds a point, 3 hint replacement.</summary>
    private static void OtherSubr(State s, int number, List<double> args)
    {
        switch (number)
        {
            case 1:
                s.Flex = true;
                s.FlexPoints.Clear();
                break;
            case 2:
                break;
            case 0:
            {
                s.Flex = false;
                // 7 points: the reference point, then two curves of three points each
                var f = s.FlexPoints;
                if (f.Count >= 7)
                {
                    s.CurveTo(f[1].X, f[1].Y, f[2].X, f[2].Y, f[3].X, f[3].Y);
                    s.CurveTo(f[4].X, f[4].Y, f[5].X, f[5].Y, f[6].X, f[6].Y);
                }
                f.Clear();
                // the end point is returned for the following setcurrentpoint
                s.PsStack.Push(s.Y);
                s.PsStack.Push(s.X);
                break;
            }
            default:
                // hint replacement (3) and unknown OtherSubrs: the arguments come back with pop, the first one first
                // (for 3 it is the number of the subroutine with the new hints)
                for (int i = args.Count - 1; i >= 0; i--) s.PsStack.Push(args[i]);
                break;
        }
    }

    /// <summary>The standard encoding names used by seac.</summary>
    private static string? Code(int code) =>
        code is >= 0 and < 256 && Interpreter.PsEncodings.Standard[code] != ".notdef" ? Interpreter.PsEncodings.Standard[code] : null;
}
