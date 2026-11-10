namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// Named resources (PLRM 3.9): defineresource, findresource, resourcestatus, undefineresource and resourceforall.
/// Instances live in a global and a local dictionary per category; local definitions take precedence and are undone
/// by restore like any other change of local VM.
/// </summary>
internal sealed partial class PsInterpreter
{
    private static readonly string[] Categories =
    {
        "Category", "Generic", "Font", "CIDFont", "CMap", "FontSet", "Encoding", "Form", "Pattern", "ProcSet", "ColorSpace",
        "Halftone", "ColorRendering", "IdiomSet", "InkParams", "TrapParams", "OutputDevice", "ControlLanguage",
        "Localization", "PDL", "HWOptions", "Filter", "ColorSpaceFamily", "Emulator", "IODevice", "ColorRenderingType",
        "FMapType", "FontType", "FormType", "HalftoneType", "ImageType", "PatternType", "FunctionType", "ShadingType",
        "TrappingType", "CIDSystemInfo",
    };

    private PsDict _globalResources = null!, _localResources = null!;

    private void RegisterResourceOperators()
    {
        bool global = GlobalMode;
        GlobalMode = true;
        _globalResources = NewDict(64);
        foreach (string category in Categories) _globalResources.Map[Key(category)] = PsObject.Dictionary(NewDict(16));
        var encodings = _globalResources.Map[Key("Encoding")].AsDict;
        encodings.Map[Key("StandardEncoding")] = SystemDict.Map[Key("StandardEncoding")];
        encodings.Map[Key("ISOLatin1Encoding")] = SystemDict.Map[Key("ISOLatin1Encoding")];
        var implicitCategories = new Dictionary<string, string[]>
        {
            ["Filter"] = new[] { "ASCIIHexDecode", "ASCII85Decode", "RunLengthDecode", "FlateDecode", "SubFileDecode", "NullEncode" },
            ["ColorSpaceFamily"] = new[] { "DeviceGray", "DeviceRGB", "DeviceCMYK", "CIEBasedA", "CIEBasedABC", "CIEBasedDEF", "CIEBasedDEFG", "Indexed", "Separation", "DeviceN", "Pattern" },
            ["FontType"] = new[] { "0", "1", "3", "42" },
            ["PatternType"] = new[] { "1", "2" },
            ["ImageType"] = new[] { "1" },
            ["FormType"] = new[] { "1" },
            ["HalftoneType"] = new[] { "1", "2", "3", "4", "5" },
        };
        foreach (var (category, keys) in implicitCategories)
        {
            var dict = _globalResources.Map[Key(category)].AsDict;
            foreach (string k in keys)
            {
                var key = int.TryParse(k, out int n) ? PsObject.Integer(n) : Key(k);
                dict.Map[key] = PsObject.True;
            }
        }
        GlobalMode = false;
        _localResources = NewDict(64);
        GlobalMode = global;

        Op("defineresource", ip =>
        {
            var category = ip.PopCategory();
            var instance = ip.Pop();
            var key = ip.NormalizeKey(ip.Pop());
            // a local instance cannot be defined in global VM
            if (ip.GlobalMode && Composite(instance) is { Global: false }) throw new PsError("invalidaccess");
            if (category == "Category" && instance.Type != PsType.Dictionary) throw new PsError("typecheck");
            if (category is "Font" or "CIDFont")
            {
                // fonts are defined as by definefont (FID, the font behind the dictionary)
                if (instance.Type != PsType.Dictionary) throw new PsError("typecheck");
                ip.Push(ip.DefineFont(key, instance.AsDict, category));
                return;
            }
            ip.DictPut(ip.CategoryDict(category, ip.GlobalMode, create: true)!, key, instance);
            if (category == "Category" && key.Type == PsType.Name) ip.CategoryDict(key.AsName.Text, global: true, create: true);
            ip.Push(instance);
        });
        Op("findresource", ip =>
        {
            var category = ip.PopCategory();
            var key = ip.Pop();
            ip.Push(ip.FindResource(category, key) ?? throw new PsError("undefinedresource"));
        });
        Op("findencoding", ip => ip.Push(ip.FindResource("Encoding", ip.Pop()) ?? throw new PsError("undefinedresource")));
        Op("resourcestatus", ip =>
        {
            var category = ip.PopCategory();
            var key = ip.NormalizeKey(ip.Pop());
            if (ip.CategoryDict(category, false, false)?.Map.ContainsKey(key) == true) ip.Push(1);
            else if (ip.CategoryDict(category, true, false)?.Map.ContainsKey(key) == true) ip.Push(2);
            else
            {
                ip.Push(false);
                return;
            }
            ip.Push(-1);
            ip.Push(true);
        });
        Op("undefineresource", ip =>
        {
            var category = ip.PopCategory();
            var key = ip.NormalizeKey(ip.Pop());
            var dict = ip.CategoryDict(category, ip.GlobalMode, false);
            if (dict == null || !dict.Map.ContainsKey(key)) return;
            ip.BeforeChange(dict);
            dict.Map.Remove(key);
        });
        Op("resourceforall", ip =>
        {
            var category = ip.PopCategory();
            var scratch = ip.PopString(write: true);
            var proc = ip.PopProc();
            var template = ip.PopString().AsString.Text;
            var keys = new List<string>();
            foreach (bool g in new[] { false, true })
            {
                var dict = ip.CategoryDict(category, g, false);
                if (dict == null) continue;
                foreach (var k in dict.Map.Keys)
                {
                    string text = k.Type == PsType.Name ? k.AsName.Text : k.Type == PsType.String ? k.AsString.Text
                        : k.Type == PsType.Integer ? k.Int.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                    if (!keys.Contains(text) && Matches(template, text)) keys.Add(text);
                }
            }
            ip.PushFrame(new ResourceForall(keys, scratch, proc));
        });
    }

    private string PopCategory()
    {
        var o = Pop();
        if (o.Type != PsType.Name) throw new PsError("typecheck");
        string category = o.AsName.Text;
        if (CategoryDict(category, true, false) == null && CategoryDict(category, false, false) == null) throw new PsError("undefined");
        return category;
    }

    /// <summary>The dictionary of a category's instances in global or local VM.</summary>
    private PsDict? CategoryDict(string category, bool global, bool create)
    {
        var root = global ? _globalResources : _localResources;
        if (root.Map.TryGetValue(Key(category), out var d)) return d.AsDict;
        if (!create) return null;
        bool mode = GlobalMode;
        GlobalMode = global;
        var dict = NewDict(16);
        GlobalMode = mode;
        DictPut(root, Key(category), PsObject.Dictionary(dict));
        return dict;
    }

    /// <summary>A resource instance (local first); null if not defined.</summary>
    public PsObject? FindResource(string category, in PsObject key)
    {
        var k = NormalizeKey(key);
        if (CategoryDict(category, false, false) is { } local && local.Map.TryGetValue(k, out var v)) return v;
        if (CategoryDict(category, true, false) is { } global && global.Map.TryGetValue(k, out v)) return v;
        return FindResourceFallback?.Invoke(category, k);
    }

    /// <summary>Resources that are not defined but can be supplied (fonts from stage 3 on).</summary>
    public Func<string, PsObject, PsObject?>? FindResourceFallback;

    /// <summary>Template matching of resourceforall: * any sequence, ? any character, \ escapes.</summary>
    private static bool Matches(string template, string text)
    {
        bool Match(int t, int s)
        {
            while (t < template.Length)
            {
                char c = template[t];
                if (c == '*')
                {
                    for (int i = s; i <= text.Length; i++)
                        if (Match(t + 1, i)) return true;
                    return false;
                }
                if (s >= text.Length) return false;
                if (c == '\\' && t + 1 < template.Length) c = template[++t];
                else if (c == '?')
                {
                    t++;
                    s++;
                    continue;
                }
                if (c != text[s]) return false;
                t++;
                s++;
            }
            return s == text.Length;
        }
        return Match(0, 0);
    }

    private sealed class ResourceForall(List<string> keys, PsObject scratch, PsObject proc) : LoopFrame
    {
        private int _index;

        public override bool Next(PsInterpreter ip)
        {
            if (_index >= keys.Count) return false;
            var bytes = PsText.Latin1.GetBytes(keys[_index++]);
            ip.Push(ip.CopyInto(scratch, bytes));
            ip.PushProcedure(proc);
            return true;
        }
    }
}
