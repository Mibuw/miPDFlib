using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MiPdf.Content;
using MiPdf.Objects;

namespace MiPdf.PdfA;

/// <summary>PDF/A conformance levels (ISO 19005).</summary>
public enum PdfAConformance
{
    /// <summary>PDF/A-1b (ISO 19005-1, PDF 1.4, no transparency).</summary>
    PdfA1B,

    /// <summary>PDF/A-2b (ISO 19005-2, PDF 1.7).</summary>
    PdfA2B,

    /// <summary>PDF/A-2u: like 2b, all text must be mappable to Unicode.</summary>
    PdfA2U,

    /// <summary>PDF/A-3b: like 2b, arbitrary embedded files allowed.</summary>
    PdfA3B,

    /// <summary>PDF/A-3u.</summary>
    PdfA3U,

    /// <summary>PDF/A-4 (ISO 19005-4, PDF 2.0): embedded files must be PDF/A files.</summary>
    PdfA4,

    /// <summary>PDF/A-4f: like PDF/A-4, arbitrary embedded files allowed (e.g. Factur-X/ZUGFeRD invoice data).</summary>
    PdfA4F,
}

/// <summary>PDF/A identification read from a document (part 1-4, conformance level "A", "B", "U", "E", "F" or "").</summary>
public sealed record PdfAIdentification(int Part, string Conformance)
{
    /// <summary>
    /// The matching output level of miPDFlib, or null if there is none (e.g. level A, which requires a complete
    /// logical structure, or PDF/A-4e). Use it to keep the conformance when modifying a document.
    /// </summary>
    public PdfAConformance? ToConformance() => (Part, Conformance) switch
    {
        (1, "B") => PdfAConformance.PdfA1B,
        (2, "B") => PdfAConformance.PdfA2B,
        (2, "U") => PdfAConformance.PdfA2U,
        (3, "B") => PdfAConformance.PdfA3B,
        (3, "U") => PdfAConformance.PdfA3U,
        (4, "") => PdfAConformance.PdfA4,
        (4, "F") => PdfAConformance.PdfA4F,
        _ => null,
    };

    /// <summary>Returns the usual notation, e.g. "PDF/A-3b" (just "PDF/A-4" if the conformance level is empty).</summary>
    public override string ToString() => $"PDF/A-{Part}{Conformance.ToLowerInvariant()}";
}

internal static class PdfAExtensions
{
    public static int Part(this PdfAConformance c) => c switch
    {
        PdfAConformance.PdfA1B => 1,
        PdfAConformance.PdfA2B or PdfAConformance.PdfA2U => 2,
        PdfAConformance.PdfA3B or PdfAConformance.PdfA3U => 3,
        _ => 4,
    };

    /// <summary>The pdfaid:conformance value; PDF/A-4 has none (or "F", "E").</summary>
    public static string Level(this PdfAConformance c) => c switch
    {
        PdfAConformance.PdfA2U or PdfAConformance.PdfA3U => "U",
        PdfAConformance.PdfA4 => "",
        PdfAConformance.PdfA4F => "F",
        _ => "B",
    };

    /// <summary>True if the level requires all text to be mappable to Unicode (2u, 3u and PDF/A-4).</summary>
    public static bool RequiresUnicode(this PdfAConformance c) => c.Level() == "U" || c.Part() == 4;

    /// <summary>True if arbitrary embedded files are allowed (PDF/A-3 and PDF/A-4f).</summary>
    public static bool AllowsAnyAttachment(this PdfAConformance c) => c.Part() == 3 || c == PdfAConformance.PdfA4F;
}

/// <summary>A property of an <see cref="XmpExtensionSchema"/>.</summary>
/// <param name="Name">Property name (local name).</param>
/// <param name="Value">Property value.</param>
/// <param name="ValueType">XMP value type for the PDF/A schema description ("Text", "Integer", ...).</param>
/// <param name="Category">"external" (user visible) or "internal".</param>
/// <param name="Description">Description for the PDF/A schema description.</param>
public sealed record XmpProperty(string Name, string Value, string ValueType = "Text", string Category = "external", string Description = "");

/// <summary>
/// Custom XMP properties (e.g. Factur-X/ZUGFeRD). For PDF/A documents the schema is also declared in the
/// pdfaExtension block as required by ISO 19005.
/// </summary>
public sealed record XmpExtensionSchema(string Prefix, string NamespaceUri, string SchemaName, IReadOnlyList<XmpProperty> Properties);

/// <summary>Creates XMP metadata packets from the document information.</summary>
public static class XmpMetadata
{
    private static XmpExtensionSchema PdfUASchema(PdfUAConformance ua)
    {
        var part = new XmpProperty("part", ua == PdfUAConformance.PdfUA2 ? "2" : "1", "Integer", "internal", "Indicates, which part of ISO 14289 standard is followed");
        var properties = ua == PdfUAConformance.PdfUA2
            ? new[] { part, new XmpProperty("rev", "2024", "Integer", "internal", "Indicates the year of the ISO 14289 part that is followed") }
            : new[] { part };
        return new("pdfuaid", "http://www.aiim.org/pdfua/ns/id/", "PDF/UA Universal Accessibility Schema", properties);
    }

    /// <summary>Builds an XMP packet with Dublin Core, XMP, PDF and (optionally) PDF/A identification properties.</summary>
    public static byte[] Create(PdfDocumentInfo info, PdfAConformance? conformance) => Create(info, conformance, null, null);

    /// <summary>Builds an XMP packet with Dublin Core, XMP, PDF and optional PDF/A and PDF/UA identification.</summary>
    public static byte[] Create(PdfDocumentInfo info, PdfAConformance? conformance, PdfUAConformance? ua, string? language) =>
        Create(info, conformance, ua, language, Array.Empty<XmpExtensionSchema>());

    /// <summary>Builds an XMP packet including custom extension schemas.</summary>
    public static byte[] Create(PdfDocumentInfo info, PdfAConformance? conformance, PdfUAConformance? ua, string? language,
        IEnumerable<XmpExtensionSchema> extensions)
    {
        static string E(string s) => SecurityElement.Escape(s);
        static string D(DateTimeOffset d) => d.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        var schemas = extensions.ToList();
        if (ua is { } uaConformance) schemas.Insert(0, PdfUASchema(uaConformance));

        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" ")
          .Append("xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n");
        if (conformance is { } c)
        {
            sb.Append($"<pdfaid:part>{c.Part()}</pdfaid:part>\n");
            if (c.Part() == 4) sb.Append("<pdfaid:rev>2020</pdfaid:rev>\n");
            if (c.Level().Length > 0) sb.Append($"<pdfaid:conformance>{c.Level()}</pdfaid:conformance>\n");
        }
        sb.Append("<dc:format>application/pdf</dc:format>\n");
        if (language != null) sb.Append($"<dc:language><rdf:Bag><rdf:li>{E(language)}</rdf:li></rdf:Bag></dc:language>\n");
        if (info.Title is { } title) sb.Append($"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{E(title)}</rdf:li></rdf:Alt></dc:title>\n");
        if (info.Author is { } author) sb.Append($"<dc:creator><rdf:Seq><rdf:li>{E(author)}</rdf:li></rdf:Seq></dc:creator>\n");
        if (info.Subject is { } subject) sb.Append($"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{E(subject)}</rdf:li></rdf:Alt></dc:description>\n");
        if (info.Keywords is { } keywords) sb.Append($"<pdf:Keywords>{E(keywords)}</pdf:Keywords>\n");
        if (info.Producer is { } producer) sb.Append($"<pdf:Producer>{E(producer)}</pdf:Producer>\n");
        if (info.Creator is { } creator) sb.Append($"<xmp:CreatorTool>{E(creator)}</xmp:CreatorTool>\n");
        if (info.CreationDate is { } created) sb.Append($"<xmp:CreateDate>{D(created)}</xmp:CreateDate>\n");
        if (info.ModificationDate is { } modified)
            sb.Append($"<xmp:ModifyDate>{D(modified)}</xmp:ModifyDate>\n<xmp:MetadataDate>{D(modified)}</xmp:MetadataDate>\n");
        sb.Append("</rdf:Description>\n");

        foreach (var schema in schemas)
        {
            sb.Append($"<rdf:Description rdf:about=\"\" xmlns:{schema.Prefix}=\"{E(schema.NamespaceUri)}\">\n");
            foreach (var p in schema.Properties) sb.Append($"<{schema.Prefix}:{p.Name}>{E(p.Value)}</{schema.Prefix}:{p.Name}>\n");
            sb.Append("</rdf:Description>\n");
        }

        if (conformance is { } described && described.Part() < 4 && schemas.Count > 0)
        {
            // PDF/A-1 to -3 require a schema description for properties outside their predefined schemas
            sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" ")
              .Append("xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">\n")
              .Append("<pdfaExtension:schemas><rdf:Bag>\n");
            foreach (var schema in schemas)
            {
                sb.Append("<rdf:li rdf:parseType=\"Resource\">\n")
                  .Append($"<pdfaSchema:schema>{E(schema.SchemaName)}</pdfaSchema:schema>\n")
                  .Append($"<pdfaSchema:namespaceURI>{E(schema.NamespaceUri)}</pdfaSchema:namespaceURI>\n")
                  .Append($"<pdfaSchema:prefix>{schema.Prefix}</pdfaSchema:prefix>\n")
                  .Append("<pdfaSchema:property><rdf:Seq>\n");
                foreach (var p in schema.Properties)
                {
                    sb.Append("<rdf:li rdf:parseType=\"Resource\">\n")
                      .Append($"<pdfaProperty:name>{p.Name}</pdfaProperty:name>\n")
                      .Append($"<pdfaProperty:valueType>{p.ValueType}</pdfaProperty:valueType>\n")
                      .Append($"<pdfaProperty:category>{p.Category}</pdfaProperty:category>\n")
                      .Append($"<pdfaProperty:description>{E(p.Description)}</pdfaProperty:description>\n")
                      .Append("</rdf:li>\n");
                }
                sb.Append("</rdf:Seq></pdfaSchema:property>\n</rdf:li>\n");
            }
            sb.Append("</rdf:Bag></pdfaExtension:schemas>\n</rdf:Description>\n");
        }
        sb.Append("</rdf:RDF>\n</x:xmpmeta>\n");
        sb.Append(new string(' ', 2000)).Append("\n<?xpacket end=\"w\"?>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private static XDocument? Parse(byte[] xmp)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new MemoryStream(xmp), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the document information properties (title, author, subject, keywords, creator, producer, dates) from XMP
    /// metadata as a document information dictionary; null if the packet cannot be parsed or has none of them.
    /// </summary>
    internal static PdfDictionary? ReadInfo(byte[] xmp)
    {
        if (Parse(xmp) is not { } document) return null;
        var descriptions = document.Descendants(Rdf + "Description").ToList();
        string? Value(XNamespace ns, string name)
        {
            if (descriptions.Attributes(ns + name).FirstOrDefault() is { } attribute) return attribute.Value;
            if (document.Descendants(ns + name).FirstOrDefault() is not { } element) return null;
            var items = element.Descendants(Rdf + "li").ToList();
            if (items.Count == 0) return element.Value.Trim();
            // language alternatives: the default language; sequences (dc:creator): all entries
            if (element.Element(Rdf + "Alt") != null)
                return (items.FirstOrDefault(li => li.Attribute(XNamespace.Xml + "lang")?.Value == "x-default") ?? items[0]).Value;
            return string.Join(", ", items.Select(li => li.Value));
        }

        var info = new PdfDictionary();
        void Text(PdfName key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) info.SetText(key, value);
        }
        void Date(PdfName key, string? value)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
                info.SetText(key, PdfDate.Format(date));
        }
        Text(PdfName.Title, Value(Dc, "title"));
        Text(PdfName.Author, Value(Dc, "creator"));
        Text(PdfName.Subject, Value(Dc, "description"));
        Text(PdfName.Keywords, Value(Pdf, "Keywords"));
        Text(PdfName.Creator, Value(Xmp, "CreatorTool"));
        Text(PdfName.Producer, Value(Pdf, "Producer"));
        Date(PdfName.CreationDate, Value(Xmp, "CreateDate"));
        Date(PdfName.ModDate, Value(Xmp, "ModifyDate"));
        return info.Count > 0 ? info : null;
    }

    /// <summary>
    /// Updates xmp:ModifyDate, xmp:MetadataDate and/or pdf:Producer in existing XMP metadata from the document
    /// information, keeping everything else; with <paramref name="allProperties"/> also the title, author, subject,
    /// keywords, creator tool and creation date that differ. Returns null if the packet cannot be parsed or nothing
    /// changed (it is then left unchanged).
    /// </summary>
    internal static byte[]? Update(byte[] xmp, PdfDocumentInfo info, bool dates, bool producer, bool allProperties = false)
    {
        if (Parse(xmp) is not { } document) return null;
        var current = allProperties ? ReadInfo(xmp) ?? new PdfDictionary() : null;
        bool changed = false;
        var description = document.Descendants(Rdf + "Description").FirstOrDefault();
        if (description == null) return null;

        void Set(XNamespace ns, string prefix, string name, string value)
        {
            changed = true;
            var element = document.Descendants(ns + name).FirstOrDefault();
            if (element != null && !element.HasElements)
            {
                element.Value = value;
                return;
            }
            var attribute = document.Descendants(Rdf + "Description").Attributes(ns + name).FirstOrDefault();
            if (attribute != null)
            {
                attribute.Value = value;
                return;
            }
            if (element != null) return; // unexpected structured value: leave it alone
            if (description.GetPrefixOfNamespace(ns) == null) description.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
            description.Add(new XElement(ns + name, value));
        }

        if (dates && info.ModificationDate is { } modified)
        {
            string date = modified.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            Set(Xmp, "xmp", "ModifyDate", date);
            Set(Xmp, "xmp", "MetadataDate", date);
        }
        if (producer && info.Producer is { } text) Set(Pdf, "pdf", "Producer", text);

        // language alternatives (rdf:Alt, the x-default entry) and ordered lists (rdf:Seq, replaced by one entry)
        void SetList(XNamespace ns, string prefix, string name, string container, string value)
        {
            changed = true;
            foreach (var attribute in document.Descendants(Rdf + "Description").Attributes(ns + name).ToList()) attribute.Remove();
            var element = document.Descendants(ns + name).FirstOrDefault();
            if (element == null)
            {
                if (description.GetPrefixOfNamespace(ns) == null) description.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
                description.Add(element = new XElement(ns + name));
            }
            var list = element.Element(Rdf + container);
            if (list == null)
            {
                element.RemoveNodes();
                element.Add(list = new XElement(Rdf + container));
            }
            if (container == "Alt")
            {
                var item = list.Elements(Rdf + "li").FirstOrDefault(li => li.Attribute(XNamespace.Xml + "lang")?.Value == "x-default");
                if (item == null) list.AddFirst(item = new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default")));
                item.Value = value;
            }
            else
            {
                list.RemoveNodes();
                list.Add(new XElement(Rdf + "li", value));
            }
        }

        if (current != null)
        {
            bool Changed(PdfName key, string? value) => value != null && current.GetText(key) != value;
            if (Changed(PdfName.Title, info.Title)) SetList(Dc, "dc", "title", "Alt", info.Title!);
            if (Changed(PdfName.Author, info.Author)) SetList(Dc, "dc", "creator", "Seq", info.Author!);
            if (Changed(PdfName.Subject, info.Subject)) SetList(Dc, "dc", "description", "Alt", info.Subject!);
            if (Changed(PdfName.Keywords, info.Keywords)) Set(Pdf, "pdf", "Keywords", info.Keywords!);
            if (Changed(PdfName.Creator, info.Creator)) Set(Xmp, "xmp", "CreatorTool", info.Creator!);
            if (info.CreationDate is { } created && PdfDate.Parse(current.GetText(PdfName.CreationDate)) != created)
                Set(Xmp, "xmp", "CreateDate", created.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        }
        if (!changed) return null;

        using var output = new MemoryStream();
        var writerSettings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = document.Declaration == null };
        using (var writer = XmlWriter.Create(output, writerSettings)) document.Save(writer);
        return output.ToArray();
    }
}

/// <summary>
/// Basic PDF/A checks (fonts embedded, no encryption, annotation appearances, transparency for PDF/A-1,
/// device color spaces, forbidden actions/filters). Not a replacement for a full validator such as veraPDF.
/// </summary>
public static class PdfAChecker
{
    /// <summary>
    /// Checks a document against the basic requirements of the given conformance level and returns a readable
    /// message per issue found (page numbers are 1-based); an empty list means no issue was found by these checks.
    /// </summary>
    public static IReadOnlyList<string> Check(PdfDocument document, PdfAConformance conformance)
    {
        var issues = new List<string>();
        if (document.IsEncrypted) issues.Add("Encryption is not allowed.");
        var visitedFonts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var visitedResources = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        bool cmykIntent = document.Catalog.GetArray(PdfName.Get("OutputIntents"))?.GetDictionary(0)
            ?.GetStream(PdfName.Get("DestOutputProfile"))?.GetInt(PdfName.N) == 4;
        for (int i = 0; i < document.PageCount; i++)
        {
            var page = document.GetPage(i + 1);
            string where = $"page {i + 1}";
            if (page.GetInheritable(PdfName.Resources) is PdfDictionary res)
                CheckResources(res, where, conformance, issues, visitedFonts, visitedResources);
            CheckContent(page.GetContentBytes(), where, cmykIntent, issues);
            foreach (var annot in page.Annotations)
            {
                var subtype = annot.Subtype;
                var rect = annot.Rect;
                bool needsAp = subtype is not ("Popup" or "Link") && rect.Width > 0 && rect.Height > 0;
                if (needsAp && annot.Dictionary.GetDictionary(PdfName.AP)?.ContainsKey(PdfName.N) != true)
                    issues.Add($"{where}: {subtype} annotation without appearance stream.");
                int flags = annot.Dictionary.GetInt(PdfName.F);
                if (subtype != "Popup" && (flags & 4) == 0) issues.Add($"{where}: {subtype} annotation without print flag.");
                if ((flags & (1 | 2 | 32)) != 0) issues.Add($"{where}: {subtype} annotation is invisible, hidden or noview.");
                if (subtype == "FileAttachment" && !conformance.AllowsAnyAttachment())
                    issues.Add($"{where}: file attachment annotations require PDF/A-3 or PDF/A-4f.");
                if (annot.Dictionary.GetDictionary(PdfName.Get("A"))?.GetName(PdfName.S)?.Value is "JavaScript" or "Launch")
                    issues.Add($"{where}: forbidden action in annotation.");
                if (annot.Dictionary.GetDictionary(PdfName.AP)?.Get(PdfName.N) is PdfStream ap && ap.GetDictionary(PdfName.Resources) is { } apRes)
                    CheckResources(apRes, where + " annotation", conformance, issues, visitedFonts, visitedResources);
            }
        }
        var names = document.Catalog.GetDictionary(PdfName.Get("Names"));
        if (names?.ContainsKey(PdfName.Get("JavaScript")) == true) issues.Add("Document-level JavaScript is not allowed.");
        if (conformance.Part() is 2 or 4 && !conformance.AllowsAnyAttachment() && names?.ContainsKey(PdfName.Get("EmbeddedFiles")) == true)
            issues.Add($"PDF/A-{conformance.Part()} allows only embedded PDF/A files (embedded files found; use PDF/A-3 or PDF/A-4f).");
        if (conformance == PdfAConformance.PdfA4F && names?.ContainsKey(PdfName.Get("EmbeddedFiles")) != true)
            issues.Add("PDF/A-4f requires at least one embedded file (use PDF/A-4 for documents without attachments).");
        if (conformance.AllowsAnyAttachment())
        {
            var associated = document.Catalog.GetArray(PdfName.Get("AF"))?.Select(o => o.Resolve()).ToList() ?? new List<PdfObject>();
            foreach (var attachment in document.Attachments)
            {
                if (attachment.FileSpec.GetName(PdfName.Get("AFRelationship")) == null)
                    issues.Add($"Embedded file '{attachment.FileName}' has no /AFRelationship.");
                if (attachment.MimeType == null) issues.Add($"Embedded file '{attachment.FileName}' has no MIME type (/Subtype).");
                if (attachment.ModificationDate == null) issues.Add($"Embedded file '{attachment.FileName}' has no /ModDate.");
                if (!associated.Any(o => ReferenceEquals(o, attachment.FileSpec)))
                    issues.Add($"Embedded file '{attachment.FileName}' is not an associated file (/AF).");
            }
        }
        if (document.Catalog.GetStream(PdfName.Metadata) == null) issues.Add("XMP metadata is missing.");
        if (document.Catalog.GetArray(PdfName.Get("OutputIntents")) is not { Count: > 0 }) issues.Add("Output intent is missing.");
        return issues;
    }

    private static void CheckResources(PdfDictionary resources, string where, PdfAConformance conformance, List<string> issues,
        HashSet<PdfDictionary> fonts, HashSet<PdfDictionary> visited)
    {
        if (!visited.Add(resources)) return;
        if (resources.GetDictionary(PdfName.Font) is { } fontDict)
        {
            foreach (var (name, value) in fontDict)
            {
                if (value.Resolve() is not PdfDictionary font || !fonts.Add(font)) continue;
                if (!IsEmbedded(font)) issues.Add($"{where}: font {font.GetName(PdfName.BaseFont)?.Value ?? name.Value} is not embedded.");
                if (conformance.RequiresUnicode() && font.GetName(PdfName.Subtype)?.Value is "Type0" && !font.ContainsKey(PdfName.ToUnicode))
                    issues.Add($"{where}: font {font.GetName(PdfName.BaseFont)?.Value} has no ToUnicode map (required for level U and PDF/A-4).");
            }
        }
        if (conformance.Part() == 1 && resources.GetDictionary(PdfName.ExtGState) is { } gsDict)
        {
            foreach (var (_, value) in gsDict)
            {
                if (value.Resolve() is not PdfDictionary gs) continue;
                if (gs.GetDouble(PdfName.Get("ca"), 1) < 1 || gs.GetDouble(PdfName.Get("CA"), 1) < 1 ||
                    (gs.Get(PdfName.SMask) is { } sm && sm is not PdfName { Value: "None" }))
                    issues.Add($"{where}: transparency is not allowed in PDF/A-1.");
            }
        }
        if (resources.GetDictionary(PdfName.XObject) is { } xobjects)
        {
            foreach (var (_, value) in xobjects)
            {
                if (value.Resolve() is not PdfStream x) continue;
                if (x.GetFilters().Any(f => f == PdfName.LZWDecode)) issues.Add($"{where}: LZW compression is not allowed.");
                if (x.GetName(PdfName.Subtype) == PdfName.Image)
                {
                    if (conformance.Part() == 1 && x.ContainsKey(PdfName.SMask)) issues.Add($"{where}: image soft masks are not allowed in PDF/A-1.");
                    if (x.GetName(PdfName.ColorSpace) == PdfName.DeviceCMYK) issues.Add($"{where}: DeviceCMYK image requires a CMYK output intent or DefaultCMYK.");
                }
                else if (x.GetDictionary(PdfName.Resources) is { } inner)
                    CheckResources(inner, where, conformance, issues, fonts, visited);
            }
        }
    }

    internal static bool IsEmbedded(PdfDictionary font)
    {
        var subtype = font.GetName(PdfName.Subtype)?.Value;
        if (subtype == "Type3") return true;
        if (subtype == "Type0") font = font.GetArray(PdfName.DescendantFonts)?.GetDictionary(0) ?? font;
        var descriptor = font.GetDictionary(PdfName.FontDescriptor);
        return descriptor != null && (descriptor.ContainsKey(PdfName.FontFile) || descriptor.ContainsKey(PdfName.FontFile2) || descriptor.ContainsKey(PdfName.FontFile3));
    }

    private static void CheckContent(byte[] content, string where, bool cmykIntent, List<string> issues)
    {
        if (cmykIntent) return;
        foreach (var op in ContentParser.Parse(content))
        {
            if (op.Operator is "k" or "K")
            {
                issues.Add($"{where}: DeviceCMYK colors require a CMYK output intent.");
                return;
            }
        }
    }
}
