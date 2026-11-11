using MiPdf.Graphics;
using MiPdf.Objects;
using MiPdf.PostScript.Fonts;
using MiPdf.PostScript.Graphics;

namespace MiPdf.PostScript.Interpreter;

/// <summary>
/// <c>pdfmark</c> (Adobe pdfmark Reference, as Ghostscript's pdfwrite implements it): document information (DOCINFO),
/// the catalog (DOCVIEW), bookmarks (OUT), annotations and links (ANN, LNK), named destinations (DEST), page entries
/// (PAGE, PAGES), article threads (ARTICLE), embedded files (EMBED), named objects (OBJ, PUT, PUTINTERVAL, APPEND,
/// CLOSE, with {Catalog}, {DocInfo}, {ThisPage}, {PrevPage}, {NextPage} and {PageN}) and named pictures (BP, EP, SP).
/// Rectangles are in the current user space, views in the default user space of their page. Unknown types are
/// ignored, as by Distiller.
/// </summary>
internal static class PdfMarkPairs
{
    /// <summary>The pairs by key; a key given twice takes the last value.</summary>
    public static Dictionary<string, PsObject> ToEntries(this List<(string Key, PsObject Value)> pairs)
    {
        var entries = new Dictionary<string, PsObject>();
        foreach (var (key, value) in pairs) entries[key] = value;
        return entries;
    }
}

internal sealed partial class PsInterpreter
{
    /// <summary>The page device of the converter: pdfmark writes into its document (other devices ignore pdfmark).</summary>
    public PsPageDevice? PageDevice { get; set; }

    private readonly List<(PsDevice? Device, PsContentWriter? Writer, PdfStream? Stream)> _pictures = new();
    private readonly Dictionary<string, PdfReference> _pictureNames = new();

    private PsDict? _distillerParams;

    private void RegisterPdfMarkOperators()
    {
        Op("pdfmark", ip => ip.PdfMark());
        // Distiller parameters: kept (programs test for these operators), they do not change the output
        Op("setdistillerparams", ip =>
        {
            var request = ip.PopDict();
            var current = ip.DistillerParams();
            foreach (var (key, value) in request.Map) current.Map[key] = value;
        });
        Op("currentdistillerparams", ip =>
        {
            var copy = ip.NewDict(ip.DistillerParams().Count);
            foreach (var (key, value) in ip.DistillerParams().Map) copy.Map[key] = value;
            ip.Push(PsObject.Dictionary(copy));
        });
    }

    private PsDict DistillerParams() => _distillerParams ??= NewDict(16);

    private void PdfMark()
    {
        int n = Ops.CountToMark();
        if (n == 0) throw new PsError("stackunderflow");
        var items = new PsObject[n];
        for (int i = n - 1; i >= 0; i--) items[i] = Ops.Peek(n - 1 - i);
        var type = items[^1];
        if (type.Type != PsType.Name) throw new PsError("typecheck");
        var args = items[..^1];
        string kind = type.AsName.Text;
        switch (kind)
        {
            case "BP":
                BeginPicture(Pairs(args));
                break;
            case "EP":
                EndPicture();
                break;
            case "SP":
                ShowPicture(args);
                break;
            default:
                if (PageDevice is { } device && Device is not (PsNullDevice or PsPathDevice)) DocumentMark(kind, args, device);
                break;
        }
        for (int i = 0; i <= n; i++) Pop();
    }

    private void DocumentMark(string kind, PsObject[] args, PsPageDevice device)
    {
        var marks = device.Marks;
        var document = device.Document!;
        switch (kind)
        {
            case "DOCINFO":
            {
                var info = marks.Info();
                foreach (var (key, value) in Pairs(args)) info[PdfName.Get(key)] = MarkValue(value, device);
                break;
            }
            case "DOCVIEW":
            {
                var entries = Pairs(args);
                var catalog = document.Catalog;
                var dictionary = entries.ToEntries();
                if (MarkAction(dictionary, device) is { } action) catalog[PdfName.Get("OpenAction")] = action;
                else if (dictionary.ContainsKey("Page") || dictionary.ContainsKey("View"))
                {
                    if (Destination(dictionary, device) is { } destination) catalog[PdfName.Get("OpenAction")] = destination;
                }
                else if (dictionary.TryGetValue("Dest", out var dest))
                    catalog[PdfName.Get("OpenAction")] = new PdfDictionary { [PdfName.Get("S")] = PdfName.Get("GoTo"), [PdfName.Get("D")] = DestName(dest) };
                foreach (var (key, value) in entries)
                    if (key is not ("Page" or "View" or "Dest" or "Action" or "File" or "URI")) catalog[PdfName.Get(key)] = MarkValue(value, device);
                break;
            }
            case "OUT":
            {
                var item = new PdfDictionary();
                var entries = Pairs(args).ToEntries();
                int count = entries.TryGetValue("Count", out var c) && c.IsNumber ? (int)Math.Clamp(c.Number, -1e6, 1e6) : 0;
                foreach (var (key, value) in entries)
                {
                    switch (key)
                    {
                        case "Count" or "Page" or "View" or "Action" or "File" or "URI" or "SrcPg":
                            break;
                        case "Dest":
                            item[PdfName.Get("Dest")] = DestName(value);
                            break;
                        case "Color":
                            item[PdfName.Get("C")] = MarkValue(value, device);
                            break;
                        default:
                            item[PdfName.Get(key)] = MarkValue(value, device);
                            break;
                    }
                }
                if (!item.ContainsKey(PdfName.Title)) item[PdfName.Title] = new PdfString(Array.Empty<byte>());
                if (MarkAction(entries, device) is { } action) item[PdfName.Get("A")] = action;
                else if (entries.ContainsKey("Page") && Destination(entries, device) is { } destination) item[PdfName.Get("Dest")] = destination;
                marks.AddOutline(item, count);
                break;
            }
            case "ANN":
            case "LNK":
                Annotation(Pairs(args), kind == "LNK", device);
                break;
            case "DEST":
            {
                var entries = Pairs(args).ToEntries();
                if (!entries.TryGetValue("Dest", out var name)) throw new PsError("rangecheck");
                if (Destination(entries, device) is { } destination)
                    marks.AddDestination(((PdfString)DestName(name)).Bytes, new PdfDictionary { [PdfName.Get("D")] = destination });
                break;
            }
            case "PAGE":
            {
                var page = device.PageObject(device.CurrentPageNumber)!.Dictionary;
                foreach (var (key, value) in Pairs(args)) page[PdfName.Get(key)] = MarkValue(value, device);
                break;
            }
            case "PAGES":
                foreach (var (key, value) in Pairs(args)) marks.PagesEntries[PdfName.Get(key)] = MarkValue(value, device);
                break;
            case "ARTICLE":
            {
                var entries = Pairs(args).ToEntries();
                if (!entries.TryGetValue("Rect", out var rect)) throw new PsError("rangecheck");
                var page = entries.TryGetValue("Page", out var p) ? MarkPage(p, device) : device.PageObject(device.CurrentPageNumber);
                if (page == null) break;
                string title = entries.TryGetValue("Title", out var t) && t.Type == PsType.String ? t.AsString.Text : "Untitled";
                var info = new PdfDictionary();
                foreach (var (key, value) in entries)
                    if (key is not ("Rect" or "Page")) info[PdfName.Get(key)] = MarkValue(value, device);
                marks.AddBead(title, info, page.Reference, UserRectangle(rect));
                break;
            }
            case "EMBED":
            {
                var entries = Pairs(args).ToEntries();
                if (!entries.TryGetValue("Name", out var name) || !entries.TryGetValue("FS", out var spec)) throw new PsError("rangecheck");
                var bytes = name.Type == PsType.String ? name.AsString.ToArray() : name.Type == PsType.Name ? PsText.Latin1.GetBytes(name.AsName.Text) : throw new PsError("typecheck");
                marks.AddEmbeddedFile(bytes, MarkValue(spec, device));
                break;
            }
            case "OBJ":
            {
                var entries = Pairs(args).ToEntries();
                if (!entries.TryGetValue("_objdef", out var name)) throw new PsError("rangecheck");
                string objectType = entries.TryGetValue("type", out var ty) && ty.Type == PsType.Name ? ty.AsName.Text : "dict";
                PdfObject value = objectType switch
                {
                    "array" => new PdfArray(),
                    "stream" => new PdfStream(),
                    "dict" => new PdfDictionary(),
                    _ => throw new PsError("rangecheck"),
                };
                var reference = marks.DefineNamed(ObjectName(name), value);
                if (value is PdfStream) marks.StreamData[reference] = new MemoryStream();
                break;
            }
            case "PUT":
            case "PUTINTERVAL":
            case "APPEND":
            case "CLOSE":
                NamedObjectMark(kind, args, device);
                break;
        }
    }

    // ------------------------------------------------------------------ operands and values

    /// <summary>The key-value pairs of a pdfmark (keys are names).</summary>
    private static List<(string Key, PsObject Value)> Pairs(PsObject[] args)
    {
        if (args.Length % 2 != 0) throw new PsError("rangecheck");
        var pairs = new List<(string, PsObject)>(args.Length / 2);
        for (int i = 0; i < args.Length; i += 2)
        {
            // other keys are skipped, as by Ghostscript
            string? key = args[i].Type switch
            {
                PsType.Name => args[i].AsName.Text,
                PsType.String => args[i].AsString.Text,
                _ => null,
            };
            if (key != null) pairs.Add((key, args[i + 1]));
        }
        return pairs;
    }

    /// <summary>The name of a named object: <c>{name}</c> (a procedure with one name).</summary>
    private static string ObjectName(in PsObject o)
    {
        if (o.IsProcedure && o.AsArray.Length == 1 && o.AsArray[0].Type == PsType.Name) return o.AsArray[0].AsName.Text;
        throw new PsError("typecheck");
    }

    /// <summary>A PostScript object as PDF object; <c>{name}</c> is a reference to a named object or a page.</summary>
    private PdfObject MarkValue(in PsObject o, PsPageDevice? device, int depth = 0)
    {
        if (depth > 32) throw new PsError("limitcheck");
        switch (o.Type)
        {
            case PsType.Integer:
                return new PdfNumber(o.Int);
            case PsType.Real:
                return new PdfNumber(o.Number);
            case PsType.Boolean:
                return o.Bool ? PdfBoolean.True : PdfBoolean.False;
            case PsType.Name:
                return PdfName.Get(o.AsName.Text);
            case PsType.String:
                return new PdfString(o.AsString.ToArray());
            case PsType.Array:
            case PsType.PackedArray:
            {
                if (o.IsProcedure)
                    return o.AsArray.Length == 1 && o.AsArray[0].Type == PsType.Name && device != null
                        ? NamedReference(o.AsArray[0].AsName.Text, device) : PdfNull.Instance;
                var array = new PdfArray();
                foreach (var item in o.AsArray.Span) array.Add(MarkValue(item, device, depth + 1));
                return array;
            }
            case PsType.Dictionary:
            {
                var dict = new PdfDictionary();
                foreach (var (key, value) in o.AsDict.Map)
                {
                    string? name = key.Type switch { PsType.Name => key.AsName.Text, PsType.String => key.AsString.Text, _ => null };
                    if (name != null) dict[PdfName.Get(name)] = MarkValue(value, device, depth + 1);
                }
                return dict;
            }
            default:
                return PdfNull.Instance;
        }
    }

    /// <summary>{Catalog}, {DocInfo}, {ThisPage}, {PrevPage}, {NextPage}, {PageN} or a named object.</summary>
    private PdfObject NamedReference(string name, PsPageDevice device)
    {
        var document = device.Document!;
        switch (name)
        {
            case "Catalog":
                return document.Trailer[PdfName.Root] ?? PdfNull.Instance;
            case "DocInfo":
                device.Marks.Info();
                return document.Trailer[PdfName.Info] ?? PdfNull.Instance;
            case "ThisPage":
            case "PrevPage":
            case "NextPage":
            {
                int number = device.CurrentPageNumber + (name == "PrevPage" ? -1 : name == "NextPage" ? 1 : 0);
                return (PdfObject?)device.PageObject(number)?.Reference ?? PdfNull.Instance;
            }
        }
        if (name.Length > 4 && name.StartsWith("Page", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4), out int n))
            return (PdfObject?)device.PageObject(n)?.Reference ?? PdfNull.Instance;
        return device.Marks.NamedObject(name);
    }

    /// <summary>A page given by number, or /Next and /Prev from the current page.</summary>
    private static PdfPage? MarkPage(in PsObject page, PsPageDevice device)
    {
        if (page.IsNumber) return device.PageObject((int)Math.Clamp(page.Number, -1, int.MaxValue / 2));
        if (page.Type == PsType.Name)
            return page.AsName.Text switch
            {
                "Next" => device.PageObject(device.CurrentPageNumber + 1),
                "Prev" => device.PageObject(device.CurrentPageNumber - 1),
                _ => null,
            };
        return null;
    }

    /// <summary>An explicit destination from /Page (default: the current page) and /View (default: /XYZ null null null).</summary>
    private PdfArray? Destination(Dictionary<string, PsObject> entries, PsPageDevice device)
    {
        var page = entries.TryGetValue("Page", out var p) ? MarkPage(p, device) : device.PageObject(device.CurrentPageNumber);
        if (page == null) return null;
        var destination = new PdfArray(page.Reference);
        if (entries.TryGetValue("View", out var view) && view.IsArray && !view.IsProcedure && view.AsArray.Length > 0)
            foreach (var item in view.AsArray.Span) destination.Add(MarkValue(item, device));
        else
        {
            destination.Add(PdfName.Get("XYZ"));
            destination.Add(PdfNull.Instance);
            destination.Add(PdfNull.Instance);
            destination.Add(PdfNull.Instance);
        }
        return destination;
    }

    /// <summary>A named destination as string (names are written as strings, as by Ghostscript).</summary>
    private static PdfObject DestName(in PsObject name) => name.Type switch
    {
        PsType.Name => new PdfString(PsText.Latin1.GetBytes(name.AsName.Text)),
        PsType.String => new PdfString(name.AsString.ToArray()),
        _ => throw new PsError("typecheck"),
    };

    /// <summary>
    /// The action of /Action: a dictionary (its /Subtype becomes /S), or a name with its parameters (/File, /URI, and
    /// for GoToR the remote /Page and /View or /Dest).
    /// </summary>
    private PdfDictionary? MarkAction(Dictionary<string, PsObject> entries, PsPageDevice device)
    {
        if (!entries.TryGetValue("Action", out var action)) return null;
        if (action.Type == PsType.Dictionary)
        {
            var dict = (PdfDictionary)MarkValue(action, device);
            if (dict.Get(PdfName.Subtype) is { } subtype && !dict.ContainsKey(PdfName.Get("S")))
            {
                dict[PdfName.Get("S")] = subtype;
                dict.Remove(PdfName.Subtype);
            }
            return dict;
        }
        if (action.Type != PsType.Name) return null;
        string kind = action.AsName.Text;
        var result = new PdfDictionary { [PdfName.Type] = PdfName.Get("Action"), [PdfName.Get("S")] = PdfName.Get(kind) };
        if (entries.TryGetValue("File", out var file)) result[PdfName.Get("F")] = MarkValue(file, device);
        if (entries.TryGetValue("URI", out var uri)) result[PdfName.Get("URI")] = MarkValue(uri, device);
        foreach (string key in new[] { "N", "JS", "NewWindow", "Params" })
            if (entries.TryGetValue(key, out var v)) result[PdfName.Get(key)] = MarkValue(v, device);
        switch (kind)
        {
            case "GoToR":
                if (entries.TryGetValue("Dest", out var remoteName)) result[PdfName.Get("D")] = DestName(remoteName);
                else
                {
                    // a page of the other document: its index
                    var d = new PdfArray(new PdfNumber(entries.TryGetValue("Page", out var rp) && rp.IsNumber ? Math.Max(0, (int)rp.Number - 1) : 0));
                    if (entries.TryGetValue("View", out var rv) && rv.IsArray && !rv.IsProcedure)
                        foreach (var item in rv.AsArray.Span) d.Add(MarkValue(item, device));
                    else d.Add(PdfName.Get("Fit"));
                    result[PdfName.Get("D")] = d;
                }
                break;
            case "GoTo":
                if (entries.TryGetValue("Dest", out var name)) result[PdfName.Get("D")] = DestName(name);
                else if (Destination(entries, device) is { } destination) result[PdfName.Get("D")] = destination;
                break;
        }
        return result;
    }

    /// <summary>A rectangle in the current user space, as rectangle in the default user space of the page.</summary>
    private PdfArray UserRectangle(in PsObject rect)
    {
        var r = NumberArray(rect, 4);
        var toDefault = GState.Ctm;
        if ((Device?.DefaultMatrix ?? Matrix.Identity).TryInvert(out var inverse)) toDefault = toDefault.Multiply(inverse);
        var corners = new[] { toDefault.Transform(r[0], r[1]), toDefault.Transform(r[2], r[1]), toDefault.Transform(r[0], r[3]), toDefault.Transform(r[2], r[3]) };
        double Round(double v) => Math.Round(v, 4);
        return new PdfArray(new PdfNumber(Round(corners.Min(c => c.X))), new PdfNumber(Round(corners.Min(c => c.Y))),
            new PdfNumber(Round(corners.Max(c => c.X))), new PdfNumber(Round(corners.Max(c => c.Y))));
    }

    // ------------------------------------------------------------------ annotations

    private void Annotation(List<(string Key, PsObject Value)> pairs, bool link, PsPageDevice device)
    {
        var entries = pairs.ToEntries();
        if (!entries.ContainsKey("Rect")) return; // ignored, as by Ghostscript
        var annotation = new PdfDictionary { [PdfName.Type] = PdfName.Annot };
        string subtype = link ? "Link" : "Text";
        bool namedAction = entries.TryGetValue("Action", out var a) && a.Type == PsType.Name;
        foreach (var (key, value) in pairs)
        {
            switch (key)
            {
                case "Rect":
                    annotation[PdfName.Rect] = UserRectangle(value);
                    break;
                case "Subtype":
                    if (value.Type == PsType.Name) subtype = value.AsName.Text;
                    break;
                case "Title":
                    annotation[PdfName.Get("T")] = MarkValue(value, device);
                    break;
                case "Color":
                    annotation[PdfName.Get("C")] = MarkValue(value, device);
                    break;
                case "Dest":
                    if (!namedAction) annotation[PdfName.Get("Dest")] = DestName(value);
                    break;
                case "Page" or "View" or "SrcPg" or "_objdef" or "Action":
                    break;
                case "File" or "URI" or "N" or "JS" or "NewWindow" or "Params" when namedAction:
                    break;
                default:
                    annotation[PdfName.Get(key)] = MarkValue(value, device);
                    break;
            }
        }
        if (MarkAction(entries, device) is { } action) annotation[PdfName.Get("A")] = action;
        else if ((entries.ContainsKey("Page") || entries.ContainsKey("View")) && Destination(entries, device) is { } destination)
            annotation[PdfName.Get("Dest")] = destination;
        annotation[PdfName.Subtype] = PdfName.Get(subtype);

        var page = entries.TryGetValue("SrcPg", out var source) ? MarkPage(source, device) : device.PageObject(device.CurrentPageNumber);
        if (page == null) return;
        var document = device.Document!;
        var reference = entries.TryGetValue("_objdef", out var name) ? device.Marks.DefineNamed(ObjectName(name), annotation) : document.AddObject(annotation);
        var annots = page.Dictionary.GetArray(PdfName.Annots) ?? new PdfArray();
        annots.Add(reference);
        page.Dictionary[PdfName.Annots] = annots;
    }

    // ------------------------------------------------------------------ named objects

    private void NamedObjectMark(string kind, PsObject[] args, PsPageDevice device)
    {
        if (args.Length == 0 || !args[0].IsProcedure) throw new PsError("rangecheck");
        // the target must exist: a named object defined before, or {Catalog}, {ThisPage}, ...
        string name = ObjectName(args[0]);
        bool predefined = name is "Catalog" or "DocInfo" or "ThisPage" or "PrevPage" or "NextPage" || name.StartsWith("Page", StringComparison.Ordinal);
        if (!predefined && !device.Marks.Named.ContainsKey(name)) throw new PsError("typecheck");
        var reference = MarkValue(args[0], device) as PdfReference;
        var target = reference?.Resolve() ?? throw new PsError("typecheck");
        switch (kind)
        {
            case "PUT" when args.Length == 2 && target is PdfStream stream && args[1].Type is PsType.String or PsType.File:
                AppendStreamData(device, reference!, args[1]);
                break;
            case "PUT" when args.Length == 2 && target is PdfDictionary dict && args[1].Type == PsType.Dictionary:
                foreach (var (key, value) in (PdfDictionary)MarkValue(args[1], device)) dict[key] = value;
                break;
            case "PUT" when args.Length == 3 && target is PdfDictionary dict && args[1].Type == PsType.Name:
                dict[PdfName.Get(args[1].AsName.Text)] = MarkValue(args[2], device);
                break;
            case "PUT" when args.Length == 3 && target is PdfArray array && args[1].Type == PsType.Integer:
                SetArrayItem(array, args[1].Int, MarkValue(args[2], device));
                break;
            case "PUTINTERVAL" when args.Length == 3 && target is PdfArray array && args[1].Type == PsType.Integer && args[2].IsArray:
            {
                long index = args[1].Int;
                foreach (var item in args[2].AsArray.Span) SetArrayItem(array, index++, MarkValue(item, device));
                break;
            }
            case "APPEND" when args.Length == 2 && target is PdfArray array:
                if (array.Count >= 1_000_000) throw new PsError("limitcheck");
                array.Add(MarkValue(args[1], device));
                break;
            case "CLOSE" when args.Length == 1:
                if (reference != null) device.Marks.CloseStream(reference);
                break;
            default:
                throw new PsError("typecheck");
        }
    }

    private static void SetArrayItem(PdfArray array, long index, PdfObject value)
    {
        if (index < 0 || index >= 1_000_000) throw new PsError("rangecheck");
        while (array.Count <= index) array.Add(PdfNull.Instance);
        array[(int)index] = value;
    }

    private void AppendStreamData(PsPageDevice device, PdfReference reference, in PsObject data)
    {
        if (!device.Marks.StreamData.TryGetValue(reference, out var buffer)) device.Marks.StreamData[reference] = buffer = new MemoryStream();
        var bytes = data.Type == PsType.String ? data.AsString.ToArray() : ReadAll(data.AsFile);
        if (buffer.Length + bytes.Length > Limits.MaxImageBytes) throw new PsError("limitcheck");
        buffer.Write(bytes);
    }

    // ------------------------------------------------------------------ named pictures

    /// <summary>BP: what is painted until EP goes into a form XObject (its space is the current user space).</summary>
    private void BeginPicture(List<(string Key, PsObject Value)> pairs)
    {
        var entries = pairs.ToEntries();
        // a picture needs a name (Ghostscript: rangecheck)
        if (!entries.TryGetValue("_objdef", out var name)) throw new PsError("rangecheck");
        string objectName = ObjectName(name);
        var device = Device;
        if (_pictures.Count >= 32) throw new PsError("limitcheck");
        if (device?.Document is not { } document || device is PsNullDevice or PsPathDevice || !GState.Ctm.TryInvert(out var inverse))
        {
            _pictures.Add((null, null, null));
            return;
        }
        var bbox = entries.TryGetValue("BBox", out var b) ? NumberArray(b, 4) : new double[] { 0, 0, device.Width, device.Height };
        var resources = new PdfDictionary();
        var stream = new PdfStream
        {
            [PdfName.Type] = PdfName.XObject,
            [PdfName.Subtype] = PdfName.Form,
            [PdfName.BBox] = new PdfArray(bbox.Select(v => (PdfObject)new PdfNumber(v))),
            [PdfName.Resources] = resources,
        };
        _pictureNames[objectName] = PageDevice != null ? PageDevice.Marks.DefineNamed(objectName, stream) : document.AddObject(stream);
        var writer = new PsContentWriter(new PsDictionaryResources(document, resources), deviceToContent: inverse, unknownStart: true);
        device.Fonts ??= new PsPdfFonts(document, this);
        _pictures.Add((device, writer, stream));
        GState.Device = new PsCaptureDevice(device, writer);
    }

    /// <summary>EP: the picture is finished; painting goes to the device again.</summary>
    private void EndPicture()
    {
        if (_pictures.Count == 0) throw new PsError("undefined");
        var (device, writer, stream) = _pictures[^1];
        _pictures.RemoveAt(_pictures.Count - 1);
        if (writer == null || stream == null) return;
        stream.SetBytes(writer.ToArray());
        GState.Device = device;
    }

    /// <summary>SP: paints a named picture in the current user space.</summary>
    private void ShowPicture(PsObject[] args)
    {
        if (args.Length != 1) throw new PsError("rangecheck");
        string name = ObjectName(args[0]);
        if (!_pictureNames.TryGetValue(name, out var reference)) throw new PsError("typecheck");
        if (Device is { Document: not null } device && device is not (PsNullDevice or PsPathDevice)) device.PaintForm(GState, reference, GState.Ctm);
    }
}
