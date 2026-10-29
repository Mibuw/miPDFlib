using MiPdf.Fonts;
using MiPdf.Graphics;
using MiPdf.Objects;

namespace MiPdf;

/// <summary>A read/write view of an annotation dictionary.</summary>
public sealed class PdfAnnotation
{
    internal PdfAnnotation(PdfDictionary dictionary, PdfReference? reference)
    {
        Dictionary = dictionary;
        Reference = reference;
    }

    public PdfDictionary Dictionary { get; }

    public PdfReference? Reference { get; }

    /// <summary>Annotation type, e.g. "Link", "Text", "Widget", "FreeText".</summary>
    public string Subtype => Dictionary.GetName(PdfName.Subtype)?.Value ?? "";

    public PdfRectangle Rect
    {
        get => Dictionary.GetRectangle(PdfName.Rect) ?? default;
        set => Dictionary[PdfName.Rect] = PdfArray.FromRectangle(value);
    }

    /// <summary>Text of the annotation (/Contents).</summary>
    public string? Contents
    {
        get => Dictionary.GetText(PdfName.Contents);
        set => Dictionary.SetText(PdfName.Contents, value);
    }

    /// <summary>Author (/T).</summary>
    public string? Author
    {
        get => Dictionary.GetText(PdfName.T);
        set => Dictionary.SetText(PdfName.T, value);
    }

    /// <summary>Color (/C) as RGB, gray or CMYK components.</summary>
    public double[]? Color => Dictionary.GetArray(PdfName.C)?.Select(o => (o.Resolve() as PdfNumber)?.Value ?? 0).ToArray();

    /// <summary>Last modification date (/M).</summary>
    public DateTimeOffset? ModificationDate => PdfDate.Parse(Dictionary.GetText(PdfName.M));

    /// <summary>Annotation flags (/F): 1 invisible, 2 hidden, 4 print, 8 no zoom, 16 no rotate, 32 no view, 64 read-only, 128 locked.</summary>
    public int Flags
    {
        get => Dictionary.GetInt(PdfName.F);
        set => Dictionary.Set(PdfName.F, value);
    }

    /// <summary>Images in the annotation's appearance (e.g. the image of a visible signature), with page coordinates.</summary>
    public IReadOnlyList<Images.PdfExtractedImage> ExtractImages() => Images.PdfImageExtractor.Extract(Dictionary);

    /// <summary>The URI of a link annotation with a URI action.</summary>
    public string? Uri => Dictionary.GetDictionary(PdfName.Get("A")) is { } a && a.GetName(PdfName.S)?.Value == "URI"
        ? a.GetText(PdfName.URI)
        : null;
}

public sealed partial class PdfPage
{
    /// <summary>The annotations of the page.</summary>
    public IReadOnlyList<PdfAnnotation> Annotations
    {
        get
        {
            if (Dictionary.Get(PdfName.Annots) is not PdfArray annots) return Array.Empty<PdfAnnotation>();
            var list = new List<PdfAnnotation>();
            foreach (var item in annots)
                if (item.Resolve() is PdfDictionary d) list.Add(new PdfAnnotation(d, item as PdfReference));
            return list;
        }
    }

    /// <summary>Adds an annotation dictionary to the page and returns it.</summary>
    public PdfAnnotation AddAnnotation(PdfDictionary annotation)
    {
        annotation[PdfName.Type] = PdfName.Annot;
        annotation[PdfName.P] = Reference;
        var reference = Document.AddObject(annotation);
        GetOrCreateAnnotations().Add(reference);
        return new PdfAnnotation(annotation, reference);
    }

    /// <summary>Removes an annotation from the page.</summary>
    public bool RemoveAnnotation(PdfAnnotation annotation)
    {
        if (Dictionary.Get(PdfName.Annots) is not PdfArray annots) return false;
        for (int i = 0; i < annots.Count; i++)
        {
            if (ReferenceEquals(annots[i].Resolve(), annotation.Dictionary))
            {
                annots.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Adds a link to a web address.</summary>
    public PdfAnnotation AddLink(PdfRectangle area, string uri)
    {
        var action = new PdfDictionary();
        action[PdfName.S] = PdfName.URI;
        action[PdfName.URI] = new PdfString(System.Text.Encoding.ASCII.GetBytes(uri));
        var link = CreateLink(area);
        link[PdfName.Get("A")] = action;
        return AddAnnotation(link);
    }

    /// <summary>Adds a link to a page of this document (optionally to a vertical position).</summary>
    public PdfAnnotation AddLink(PdfRectangle area, PdfPage target, double? top = null)
    {
        if (!ReferenceEquals(target.Document, Document)) throw new ArgumentException("The target page must belong to the same document.", nameof(target));
        var link = CreateLink(area);
        link[PdfName.Dest] = PdfOutline.CreateDestination(target, top);
        return AddAnnotation(link);
    }

    private static PdfDictionary CreateLink(PdfRectangle area)
    {
        var link = new PdfDictionary();
        link[PdfName.Subtype] = PdfName.Link;
        link[PdfName.Rect] = PdfArray.FromRectangle(area);
        link[PdfName.Border] = PdfArray.FromNumbers(0, 0, 0);
        link.Set(PdfName.F, 4); // print
        return link;
    }

    /// <summary>Adds a sticky note (text annotation) at the given position.</summary>
    public PdfAnnotation AddTextNote(double x, double y, string text, string? author = null, PdfColor? color = null)
    {
        var note = new PdfDictionary();
        note[PdfName.Subtype] = PdfName.Get("Text");
        note[PdfName.Rect] = PdfArray.FromNumbers(x, y - 20, x + 20, y);
        note.SetText(PdfName.Contents, text);
        if (author != null) note.SetText(PdfName.T, author);
        note[PdfName.M] = new PdfString(PdfDate.Format(DateTimeOffset.Now));
        var c = color ?? PdfColor.Rgb(1, 0.85, 0);
        note[PdfName.C] = PdfArray.FromNumbers(c.Components);
        note.Set(PdfName.F, 4 | 8 | 16); // print, no zoom, no rotate
        note[PdfName.Name] = PdfName.Get("Comment");
        // icon appearance (required for PDF/A)
        SetAppearance(note, new PdfRectangle(x, y - 20, x + 20, y), canvas =>
        {
            canvas.DrawRectangle(x + 1, y - 17, 18, 16, PdfColor.Gray(0.25), c, 0.8);
            canvas.MoveTo(x + 5, y - 17).LineTo(x + 4, y - 20).LineTo(x + 9, y - 17).ClosePath().SetFillColor(c).Fill();
            for (int i = 0; i < 3; i++) canvas.DrawLine(x + 4, y - 5 - i * 4, x + 16, y - 5 - i * 4, PdfColor.Gray(0.25), 0.8);
        });
        return AddAnnotation(note);
    }

    /// <summary>
    /// Draws a text watermark centered on the visible page area. The angle is relative to the page as
    /// displayed, so the watermark looks the same on rotated pages.
    /// </summary>
    public void AddTextWatermark(string text, PdfFont? font = null, double fontSize = 60, PdfColor? color = null,
        double opacity = 0.25, double angle = 45, bool behindContent = false)
    {
        font ??= PdfStandardFont.HelveticaBold;
        var box = CropBox;
        double cx = box.Left + box.Width / 2, cy = box.Bottom + box.Height / 2;
        double textWidth = font.GetTextWidth(text, fontSize);
        using var canvas = GetCanvas(behindContent);
        canvas.SaveState().SetOpacity(opacity).SetFillColor(color ?? PdfColor.Gray(0.5));
        canvas.Rotate(angle + Rotation, cx, cy);
        canvas.BeginText().SetFont(font, fontSize)
            .SetTextPosition(cx - textWidth / 2, cy - font.CapHeight * fontSize / 2000)
            .ShowText(text).EndText();
        canvas.RestoreState();
    }
}
