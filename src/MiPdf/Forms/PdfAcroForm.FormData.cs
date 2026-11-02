using System.Text;
using System.Xml;
using System.Xml.Linq;
using MiPdf.IO;
using MiPdf.Objects;

namespace MiPdf.Forms;

/// <summary>Result of importing form data with <see cref="PdfAcroForm.ImportFdf"/> or <see cref="PdfAcroForm.ImportXfdf(string)"/>.</summary>
public sealed class FormDataImportResult
{
    /// <summary>Number of fields whose value was set.</summary>
    public int FieldsSet { get; init; }

    /// <summary>Field names in the data that do not exist in the form (they are skipped).</summary>
    public IReadOnlyList<string> UnknownFields { get; init; } = Array.Empty<string>();

    /// <summary>Values that could not be set, e.g. a value that is not an option of a list box ("name: reason").</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public sealed partial class PdfAcroForm
{
    private const string XfdfNamespace = "http://ns.adobe.com/xfdf/";
    private static readonly string[] CheckedWords = { "yes", "on", "true", "1", "x" };

    // ------------------------------------------------------------------ XFDF (ISO 19444-1)

    /// <summary>
    /// Exports the field values as XFDF (XML Forms Data Format, ISO 19444-1): text fields, check boxes, radio groups,
    /// combo and list boxes (several <c>&lt;value&gt;</c> elements for multiple selections). Fields without a value,
    /// push buttons and signature fields are left out. Hierarchical names ("address.street") become nested fields.
    /// </summary>
    /// <param name="pdfFileName">Optional file name of the PDF the data belongs to (<c>&lt;f href&gt;</c>).</param>
    public string ExportXfdf(string? pdfFileName = null)
    {
        XNamespace ns = XfdfNamespace;
        var fields = new XElement(ns + "fields");
        foreach (var (name, values) in ExportableValues()) Node(fields, ns, name.Split('.')).Add(values.Select(v => new XElement(ns + "value", v)));
        var root = new XElement(ns + "xfdf", new XAttribute(XNamespace.Xml + "space", "preserve"));
        if (pdfFileName != null) root.Add(new XElement(ns + "f", new XAttribute("href", pdfFileName)));
        root.Add(fields);
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(new StringWriterUtf8(sb), new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        return sb.ToString();

        static XElement Node(XElement parent, XNamespace ns, string[] path)
        {
            foreach (var part in path)
            {
                var child = parent.Elements(ns + "field").FirstOrDefault(e => (string?)e.Attribute("name") == part);
                if (child == null) parent.Add(child = new XElement(ns + "field", new XAttribute("name", part)));
                parent = child;
            }
            return parent;
        }
    }

    /// <summary>
    /// Imports field values from XFDF (ISO 19444-1). Nested and flat field names ("address.street") are accepted;
    /// unknown fields are reported in <see cref="FormDataImportResult.UnknownFields"/>. Appearances are regenerated.
    /// DTDs are not processed, so untrusted data can be imported safely. Annotations in the XFDF are ignored.
    /// </summary>
    /// <exception cref="PdfException">The data is not well-formed XML or not XFDF.</exception>
    public FormDataImportResult ImportXfdf(string xfdf)
    {
        ArgumentNullException.ThrowIfNull(xfdf);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xfdf), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new PdfException("The form data is not well-formed XML: " + ex.Message, ex);
        }
        if (document.Root?.Name.LocalName != "xfdf") throw new PdfException("The form data is not XFDF (root element 'xfdf' expected).");

        var data = new List<(string Name, List<string> Values)>();
        void Collect(XElement element, string prefix)
        {
            foreach (var field in element.Elements().Where(e => e.Name.LocalName == "field"))
            {
                string name = prefix + ((string?)field.Attribute("name") ?? "");
                var values = field.Elements().Where(e => e.Name.LocalName == "value").Select(e => e.Value).ToList();
                if (values.Count > 0) data.Add((name, values));
                Collect(field, name + ".");
            }
        }
        foreach (var fields in document.Root.Elements().Where(e => e.Name.LocalName == "fields")) Collect(fields, "");
        return Import(data);
    }

    /// <summary>Imports field values from an XFDF stream (see <see cref="ImportXfdf(string)"/>).</summary>
    public FormDataImportResult ImportXfdf(Stream xfdf)
    {
        ArgumentNullException.ThrowIfNull(xfdf);
        using var reader = new StreamReader(xfdf, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return ImportXfdf(reader.ReadToEnd());
    }

    // ------------------------------------------------------------------ FDF (ISO 32000, 12.7.8)

    /// <summary>
    /// Exports the field values as FDF (Forms Data Format, ISO 32000-1, 12.7.8), the binary counterpart of XFDF that
    /// Acrobat and many form tools read. Same scope as <see cref="ExportXfdf"/>.
    /// </summary>
    /// <param name="pdfFileName">Optional file name of the PDF the data belongs to (/F).</param>
    public byte[] ExportFdf(string? pdfFileName = null)
    {
        var top = new PdfArray();
        var byPath = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
        foreach (var (name, values) in ExportableValues())
        {
            var field = GetFdfNode(top, byPath, name);
            var formField = GetField(name);
            field[PdfName.V] = formField is PdfCheckBox or PdfRadioButtonGroup ? PdfName.Get(values[0])
                : values.Count == 1 ? new PdfString(values[0])
                : new PdfArray(values.Select(v => (PdfObject)new PdfString(v)));
        }
        var fdf = new PdfDictionary();
        fdf[PdfName.Get("Fields")] = top;
        if (pdfFileName != null) fdf.SetText(PdfName.F, pdfFileName);
        var root = new PdfDictionary();
        root[PdfName.Get("FDF")] = fdf;

        using var ms = new MemoryStream();
        var output = new PdfOutput(ms);
        output.Write("%FDF-1.2\n%").Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Write("\n");
        new PdfObjectSerializer().WriteIndirect(output, 1, 0, root);
        output.Write("trailer\n<< /Root 1 0 R >>\n%%EOF\n");
        return ms.ToArray();

        static PdfDictionary GetFdfNode(PdfArray top, Dictionary<string, PdfDictionary> byPath, string fullName)
        {
            PdfDictionary? parent = null;
            string path = "";
            foreach (var part in fullName.Split('.'))
            {
                path = path.Length == 0 ? part : path + "." + part;
                if (!byPath.TryGetValue(path, out var node))
                {
                    node = new PdfDictionary();
                    node.SetText(PdfName.T, part);
                    if (parent == null) top.Add(node);
                    else
                    {
                        var kids = parent.GetArray(PdfName.Kids);
                        if (kids == null) parent[PdfName.Kids] = kids = new PdfArray();
                        kids.Add(node);
                    }
                    byPath[path] = node;
                }
                parent = node;
            }
            return parent!;
        }
    }

    /// <summary>
    /// Imports ("merges") field values from FDF data (ISO 32000-1, 12.7.8) into this form: hierarchical fields (/Kids)
    /// and flat names are supported; unknown fields are reported in <see cref="FormDataImportResult.UnknownFields"/>.
    /// Appearances are regenerated. Annotations, embedded files and JavaScript in the FDF are ignored.
    /// </summary>
    /// <exception cref="PdfException">The data is not FDF or cannot be parsed.</exception>
    public FormDataImportResult ImportFdf(byte[] fdf)
    {
        ArgumentNullException.ThrowIfNull(fdf);
        if (fdf.Length < 5 || Encoding.ASCII.GetString(fdf, 0, 5) != "%FDF-") throw new PdfException("The form data is not FDF (header %FDF- expected).");
        var file = FdfFile.Parse(fdf);
        var data = new List<(string Name, List<string> Values)>();
        void Collect(PdfArray? fields, string prefix, int depth)
        {
            if (fields == null || depth > 32) return;
            foreach (var field in fields.Select(o => o.Resolve()).OfType<PdfDictionary>())
            {
                string? partial = field.GetText(PdfName.T);
                string name = partial == null ? prefix.TrimEnd('.') : prefix + partial;
                var values = field.Get(PdfName.V)?.Resolve() switch
                {
                    PdfString s => new List<string> { s.Text },
                    PdfName n => new List<string> { n.Value },
                    PdfArray a => a.Select(o => o.Resolve()).Select(o => o is PdfString s ? s.Text : o is PdfName n ? n.Value : null).OfType<string>().ToList(),
                    _ => null,
                };
                if (values != null && name.Length > 0) data.Add((name, values));
                Collect(field.GetArray(PdfName.Kids), name.Length > 0 ? name + "." : "", depth + 1);
            }
        }
        Collect(file.Fields, "", 0);
        return Import(data);
    }

    // ------------------------------------------------------------------ shared

    /// <summary>Field values to export: name → values (in field order, without buttons, signatures and empty fields).</summary>
    private IEnumerable<(string Name, List<string> Values)> ExportableValues()
    {
        foreach (var field in Fields.GroupBy(f => f.FullName, StringComparer.Ordinal).Select(g => g.First()))
        {
            List<string>? values = field switch
            {
                PdfPushButton or PdfSignatureField => null,
                PdfChoiceField choice => choice.SelectedValues.Count > 0 ? choice.SelectedValues.ToList() : null,
                _ => field.Value is { } v ? new List<string> { v } : null,
            };
            if (values != null) yield return (field.FullName, values);
        }
    }

    private FormDataImportResult Import(List<(string Name, List<string> Values)> data)
    {
        int set = 0;
        var unknown = new List<string>();
        var errors = new List<string>();
        foreach (var (name, values) in data)
        {
            var field = GetField(name);
            if (field == null)
            {
                unknown.Add(name);
                continue;
            }
            try
            {
                switch (field)
                {
                    case PdfPushButton or PdfSignatureField:
                        errors.Add($"{name}: {field.GetType().Name} has no value");
                        continue;
                    case PdfChoiceField choice:
                        choice.SetSelection(values);
                        break;
                    case PdfCheckBox box when values[0] != box.OnValue && CheckedWords.Contains(values[0].ToLowerInvariant()):
                        box.IsChecked = true;   // "Yes"/"On"/"1" for a box whose on-state has another name
                        break;
                    default:
                        field.Value = values[0];
                        break;
                }
                set++;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
        return new FormDataImportResult { FieldsSet = set, UnknownFields = unknown, Errors = errors };
    }

    private sealed class StringWriterUtf8(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    /// <summary>Minimal FDF reader: indirect objects and the trailer, resolved in memory.</summary>
    private sealed class FdfFile : IPdfObjectResolver
    {
        private readonly Dictionary<int, PdfObject> _objects = new();

        public PdfArray? Fields { get; private set; }

        public PdfObject? ResolveReference(PdfReference reference) => _objects.GetValueOrDefault(reference.ObjectNumber);

        public static FdfFile Parse(byte[] data)
        {
            var file = new FdfFile();
            try
            {
                var lexer = new PdfLexer(data);
                var parser = new PdfObjectParser(lexer, (n, g) => new PdfReference(n, g, file));
                PdfDictionary? trailer = null;
                while (lexer.Next())
                {
                    if (lexer.IsKeyword("trailer"))
                    {
                        trailer = parser.ParseObject() as PdfDictionary;
                        continue;
                    }
                    if (lexer.Kind != TokenKind.Integer) continue;
                    long number = lexer.IntValue;
                    int save = lexer.Position;
                    if (lexer.Next() && lexer.Kind == TokenKind.Integer && lexer.Next() && lexer.IsKeyword("obj")
                        && number is > 0 and < 1_000_000 && file._objects.Count < 100_000)
                    {
                        if (parser.ParseObject() is { } obj) file._objects[(int)number] = obj;
                    }
                    else lexer.Position = save;
                }
                var root = trailer?.Get(PdfName.Root)?.Resolve() as PdfDictionary
                    ?? file._objects.Values.OfType<PdfDictionary>().FirstOrDefault(d => d.ContainsKey(PdfName.Get("FDF")));
                var fdf = root?.GetDictionary(PdfName.Get("FDF")) ?? throw new PdfException("The FDF data has no /FDF dictionary.");
                file.Fields = fdf.GetArray(PdfName.Get("Fields"));
                return file;
            }
            catch (PdfParseException ex)
            {
                throw new PdfException("The FDF data cannot be parsed: " + ex.Message, ex);
            }
        }
    }
}
