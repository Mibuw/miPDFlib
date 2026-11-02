using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Forms;

/// <summary>Field flags (ISO 32000-2, 12.7.4).</summary>
[Flags]
public enum FieldFlags
{
    /// <summary>No flags set.</summary>
    None = 0,
    /// <summary>The user may not change the value (all field types).</summary>
    ReadOnly = 1 << 0,
    /// <summary>The field must have a value when the form is submitted (all field types).</summary>
    Required = 1 << 1,
    /// <summary>The field is not exported when the form is submitted (all field types).</summary>
    NoExport = 1 << 2,
    /// <summary>Text field: the text may span multiple lines.</summary>
    Multiline = 1 << 12,
    /// <summary>Text field: the entered text is masked (e.g. with asterisks).</summary>
    Password = 1 << 13,
    /// <summary>Radio buttons: exactly one button must be selected at all times.</summary>
    NoToggleToOff = 1 << 14,
    /// <summary>Button field: radio buttons instead of a check box.</summary>
    Radio = 1 << 15,
    /// <summary>Button field: a push button without a permanent value.</summary>
    PushButton = 1 << 16,
    /// <summary>Choice field: a combo box (drop-down list) instead of a list box.</summary>
    Combo = 1 << 17,
    /// <summary>Combo box: the user may enter a value that is not in the option list.</summary>
    Edit = 1 << 18,
    /// <summary>Choice field: the options should be sorted alphabetically (a hint for form authoring tools).</summary>
    Sort = 1 << 19,
    /// <summary>Text field: the value is the path of a file whose contents are submitted.</summary>
    FileSelect = 1 << 20,
    /// <summary>Choice field: more than one option may be selected.</summary>
    MultiSelect = 1 << 21,
    /// <summary>Text or combo box field: the text is not spell-checked.</summary>
    DoNotSpellCheck = 1 << 22,
    /// <summary>Text field: no scrolling; text that does not fit is not accepted.</summary>
    DoNotScroll = 1 << 23,
    /// <summary>Text field: the box is divided into /MaxLen equally spaced character cells.</summary>
    Comb = 1 << 24,
}

/// <summary>A widget annotation (the visible part of a field on a page).</summary>
public sealed class PdfWidget
{
    internal PdfWidget(PdfDictionary dictionary, PdfPage? page)
    {
        Dictionary = dictionary;
        Page = page;
    }

    /// <summary>The widget annotation dictionary (may be merged with the field dictionary).</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The page the widget is placed on (null if it is not referenced by any page).</summary>
    public PdfPage? Page { get; internal set; }

    /// <summary>The widget rectangle (/Rect) in page coordinates; an empty rectangle if missing.</summary>
    public PdfRectangle Rect
    {
        get => Dictionary.GetRectangle(PdfName.Rect) ?? default;
        set => Dictionary[PdfName.Rect] = PdfArray.FromRectangle(value);
    }

    /// <summary>The selected appearance state (/AS) for check boxes and radio buttons.</summary>
    public string? AppearanceState => Dictionary.GetName(PdfName.AS)?.Value;

    /// <summary>Names of the "on" states defined in the normal appearance (/AP /N), excluding "Off".</summary>
    public IReadOnlyList<string> OnStates =>
        Dictionary.GetDictionary(PdfName.AP)?.GetDictionary(PdfName.N) is { } n && n is not PdfStream
            ? n.Keys.Select(k => k.Value).Where(k => k != "Off").ToList()
            : Array.Empty<string>();

    /// <summary>True if the annotation flag Hidden (/F bit 2) is set.</summary>
    public bool IsHidden => (Dictionary.GetInt(PdfName.F) & 2) != 0;

    /// <summary>The field this widget belongs to.</summary>
    internal PdfFormField? Field { get; set; }

    /// <summary>
    /// Counterclockwise rotation of the widget content in degrees (/MK /R): 0, 90, 180 or 270. Setting it regenerates
    /// the appearance of text, choice, check box and radio button fields.
    /// </summary>
    public int Rotation
    {
        get => AppearanceGenerator.GetRotation(Dictionary);
        set
        {
            if (value % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90.", nameof(value));
            int r = AppearanceGenerator.NormalizeRotation(value);
            if (r == 0) GetMk(false)?.Remove(PdfName.Get("R"));
            else GetMk(true)!.Set(PdfName.Get("R"), r);
            Field?.RegenerateAppearance();
        }
    }

    /// <summary>Border color (/MK /BC); null = no border. Setting it regenerates the appearance (see <see cref="Rotation"/>).</summary>
    public PdfColor? BorderColor
    {
        get => AppearanceGenerator.ReadColor(Dictionary.GetDictionary(PdfName.MK), "BC");
        set => SetMkColor("BC", value);
    }

    /// <summary>Background color (/MK /BG); null = transparent. Setting it regenerates the appearance (see <see cref="Rotation"/>).</summary>
    public PdfColor? BackgroundColor
    {
        get => AppearanceGenerator.ReadColor(Dictionary.GetDictionary(PdfName.MK), "BG");
        set => SetMkColor("BG", value);
    }

    /// <summary>Border width in points (/BS /W, default 1); 0 = no border. Setting it regenerates the appearance (see <see cref="Rotation"/>).</summary>
    public double BorderWidth
    {
        get => Dictionary.GetDictionary(PdfName.BS)?.GetDouble(PdfName.W, 1) ?? 1;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "The border width must not be negative.");
            var bs = Dictionary.GetDictionary(PdfName.BS);
            if (bs == null) Dictionary[PdfName.BS] = bs = new PdfDictionary();
            bs.Set(PdfName.W, value);
            Field?.RegenerateAppearance();
        }
    }

    private PdfDictionary? GetMk(bool create)
    {
        var mk = Dictionary.GetDictionary(PdfName.MK);
        if (mk == null && create) Dictionary[PdfName.MK] = mk = new PdfDictionary();
        return mk;
    }

    private void SetMkColor(string key, PdfColor? color)
    {
        if (color is { } c) GetMk(true)![PdfName.Get(key)] = PdfArray.FromNumbers(c.Components);
        else GetMk(false)?.Remove(PdfName.Get(key));
        Field?.RegenerateAppearance();
    }

    /// <summary>Images in the widget's appearance (e.g. a signature image), with page coordinates.</summary>
    public IReadOnlyList<Images.PdfExtractedImage> ExtractImages() => Images.PdfImageExtractor.Extract(Dictionary);
}

/// <summary>Symbol shown in a checked check box.</summary>
public enum CheckBoxStyle
{
    /// <summary>A check mark (ZapfDingbats '4').</summary>
    Check,
    /// <summary>A cross (ZapfDingbats '8').</summary>
    Cross,
    /// <summary>A filled circle (ZapfDingbats 'l').</summary>
    Circle,
    /// <summary>A filled square (ZapfDingbats 'n').</summary>
    Square,
    /// <summary>A filled diamond (ZapfDingbats 'u').</summary>
    Diamond,
}

/// <summary>Base class of form fields. Use the derived types for type-specific operations.</summary>
public abstract class PdfFormField
{
    internal PdfFormField(PdfAcroForm form, PdfDictionary dictionary, string fullName, List<PdfWidget> widgets)
    {
        Form = form;
        Dictionary = dictionary;
        FullName = fullName;
        Widgets = widgets;
        foreach (var widget in widgets) widget.Field = this;
    }

    /// <summary>The form the field belongs to.</summary>
    public PdfAcroForm Form { get; }

    /// <summary>The terminal field dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Fully qualified name, e.g. "address.street".</summary>
    public string FullName { get; }

    /// <summary>The field's own name (/T) without the names of its parents; "" if not set.</summary>
    public string PartialName => Dictionary.GetText(PdfName.T) ?? "";

    /// <summary>The widget annotations (visible instances) of the field.</summary>
    public IReadOnlyList<PdfWidget> Widgets { get; }

    /// <summary>Description shown as tooltip and read by screen readers (/TU, required by PDF/UA).</summary>
    public string? ToolTip
    {
        get => Dictionary.GetText(PdfName.Get("TU"));
        set => Dictionary.SetText(PdfName.Get("TU"), value);
    }

    /// <summary>"Tx", "Btn", "Ch" or "Sig".</summary>
    public string FieldType => GetInheritable(PdfName.FT) is PdfName n ? n.Value : "";

    /// <summary>The field flags (/Ff, inherited from parent fields if not set); setting writes them to this field.</summary>
    public FieldFlags Flags
    {
        get => (FieldFlags)((GetInheritable(PdfName.Ff) as PdfNumber)?.IntValue ?? 0);
        set => Dictionary.Set(PdfName.Ff, (int)value);
    }

    /// <summary>Gets or sets the <see cref="FieldFlags.ReadOnly"/> flag.</summary>
    public bool ReadOnly
    {
        get => Flags.HasFlag(FieldFlags.ReadOnly);
        set => Flags = value ? Flags | FieldFlags.ReadOnly : Flags & ~FieldFlags.ReadOnly;
    }

    /// <summary>Gets or sets the <see cref="FieldFlags.Required"/> flag.</summary>
    public bool Required
    {
        get => Flags.HasFlag(FieldFlags.Required);
        set => Flags = value ? Flags | FieldFlags.Required : Flags & ~FieldFlags.Required;
    }

    /// <summary>Tooltip / alternate name (/TU).</summary>
    public string? Tooltip
    {
        get => Dictionary.GetText(PdfName.Get("TU"));
        set => Dictionary.SetText(PdfName.Get("TU"), value);
    }

    /// <summary>The field value as text (for buttons: the selected state name, "Off" if unchecked).</summary>
    public virtual string? Value
    {
        get => GetInheritable(PdfName.V) switch
        {
            PdfString s => s.Text,
            PdfName n => n.Value,
            PdfArray a => string.Join(", ", a.Select(o => o.Resolve()).Select(o => o is PdfString s ? s.Text : o.ToString())),
            _ => null,
        };
        set => throw new NotSupportedException($"Setting the value of a {GetType().Name} is not supported.");
    }

    /// <summary>Font used to generate the appearance (overrides the field's /DA font).</summary>
    public PdfFont? AppearanceFont { get; set; }

    internal PdfObject? GetInheritable(PdfName key)
    {
        var node = Dictionary;
        for (int depth = 0; node != null && depth < 32; depth++)
        {
            var v = node.Get(key);
            if (v != null) return v;
            node = node.GetDictionary(PdfName.Parent);
        }
        return null;
    }

    internal string DefaultAppearance =>
        (GetInheritable(PdfName.DA) as PdfString)?.Text ?? Form.Dictionary.GetText(PdfName.DA) ?? "/Helv 0 Tf 0 g";

    /// <summary>Regenerates the appearance streams of all widgets from the current value.</summary>
    public virtual void RegenerateAppearance() { }

    /// <summary>Returns the field type, full name and value, for debugging.</summary>
    public override string ToString() => $"{GetType().Name} '{FullName}' = {Value}";
}

/// <summary>A text field.</summary>
public sealed class PdfTextField : PdfFormField
{
    internal PdfTextField(PdfAcroForm form, PdfDictionary d, string name, List<PdfWidget> w) : base(form, d, name, w) { }

    /// <summary>True if the <see cref="FieldFlags.Multiline"/> flag is set.</summary>
    public bool Multiline => Flags.HasFlag(FieldFlags.Multiline);

    /// <summary>True if the <see cref="FieldFlags.Password"/> flag is set.</summary>
    public bool Password => Flags.HasFlag(FieldFlags.Password);

    /// <summary>True if the <see cref="FieldFlags.Comb"/> flag is set.</summary>
    public bool Comb => Flags.HasFlag(FieldFlags.Comb);

    /// <summary>Maximum length (/MaxLen), 0 if unlimited.</summary>
    public int MaxLength
    {
        get => (GetInheritable(PdfName.Get("MaxLen")) as PdfNumber)?.IntValue ?? 0;
        set => Dictionary.Set(PdfName.Get("MaxLen"), value);
    }

    /// <summary>Text alignment (/Q): left, center or right.</summary>
    public TextAlignment Alignment
    {
        get => ((GetInheritable(PdfName.Q) as PdfNumber)?.IntValue ?? Form.Dictionary.GetInt(PdfName.Q)) switch
        {
            1 => TextAlignment.Center,
            2 => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        set => Dictionary.Set(PdfName.Q, value switch { TextAlignment.Center => 1, TextAlignment.Right => 2, _ => 0 });
    }

    /// <summary>
    /// The text value (/V). Setting it truncates the text to <see cref="MaxLength"/> (if set), stores it
    /// (null removes the value) and regenerates the appearance.
    /// </summary>
    public override string? Value
    {
        get => base.Value;
        set
        {
            if (value != null && MaxLength > 0 && value.Length > MaxLength) value = value[..MaxLength];
            Dictionary[PdfName.V] = value == null ? null : new PdfString(value);
            RegenerateAppearance();
        }
    }

    /// <summary>Rebuilds the appearance streams of all widgets from the current value, font, alignment and /MK settings.</summary>
    public override void RegenerateAppearance()
    {
        foreach (var widget in Widgets) AppearanceGenerator.TextField(this, widget);
    }
}

/// <summary>A check box.</summary>
public sealed class PdfCheckBox : PdfFormField
{
    internal PdfCheckBox(PdfAcroForm form, PdfDictionary d, string name, List<PdfWidget> w) : base(form, d, name, w) { }

    /// <summary>The name of the "checked" state (usually "Yes" or "On").</summary>
    public string OnValue => Widgets.SelectMany(w => w.OnStates).FirstOrDefault() ?? "Yes";

    /// <summary>Gets or sets whether the box is checked (sets the value to <see cref="OnValue"/> or "Off").</summary>
    public bool IsChecked
    {
        get => Value is { } v && v != "Off" && v.Length > 0;
        set => Value = value ? OnValue : "Off";
    }

    /// <summary>
    /// The state name: an "on" state such as <see cref="OnValue"/>, or "Off" (null or "" also means "Off").
    /// Setting it updates /V and the /AS of all widgets.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not one of the widgets' "on" states.</exception>
    public override string? Value
    {
        get => base.Value;
        set
        {
            var state = string.IsNullOrEmpty(value) || value == "Off" ? "Off" : value;
            if (state != "Off" && Widgets.All(w => !w.OnStates.Contains(state)) && Widgets.Any(w => w.OnStates.Count > 0))
                throw new ArgumentException($"'{value}' is not a valid state of check box '{FullName}' (valid: {OnValue}, Off).");
            Dictionary[PdfName.V] = PdfName.Get(state);
            foreach (var w in Widgets)
                w.Dictionary[PdfName.AS] = PdfName.Get(w.OnStates.Contains(state) || (w.OnStates.Count == 0 && state != "Off") ? state : "Off");
        }
    }

    /// <summary>Rebuilds the on/off appearances from /MK (symbol, colors, rotation) and the border width.</summary>
    public override void RegenerateAppearance() => AppearanceGenerator.ButtonStates(this, '4');
}

/// <summary>A group of radio buttons; the value is the export name of the selected button.</summary>
public sealed class PdfRadioButtonGroup : PdfFormField
{
    internal PdfRadioButtonGroup(PdfAcroForm form, PdfDictionary d, string name, List<PdfWidget> w) : base(form, d, name, w) { }

    /// <summary>The export values of the buttons.</summary>
    public IReadOnlyList<string> Options => Widgets.SelectMany(w => w.OnStates).Distinct().ToList();

    /// <summary>
    /// The export name of the selected button, or "Off" if none is selected. Setting it (null or "" = "Off")
    /// updates /V and the /AS of all widgets.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not one of the <see cref="Options"/>.</exception>
    public override string? Value
    {
        get => base.Value;
        set
        {
            var state = string.IsNullOrEmpty(value) ? "Off" : value;
            if (state != "Off" && !Options.Contains(state))
                throw new ArgumentException($"'{value}' is not an option of radio group '{FullName}' (valid: {string.Join(", ", Options)}).");
            Dictionary[PdfName.V] = PdfName.Get(state);
            foreach (var w in Widgets) w.Dictionary[PdfName.AS] = PdfName.Get(w.OnStates.Contains(state) ? state : "Off");
        }
    }

    /// <summary>Rebuilds the on/off appearances from /MK (symbol, colors, rotation) and the border width.</summary>
    public override void RegenerateAppearance() => AppearanceGenerator.ButtonStates(this, 'l');
}

/// <summary>A push button (no value).</summary>
public sealed class PdfPushButton : PdfFormField
{
    internal PdfPushButton(PdfAcroForm form, PdfDictionary d, string name, List<PdfWidget> w) : base(form, d, name, w) { }

    /// <summary>The caption (/MK /CA) of the first widget.</summary>
    public string? Caption => Widgets.FirstOrDefault()?.Dictionary.GetDictionary(PdfName.MK)?.GetText(PdfName.Get("CA"));

    /// <summary>Replaces the icon and/or caption of all widgets (icon only if <paramref name="caption"/> is null).</summary>
    public void SetAppearance(Images.PdfImage? icon, string? caption = null)
    {
        foreach (var widget in Widgets) Form.SetPushButtonAppearance(widget.Dictionary, icon, caption);
    }

    /// <summary>Replaces the button image (icon only), e.g. with a signature image.</summary>
    public void SetImage(Images.PdfImage image) => SetAppearance(image);
}

/// <summary>A combo box or list box.</summary>
public sealed class PdfChoiceField : PdfFormField
{
    internal PdfChoiceField(PdfAcroForm form, PdfDictionary d, string name, List<PdfWidget> w) : base(form, d, name, w) { }

    /// <summary>True for a combo box (<see cref="FieldFlags.Combo"/>), false for a list box.</summary>
    public bool IsComboBox => Flags.HasFlag(FieldFlags.Combo);

    /// <summary>True if values outside the option list are allowed (<see cref="FieldFlags.Edit"/>).</summary>
    public bool IsEditable => Flags.HasFlag(FieldFlags.Edit);

    /// <summary>True if several options may be selected (<see cref="FieldFlags.MultiSelect"/>).</summary>
    public bool IsMultiSelect => Flags.HasFlag(FieldFlags.MultiSelect);

    /// <summary>The options as (export value, display text) pairs.</summary>
    public IReadOnlyList<(string Export, string Display)> Options
    {
        get
        {
            var list = new List<(string, string)>();
            if (GetInheritable(PdfName.Opt) is not PdfArray opt) return list;
            foreach (var item in opt.Select(o => o.Resolve()))
            {
                if (item is PdfString s) list.Add((s.Text, s.Text));
                else if (item is PdfArray pair && pair.Count >= 2)
                    list.Add((pair.GetString(0)?.Text ?? "", pair.GetString(1)?.Text ?? ""));
            }
            return list;
        }
    }

    /// <summary>Selected export values.</summary>
    public IReadOnlyList<string> SelectedValues => GetInheritable(PdfName.V) switch
    {
        PdfString s => new[] { s.Text },
        PdfArray a => a.Select(o => o.Resolve()).OfType<PdfString>().Select(s => s.Text).ToList(),
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// The selected value; several selected values are returned comma-separated. Setting it selects a single option
    /// (export value or display text, see <see cref="SetSelection"/>); null clears the selection.
    /// </summary>
    public override string? Value
    {
        get => base.Value;
        set => SetSelection(value == null ? Array.Empty<string>() : new[] { value });
    }

    /// <summary>Selects one or more options (export values or display texts).</summary>
    public void SetSelection(IEnumerable<string> values)
    {
        var options = Options;
        var selected = new List<string>();
        var indexes = new PdfArray();
        foreach (var v in values)
        {
            int i = options.ToList().FindIndex(o => o.Export == v || o.Display == v);
            if (i < 0 && !IsEditable) throw new ArgumentException($"'{v}' is not an option of '{FullName}'.");
            selected.Add(i >= 0 ? options[i].Export : v);
            if (i >= 0) indexes.Add(new PdfNumber(i));
        }
        Dictionary[PdfName.V] = selected.Count switch
        {
            0 => null,
            1 => new PdfString(selected[0]),
            _ => new PdfArray(selected.Select(s => (PdfObject)new PdfString(s))),
        };
        Dictionary[PdfName.Get("I")] = indexes.Count > 0 ? indexes : null;
        RegenerateAppearance();
    }

    /// <summary>Rebuilds the appearance streams of all widgets from the current selection, font and /MK settings.</summary>
    public override void RegenerateAppearance()
    {
        foreach (var widget in Widgets) AppearanceGenerator.ChoiceField(this, widget);
    }
}

/// <summary>A signature field. The signature dictionary is in /V once signed.</summary>
public sealed class PdfSignatureField : PdfFormField
{
    internal PdfSignatureField(PdfAcroForm form, PdfDictionary d, string name, List<PdfWidget> w) : base(form, d, name, w) { }

    /// <summary>True if the field has a signature dictionary (/V).</summary>
    public bool IsSigned => Dictionary.GetDictionary(PdfName.V) != null;

    /// <summary>The signature dictionary, or null if not signed.</summary>
    public PdfDictionary? SignatureDictionary => Dictionary.GetDictionary(PdfName.V);

    /// <summary>
    /// Fields locked when this field is signed (/Lock, FieldMDP), or null. Defining the lock on an unsigned field lets
    /// the form author decide what a signer locks; <see cref="Signatures.PdfSigner"/> applies it automatically.
    /// </summary>
    public Signatures.PdfFieldLock? Lock
    {
        get => Signatures.PdfFieldLock.FromDictionary(Dictionary.GetDictionary(PdfName.Get("Lock")));
        set
        {
            if (IsSigned) throw new InvalidOperationException($"Signature field '{FullName}' is already signed.");
            Dictionary[PdfName.Get("Lock")] = value == null ? null
                : Form.Document.AddObject(value.ToDictionary(Signatures.PdfFieldLock.SigFieldLock)); // must be indirect
        }
    }

    /// <summary>
    /// The signer name (/Name of the signature dictionary), "(signed)" if signed without a name, or null if unsigned.
    /// Read-only: setting throws <see cref="NotSupportedException"/>; use <see cref="Signatures.PdfSigner"/> to sign.
    /// </summary>
    public override string? Value
    {
        get => IsSigned ? SignatureDictionary!.GetText(PdfName.Name) ?? "(signed)" : null;
        set => throw new NotSupportedException("Use PdfSigner to sign a signature field.");
    }
}
