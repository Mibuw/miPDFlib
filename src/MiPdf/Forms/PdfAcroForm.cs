using System.Text;
using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Forms;

/// <summary>
/// The interactive form (AcroForm) of a document: read, fill, create and flatten fields.
/// Obtain it with <see cref="PdfDocument.GetForm"/> or <see cref="PdfDocument.GetOrCreateForm"/>.
/// </summary>
public sealed class PdfAcroForm
{
    private List<PdfFormField>? _fields;

    internal PdfAcroForm(PdfDocument document, PdfDictionary dictionary)
    {
        Document = document;
        Dictionary = dictionary;
    }

    public PdfDocument Document { get; }

    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// Font used for generated appearances when a field does not specify one. Set an embedded
    /// <see cref="PdfTrueTypeFont"/> to fill in characters outside WinAnsi (e.g. Polish, Cyrillic).
    /// </summary>
    public PdfFont? AppearanceFont { get; set; }

    /// <summary>Asks viewers to regenerate appearances (/NeedAppearances).</summary>
    public bool NeedAppearances
    {
        get => Dictionary.GetBool(PdfName.NeedAppearances);
        set
        {
            if (value) Dictionary.Set(PdfName.NeedAppearances, true);
            else Dictionary.Remove(PdfName.NeedAppearances);
        }
    }

    /// <summary>All terminal fields (fields that hold a value) in document order.</summary>
    public IReadOnlyList<PdfFormField> Fields => _fields ??= LoadFields();

    /// <summary>Returns the field with the given fully qualified name, or null.</summary>
    public PdfFormField? GetField(string fullName) => Fields.FirstOrDefault(f => f.FullName == fullName);

    public T? GetField<T>(string fullName) where T : PdfFormField => GetField(fullName) as T;

    /// <summary>Sets the value of a field by name (text, check box state, radio option, choice).</summary>
    public void SetValue(string fullName, string? value)
    {
        var field = GetField(fullName) ?? throw new KeyNotFoundException($"Field '{fullName}' not found.");
        field.Value = value;
    }

    /// <summary>Returns all field values as name → value.</summary>
    public IReadOnlyDictionary<string, string?> GetValues() =>
        Fields.GroupBy(f => f.FullName).ToDictionary(g => g.Key, g => g.First().Value);

    internal void Invalidate() => _fields = null;

    // ------------------------------------------------------------------ loading

    private List<PdfFormField> LoadFields()
    {
        var widgetPages = new Dictionary<PdfDictionary, PdfPage>(ReferenceEqualityComparer.Instance);
        foreach (var page in Document.Pages)
            if (page.Dictionary.Get(PdfName.Annots) is PdfArray annots)
                foreach (var a in annots)
                    if (a.Resolve() is PdfDictionary d) widgetPages[d] = page;

        var result = new List<PdfFormField>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (Dictionary.GetArray(PdfName.Fields) is { } fields)
            foreach (var f in fields)
                if (f.Resolve() is PdfDictionary d) Collect(d, null, result, visited, widgetPages, 0);
        return result;
    }

    private void Collect(PdfDictionary node, string? parentName, List<PdfFormField> result, HashSet<PdfDictionary> visited,
        Dictionary<PdfDictionary, PdfPage> widgetPages, int depth)
    {
        if (depth > 32 || !visited.Add(node)) return;
        var partial = node.GetText(PdfName.T);
        string name = partial == null ? parentName ?? "" : parentName == null ? partial : parentName + "." + partial;
        var kids = node.GetArray(PdfName.Kids)?.Select(k => k.Resolve()).OfType<PdfDictionary>().ToList();
        // a field is terminal if it has no kids or all kids are widgets (kids without /T)
        bool terminal = kids == null || kids.Count == 0 || kids.All(k => !k.ContainsKey(PdfName.T) && (k.IsType(PdfName.Annot) || k.GetName(PdfName.Subtype) == PdfName.Widget || !k.ContainsKey(PdfName.FT)));
        if (!terminal)
        {
            foreach (var kid in kids!) Collect(kid, name, result, visited, widgetPages, depth + 1);
            return;
        }
        var widgets = new List<PdfWidget>();
        if (kids == null || kids.Count == 0) widgets.Add(new PdfWidget(node, widgetPages.GetValueOrDefault(node)));
        else widgets.AddRange(kids.Select(k => new PdfWidget(k, widgetPages.GetValueOrDefault(k))));
        result.Add(CreateField(node, name, widgets));
    }

    private PdfFormField CreateField(PdfDictionary node, string name, List<PdfWidget> widgets)
    {
        var probe = new PdfTextField(this, node, name, widgets);
        var flags = probe.Flags;
        return probe.FieldType switch
        {
            "Tx" => probe,
            "Btn" when flags.HasFlag(FieldFlags.PushButton) => new PdfPushButton(this, node, name, widgets),
            "Btn" when flags.HasFlag(FieldFlags.Radio) => new PdfRadioButtonGroup(this, node, name, widgets),
            "Btn" => new PdfCheckBox(this, node, name, widgets),
            "Ch" => new PdfChoiceField(this, node, name, widgets),
            "Sig" => new PdfSignatureField(this, node, name, widgets),
            _ => probe,
        };
    }

    // ------------------------------------------------------------------ default resources

    /// <summary>Ensures /DR contains a font resource with the given name and returns the font dictionary reference.</summary>
    internal PdfReference EnsureDefaultFont(string resourceName, PdfStandardFont font)
    {
        var dr = Dictionary.GetDictionary(PdfName.DR);
        if (dr == null)
        {
            dr = new PdfDictionary();
            Dictionary[PdfName.DR] = dr;
        }
        var fonts = dr.GetDictionary(PdfName.Font);
        if (fonts == null)
        {
            fonts = new PdfDictionary();
            dr[PdfName.Font] = fonts;
        }
        if (fonts[PdfName.Get(resourceName)] is PdfReference existing) return existing;
        var reference = Document.GetFontResource(font).Reference;
        fonts[PdfName.Get(resourceName)] = reference;
        return reference;
    }

    // ------------------------------------------------------------------ creating fields

    private PdfDictionary NewWidgetField(PdfPage page, string name, PdfRectangle rect, string fieldType)
    {
        if (name.Contains('.')) throw new ArgumentException("Field names must not contain '.'.", nameof(name));
        if (GetField(name) != null) throw new ArgumentException($"A field named '{name}' already exists.", nameof(name));
        var field = new PdfDictionary();
        field[PdfName.FT] = PdfName.Get(fieldType);
        field.SetText(PdfName.T, name);
        field[PdfName.Subtype] = PdfName.Widget;
        field[PdfName.Rect] = PdfArray.FromRectangle(rect);
        field.Set(PdfName.F, 4); // print
        var annotation = page.AddAnnotation(field);
        AddRootField(annotation.Reference!);
        return field;
    }

    internal void AddRootField(PdfReference field)
    {
        var fields = Dictionary.GetArray(PdfName.Fields);
        if (fields == null)
        {
            fields = new PdfArray();
            Dictionary[PdfName.Fields] = fields;
        }
        fields.Add(field);
        _fields = null;
    }

    private static PdfDictionary CreateMk(PdfColor? border, PdfColor? background)
    {
        var mk = new PdfDictionary();
        if (border is { } b) mk[PdfName.Get("BC")] = PdfArray.FromNumbers(b.Components);
        if (background is { } bg) mk[PdfName.Get("BG")] = PdfArray.FromNumbers(bg.Components);
        return mk;
    }

    /// <summary>The XFA part of the form (data of XFA forms).</summary>
    public XfaForm Xfa => _xfa ??= new XfaForm(this);

    private XfaForm? _xfa;

    /// <summary>
    /// Adds a push button showing an image (icon only) and/or a caption, e.g. to place a signature image as a form
    /// field without a digital signature.
    /// </summary>
    public PdfPushButton AddPushButton(PdfPage page, string name, PdfRectangle rect, Images.PdfImage? icon = null, string? caption = null,
        PdfColor? border = null, PdfColor? background = null)
    {
        EnsureDefaultFont("Helv", PdfStandardFont.Helvetica);
        var field = NewWidgetField(page, name, rect, "Btn");
        field.Set(PdfName.Ff, (int)FieldFlags.PushButton);
        field[PdfName.DA] = new PdfString("/Helv 0 Tf 0 g");
        field[PdfName.MK] = CreateMk(border, background);
        var button = (PdfPushButton)GetField(name)!;
        button.SetAppearance(icon, caption);
        return button;
    }

    /// <summary>Builds icon (/MK /I) and normal appearance of a push button widget.</summary>
    internal void SetPushButtonAppearance(PdfDictionary widget, Images.PdfImage? icon, string? caption)
    {
        var rect = widget.GetRectangle(PdfName.Rect) ?? default;
        double w = rect.Width, h = rect.Height;
        var mk = widget.GetDictionary(PdfName.MK);
        if (mk == null) widget[PdfName.MK] = mk = new PdfDictionary();
        PdfFormXObject? iconXObject = null;
        if (icon != null)
        {
            iconXObject = new PdfFormXObject(Document, new PdfRectangle(0, 0, icon.Width, icon.Height));
            using (var c = iconXObject.GetCanvas()) c.DrawImage(icon, 0, 0, icon.Width, icon.Height);
            mk[PdfName.Get("I")] = iconXObject.Reference;
            // scale proportionally, centered
            mk[PdfName.Get("IF")] = new PdfDictionary
            {
                [PdfName.Get("SW")] = PdfName.Get("A"),
                [PdfName.S] = PdfName.P,
                [PdfName.Get("A")] = PdfArray.FromNumbers(0.5, 0.5),
            };
        }
        else mk.Remove(PdfName.Get("I"));
        if (caption != null) mk.SetText(PdfName.Get("CA"), caption);
        else mk.Remove(PdfName.Get("CA"));
        mk.Set(PdfName.Get("TP"), icon != null && caption == null ? 1 : icon != null ? 2 : 0); // icon only / caption below icon / caption only

        var appearance = new PdfFormXObject(Document, new PdfRectangle(0, 0, w, h));
        using (var canvas = appearance.GetCanvas())
        {
            if (mk.GetArray(PdfName.Get("BG")) is { } bg) canvas.DrawRectangle(0, 0, w, h, fill: ToColor(bg));
            if (mk.GetArray(PdfName.Get("BC")) is { } bc) canvas.DrawRectangle(0.5, 0.5, w - 1, h - 1, ToColor(bc), lineWidth: 1);
            double captionHeight = caption != null && icon != null ? Math.Min(h * 0.3, 14) : 0;
            if (icon != null) canvas.DrawImage(icon, new PdfRectangle(1, 1 + captionHeight, w - 1, h - 1));
            if (caption != null)
            {
                var font = AppearanceFont ?? PdfStandardFont.Helvetica;
                var box = icon != null ? new PdfRectangle(1, 1, w - 1, 1 + captionHeight) : new PdfRectangle(1, 1, w - 1, h - 1);
                double size = Math.Max(4, Math.Min(box.Height * 0.7, (box.Width - 4) / Math.Max(1, font.GetTextWidth(caption, 1))));
                canvas.DrawTextBox(caption, font, size, box, Graphics.TextAlignment.Center, Layout.VerticalAlignment.Middle);
            }
        }
        widget[PdfName.AP] = new PdfDictionary { [PdfName.N] = appearance.Reference };
    }

    private static PdfColor ToColor(PdfArray components) => components.Count switch
    {
        1 => PdfColor.Gray(components.GetDouble(0)),
        4 => PdfColor.Cmyk(components.GetDouble(0), components.GetDouble(1), components.GetDouble(2), components.GetDouble(3)),
        _ => PdfColor.Rgb(components.GetDouble(0), components.GetDouble(1), components.GetDouble(2)),
    };

    /// <summary>Adds a text field. A font size of 0 means auto-size.</summary>
    public PdfTextField AddTextField(PdfPage page, string name, PdfRectangle rect, string? value = null, double fontSize = 0,
        bool multiline = false, PdfColor? border = null, PdfColor? background = null)
    {
        EnsureDefaultFont("Helv", PdfStandardFont.Helvetica);
        var field = NewWidgetField(page, name, rect, "Tx");
        field[PdfName.DA] = new PdfString($"/Helv {PdfNumber.FormatReal(fontSize)} Tf 0 g");
        if (multiline) field.Set(PdfName.Ff, (int)FieldFlags.Multiline);
        field[PdfName.MK] = CreateMk(border ?? PdfColor.Black, background);
        var text = (PdfTextField)GetField(name)!;
        text.Value = value ?? "";
        if (value == null) field.Remove(PdfName.V);
        return text;
    }

    /// <summary>Adds a check box with the on-state "Yes".</summary>
    public PdfCheckBox AddCheckBox(PdfPage page, string name, PdfRectangle rect, bool isChecked = false, PdfColor? border = null,
        CheckBoxStyle style = CheckBoxStyle.Check)
    {
        EnsureDefaultFont("ZaDb", PdfStandardFont.ZapfDingbats);
        var field = NewWidgetField(page, name, rect, "Btn");
        field[PdfName.DA] = new PdfString("/ZaDb 0 Tf 0 g");
        char symbol = style switch
        {
            CheckBoxStyle.Cross => '8',
            CheckBoxStyle.Circle => 'l',
            CheckBoxStyle.Square => 'n',
            CheckBoxStyle.Diamond => 'u',
            _ => '4',
        };
        var mk = CreateMk(border ?? PdfColor.Black, PdfColor.White);
        mk[PdfName.Get("CA")] = new PdfString(symbol.ToString());
        field[PdfName.MK] = mk;
        AppearanceGenerator.CheckBoxStates(this, field, "Yes", symbol);
        var box = (PdfCheckBox)GetField(name)!;
        box.IsChecked = isChecked;
        return box;
    }

    /// <summary>Adds a radio button group with one button per option.</summary>
    /// <param name="page">Page of the buttons.</param>
    /// <param name="name">Field name.</param>
    /// <param name="options">Export value and rectangle of every button.</param>
    /// <param name="selected">Initially selected export value.</param>
    public PdfRadioButtonGroup AddRadioGroup(PdfPage page, string name, IEnumerable<(string Value, PdfRectangle Rect)> options, string? selected = null)
    {
        if (GetField(name) != null) throw new ArgumentException($"A field named '{name}' already exists.", nameof(name));
        EnsureDefaultFont("ZaDb", PdfStandardFont.ZapfDingbats);
        var group = new PdfDictionary();
        group[PdfName.FT] = PdfName.Get("Btn");
        group.SetText(PdfName.T, name);
        group.Set(PdfName.Ff, (int)(FieldFlags.Radio | FieldFlags.NoToggleToOff));
        group[PdfName.DA] = new PdfString("/ZaDb 0 Tf 0 g");
        var groupRef = Document.AddObject(group);
        var kids = new PdfArray();
        group[PdfName.Kids] = kids;
        foreach (var (value, rect) in options)
        {
            var widget = new PdfDictionary();
            widget[PdfName.Subtype] = PdfName.Widget;
            widget[PdfName.Rect] = PdfArray.FromRectangle(rect);
            widget[PdfName.Parent] = groupRef;
            widget.Set(PdfName.F, 4);
            var mk = CreateMk(PdfColor.Black, PdfColor.White);
            mk[PdfName.Get("CA")] = new PdfString("l");
            widget[PdfName.MK] = mk;
            AppearanceGenerator.CheckBoxStates(this, widget, value, 'l');
            kids.Add(page.AddAnnotation(widget).Reference!);
        }
        AddRootField(groupRef);
        var radio = (PdfRadioButtonGroup)GetField(name)!;
        radio.Value = selected;
        return radio;
    }

    /// <summary>Adds a combo box (drop-down list).</summary>
    public PdfChoiceField AddComboBox(PdfPage page, string name, PdfRectangle rect, IEnumerable<string> options, string? selected = null, bool editable = false) =>
        AddChoice(page, name, rect, options, selected, FieldFlags.Combo | (editable ? FieldFlags.Edit : 0));

    /// <summary>Adds a list box.</summary>
    public PdfChoiceField AddListBox(PdfPage page, string name, PdfRectangle rect, IEnumerable<string> options, string? selected = null, bool multiSelect = false) =>
        AddChoice(page, name, rect, options, selected, multiSelect ? FieldFlags.MultiSelect : 0);

    private PdfChoiceField AddChoice(PdfPage page, string name, PdfRectangle rect, IEnumerable<string> options, string? selected, FieldFlags flags)
    {
        EnsureDefaultFont("Helv", PdfStandardFont.Helvetica);
        var field = NewWidgetField(page, name, rect, "Ch");
        field[PdfName.DA] = new PdfString("/Helv 0 Tf 0 g");
        field.Set(PdfName.Ff, (int)flags);
        field[PdfName.Opt] = new PdfArray(options.Select(o => (PdfObject)new PdfString(o)));
        field[PdfName.MK] = CreateMk(PdfColor.Black, PdfColor.White);
        var choice = (PdfChoiceField)GetField(name)!;
        if (selected != null) choice.Value = selected;
        else choice.RegenerateAppearance();
        return choice;
    }

    /// <summary>Adds an empty signature field (use an empty rectangle for an invisible signature).</summary>
    public PdfSignatureField AddSignatureField(PdfPage page, string name, PdfRectangle rect)
    {
        var field = NewWidgetField(page, name, rect, "Sig");
        field.Set(PdfName.F, 132); // print + locked
        if (rect.Width > 0 && rect.Height > 0)
        {
            var ap = new PdfFormXObject(Document, new PdfRectangle(0, 0, rect.Width, rect.Height));
            ap.Stream.SetBytes(Array.Empty<byte>());
            var apDict = new PdfDictionary { [PdfName.N] = ap.Reference };
            field[PdfName.AP] = apDict;
        }
        return (PdfSignatureField)GetField(name)!;
    }

    // ------------------------------------------------------------------ removing and flattening

    /// <summary>Removes a field and its widgets.</summary>
    public void RemoveField(PdfFormField field)
    {
        foreach (var w in field.Widgets) w.Page?.RemoveAnnotation(new PdfAnnotation(w.Dictionary, null));
        RemoveFromTree(field.Dictionary);
        _fields = null;
    }

    private void RemoveFromTree(PdfDictionary node)
    {
        var parent = node.GetDictionary(PdfName.Parent);
        var array = parent?.GetArray(PdfName.Kids) ?? Dictionary.GetArray(PdfName.Fields);
        if (array == null) return;
        for (int i = array.Count - 1; i >= 0; i--)
            if (ReferenceEquals(array[i].Resolve(), node)) array.RemoveAt(i);
        if (parent != null && array.Count == 0) RemoveFromTree(parent);
    }

    /// <summary>
    /// Flattens the given fields (all if null): their current appearance becomes part of the page content
    /// and the fields are removed. The values can no longer be edited.
    /// </summary>
    public void Flatten(IEnumerable<PdfFormField>? fields = null)
    {
        var list = (fields ?? Fields).ToList();
        foreach (var field in list)
        {
            foreach (var widget in field.Widgets)
            {
                if (widget.Page == null) continue;
                if (!widget.IsHidden) DrawAppearance(widget.Page, widget.Dictionary);
                widget.Page.RemoveAnnotation(new PdfAnnotation(widget.Dictionary, null));
            }
            RemoveFromTree(field.Dictionary);
        }
        _fields = null;
        if (Dictionary.GetArray(PdfName.Fields) is not { Count: > 0 })
        {
            Document.Catalog.Remove(PdfName.AcroForm);
        }
    }

    /// <summary>Draws the normal appearance of an annotation into the page content.</summary>
    internal static void DrawAppearance(PdfPage page, PdfDictionary annotation)
    {
        var normal = annotation.GetDictionary(PdfName.AP)?.Get(PdfName.N);
        if (normal is PdfDictionary states and not PdfStream)
        {
            var state = annotation.GetName(PdfName.AS);
            normal = state != null ? states.Get(state) : null;
        }
        if (normal is not PdfStream ap || annotation.GetRectangle(PdfName.Rect) is not { } rect) return;
        var bbox = ap.GetRectangle(PdfName.BBox);
        if (bbox is not { } box || box.Width <= 0 || box.Height <= 0) return;
        var matrix = ap.GetArray(PdfName.Get("Matrix")) is { Count: 6 } m
            ? new Matrix(m.GetDouble(0), m.GetDouble(1), m.GetDouble(2), m.GetDouble(3), m.GetDouble(4), m.GetDouble(5))
            : Matrix.Identity;
        // transformed bbox (ISO 32000-2, 12.5.5)
        var corners = new[] { matrix.Transform(box.Left, box.Bottom), matrix.Transform(box.Right, box.Bottom), matrix.Transform(box.Left, box.Top), matrix.Transform(box.Right, box.Top) };
        double minX = corners.Min(c => c.X), minY = corners.Min(c => c.Y), maxX = corners.Max(c => c.X), maxY = corners.Max(c => c.Y);
        double sx = (maxX - minX) == 0 ? 1 : rect.Width / (maxX - minX);
        double sy = (maxY - minY) == 0 ? 1 : rect.Height / (maxY - minY);
        var reference = page.Document.GetReference(ap) ?? page.Document.AddObject(ap);
        var name = page.AddResource(PdfName.XObject, reference, "Fm");
        var sb = new StringBuilder();
        sb.Append("q ").Append(string.Join(' ', new[] { sx, 0, 0, sy, rect.Left - minX * sx, rect.Bottom - minY * sy }.Select(PdfNumber.FormatReal)))
          .Append(" cm /").Append(name.Value).Append(" Do Q\n");
        page.AppendContent(Encoding.ASCII.GetBytes(sb.ToString()));
    }
}
