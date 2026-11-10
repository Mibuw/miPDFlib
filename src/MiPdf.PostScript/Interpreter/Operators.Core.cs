namespace MiPdf.PostScript.Interpreter;

/// <summary>Setup of the standard dictionaries and helpers for operators.</summary>
internal sealed partial class PsInterpreter
{
    /// <summary>The standard errors (PLRM 3.11) with their errordict entries.</summary>
    public static readonly string[] ErrorNames =
    {
        "configurationerror", "dictfull", "dictstackoverflow", "dictstackunderflow", "execstackoverflow", "interrupt",
        "invalidaccess", "invalidexit", "invalidfileaccess", "invalidfont", "invalidrestore", "ioerror", "limitcheck",
        "nocurrentpoint", "rangecheck", "stackoverflow", "stackunderflow", "syntaxerror", "timeout", "typecheck",
        "undefined", "undefinedfilename", "undefinedresource", "undefinedresult", "unmatchedmark", "unregistered",
        "VMerror",
    };

    private void Initialize()
    {
        // systemdict and globaldict live in global VM, the others in local VM (as in Adobe interpreters)
        GlobalMode = true;
        SystemDict = NewDict(512);
        GlobalDict = NewDict(64);
        GlobalMode = false;
        UserDict = NewDict(256);
        ErrorDict = NewDict(40);
        ErrorInfo = NewDict(20);
        StatusDict = NewDict(32);
        DictStack.Add(SystemDict);
        DictStack.Add(GlobalDict);
        DictStack.Add(UserDict);

        DefineValue("systemdict", PsObject.Dictionary(SystemDict));
        DefineValue("globaldict", PsObject.Dictionary(GlobalDict));
        DefineValue("userdict", PsObject.Dictionary(UserDict));
        DefineValue("errordict", PsObject.Dictionary(ErrorDict));
        DefineValue("$error", PsObject.Dictionary(ErrorInfo));
        DefineValue("statusdict", PsObject.Dictionary(StatusDict));
        DefineValue("true", PsObject.True);
        DefineValue("false", PsObject.False);
        DefineValue("null", PsObject.Null);

        foreach (string error in ErrorNames)
        {
            string name = error;
            ErrorDict.Map[Key(name)] = PsObject.Operator(new PsOperator(Name(name), ip => ip.DefaultErrorHandler(name)));
        }
        ErrorDict.Map[Key("handleerror")] = PsObject.Operator(new PsOperator(Name("handleerror"), ip => ip.HandleErrorMessage()));
        ErrorInfo.Map[Key("newerror")] = PsObject.False;
        ErrorInfo.Map[Key("recordstacks")] = PsObject.True;
        ErrorInfo.Map[Key("binary")] = PsObject.False;

        GlobalMode = true;
        DefineValue("StandardEncoding", EncodingArray(PsEncodings.Standard));
        DefineValue("ISOLatin1Encoding", EncodingArray(PsEncodings.IsoLatin1));
        GlobalMode = false;

        RegisterStackOperators();
        RegisterMathOperators();
        RegisterTypeOperators();
        RegisterCompositeOperators();
        RegisterControlOperators();
        RegisterVmOperators();
        RegisterFileOperators();
        RegisterMiscOperators();
        RegisterGraphicsOperators();
        RegisterColorOperators();
        RegisterDeviceOperators();
        RegisterResourceOperators();

        SystemDict.Access = PsDictAccess.ReadOnly;
    }

    /// <summary>Defines an operator in systemdict.</summary>
    internal void Op(string name, Action<PsInterpreter> run) =>
        SystemDict.Map[Key(name)] = PsObject.Operator(new PsOperator(Name(name), run));

    private void DefineValue(string name, PsObject value) => SystemDict.Map[Key(name)] = value;

    private PsObject EncodingArray(string[] names) =>
        NewArray(names.Select(n => PsObject.Name(Name(n))).ToArray()).WithAttributes(PsAttributes.ReadOnly);

    /// <summary><c>handleerror</c>: reports the error recorded in $error.</summary>
    private void HandleErrorMessage()
    {
        if (!ErrorInfo.Map.TryGetValue(Key("newerror"), out var newError) || newError.Type != PsType.Boolean || !newError.Bool) return;
        string error = ErrorInfo.Map.TryGetValue(Key("errorname"), out var n) ? PsText.Latin1.GetString(Formatter.Text(n)) : "unknown";
        string command = ErrorInfo.Map.TryGetValue(Key("command"), out var c) ? PsText.Latin1.GetString(Formatter.Text(c)) : "";
        Write(StdOut, $"%%[ Error: {error}; OffendingCommand: {command} ]%%\n");
        ErrorInfo.Map[Key("newerror")] = PsObject.False;
    }

    // ------------------------------------------------------------------ operand helpers

    public PsObject Pop() => Ops.Pop();

    public void Push(in PsObject o) => Ops.Push(o);

    public void Push(long value) => Ops.Push(PsObject.Integer(value));

    public void Push(bool value) => Ops.Push(PsObject.Boolean(value));

    /// <summary>Pushes a real; an infinite or undefined result is an error.</summary>
    public void PushReal(double value)
    {
        float f = (float)value;
        if (float.IsInfinity(f) || float.IsNaN(f)) throw new PsError("undefinedresult");
        Ops.Push(PsObject.RealValue(f));
    }

    public long PopInt()
    {
        var o = Ops.Pop();
        return o.Type == PsType.Integer ? o.Int : throw new PsError("typecheck");
    }

    /// <summary>A non-negative integer that fits an int (counts, indices, sizes).</summary>
    public int PopCount()
    {
        long v = PopInt();
        if (v < 0) throw new PsError("rangecheck");
        return v > int.MaxValue ? throw new PsError("limitcheck") : (int)v;
    }

    public PsObject PopNumber()
    {
        var o = Ops.Pop();
        return o.IsNumber ? o : throw new PsError("typecheck");
    }

    public double PopReal() => PopNumber().Number;

    public bool PopBool()
    {
        var o = Ops.Pop();
        return o.Type == PsType.Boolean ? o.Bool : throw new PsError("typecheck");
    }

    public PsObject PopString(bool write = false)
    {
        var o = Ops.Pop();
        if (o.Type != PsType.String) throw new PsError("typecheck");
        if (write ? !o.CanWrite : !o.CanRead) throw new PsError("invalidaccess");
        return o;
    }

    public PsObject PopArray(bool write = false)
    {
        var o = Ops.Pop();
        if (!o.IsArray) throw new PsError("typecheck");
        if (write ? !o.CanWrite : !o.CanRead) throw new PsError("invalidaccess");
        return o;
    }

    public PsDict PopDict(bool write = false)
    {
        var o = Ops.Pop();
        if (o.Type != PsType.Dictionary) throw new PsError("typecheck");
        var d = o.AsDict;
        if (write ? d.Access != PsDictAccess.Unlimited : d.Access >= PsDictAccess.ExecuteOnly) throw new PsError("invalidaccess");
        return d;
    }

    /// <summary>A procedure operand (any array; literal arrays are executed as procedures too).</summary>
    public PsObject PopProc()
    {
        var o = Ops.Pop();
        if (!o.IsArray) throw new PsError("typecheck");
        if ((o.Attributes & PsAttributes.NoAccess) != 0) throw new PsError("invalidaccess");
        return o;
    }

    public PsFile PopFile()
    {
        var o = Ops.Pop();
        return o.Type == PsType.File ? o.AsFile : throw new PsError("typecheck");
    }
}
