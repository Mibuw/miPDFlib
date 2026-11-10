namespace MiPdf.PostScript.Interpreter;

/// <summary>Array, packed array, string and dictionary operators (PLRM 8.1).</summary>
internal sealed partial class PsInterpreter
{
    private void RegisterCompositeOperators()
    {
        Op("array", ip => ip.Push(ip.NewArray(ip.PopCount())));
        Op("packedarray", ip =>
        {
            int n = ip.PopCount();
            if (n > ip.Ops.Count) throw new PsError("stackunderflow");
            var items = new PsObject[n];
            for (int i = n - 1; i >= 0; i--) items[i] = ip.Pop();
            ip.Push(ip.NewArray(items, packed: true));
        });
        Op("setpacking", ip => ip.Packing = ip.PopBool());
        Op("currentpacking", ip => ip.Push(ip.Packing));
        Op("string", ip => ip.Push(ip.NewString(ip.PopCount())));
        Op("dict", ip => ip.Push(PsObject.Dictionary(ip.NewDict(ip.PopCount()))));
        Op("length", ip =>
        {
            var o = ip.Pop();
            ip.Push(o.Type switch
            {
                PsType.Array or PsType.PackedArray => ip.Readable(o).AsArray.Length,
                PsType.String => ip.Readable(o).AsString.Length,
                PsType.Dictionary => ip.Readable(o).AsDict.Count,
                PsType.Name => o.AsName.Bytes.Length,
                _ => throw new PsError("typecheck"),
            });
        });
        Op("maxlength", ip => ip.Push(ip.PopDict().MaxLength));
        Op("get", ip =>
        {
            var key = ip.Pop();
            var o = ip.Pop();
            switch (o.Type)
            {
                case PsType.Array:
                case PsType.PackedArray:
                {
                    int i = Index(key, ip.Readable(o).AsArray.Length);
                    ip.Push(o.AsArray[i]);
                    break;
                }
                case PsType.String:
                {
                    int i = Index(key, ip.Readable(o).AsString.Length);
                    ip.Push(o.AsString[i]);
                    break;
                }
                case PsType.Dictionary:
                    ip.Push(ip.DictGet(o.AsDict, key));
                    break;
                default:
                    throw new PsError("typecheck");
            }
        });
        Op("put", ip =>
        {
            var value = ip.Pop();
            var key = ip.Pop();
            var o = ip.Pop();
            switch (o.Type)
            {
                case PsType.Array:
                    ip.ArrayPut(o, Index(key, o.AsArray.Length), value);
                    break;
                case PsType.PackedArray:
                    throw new PsError("invalidaccess");
                case PsType.String:
                {
                    if (!o.CanWrite) throw new PsError("invalidaccess");
                    int i = Index(key, o.AsString.Length);
                    if (value.Type != PsType.Integer) throw new PsError("typecheck");
                    if (value.Int < 0 || value.Int > 255) throw new PsError("rangecheck");
                    o.AsString[i] = (byte)value.Int;
                    break;
                }
                case PsType.Dictionary:
                    ip.DictPut(o.AsDict, key, value);
                    break;
                default:
                    throw new PsError("typecheck");
            }
        });
        Op("getinterval", ip =>
        {
            int count = ip.PopCount();
            int index = ip.PopCount();
            var o = ip.Pop();
            int length = o.Type switch
            {
                PsType.Array or PsType.PackedArray => ip.Readable(o).AsArray.Length,
                PsType.String => ip.Readable(o).AsString.Length,
                _ => throw new PsError("typecheck"),
            };
            if ((long)index + count > length) throw new PsError("rangecheck");
            ip.Push(o.Type == PsType.String
                ? PsObject.String(o.AsString.Interval(index, count), o.Attributes)
                : PsObject.Array(o.AsArray.Interval(index, count), o.Attributes));
        });
        Op("putinterval", ip =>
        {
            var source = ip.Pop();
            int index = ip.PopCount();
            var target = ip.Pop();
            if (target.Type == PsType.String && source.Type == PsType.String)
            {
                if (!target.CanWrite || !source.CanRead) throw new PsError("invalidaccess");
                if ((long)index + source.AsString.Length > target.AsString.Length) throw new PsError("rangecheck");
                source.AsString.Span.CopyTo(target.AsString.Span[index..]);
            }
            else if (target.Type == PsType.Array && source.IsArray)
            {
                if (!target.CanWrite || !source.CanRead) throw new PsError("invalidaccess");
                if ((long)index + source.AsArray.Length > target.AsArray.Length) throw new PsError("rangecheck");
                var items = source.AsArray.Span.ToArray(); // the intervals may overlap
                for (int i = 0; i < items.Length; i++) ip.ArrayPut(target, index + i, items[i]);
            }
            else throw new PsError(target.Type == PsType.PackedArray ? "invalidaccess" : "typecheck");
        });
        Op("copy", ip =>
        {
            var top = ip.Ops.Peek();
            if (top.Type == PsType.Integer)
            {
                ip.Pop();
                if (top.Int < 0) throw new PsError("rangecheck");
                if (top.Int > ip.Ops.Count) throw new PsError("stackunderflow");
                int n = (int)top.Int;
                for (int i = 0; i < n; i++) ip.Push(ip.Ops.Peek(n - 1));
                return;
            }
            var target = ip.Pop();
            var source = ip.Pop();
            if (source.Type == PsType.String && target.Type == PsType.String)
            {
                if (!target.CanWrite || !source.CanRead) throw new PsError("invalidaccess");
                if (source.AsString.Length > target.AsString.Length) throw new PsError("rangecheck");
                source.AsString.Span.CopyTo(target.AsString.Span);
                ip.Push(PsObject.String(target.AsString.Interval(0, source.AsString.Length), target.Attributes));
            }
            else if (source.IsArray && target.Type == PsType.Array)
            {
                if (!target.CanWrite || !source.CanRead) throw new PsError("invalidaccess");
                if (source.AsArray.Length > target.AsArray.Length) throw new PsError("rangecheck");
                var items = source.AsArray.Span.ToArray();
                for (int i = 0; i < items.Length; i++) ip.ArrayPut(target, i, items[i]);
                ip.Push(PsObject.Array(target.AsArray.Interval(0, items.Length), target.Attributes));
            }
            else if (source.Type == PsType.Dictionary && target.Type == PsType.Dictionary)
            {
                if (source.AsDict.Access >= PsDictAccess.ExecuteOnly) throw new PsError("invalidaccess");
                foreach (var (k, v) in source.AsDict.Map.ToArray()) ip.DictPut(target.AsDict, k, v);
                ip.Push(target);
            }
            else throw new PsError("typecheck");
        });
        Op("aload", ip =>
        {
            var a = ip.PopArray();
            foreach (var item in a.AsArray.Span) ip.Push(item);
            ip.Push(a);
        });
        Op("astore", ip =>
        {
            var a = ip.PopArray(write: true);
            int n = a.AsArray.Length;
            if (n > ip.Ops.Count) throw new PsError("stackunderflow");
            for (int i = n - 1; i >= 0; i--) ip.ArrayPut(a, i, ip.Pop());
            ip.Push(a);
        });
        Op("forall", ip =>
        {
            var proc = ip.PopProc();
            var o = ip.Pop();
            ip.PushFrame(o.Type switch
            {
                PsType.Array or PsType.PackedArray => new ArrayForall(ip.Readable(o).AsArray, proc),
                PsType.String => new StringForall(ip.Readable(o).AsString, proc),
                PsType.Dictionary => new DictForall(ip.Readable(o).AsDict.Map.ToArray(), proc),
                _ => throw new PsError("typecheck"),
            });
        });
        Op("anchorsearch", ip =>
        {
            var seek = ip.PopString();
            var s = ip.PopString();
            var str = s.AsString;
            int n = seek.AsString.Length;
            if (str.Span.StartsWith(seek.AsString.Span))
            {
                ip.Push(PsObject.String(str.Interval(n, str.Length - n), s.Attributes));
                ip.Push(PsObject.String(str.Interval(0, n), s.Attributes));
                ip.Push(true);
            }
            else
            {
                ip.Push(s);
                ip.Push(false);
            }
        });
        Op("search", ip =>
        {
            var seek = ip.PopString();
            var s = ip.PopString();
            var str = s.AsString;
            int n = seek.AsString.Length;
            int at = str.Span.IndexOf(seek.AsString.Span);
            if (at >= 0)
            {
                ip.Push(PsObject.String(str.Interval(at + n, str.Length - at - n), s.Attributes));
                ip.Push(PsObject.String(str.Interval(at, n), s.Attributes));
                ip.Push(PsObject.String(str.Interval(0, at), s.Attributes));
                ip.Push(true);
            }
            else
            {
                ip.Push(s);
                ip.Push(false);
            }
        });
        Op("token", ip =>
        {
            var o = ip.Pop();
            if (o.Type == PsType.File)
            {
                if (!o.AsFile.CanRead) throw new PsError("invalidaccess");
                if (ip._scanner.Next(o.AsFile, out var t))
                {
                    ip.Push(t);
                    ip.Push(true);
                }
                else ip.Push(false);
                return;
            }
            if (o.Type != PsType.String) throw new PsError("typecheck");
            if (!o.CanRead) throw new PsError("invalidaccess");
            var str = o.AsString;
            var file = new PsMemoryFile(str.ToArray());
            if (!ip._scanner.Next(file, out var token))
            {
                ip.Push(false);
                return;
            }
            int used = (int)file.Position - (file.HasPushback ? 1 : 0);
            ip.Push(PsObject.String(str.Interval(used, str.Length - used), o.Attributes));
            ip.Push(token);
            ip.Push(true);
        });

        // dictionary stack
        Op("begin", ip =>
        {
            var d = ip.PopDict();
            if (ip.DictStack.Count >= ip.Limits.MaxDictStack) throw new PsError("dictstackoverflow");
            ip.DictStack.Add(d);
        });
        Op("end", ip =>
        {
            if (ip.DictStack.Count <= 3) throw new PsError("dictstackunderflow");
            ip.DictStack.RemoveAt(ip.DictStack.Count - 1);
        });
        Op("def", ip =>
        {
            var value = ip.Pop();
            var key = ip.Pop();
            ip.DictPut(ip.CurrentDict, key, value);
        });
        Op("load", ip =>
        {
            var key = ip.Pop();
            var d = ip.Where(key) ?? throw new PsError("undefined");
            ip.Push(ip.DictGet(d, key));
        });
        Op("store", ip =>
        {
            var value = ip.Pop();
            var key = ip.Pop();
            ip.DictPut(ip.Where(key) ?? ip.CurrentDict, key, value);
        });
        Op("undef", ip =>
        {
            var key = ip.Pop();
            var d = ip.PopDict(write: true);
            var k = ip.NormalizeKey(key);
            if (!d.Map.ContainsKey(k)) return;
            ip.BeforeChange(d);
            d.Map.Remove(k);
        });
        Op("known", ip =>
        {
            var key = ip.Pop();
            var d = ip.PopDict();
            ip.Push(d.Map.ContainsKey(ip.NormalizeKey(key)));
        });
        Op("where", ip =>
        {
            var key = ip.Pop();
            var d = ip.Where(key);
            if (d != null) ip.Push(PsObject.Dictionary(d));
            ip.Push(d != null);
        });
        Op("currentdict", ip => ip.Push(PsObject.Dictionary(ip.CurrentDict)));
        Op("countdictstack", ip => ip.Push(ip.DictStack.Count));
        Op("dictstack", ip =>
        {
            var a = ip.PopArray(write: true);
            if (a.AsArray.Length < ip.DictStack.Count) throw new PsError("rangecheck");
            for (int i = 0; i < ip.DictStack.Count; i++) ip.ArrayPut(a, i, PsObject.Dictionary(ip.DictStack[i]));
            ip.Push(PsObject.Array(a.AsArray.Interval(0, ip.DictStack.Count), a.Attributes));
        });
        Op("cleardictstack", ip => ip.DictStack.RemoveRange(3, ip.DictStack.Count - 3));
    }

    /// <summary>The object if it is readable.</summary>
    private PsObject Readable(in PsObject o) => o.CanRead ? o : throw new PsError("invalidaccess");

    private static int Index(in PsObject key, int length)
    {
        if (key.Type != PsType.Integer) throw new PsError("typecheck");
        if (key.Int < 0 || key.Int >= length) throw new PsError("rangecheck");
        return (int)key.Int;
    }

    private sealed class ArrayForall(PsArray array, PsObject proc) : LoopFrame
    {
        private int _index;

        public override bool Next(PsInterpreter ip)
        {
            if (_index >= array.Length) return false;
            ip.Push(array[_index++]);
            ip.PushProcedure(proc);
            return true;
        }
    }

    private sealed class StringForall(PsString str, PsObject proc) : LoopFrame
    {
        private int _index;

        public override bool Next(PsInterpreter ip)
        {
            if (_index >= str.Length) return false;
            ip.Push(str[_index++]);
            ip.PushProcedure(proc);
            return true;
        }
    }

    private sealed class DictForall(KeyValuePair<PsObject, PsObject>[] entries, PsObject proc) : LoopFrame
    {
        private int _index;

        public override bool Next(PsInterpreter ip)
        {
            if (_index >= entries.Length) return false;
            var (key, value) = entries[_index++];
            ip.Push(key);
            ip.Push(value);
            ip.PushProcedure(proc);
            return true;
        }
    }
}
