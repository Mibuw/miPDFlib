using MiPdf.Objects;
using System.Diagnostics;
using System.Text;

namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// File, filter and output operators (PLRM 8.1). The sandbox has no file system: only %stdin (empty), %stdout and
/// %stderr can be opened; everything else is an invalidfileaccess.
/// </summary>
internal sealed partial class PsInterpreter
{
    private void RegisterFileOperators()
    {
        Op("file", ip =>
        {
            var access = ip.PopString();
            var name = ip.PopString();
            string n = name.AsString.Text, a = access.AsString.Text;
            PsFile file = n switch
            {
                "%stdout" when a.StartsWith('w') || a.StartsWith('a') => ip.StdOut,
                "%stderr" when a.StartsWith('w') || a.StartsWith('a') => ip.StdErr,
                "%stdin" or "%lineedit" or "%statementedit" when a.StartsWith('r') => ip.StdIn,
                _ => throw new PsError("invalidfileaccess"),
            };
            ip.Push(PsObject.File(file));
        });
        Op("closefile", ip => ip.PopFile().Close());
        Op("read", ip =>
        {
            var f = ip.PopReadable();
            int b = f.ReadByte();
            if (b < 0)
            {
                f.Close();
                ip.Push(false);
                return;
            }
            ip.Push(b);
            ip.Push(true);
        });
        Op("write", ip =>
        {
            long b = ip.PopInt();
            var f = ip.PopWritable();
            f.Write(new[] { (byte)b });
        });
        Op("readstring", ip =>
        {
            var s = ip.PopString(write: true);
            var f = ip.PopReadable();
            var str = s.AsString;
            int n = 0;
            while (n < str.Length)
            {
                int b = f.ReadByte();
                if (b < 0) break;
                str[n++] = (byte)b;
            }
            ip.Push(PsObject.String(str.Interval(0, n), s.Attributes));
            ip.Push(n == str.Length);
        });
        Op("readhexstring", ip =>
        {
            var s = ip.PopString(write: true);
            var f = ip.PopReadable();
            var str = s.AsString;
            int n = 0, high = -1;
            while (n < str.Length)
            {
                int b = f.ReadByte();
                if (b < 0) break;
                int digit = PsAsciiHexDecode.HexDigit(b);
                if (digit < 0) continue; // other characters are ignored
                if (high < 0) high = digit;
                else
                {
                    str[n++] = (byte)(high << 4 | digit);
                    high = -1;
                }
            }
            ip.Push(PsObject.String(str.Interval(0, n), s.Attributes));
            ip.Push(n == str.Length);
        });
        Op("readline", ip =>
        {
            var s = ip.PopString(write: true);
            var f = ip.PopReadable();
            var str = s.AsString;
            int n = 0;
            bool newline = false;
            while (true)
            {
                int b = f.ReadByte();
                if (b < 0) break;
                if (b == '\n')
                {
                    newline = true;
                    break;
                }
                if (b == '\r')
                {
                    int c = f.ReadByte();
                    if (c != '\n') f.Unread(c);
                    newline = true;
                    break;
                }
                if (n >= str.Length) throw new PsError("rangecheck");
                str[n++] = (byte)b;
            }
            ip.Push(PsObject.String(str.Interval(0, n), s.Attributes));
            ip.Push(newline);
        });
        Op("writestring", ip =>
        {
            var s = ip.PopString();
            ip.PopWritable().Write(s.AsString.Span);
        });
        Op("writehexstring", ip =>
        {
            var s = ip.PopString();
            ip.PopWritable().Write(Encoding.ASCII.GetBytes(Convert.ToHexString(s.AsString.Span).ToLowerInvariant()));
        });
        Op("bytesavailable", ip => ip.Push(ip.PopFile().BytesAvailable));
        Op("flushfile", ip =>
        {
            var f = ip.PopFile();
            if (f.CanRead) f.Drain();
            else f.Flush();
        });
        Op("resetfile", ip => ip.PopFile());
        Op("status", ip =>
        {
            var o = ip.Pop();
            if (o.Type == PsType.File) ip.Push(!o.AsFile.Closed);
            else if (o.Type == PsType.String) ip.Push(false); // no files in the sandbox
            else throw new PsError("typecheck");
        });
        Op("fileposition", ip =>
        {
            long position = ip.PopFile().Position;
            if (position < 0) throw new PsError("ioerror");
            ip.Push(position);
        });
        Op("setfileposition", ip =>
        {
            ip.PopInt();
            ip.PopFile();
            throw new PsError("ioerror");
        });
        Op("run", ip =>
        {
            ip.PopString();
            throw new PsError("invalidfileaccess");
        });
        Op("deletefile", ip =>
        {
            ip.PopString();
            throw new PsError("invalidfileaccess");
        });
        Op("renamefile", ip =>
        {
            ip.PopString();
            ip.PopString();
            throw new PsError("invalidfileaccess");
        });
        Op("filenameforall", ip =>
        {
            ip.PopString(write: true);
            ip.PopProc();
            ip.PopString();
        });
        Op("currentfile", ip =>
        {
            for (int i = ip.Exec.Count - 1; i >= 0; i--)
            {
                if (ip.Exec[i] is FileFrame f)
                {
                    ip.Push(PsObject.File(f.File));
                    return;
                }
            }
            var closed = new PsMemoryFile(Array.Empty<byte>());
            closed.Close();
            ip.Push(PsObject.File(closed));
        });
        Op("filter", ip => ip.Push(PsObject.File(ip.CreateFilter())));

        Op("print", ip => ip.StdOut.Write(ip.PopString().AsString.Span));
        Op("=", ip => Write(ip.StdOut, PsText.Latin1.GetString(Formatter.Text(ip.Pop())) + "\n"));
        Op("==", ip => Write(ip.StdOut, Formatter.Syntax(ip.Pop(), 0) + "\n"));
        // Ghostscript extensions used by some generated files: = and == without the newline
        Op("=only", ip => ip.StdOut.Write(Formatter.Text(ip.Pop())));
        Op("==only", ip => Write(ip.StdOut, Formatter.Syntax(ip.Pop(), 0)));
        Op("stack", ip =>
        {
            for (int i = 0; i < ip.Ops.Count; i++) Write(ip.StdOut, PsText.Latin1.GetString(Formatter.Text(ip.Ops.Peek(i))) + "\n");
        });
        Op("pstack", ip =>
        {
            for (int i = 0; i < ip.Ops.Count; i++) Write(ip.StdOut, Formatter.Syntax(ip.Ops.Peek(i), 0) + "\n");
        });
        Op("flush", _ => { });
        Op("echo", ip => ip.PopBool());
        Op("prompt", _ => { });
    }

    private PsFile PopReadable()
    {
        var f = PopFile();
        return f.CanRead ? f : throw new PsError("invalidaccess");
    }

    private PsFile PopWritable()
    {
        var f = PopFile();
        return f.CanWrite ? f : throw new PsError("invalidaccess");
    }

    /// <summary>
    /// <c>filter</c>: source [parameters] /Name filter. Decoding filters read a file or a string; SubFileDecode also
    /// takes a count and an end string.
    /// </summary>
    private PsFile CreateFilter()
    {
        var nameObject = Pop();
        if (nameObject.Type != PsType.Name) throw new PsError("typecheck");
        string name = nameObject.AsName.Text;
        int count = 0;
        byte[]? eod = null;
        if (name == "SubFileDecode" && Ops.Count > 0 && Ops.Peek().Type == PsType.Dictionary)
        {
            // parameters in a dictionary (LanguageLevel 3)
            var parameters = PopDict();
            count = parameters.Map.TryGetValue(Key("EODCount"), out var c) && c.Type == PsType.Integer && c.Int >= 0 ? (int)Math.Min(c.Int, int.MaxValue) : 0;
            eod = parameters.Map.TryGetValue(Key("EODString"), out var s) && s.Type == PsType.String ? s.AsString.ToArray() : Array.Empty<byte>();
        }
        else if (name == "SubFileDecode")
        {
            var e = PopString();
            eod = e.AsString.ToArray();
            count = PopCount();
        }
        // an optional parameter dictionary
        PsDict? parms = Ops.Count > 0 && Ops.Peek().Type == PsType.Dictionary ? PopDict() : null;
        var source = Pop();
        if (name.EndsWith("Encode", StringComparison.Ordinal))
        {
            if (source.Type != PsType.File) throw new PsError("typecheck");
            if (!source.AsFile.CanWrite) throw new PsError("invalidaccess");
            return name == "NullEncode" ? new PsNullEncode(source.AsFile) : throw new PsError("undefined");
        }
        PsFile input = source.Type switch
        {
            PsType.File when source.AsFile.CanRead => source.AsFile,
            PsType.File => throw new PsError("invalidaccess"),
            PsType.String => new PsMemoryFile(Readable(source).AsString.ToArray()),
            PsType.Array or PsType.PackedArray when source.IsExecutable => new PsProcedureFile(this, source),
            _ => throw new PsError("typecheck"),
        };
        var pdfParms = parms != null ? ToPdfParameters(parms) : null;
        int limit = Limits.MaxStringLength;
        PsFile filter = name switch
        {
            "ASCIIHexDecode" or "AHx" => new PsAsciiHexDecode(input),
            "ASCII85Decode" or "A85" => new PsAscii85Decode(input),
            "RunLengthDecode" or "RL" => new PsRunLengthDecode(input),
            "FlateDecode" or "Fl" => new PsFlateDecode(input),
            "LZWDecode" or "LZW" => new PsLzwDecode(input, pdfParms?.GetInt(PdfName.EarlyChange, 1) ?? 1),
            "SubFileDecode" => new PsSubFileDecode(input, count, eod!),
            "CCITTFaxDecode" or "CCF" => new PsBufferedFilter(() => Decoded(() => MiPdf.Filters.CcittFaxDecoder.Decode(PsBufferedFilter.ReadAll(input, limit), pdfParms))),
            "DCTDecode" or "DCT" => new PsDctDecode(input, pdfParms, limit),
            _ => throw new PsError("undefined"),
        };
        // a predictor (Flate and LZW) works on the decoded data as a whole
        if (name is "FlateDecode" or "Fl" or "LZWDecode" or "LZW" && pdfParms != null && pdfParms.GetInt(PdfName.Get("Predictor"), 1) > 1)
        {
            var inner = filter;
            filter = new PsBufferedFilter(() => Decoded(() => MiPdf.Filters.Predictor.Apply(PsBufferedFilter.ReadAll(inner, limit), pdfParms)));
        }
        return filter;
    }

    /// <summary>Runs a decoder of miPDFlib; damaged data is an ioerror.</summary>
    private static byte[] Decoded(Func<byte[]> decode)
    {
        try
        {
            return decode();
        }
        catch (PdfException)
        {
            throw new PsError("ioerror");
        }
    }

    /// <summary>The simple entries (numbers, booleans, names) of filter parameters as a PDF dictionary.</summary>
    private static PdfDictionary ToPdfParameters(PsDict parms)
    {
        var result = new PdfDictionary();
        foreach (var (key, value) in parms.Map)
        {
            if (key.Type != PsType.Name) continue;
            PdfObject? v = value.Type switch
            {
                PsType.Integer => new PdfNumber(value.Int),
                PsType.Real => new PdfNumber(value.Real),
                PsType.Boolean => value.Bool ? PdfBoolean.True : PdfBoolean.False,
                PsType.Name => PdfName.Get(value.AsName.Text),
                _ => null,
            };
            if (v != null) result[PdfName.Get(key.AsName.Text)] = v;
        }
        return result;
    }

    private void RegisterMiscOperators()
    {
        Op("version", ip => ip.Push(ip.NewString(Encoding.ASCII.GetBytes("3010"))));
        Op("product", ip => ip.Push(ip.NewString(Encoding.ASCII.GetBytes("miPDFlib"))));
        Op("revision", ip => ip.Push(1));
        Op("serialnumber", ip => ip.Push(0));
        Op("languagelevel", ip => ip.Push(3));
        Op("realtime", ip => ip.Push(ip._clock.ElapsedMilliseconds));
        Op("usertime", ip => ip.Push((long)Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds));
        // user and system parameters (PLRM appendix C): kept and returned, without effect
        Op("setuserparams", ip => ip.MergeParameters(ip.UserParameters(), ip.PopDict()));
        Op("currentuserparams", ip => ip.Push(ip.CopyParameters(ip.UserParameters())));
        Op("setsystemparams", ip => ip.MergeParameters(ip.SystemParameters(), ip.PopDict()));
        Op("currentsystemparams", ip => ip.Push(ip.CopyParameters(ip.SystemParameters())));
    }

    private PsDict? _userParameters, _systemParameters;

    private PsDict UserParameters() => _userParameters ??= Parameters(new (string, long)[]
    {
        ("MaxFontItem", 12000), ("MinFontCompress", 20), ("MaxUPathItem", 5000), ("MaxFormItem", 100000),
        ("MaxPatternItem", 20000), ("MaxScreenItem", 48000), ("MaxOpStack", Limits.MaxOperandStack),
        ("MaxDictStack", Limits.MaxDictStack), ("MaxExecStack", Limits.MaxExecStack), ("MaxLocalVM", Limits.MaxMemory),
        ("VMReclaim", 0), ("VMThreshold", 1000000), ("AccurateScreens", 0),
    }, ("JobName", ""));

    private PsDict SystemParameters() => _systemParameters ??= Parameters(new (string, long)[]
    {
        ("BuildTime", 1), ("ByteOrder", 0), ("CurDisplayList", 0), ("CurFontCache", 0), ("CurFormCache", 0),
        ("CurOutlineCache", 0), ("CurPatternCache", 0), ("CurScreenStorage", 0), ("CurSourceList", 0),
        ("CurStoredFontCache", 0), ("CurStoredScreenCache", 0), ("CurUPathCache", 0), ("MaxDisplayList", 10000000),
        ("MaxFontCache", 10000000), ("MaxFormCache", 10000000), ("MaxGlobalVM", Limits.MaxMemory),
        ("MaxOutlineCache", 10000000), ("MaxPatternCache", 10000000), ("MaxScreenStorage", 10000000),
        ("MaxSourceList", 10000000), ("MaxUPathCache", 10000000), ("RealFormat", 0),
    }, ("Revision", "1"));

    private PsDict Parameters((string Name, long Value)[] numbers, (string Name, string Value) text)
    {
        bool global = GlobalMode;
        GlobalMode = true;
        var d = NewDict(numbers.Length + 1);
        foreach (var (name, value) in numbers)
            d.Map[Key(name)] = name is "AccurateScreens" ? PsObject.False : name == "ByteOrder" ? PsObject.False
                : name == "RealFormat" ? NewString(Encoding.ASCII.GetBytes("IEEE")) : PsObject.Integer(value);
        d.Map[Key(text.Name)] = NewString(Encoding.ASCII.GetBytes(text.Value));
        GlobalMode = global;
        return d;
    }

    private void MergeParameters(PsDict target, PsDict values)
    {
        // only known parameters with values of the same type are taken
        foreach (var (key, value) in values.Map)
            if (target.Map.TryGetValue(key, out var old) && (old.Type == value.Type || old.IsNumber && value.IsNumber) && Composite(value) == null)
                target.Map[key] = value;
    }

    private PsObject CopyParameters(PsDict source)
    {
        var copy = NewDict(source.Count);
        foreach (var (key, value) in source.Map) copy.Map[key] = value;
        return PsObject.Dictionary(copy);
    }
}
