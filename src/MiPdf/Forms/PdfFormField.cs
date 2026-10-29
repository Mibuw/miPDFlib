using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf.Forms;

/// <summary>Field flags (ISO 32000-2, 12.7.4).</summary>
[Flags]
public enum FieldFlags
{
    None = 0,
    ReadOnly = 1 << 0,
    Required = 1 << 1,
    NoExport = 1 << 2,
    Multiline = 1 << 12,
    Password = 1 << 13,
    NoToggleToOff = 1 << 14,
    Radio = 1 << 15,
    PushButton = 1 << 16,
    Combo = 1 << 17,
    Edit = 1 << 18,
    Sort = 1 << 19,
    FileSelect = 1 << 20,
    MultiSelect = 1 << 21,
    DoNotSpellCheck = 1 << 22,
    DoNotScroll = 1 << 23,
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

    public PdfDictionary Dictionary { get; }

    /// <summary>The page the widget is placed on (null if it is not referenced by any page).</summary>
    public PdfPage? Page { get; internal set; }

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

    public bool IsHidden => (Dictionary.GetInt(PdfName.F) & 2) != 0;

    /// <summary>Images in the widget's appearance (e.g. a signature image), with page coordinates.</summary>
    public IReadOnlyList<Images.PdfExtractedImage> ExtractImages() => Images.PdfImageExtractor.Extract(Dictionary);
}

/// <summary>Symbol shown in a checked check box.</summary>
public enum CheckBoxStyle
{
    Check,
    Cross,
    Circle,
    Square,
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
    }

    public PdfAcroForm Form { get; }

    /// <summary>The terminal field dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Fully qualified name, e.g. "address.street".</summary>
    public string FullName { get; }

    public string PartialName => Dictionary.GetText(PdfName.T) ?? "";

    public IReadOnlyList<PdfWidget> Widgets { get; }

    /// <summary>Description shown as tooltip and read by screen readers (/TU, required by PDF/UA).</summary>
    public string? ToolTip
    {
        get => Dictionary.GetText(PdfName.Get("TU"));
        set => Dictionary.SetText(PdfName.Get("TU"), value);
    }

    /// <summary>"Tx", "Btn", "Ch" or "Sig".</summary>
    public string FieldType => GetInheritable(PdfName.FT) is PdfName n ? n.Value : "";

    public FieldFlags Flags
    {
        get => (FieldFlags)((GetInheritable(PdfName.Ff) as PdfNumber)?.IntValue ?? 0);
        set => Dictionary.Set(PdfName.Ff, (int)value);
    }

    public bool ReadOnly
    {
        get => Flags.HasFlag(FieldFlags.ReadOnly);
        set => Flags = value ? Flags | FieldFlags.ReadOnly : Flags & ~FieldFlags.ReadOnly;
    }

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

    public override string ToString() => $"{GetType().Name} '{FullName}' = {Value}";
}

/// <summary>A text field.</summary>
public sealed class PdfTextField : PdfFormField
{
    internal PdfTextField(PdfAcroForm form, PdfDictionary d, string name, List<PdfWidget> w) : base(form, d, name, w) { }

    public bool Multiline => Flags.HasFlag(FieldFlags.Multiline);

    public bool Password => Flags.HasFlag(FieldFlags.Password);

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

    public bool IsChecked
    {
        get => Value is { } v && v != "Off" && v.Length > 0;
        set => Value = value ? OnValue : "Off";
    }

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
}

/// <summary>A group of radio buttons; the value is the export name of the selected button.</summary>
public sealed class PdfRadioButtonGroup : PdfFormField
{
    internal PdfRadioButtonGroup(PdfAcroForm form, PdfDictionary d, string name, List<PdfWidget> w) : base(form, d, name, w) { }

    /// <summary>The export values of the buttons.</summary>
    public IReadOnlyList<string> Options => Widgets.SelectMany(w => w.OnStates).Distinct().ToList();

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

    public bool IsComboBox => Flags.HasFlag(FieldFlags.Combo);

    public bool IsEditable => Flags.HasFlag(FieldFlags.Edit);

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

    public override void RegenerateAppearance()
    {
        foreach (var widget in Widgets) AppearanceGenerator.ChoiceField(this, widget);
    }
}

/// <summary>A signature field. The signature dictionary is in /V once signed.</summary>
public sealed class PdfSignatureField : PdfFormField
{
    internal PdfSignatureField(PdfAcroForm form, PdfDictionary d, string name, List<PdfWidget> w) : base(form, d, name, w) { }

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

    public override string? Value
    {
        get => IsSigned ? SignatureDictionary!.GetText(PdfName.Name) ?? "(signed)" : null;
        set => throw new NotSupportedException("Use PdfSigner to sign a signature field.");
    }
}
