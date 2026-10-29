using MiPdf.Forms;
using MiPdf.Objects;

namespace MiPdf;

public sealed partial class PdfDocument
{
    private PdfAcroForm? _form;

    /// <summary>Returns the interactive form, or null if the document has none.</summary>
    public PdfAcroForm? GetForm()
    {
        if (_form != null && ReferenceEquals(Catalog.GetDictionary(PdfName.AcroForm), _form.Dictionary)) return _form;
        var dict = Catalog.GetDictionary(PdfName.AcroForm);
        _form = dict == null ? null : new PdfAcroForm(this, dict);
        return _form;
    }

    /// <summary>Returns the interactive form, creating an empty one if necessary.</summary>
    public PdfAcroForm GetOrCreateForm()
    {
        if (GetForm() is { } form) return form;
        var dict = new PdfDictionary();
        dict[PdfName.Fields] = new PdfArray();
        dict[PdfName.DA] = new PdfString("/Helv 0 Tf 0 g");
        Catalog[PdfName.AcroForm] = AddObject(dict);
        return GetForm()!;
    }

    /// <summary>Adds the fields of imported pages to this document's form.</summary>
    partial void OnPagesImported(PdfDocument source, ObjectImporter importer, List<(PdfPage Source, PdfReference Target)> pages)
    {
        if (source.GetForm() is not { } sourceForm) return;
        PdfAcroForm? form = null;
        var added = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var (_, target) in pages)
        {
            if (target.Resolve() is not PdfDictionary page || page.Get(PdfName.Annots) is not PdfArray annots) continue;
            foreach (var annot in annots)
            {
                if (annot.Resolve() is not PdfDictionary widget || widget.GetName(PdfName.Subtype) != PdfName.Widget) continue;
                // walk up to the root field
                var root = widget;
                var rootRef = annot as PdfReference;
                for (int depth = 0; depth < 32 && root[PdfName.Parent] is PdfReference parentRef && parentRef.Resolve() is PdfDictionary parent; depth++)
                {
                    root = parent;
                    rootRef = parentRef;
                }
                if (rootRef == null || !added.Add(root)) continue;
                form ??= GetOrCreateForm();
                if (form.Dictionary.GetArray(PdfName.Fields)!.Any(f => ReferenceEquals(f.Resolve(), root))) continue;
                form.AddRootField(rootRef);
            }
        }
        if (form == null) return;
        // default resources and appearance of the source form
        if (sourceForm.Dictionary.GetDictionary(PdfName.DR)?.GetDictionary(PdfName.Font) is { } sourceFonts)
        {
            var dr = form.Dictionary.GetDictionary(PdfName.DR) ?? new PdfDictionary();
            form.Dictionary[PdfName.DR] = dr;
            var fonts = dr.GetDictionary(PdfName.Font) ?? new PdfDictionary();
            dr[PdfName.Font] = fonts;
            foreach (var (name, value) in sourceFonts)
                if (!fonts.ContainsKey(name)) fonts[name] = importer.Import(value);
        }
        if (sourceForm.NeedAppearances) form.NeedAppearances = true;
        if (sourceForm.Dictionary.GetInt(PdfName.SigFlags) is var sf and > 0)
            form.Dictionary.Set(PdfName.SigFlags, form.Dictionary.GetInt(PdfName.SigFlags) | sf);
        form.Invalidate();
    }
}
