namespace MiPdf.PostScript.Interpreter;

/// <summary>Control and VM operators (PLRM 8.1).</summary>
internal sealed partial class PsInterpreter
{
    private void RegisterControlOperators()
    {
        Op("exec", ip => ip.Execute(ip.Pop()));
        Op("if", ip =>
        {
            var proc = ip.PopProc();
            if (ip.PopBool()) ip.PushProcedure(proc);
        });
        Op("ifelse", ip =>
        {
            var no = ip.PopProc();
            var yes = ip.PopProc();
            ip.PushProcedure(ip.PopBool() ? yes : no);
        });
        Op("for", ip =>
        {
            var proc = ip.PopProc();
            var limit = ip.PopNumber();
            var increment = ip.PopNumber();
            var initial = ip.PopNumber();
            if (initial.Type == PsType.Integer && increment.Type == PsType.Integer && limit.Type == PsType.Integer)
                ip.PushFrame(new IntegerFor(initial.Int, increment.Int, limit.Int, proc));
            else
                ip.PushFrame(new RealFor((float)initial.Number, (float)increment.Number, (float)limit.Number, proc));
        });
        Op("repeat", ip =>
        {
            var proc = ip.PopProc();
            long n = ip.PopInt();
            if (n < 0) throw new PsError("rangecheck");
            ip.PushFrame(new Repeat(n, proc));
        });
        Op("loop", ip => ip.PushFrame(new Forever(ip.PopProc())));
        Op("exit", ip => ip.Exit());
        Op("stop", ip => ip.Stop());
        Op("stopped", ip =>
        {
            var o = ip.Pop();
            ip.PushFrame(new StoppedFrame());
            ip.Execute(o);
        });
        Op("countexecstack", ip => ip.Push(ip.Exec.Count));
        Op("execstack", ip =>
        {
            var a = ip.PopArray(write: true);
            // the procedures being executed (other frames are internal)
            var items = ip.Exec.Select(f => f switch
            {
                ProcFrame p => PsObject.Array(p.Proc, PsAttributes.Executable),
                FileFrame file => PsObject.File(file.File, PsAttributes.Executable),
                _ => PsObject.Null,
            }).ToArray();
            if (a.AsArray.Length < items.Length) throw new PsError("rangecheck");
            for (int i = 0; i < items.Length; i++) ip.ArrayPut(a, i, items[i]);
            ip.Push(PsObject.Array(a.AsArray.Interval(0, items.Length), a.Attributes));
        });
        Op("quit", ip => ip.Quit());
    }

    private void RegisterVmOperators()
    {
        Op("save", ip => ip.Push(PsObject.Save(ip.Save())));
        Op("restore", ip =>
        {
            var o = ip.Pop();
            if (o.Type != PsType.Save) throw new PsError("typecheck");
            ip.Restore((PsSave)o.Ref!);
        });
        Op("setglobal", ip => ip.GlobalMode = ip.PopBool());
        Op("currentglobal", ip => ip.Push(ip.GlobalMode));
        Op("gcheck", ip => ip.Push(Composite(ip.Pop()) is not { Global: false }));
        Op("vmstatus", ip =>
        {
            ip.Push(ip.SaveLevel);
            ip.Push(Math.Min(int.MaxValue, GC.GetTotalMemory(false) - ip._memoryBaseline));
            ip.Push(Math.Min(int.MaxValue, ip.Limits.MaxMemory));
        });
        Op("vmreclaim", ip => ip.PopInt());
        Op("setvmthreshold", ip => ip.PopInt());
    }

    private sealed class IntegerFor(long current, long increment, long limit, PsObject proc) : LoopFrame
    {
        private bool _last;

        public override bool Next(PsInterpreter ip)
        {
            if (_last || (increment >= 0 ? current > limit : current < limit)) return false;
            ip.Push(current);
            // the next value would leave the integer range: this step is the last one
            if (increment > 0 ? current > long.MaxValue - increment : current < long.MinValue - increment) _last = true;
            else current += increment;
            ip.PushProcedure(proc);
            return true;
        }
    }

    private sealed class RealFor(float current, float increment, float limit, PsObject proc) : LoopFrame
    {
        public override bool Next(PsInterpreter ip)
        {
            if (increment >= 0 ? current > limit : current < limit) return false;
            ip.Push(PsObject.RealValue(current));
            current += increment;
            ip.PushProcedure(proc);
            return true;
        }
    }

    private sealed class Repeat(long remaining, PsObject proc) : LoopFrame
    {
        public override bool Next(PsInterpreter ip)
        {
            if (remaining <= 0) return false;
            remaining--;
            ip.PushProcedure(proc);
            return true;
        }
    }

    private sealed class Forever(PsObject proc) : LoopFrame
    {
        public override bool Next(PsInterpreter ip)
        {
            ip.PushProcedure(proc);
            return true;
        }
    }
}
