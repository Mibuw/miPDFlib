using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// Page device operators (PLRM 6.1): the page size and the BeginPage, EndPage and Install procedures; the other
/// parameters are kept but have no effect.
/// </summary>
internal sealed partial class PsInterpreter
{
    private PsDict? _pageDevice;
    private PsObject _defaultBeginPage, _defaultEndPage, _defaultInstall;

    /// <summary>The number of pages shown (showpage and copypage) as passed to BeginPage and EndPage.</summary>
    private int _pageCount;

    /// <summary>The page sizes of the LanguageLevel 1 page size operators (in points).</summary>
    private static readonly Dictionary<string, (double Width, double Height)> PageSizes = new()
    {
        ["letter"] = (612, 792), ["lettersmall"] = (612, 792), ["legal"] = (612, 1008), ["ledger"] = (1224, 792),
        ["11x17"] = (792, 1224), ["a3"] = (842, 1191), ["a4"] = (595, 842), ["a4small"] = (595, 842), ["a5"] = (420, 595),
        ["b5"] = (516, 729), ["executivepage"] = (540, 720),
    };

    /// <summary>
    /// The program is an encapsulated graphic (EPS import): as the EPSF specification asks of an importing application,
    /// showpage and copypage do nothing, and the page level operators it forbids (erasepage, setpagedevice and the page
    /// size operators) do not erase what was painted, reset the graphics state or change the size.
    /// </summary>
    public bool Encapsulated { get; init; }

    private void RegisterDeviceOperators()
    {
        Op("setpagedevice", ip =>
        {
            var request = ip.PopDict();
            var current = ip.PageDeviceDict();
            if (ip.Encapsulated)
            {
                // the parameters are kept (currentpagedevice returns them), but there is no new page
                foreach (var (key, value) in request.Map)
                    if (key.Type != PsType.Name || key.AsName.Text != "PageSize") current.Map[key] = value;
                return;
            }
            // the current page ends as at the end of the device (EndPage with reason 2, normally not shown)
            if (ip.EndPage(2)) ip.Device?.ShowPage();
            foreach (var (key, value) in request.Map) current.Map[key] = value;
            if (request.Map.TryGetValue(ip.Key("PageSize"), out var size) && size.IsArray && size.AsArray.Length >= 2
                && size.AsArray[0].IsNumber && size.AsArray[1].IsNumber)
            {
                double w = size.AsArray[0].Number, h = size.AsArray[1].Number;
                if (w <= 0 || h <= 0 || w > 200_000 || h > 200_000) throw new PsError("rangecheck");
                ip.Device?.SetPageSize(w, h);
            }
            // setpagedevice starts a new page with a fresh graphics state (the page count goes on, as in Ghostscript)
            ip.Device?.ErasePage();
            ip.InitGraphics(ip.Device);
            if (ip.PageProcedure("Install", ip._defaultInstall) is { } install) ip.PushAll(ip.CallProcedure(install, ReadOnlySpan<PsObject>.Empty));
            ip.BeginPage();
        });
        Op(".endjobpage", ip =>
        {
            if (ip.EndPage(2)) ip.Device?.ShowPage();
        });
        Op("currentpagedevice", ip =>
        {
            var copy = ip.NewDict(ip.PageDeviceDict().Count);
            foreach (var (key, value) in ip.PageDeviceDict().Map) copy.Map[key] = value;
            ip.Push(PsObject.Dictionary(copy));
        });
        foreach (var (name, (w, h)) in PageSizes)
        {
            double width = w, height = h;
            UserDict.Map[Key(name)] = PsObject.Operator(new PsOperator(Name(name), ip =>
            {
                if (ip.Encapsulated) return;
                ip.Device?.SetPageSize(width, height);
                ip.Device?.ErasePage();
                ip.InitGraphics(ip.Device);
            }));
        }
    }

    private PsDict PageDeviceDict()
    {
        if (_pageDevice == null)
        {
            bool global = GlobalMode;
            GlobalMode = false;
            _pageDevice = NewDict(16);
            GlobalMode = global;
            _pageDevice.Map[Key("HWResolution")] = NewArray(new[] { PsObject.RealValue(720), PsObject.RealValue(720) });
            _pageDevice.Map[Key("Orientation")] = PsObject.Integer(0);
            _pageDevice.Map[Key("NumCopies")] = PsObject.Null;
            _pageDevice.Map[Key("Policies")] = PsObject.Dictionary(NewDict(0));
            _defaultBeginPage = Procedure("{pop}");
            _defaultEndPage = Procedure("{exch pop 2 ne}");
            _defaultInstall = Procedure("{}");
            _pageDevice.Map[Key("BeginPage")] = _defaultBeginPage;
            _pageDevice.Map[Key("EndPage")] = _defaultEndPage;
            _pageDevice.Map[Key("Install")] = _defaultInstall;
        }
        double w = Device?.Width ?? 612, h = Device?.Height ?? 792;
        _pageDevice.Map[Key("PageSize")] = NewArray(new[] { PsObject.RealValue((float)w), PsObject.RealValue((float)h) });
        return _pageDevice;
    }

    private PsObject Procedure(string source)
    {
        _scanner.Next(new PsMemoryFile(System.Text.Encoding.ASCII.GetBytes(source)), out var proc);
        return proc;
    }

    /// <summary>A page device procedure set by the program (null for the default, which needs no call).</summary>
    private PsObject? PageProcedure(string key, PsObject defaultProc)
    {
        if (_pageDevice == null || !_pageDevice.Map.TryGetValue(Key(key), out var proc) || !proc.IsProcedure) return null;
        return defaultProc.IsArray && proc.AsArray.SameAs(defaultProc.AsArray) ? null : proc;
    }

    private void PushAll(PsObject[] values)
    {
        foreach (var v in values) Push(v);
    }

    /// <summary>EndPage with the page count and the reason (0 showpage, 1 copypage, 2 end of the device): true to show the page.</summary>
    private bool EndPage(int reason)
    {
        if (PageProcedure("EndPage", _defaultEndPage) is not { } proc) return reason != 2;
        var results = CallProcedure(proc, new[] { PsObject.Integer(_pageCount), PsObject.Integer(reason) });
        if (results.Length == 0 || results[^1].Type != PsType.Boolean) throw new PsError("typecheck");
        PushAll(results[..^1]);
        return results[^1].Bool;
    }

    private void BeginPage()
    {
        if (PageProcedure("BeginPage", _defaultBeginPage) is { } proc) PushAll(CallProcedure(proc, new[] { PsObject.Integer(_pageCount) }));
    }

    /// <summary>
    /// showpage (and copypage, which keeps the graphics state): EndPage, the page, BeginPage. A page that EndPage does
    /// not let through is not erased either: the next page is painted over it (N-up printing works this way).
    /// </summary>
    private void ShowPage(bool erase)
    {
        if (EndPage(0)) Device?.ShowPage();
        if (erase) InitGraphics(Device);
        _pageCount++;
        BeginPage();
    }

    /// <summary>The end of the job: a program's EndPage procedure is called with reason 2 and may show the page.</summary>
    public void EndJob()
    {
        if (PageProcedure("EndPage", _defaultEndPage) != null) Run(System.Text.Encoding.ASCII.GetBytes(".endjobpage"));
    }
}
