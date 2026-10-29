using System.Security.Cryptography;
using MiPdf.Objects;

namespace MiPdf;

/// <summary>Relationship of an embedded file to the document (/AFRelationship, PDF/A-3 and PDF 2.0).</summary>
public enum AttachmentRelationship
{
    Unspecified,

    /// <summary>The original source of the document (e.g. a word processing file).</summary>
    Source,

    /// <summary>Data used to derive the visual content (e.g. tables, invoice data).</summary>
    Data,

    /// <summary>An alternative representation of the content (e.g. XML invoice).</summary>
    Alternative,

    /// <summary>Supplemental information.</summary>
    Supplement,
}

/// <summary>A file embedded in the document (attachment).</summary>
public sealed class PdfAttachment
{
    internal PdfAttachment(string name, PdfDictionary fileSpec)
    {
        Name = name;
        FileSpec = fileSpec;
    }

    /// <summary>Key in the EmbeddedFiles name tree.</summary>
    public string Name { get; }

    /// <summary>The file specification dictionary.</summary>
    public PdfDictionary FileSpec { get; }

    /// <summary>File name (/UF or /F).</summary>
    public string FileName => FileSpec.GetText(PdfName.Get("UF")) ?? FileSpec.GetText(PdfName.F) ?? Name;

    public string? Description => FileSpec.GetText(PdfName.Get("Desc"));

    /// <summary>MIME type (/Subtype of the embedded file stream).</summary>
    public string? MimeType => Stream?.GetName(PdfName.Subtype)?.Value;

    public AttachmentRelationship Relationship =>
        Enum.TryParse<AttachmentRelationship>(FileSpec.GetName(PdfName.Get("AFRelationship"))?.Value, out var r) ? r : AttachmentRelationship.Unspecified;

    public DateTimeOffset? ModificationDate => PdfDate.Parse(Stream?.GetDictionary(PdfName.Get("Params"))?.GetText(PdfName.Get("ModDate")));

    /// <summary>Size in bytes as declared in /Params (may be missing).</summary>
    public long? Size => Stream?.GetDictionary(PdfName.Get("Params"))?.GetNumber(PdfName.Get("Size"))?.LongValue;

    private PdfStream? Stream => FileSpec.GetDictionary(PdfName.Get("EF")) is { } ef
        ? ef.GetStream(PdfName.Get("UF")) ?? ef.GetStream(PdfName.F)
        : null;

    /// <summary>Returns the file content.</summary>
    public byte[] GetData() => Stream?.GetBytes() ?? throw new PdfException($"Attachment '{Name}' has no embedded file stream.");

    public override string ToString() => $"{FileName} ({MimeType}, {Size} bytes)";
}

public sealed partial class PdfDocument
{
    private static readonly PdfName EmbeddedFilesKey = PdfName.Get("EmbeddedFiles");

    /// <summary>The files embedded in the document (EmbeddedFiles name tree).</summary>
    public IReadOnlyList<PdfAttachment> Attachments =>
        NameTree.Read(Catalog.GetDictionary(PdfName.Get("Names"))?.GetDictionary(EmbeddedFilesKey))
            .Where(e => e.Value.Resolve() is PdfDictionary)
            .Select(e => new PdfAttachment(PdfString.DecodeText(e.Key), (PdfDictionary)e.Value.Resolve()))
            .ToList();

    /// <summary>Returns the attachment with the given name, or null.</summary>
    public PdfAttachment? GetAttachment(string name) => Attachments.FirstOrDefault(a => a.Name == name || a.FileName == name);

    /// <summary>
    /// Embeds a file. An existing attachment with the same name is replaced. For PDF/A-3 the file is also listed in the
    /// catalog's /AF array (associated file) with the given relationship.
    /// </summary>
    /// <param name="fileName">File name shown in viewers, e.g. "invoice.xml".</param>
    /// <param name="data">File content.</param>
    /// <param name="mimeType">MIME type, e.g. "text/xml" or "application/pdf" (required by PDF/A-3).</param>
    /// <param name="description">Optional description.</param>
    /// <param name="relationship">Relationship to the document (PDF/A-3 /AFRelationship).</param>
    /// <param name="modificationDate">Modification date of the file (default: now).</param>
    public PdfAttachment AddAttachment(string fileName, byte[] data, string? mimeType = null, string? description = null,
        AttachmentRelationship relationship = AttachmentRelationship.Unspecified, DateTimeOffset? modificationDate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(data);
        RemoveAttachment(fileName);

        var stream = new PdfStream(data, compress: true);
        stream[PdfName.Type] = PdfName.Get("EmbeddedFile");
        stream[PdfName.Subtype] = PdfName.Get(mimeType ?? "application/octet-stream");
        var parameters = new PdfDictionary();
        parameters.Set(PdfName.Get("Size"), data.Length);
        var date = new PdfString(PdfDate.Format(modificationDate ?? DateTimeOffset.Now));
        parameters[PdfName.Get("ModDate")] = date;
        parameters[PdfName.Get("CreationDate")] = date.Clone();
        parameters[PdfName.Get("CheckSum")] = new PdfString(MD5.HashData(data), hex: true);
        stream[PdfName.Get("Params")] = parameters;
        var streamRef = AddObject(stream);

        var spec = new PdfDictionary(PdfName.Get("Filespec"));
        spec[PdfName.F] = new PdfString(System.Text.Encoding.ASCII.GetBytes(AsciiFileName(fileName)));
        spec.SetText(PdfName.Get("UF"), fileName);
        if (description != null) spec.SetText(PdfName.Get("Desc"), description);
        spec[PdfName.Get("AFRelationship")] = PdfName.Get(relationship.ToString());
        spec[PdfName.Get("EF")] = new PdfDictionary { [PdfName.F] = streamRef, [PdfName.Get("UF")] = streamRef };
        var specRef = AddObject(spec);

        var entries = NameTree.Read(EmbeddedFilesTree()).ToList();
        entries.Add((PdfString.EncodeText(fileName), specRef));
        SetEmbeddedFilesTree(entries);

        var af = Catalog.GetArray(PdfName.Get("AF"));
        if (af == null) Catalog[PdfName.Get("AF")] = af = new PdfArray();
        af.Add(specRef);
        return new PdfAttachment(fileName, spec);
    }

    /// <summary>Removes an attachment (and its /AF entry); returns false if it does not exist.</summary>
    public bool RemoveAttachment(string name)
    {
        var entries = NameTree.Read(EmbeddedFilesTree());
        var match = entries.FindIndex(e => PdfString.DecodeText(e.Key) == name);
        if (match < 0) return false;
        var spec = entries[match].Value.Resolve();
        entries.RemoveAt(match);
        SetEmbeddedFilesTree(entries);
        if (Catalog.GetArray(PdfName.Get("AF")) is { } af)
            for (int i = af.Count - 1; i >= 0; i--)
                if (ReferenceEquals(af[i].Resolve(), spec)) af.RemoveAt(i);
        return true;
    }

    private PdfDictionary? EmbeddedFilesTree() => Catalog.GetDictionary(PdfName.Get("Names"))?.GetDictionary(EmbeddedFilesKey);

    private void SetEmbeddedFilesTree(List<(byte[] Key, PdfObject Value)> entries)
    {
        var names = Catalog.GetDictionary(PdfName.Get("Names"));
        if (names == null) Catalog[PdfName.Get("Names")] = names = new PdfDictionary();
        if (entries.Count == 0) names.Remove(EmbeddedFilesKey);
        else names[EmbeddedFilesKey] = NameTree.Write(entries);
    }

    private static string AsciiFileName(string name)
    {
        var chars = name.Select(c => c is >= ' ' and < (char)127 ? c : '_').ToArray();
        return new string(chars);
    }
}
