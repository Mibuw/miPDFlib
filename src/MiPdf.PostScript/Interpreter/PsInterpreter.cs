using System.Diagnostics;
using System.Text;

namespace MiPdf.PostScript.Interpreter;

/// <summary>A PostScript error (PLRM 3.11): the name of the errordict entry that handles it.</summary>
internal sealed class PsError : Exception
{
    public readonly string Name;
    public readonly PsObject? Command;

    public PsError(string name, PsObject? command = null) : base(name)
    {
        Name = name;
        Command = command;
    }
}

/// <summary>Limits of the sandbox. Exceeding one ends the job with a <see cref="PdfException"/>; PostScript cannot catch it.</summary>
internal sealed class PsLimits
{
    public long MaxOperations { get; init; } = 1_000_000_000;
    public TimeSpan? Timeout { get; init; }
    public long MaxMemory { get; init; } = 1L << 30;
    public int MaxOperandStack { get; init; } = 1_000_000;
    public int MaxDictStack { get; init; } = 10_000;
    public int MaxExecStack { get; init; } = 100_000;
    public int MaxStringLength { get; init; } = 1 << 26;
    public int MaxArrayLength { get; init; } = 1 << 24;
}

/// <summary>The outcome of a job: the error that ended it (if any) and the text written to %stdout and %stderr.</summary>
internal sealed class PsJobResult
{
    public string? ErrorName { get; init; }
    public string? OffendingCommand { get; init; }
    public string Output { get; init; } = "";
}

/// <summary>
/// The PostScript interpreter (PLRM chapter 3): operand, dictionary and execution stacks, the scanner, error handling
/// through errordict, save/restore of local VM and the built-in operators. It runs in a sandbox: no file system access,
/// and limits on operations, time, memory and stack depths.
/// </summary>
internal sealed partial class PsInterpreter
{
    public readonly PsLimits Limits;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<string, PsName> _names = new();
    private readonly PsScanner _scanner;
    public readonly OperandStack Ops;
    public readonly List<PsDict> DictStack = new();
    internal readonly List<ExecFrame> Exec = new();
    public PsDict SystemDict = null!, GlobalDict = null!, UserDict = null!, ErrorDict = null!, ErrorInfo = null!, StatusDict = null!;
    public readonly PsFile StdOut, StdErr, StdIn;
    private readonly MemoryStream _output = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _operations, _allocatedSinceCheck, _memoryBaseline;
    private bool _quit;
    private PsObject? _command;

    public bool Packing;
    public bool GlobalMode;

    public PsInterpreter(PsLimits? limits = null, CancellationToken cancellation = default)
    {
        Limits = limits ?? new PsLimits();
        _cancellation = cancellation;
        _scanner = new PsScanner(this);
        Ops = new OperandStack(Limits.MaxOperandStack);
        StdOut = new PsOutputFile(WriteOutput);
        StdErr = new PsOutputFile(WriteOutput);
        StdIn = new PsMemoryFile(Array.Empty<byte>());
        _memoryBaseline = GC.GetTotalMemory(false);
        Initialize();
    }

    /// <summary>The text written to %stdout and %stderr so far.</summary>
    public string Output => PsText.Latin1.GetString(_output.GetBuffer(), 0, (int)_output.Length);

    private void WriteOutput(ReadOnlySpan<byte> data)
    {
        if (_output.Length + data.Length > Limits.MaxStringLength) return; // output beyond the limit is dropped
        _output.Write(data);
    }

    public PsName Name(string text)
    {
        if (!_names.TryGetValue(text, out var name)) _names[text] = name = new PsName(text);
        return name;
    }

    // ------------------------------------------------------------------ jobs

    /// <summary>
    /// Runs a program as a job: an error that is not caught ends it (the rest of the program is skipped) and is
    /// reported in the result, like a printer reports it.
    /// </summary>
    public PsJobResult Run(byte[] program)
    {
        _quit = false;
        ErrorInfo.Map[Key("newerror")] = PsObject.False;
        int depth = Exec.Count;
        Exec.Add(new StoppedFrame { Job = true });
        Exec.Add(new FileFrame(new PsMemoryFile(program)));
        string? error = null, command = null;
        Loop(depth);
        if (_jobStopped)
        {
            _jobStopped = false;
            if (ErrorInfo.Map.TryGetValue(Key("newerror"), out var newError) && newError.Type == PsType.Boolean && newError.Bool)
            {
                error = ErrorInfo.Map.TryGetValue(Key("errorname"), out var n) && n.Type == PsType.Name ? n.AsName.Text : "unknown";
                command = ErrorInfo.Map.TryGetValue(Key("command"), out var c) ? PsText.Latin1.GetString(Formatter.Text(c)) : "";
                if (command == "--nostringval--" && c.Type != PsType.Null) command = Formatter.Syntax(c, 0);
                Write(StdOut, $"%%[ Error: {error}; OffendingCommand: {command} ]%%\n");
                ErrorInfo.Map[Key("newerror")] = PsObject.False;
            }
        }
        while (Exec.Count > depth) Exec.RemoveAt(Exec.Count - 1);
        return new PsJobResult { ErrorName = error, OffendingCommand = command, Output = Output };
    }

    private bool _jobStopped;

    internal static void Write(PsFile file, string text) => file.Write(PsText.Latin1.GetBytes(text));

    // ------------------------------------------------------------------ execution

    /// <summary>Runs the execution stack down to <paramref name="depth"/> frames.</summary>
    internal void Loop(int depth)
    {
        while (Exec.Count > depth && !_quit)
        {
            var frame = Exec[^1];
            PsObject current = PsObject.Null;
            Ops.BeginOperator();
            _command = null;
            try
            {
                switch (frame)
                {
                    case ProcFrame p:
                    {
                        if (p.Index >= p.Proc.Length)
                        {
                            Exec.RemoveAt(Exec.Count - 1);
                            break;
                        }
                        current = p.Proc[p.Index++];
                        if (p.Index >= p.Proc.Length) Exec.RemoveAt(Exec.Count - 1); // tail call
                        ExecuteElement(current);
                        break;
                    }
                    case FileFrame f:
                    {
                        if (f.File.Closed || !_scanner.Next(f.File, out current))
                        {
                            Exec.RemoveAt(Exec.Count - 1);
                            break;
                        }
                        ExecuteElement(current);
                        break;
                    }
                    case LoopFrame l:
                        CountOperation();
                        if (!l.Next(this)) Remove(l);
                        break;
                    case StoppedFrame s:
                        Exec.RemoveAt(Exec.Count - 1);
                        if (s.Job) return;
                        Ops.Push(PsObject.False);
                        break;
                    case ContinuationFrame c:
                        Exec.RemoveAt(Exec.Count - 1);
                        c.Continue(this);
                        break;
                }
            }
            catch (PsError e)
            {
                HandleError(e.Name, e.Command ?? _command ?? current);
            }
        }
    }

    private void Remove(ExecFrame frame)
    {
        int i = Exec.LastIndexOf(frame);
        if (i >= 0) Exec.RemoveRange(i, Exec.Count - i);
    }

    /// <summary>An object met in a procedure or read from a file: procedures are pushed, other executable objects run.</summary>
    private void ExecuteElement(in PsObject o)
    {
        if (!o.IsExecutable || o.IsArray)
        {
            Ops.Push(o);
            CountOperation();
            return;
        }
        Execute(o);
    }

    /// <summary>Executes an object (as <c>exec</c> does).</summary>
    public void Execute(PsObject o)
    {
        for (int indirection = 0; ; indirection++)
        {
            CountOperation();
            if (!o.IsExecutable)
            {
                Ops.Push(o);
                return;
            }
            switch (o.Type)
            {
                case PsType.Name:
                {
                    var value = Lookup(o.AsName) ?? throw new PsError("undefined", o);
                    if (value.IsProcedure)
                    {
                        PushProcedure(value);
                        return;
                    }
                    o = value;
                    continue;
                }
                case PsType.Operator:
                    _command = o;
                    Ops.BeginOperator();
                    o.AsOperator.Run(this);
                    return;
                case PsType.Array:
                case PsType.PackedArray:
                    if ((o.Attributes & PsAttributes.NoAccess) != 0) throw new PsError("invalidaccess");
                    PushProcedure(o);
                    return;
                case PsType.String:
                    if ((o.Attributes & PsAttributes.NoAccess) != 0) throw new PsError("invalidaccess");
                    PushFrame(new FileFrame(new PsMemoryFile(o.AsString.ToArray())));
                    return;
                case PsType.File:
                    PushFrame(new FileFrame(o.AsFile));
                    return;
                case PsType.Null:
                    return;
                default:
                    Ops.Push(o);
                    return;
            }
        }
    }

    public void PushProcedure(in PsObject proc)
    {
        if (proc.AsArray.Length > 0) PushFrame(new ProcFrame(proc.AsArray));
    }

    internal void PushFrame(ExecFrame frame)
    {
        if (Exec.Count >= Limits.MaxExecStack) throw new PsError("execstackoverflow");
        Exec.Add(frame);
    }

    /// <summary>Counts an operation and checks the limits now and then.</summary>
    public void CountOperation()
    {
        if ((++_operations & 0xFFFF) == 0) CheckLimits();
    }

    private void CheckLimits()
    {
        if (_operations > Limits.MaxOperations)
            throw new PdfException($"PostScript program exceeds the limit of {Limits.MaxOperations} operations.");
        if (Limits.Timeout is { } timeout && _clock.Elapsed > timeout)
            throw new PdfException($"PostScript program exceeds the time limit of {timeout.TotalSeconds:0.#} s.");
        _cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>Accounts for an allocation; checks the memory in use after every 16 MB allocated.</summary>
    public void Allocated(long bytes)
    {
        _allocatedSinceCheck += bytes;
        if (_allocatedSinceCheck < 16 << 20) return;
        _allocatedSinceCheck = 0;
        if (GC.GetTotalMemory(false) - _memoryBaseline <= Limits.MaxMemory) return;
        if (GC.GetTotalMemory(true) - _memoryBaseline > Limits.MaxMemory)
            throw new PdfException($"PostScript program exceeds the memory limit of {Limits.MaxMemory >> 20} MB.");
    }

    // ------------------------------------------------------------------ errors and control

    private void HandleError(string name, PsObject command)
    {
        Ops.RestoreAfterError();
        if (name == "stackoverflow") Ops.Clear();
        if (name == "execstackoverflow")
        {
            // keep the stopped and job frames, drop the rest above the innermost one
            int keep = Exec.FindLastIndex(f => f is StoppedFrame);
            if (keep >= 0) Exec.RemoveRange(keep + 1, Exec.Count - keep - 1);
        }
        Ops.Push(command);
        var handler = ErrorDict.Map.TryGetValue(Key(name), out var h) ? h : (PsObject?)null;
        try
        {
            if (handler == null) DefaultErrorHandler(name);
            else Execute(handler.Value);
        }
        catch (PsError)
        {
            DefaultErrorHandler(name);
        }
    }

    /// <summary>The default error handler: records the error in $error and executes <c>stop</c>.</summary>
    internal void DefaultErrorHandler(string name)
    {
        var command = Ops.Count > 0 ? Ops.Pop() : PsObject.Null;
        var map = ErrorInfo.Map;
        map[Key("newerror")] = PsObject.True;
        map[Key("errorname")] = PsObject.Name(Name(name));
        map[Key("command")] = command;
        map[Key("errorinfo")] = PsObject.Null;
        if (!map.TryGetValue(Key("recordstacks"), out var record) || record.Type != PsType.Boolean || record.Bool)
        {
            map[Key("ostack")] = NewArray(Ops.ToArray());
            map[Key("dstack")] = NewArray(DictStack.Select(PsObject.Dictionary).ToArray());
            map[Key("estack")] = NewArray(Array.Empty<PsObject>());
        }
        // the operands stay on the stack, the command does not (as in Ghostscript)
        Stop();
    }

    /// <summary><c>stop</c>: back to the innermost <c>stopped</c> (which pushes true) or the end of the job.</summary>
    public void Stop()
    {
        int i = Exec.FindLastIndex(f => f is StoppedFrame);
        if (i < 0)
        {
            _quit = true;
            return;
        }
        bool job = ((StoppedFrame)Exec[i]).Job;
        Exec.RemoveRange(i, Exec.Count - i);
        if (job) _jobStopped = true;
        else Ops.Push(PsObject.True);
    }

    /// <summary><c>exit</c>: leaves the innermost loop; across a <c>stopped</c> it is an error.</summary>
    public void Exit()
    {
        for (int i = Exec.Count - 1; i >= 0; i--)
        {
            if (Exec[i] is LoopFrame)
            {
                Exec.RemoveRange(i, Exec.Count - i);
                return;
            }
            if (Exec[i] is StoppedFrame) break;
        }
        throw new PsError("invalidexit");
    }

    public void Quit()
    {
        _quit = true;
        Exec.Clear();
    }

    // ------------------------------------------------------------------ dictionaries

    public PsObject Key(string name) => PsObject.Name(Name(name));

    /// <summary>A dictionary key (PLRM 3.3.9): strings become names, integral reals integers.</summary>
    public PsObject NormalizeKey(in PsObject key)
    {
        switch (key.Type)
        {
            case PsType.String:
                if (!key.CanRead) throw new PsError("invalidaccess");
                return PsObject.Name(Name(key.AsString.Text));
            case PsType.Name:
                return key.IsExecutable ? key.AsLiteral() : key;
            case PsType.Real:
                return key.Real == Math.Floor(key.Real) && Math.Abs(key.Real) < 9e18 ? PsObject.Integer((long)key.Real) : key;
            case PsType.Null:
                throw new PsError("typecheck");
            default:
                return key.WithAttributes(PsAttributes.None);
        }
    }

    public PsObject? Lookup(PsName name)
    {
        var key = PsObject.Name(name);
        for (int i = DictStack.Count - 1; i >= 0; i--)
            if (DictStack[i].Map.TryGetValue(key, out var value)) return value;
        return null;
    }

    /// <summary>The topmost dictionary that defines <paramref name="key"/>.</summary>
    public PsDict? Where(in PsObject key)
    {
        var k = NormalizeKey(key);
        for (int i = DictStack.Count - 1; i >= 0; i--)
            if (DictStack[i].Map.ContainsKey(k)) return DictStack[i];
        return null;
    }

    public PsDict CurrentDict => DictStack[^1];

    /// <summary>Puts a value into a dictionary with the checks and the bookkeeping for <c>restore</c>.</summary>
    public void DictPut(PsDict dict, in PsObject key, in PsObject value)
    {
        if (dict.Access != PsDictAccess.Unlimited) throw new PsError("invalidaccess");
        var k = NormalizeKey(key);
        CheckStore(dict, k);
        CheckStore(dict, value);
        BeforeChange(dict);
        if (!dict.Map.ContainsKey(k))
        {
            if (dict.Map.Count >= Limits.MaxArrayLength) throw new PsError("dictfull");
            Allocated(48);
        }
        dict.Map[k] = value;
        if (dict.Map.Count > dict.MaxLength) dict.MaxLength = dict.Map.Count;
    }

    public PsObject DictGet(PsDict dict, in PsObject key)
    {
        if (dict.Access >= PsDictAccess.ExecuteOnly) throw new PsError("invalidaccess");
        return dict.Map.TryGetValue(NormalizeKey(key), out var value) ? value : throw new PsError("undefined");
    }

    /// <summary>Local objects must not be stored into global VM (PLRM 3.7.2).</summary>
    private static void CheckStore(PsComposite target, in PsObject value)
    {
        if (!target.Global) return;
        var composite = Composite(value);
        if (composite != null && !composite.Global) throw new PsError("invalidaccess");
    }

    internal static PsComposite? Composite(in PsObject o) => o.Type switch
    {
        PsType.String => o.AsString.Storage,
        PsType.Array or PsType.PackedArray => o.AsArray.Storage,
        PsType.Dictionary => o.AsDict,
        _ => null,
    };

    public void ArrayPut(in PsObject array, int index, in PsObject value)
    {
        if (!array.CanWrite) throw new PsError("invalidaccess");
        var a = array.AsArray;
        if ((uint)index >= (uint)a.Length) throw new PsError("rangecheck");
        CheckStore(a.Storage, value);
        BeforeChange(a.Storage);
        a.Storage.Items[a.Offset + index] = value;
    }

    // ------------------------------------------------------------------ VM: allocation, save and restore

    public int SaveLevel;
    private readonly List<JournalEntry> _journal = new();
    private readonly List<PsSave> _saves = new();

    private readonly record struct JournalEntry(PsComposite Target, object Snapshot, int PreviousJournaled, int Level);

    private T Track<T>(T composite) where T : PsComposite
    {
        composite.Level = SaveLevel;
        composite.Global = GlobalMode;
        composite.JournaledLevel = SaveLevel;
        return composite;
    }

    public PsObject NewString(byte[] bytes)
    {
        if (bytes.Length > Limits.MaxStringLength) throw new PsError("limitcheck");
        Allocated(bytes.Length + 32);
        return PsObject.String(new PsString(Track(new PsStringStorage(bytes)), 0, bytes.Length));
    }

    public PsObject NewString(int length)
    {
        if (length < 0) throw new PsError("rangecheck");
        if (length > Limits.MaxStringLength) throw new PsError("limitcheck");
        return NewString(new byte[length]);
    }

    public PsObject NewArray(PsObject[] items, bool packed = false)
    {
        if (items.Length > Limits.MaxArrayLength) throw new PsError("limitcheck");
        Allocated(items.Length * 32L + 32);
        return PsObject.Array(new PsArray(Track(new PsArrayStorage(items)), 0, items.Length, packed));
    }

    public PsObject NewArray(int length)
    {
        if (length < 0) throw new PsError("rangecheck");
        if (length > Limits.MaxArrayLength) throw new PsError("limitcheck");
        var items = new PsObject[length];
        Array.Fill(items, PsObject.Null);
        return NewArray(items);
    }

    public PsDict NewDict(int capacity)
    {
        if (capacity < 0) throw new PsError("rangecheck");
        if (capacity > Limits.MaxArrayLength) throw new PsError("limitcheck");
        Allocated(64 + Math.Min(capacity, 1024) * 48L);
        return Track(new PsDict(capacity));
    }

    /// <summary>Records the contents of an older local object before its first change after a <c>save</c>.</summary>
    private void BeforeChange(PsComposite c)
    {
        if (c.Global || SaveLevel == 0 || c.Level >= SaveLevel || c.JournaledLevel >= SaveLevel) return;
        object snapshot = c switch
        {
            PsArrayStorage a => (PsObject[])a.Items.Clone(),
            PsDict d => new DictSnapshot(new Dictionary<PsObject, PsObject>(d.Map, PsKeyComparer.Instance), d.MaxLength, d.Access),
            _ => throw new InvalidOperationException(),
        };
        Allocated(c is PsArrayStorage arr ? arr.Items.Length * 32L : ((PsDict)c).Count * 48L);
        _journal.Add(new JournalEntry(c, snapshot, c.JournaledLevel, SaveLevel));
        c.JournaledLevel = SaveLevel;
    }

    private sealed record DictSnapshot(Dictionary<PsObject, PsObject> Map, int MaxLength, PsDictAccess Access);

    public PsSave Save()
    {
        if (_saves.Count >= 255) throw new PsError("limitcheck");
        SaveLevel++;
        var save = new PsSave(SaveLevel, GlobalMode);
        _saves.Add(save);
        SaveHook?.Invoke(save);
        return save;
    }

    /// <summary>Called on <c>save</c> and <c>restore</c> (the graphics state is saved and restored with VM).</summary>
    public Action<PsSave>? SaveHook, RestoreHook;

    public void Restore(PsSave save)
    {
        if (!save.Valid || save.Level > SaveLevel) throw new PsError("invalidrestore");
        int level = save.Level;
        // objects created after the save must not be referenced from the stacks
        for (int i = 0; i < Ops.Count; i++)
            if (Composite(Ops[i]) is { Global: false } c && c.Level >= level) throw new PsError("invalidrestore");
        foreach (var d in DictStack)
            if (!d.Global && d.Level >= level) throw new PsError("invalidrestore");
        for (int i = _journal.Count - 1; i >= 0 && _journal[i].Level >= level; i--)
        {
            var entry = _journal[i];
            switch (entry.Target)
            {
                case PsArrayStorage a:
                    ((PsObject[])entry.Snapshot).CopyTo(a.Items, 0);
                    break;
                case PsDict d:
                {
                    var snapshot = (DictSnapshot)entry.Snapshot;
                    d.Map.Clear();
                    foreach (var (k, v) in snapshot.Map) d.Map[k] = v;
                    d.MaxLength = snapshot.MaxLength;
                    d.Access = snapshot.Access;
                    break;
                }
            }
            entry.Target.JournaledLevel = entry.PreviousJournaled;
            _journal.RemoveAt(i);
        }
        for (int i = _saves.Count - 1; i >= 0 && _saves[i].Level >= level; i--)
        {
            _saves[i].Valid = false;
            _saves.RemoveAt(i);
        }
        SaveLevel = level - 1;
        GlobalMode = save.Global;
        RestoreHook?.Invoke(save);
    }
}

// ---------------------------------------------------------------------- execution frames

internal abstract class ExecFrame
{
}

/// <summary>A procedure being executed element by element.</summary>
internal sealed class ProcFrame : ExecFrame
{
    public readonly PsArray Proc;
    public int Index;

    public ProcFrame(PsArray proc) => Proc = proc;
}

/// <summary>A file (the program or an executed string) being scanned token by token.</summary>
internal sealed class FileFrame : ExecFrame
{
    public readonly PsFile File;

    public FileFrame(PsFile file) => File = file;
}

/// <summary>The context of <c>stopped</c>, or of the whole job.</summary>
internal sealed class StoppedFrame : ExecFrame
{
    public bool Job;
}

/// <summary>Code to run when the frames above have finished (for operators that call procedures).</summary>
internal sealed class ContinuationFrame : ExecFrame
{
    public readonly Action<PsInterpreter> Continue;

    public ContinuationFrame(Action<PsInterpreter> next) => Continue = next;
}

/// <summary>A loop: each step pushes its operands and the body, or ends the loop.</summary>
internal abstract class LoopFrame : ExecFrame
{
    public abstract bool Next(PsInterpreter ip);
}

/// <summary>The operand stack, which can undo the changes of an operator that fails (PLRM 3.11.1).</summary>
internal sealed class OperandStack
{
    private PsObject[] _items = new PsObject[256];
    private int _count, _guard;
    private readonly int _max;
    private readonly List<PsObject> _popped = new();

    public OperandStack(int max) => _max = max;

    public int Count => _count;

    /// <summary>Element <paramref name="index"/> from the bottom.</summary>
    public PsObject this[int index] => _items[index];

    /// <summary>Marks the start of an operator: pops below this point are recorded to undo them.</summary>
    public void BeginOperator()
    {
        _guard = _count;
        _popped.Clear();
    }

    public void Push(in PsObject o)
    {
        if (_count >= _max) throw new PsError("stackoverflow");
        if (_count == _items.Length) Array.Resize(ref _items, Math.Min(_max, _items.Length * 2));
        _items[_count++] = o;
    }

    public PsObject Pop()
    {
        if (_count == 0) throw new PsError("stackunderflow");
        var o = _items[--_count];
        if (_count < _guard)
        {
            _popped.Add(o);
            _guard = _count;
        }
        return o;
    }

    /// <summary>The object <paramref name="depth"/> places below the top (0 = top).</summary>
    public PsObject Peek(int depth = 0)
    {
        if (depth < 0) throw new PsError("rangecheck");
        if (depth >= _count) throw new PsError("stackunderflow");
        return _items[_count - 1 - depth];
    }

    public void RestoreAfterError()
    {
        if (_count > _guard) _count = _guard;
        for (int i = _popped.Count - 1; i >= 0; i--)
        {
            if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
            _items[_count++] = _popped[i];
        }
        _popped.Clear();
        _guard = _count;
    }

    public void Clear()
    {
        while (_count > 0) Pop();
    }

    public PsObject[] ToArray() => _items.AsSpan(0, _count).ToArray();

    /// <summary>The number of objects above the topmost mark.</summary>
    public int CountToMark()
    {
        for (int i = _count - 1; i >= 0; i--)
            if (_items[i].Type == PsType.Mark) return _count - 1 - i;
        throw new PsError("unmatchedmark");
    }
}
