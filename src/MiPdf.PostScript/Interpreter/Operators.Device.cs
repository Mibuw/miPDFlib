using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>Page device operators (PLRM 6.1): the page size; the other parameters are kept but have no effect.</summary>
internal sealed partial class PsInterpreter
{
    private PsDict? _pageDevice;

    /// <summary>The page sizes of the LanguageLevel 1 page size operators (in points).</summary>
    private static readonly Dictionary<string, (double Width, double Height)> PageSizes = new()
    {
        ["letter"] = (612, 792), ["lettersmall"] = (612, 792), ["legal"] = (612, 1008), ["ledger"] = (1224, 792),
        ["11x17"] = (792, 1224), ["a3"] = (842, 1191), ["a4"] = (595, 842), ["a4small"] = (595, 842), ["a5"] = (420, 595),
        ["b5"] = (516, 729), ["executivepage"] = (540, 720),
    };

    private void RegisterDeviceOperators()
    {
        Op("setpagedevice", ip =>
        {
            var request = ip.PopDict();
            var current = ip.PageDeviceDict();
            foreach (var (key, value) in request.Map) current.Map[key] = value;
            if (request.Map.TryGetValue(ip.Key("PageSize"), out var size) && size.IsArray && size.AsArray.Length >= 2
                && size.AsArray[0].IsNumber && size.AsArray[1].IsNumber)
            {
                double w = size.AsArray[0].Number, h = size.AsArray[1].Number;
                if (w <= 0 || h <= 0 || w > 200_000 || h > 200_000) throw new PsError("rangecheck");
                ip.Device?.SetPageSize(w, h);
            }
            // setpagedevice starts a new page with a fresh graphics state
            ip.Device?.ErasePage();
            ip.InitGraphics(ip.Device);
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
        }
        double w = Device?.Width ?? 612, h = Device?.Height ?? 792;
        _pageDevice.Map[Key("PageSize")] = NewArray(new[] { PsObject.RealValue((float)w), PsObject.RealValue((float)h) });
        return _pageDevice;
    }
}
