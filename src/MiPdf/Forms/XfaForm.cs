using System.Text;
using System.Xml;
using MiPdf.Objects;

namespace MiPdf.Forms;

/// <summary>
/// Access to the XFA part of a form (/AcroForm /XFA): packets and the form data (datasets). Typically used for
/// "static" XFA forms that also contain AcroForm fields. Rendering of dynamic XFA is not supported.
/// </summary>
public sealed class XfaForm
{
    private const string DataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";
    private readonly PdfAcroForm _form;

    internal XfaForm(PdfAcroForm form) => _form = form;

    private PdfObject? Xfa => _form.Dictionary.Get(PdfName.Get("XFA"));

    /// <summary>True if the form contains XFA.</summary>
    public bool Exists => Xfa != null;

    /// <summary>The XFA packets by name (e.g. "template", "datasets"); a single-stream XFA is returned as "xdp".</summary>
    public IReadOnlyDictionary<string, byte[]> GetPackets()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        switch (Xfa)
        {
            case PdfStream single:
                result["xdp"] = single.GetBytes();
                break;
            case PdfArray array:
                for (int i = 0; i + 1 < array.Count; i += 2)
                    if (array[i].Resolve() is PdfString name && array[i + 1].Resolve() is PdfStream s) result[name.Text] = s.GetBytes();
                break;
        }
        return result;
    }

    /// <summary>Returns the XML of the form data (the content of xfa:data), or null.</summary>
    public string? GetData()
    {
        var datasets = LoadDatasets(out _, out _);
        var data = datasets == null ? null : FindData(datasets);
        return data?.InnerXml;
    }

    /// <summary>
    /// Replaces the form data. <paramref name="xml"/> is the data root element (e.g. the content exported by Adobe:
    /// <c>&lt;form1&gt;...&lt;/form1&gt;</c>), an xfa:data element or a complete xfa:datasets element.
    /// </summary>
    public void FillData(string xml)
    {
        var input = Parse(Encoding.UTF8.GetBytes(xml));
        FillData(input.DocumentElement ?? throw new ArgumentException("Empty XML.", nameof(xml)));
    }

    /// <summary>Replaces the form data with the given XML node (see <see cref="FillData(string)"/>).</summary>
    public void FillData(XmlNode node)
    {
        if (!Exists) throw new PdfException("The form contains no XFA.");
        var datasets = LoadDatasets(out var document, out var packetStream) ?? CreateDatasets(out document, out packetStream);
        var data = FindData(datasets);
        if (data == null)
        {
            data = document.CreateElement("xfa", "data", DataNamespace);
            datasets.PrependChild(data);
        }
        var source = node is XmlDocument doc ? doc.DocumentElement! : node;
        if (source.NamespaceURI == DataNamespace && source.LocalName == "datasets") source = FindData((XmlElement)source) ?? source;
        var nodes = source.NamespaceURI == DataNamespace && source.LocalName == "data"
            ? source.ChildNodes.Cast<XmlNode>().ToList()
            : new List<XmlNode> { source };
        data.RemoveAll();
        foreach (var child in nodes) data.AppendChild(document.ImportNode(child, deep: true));

        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            if (packetStream.Name == "xdp") document.WriteTo(writer);
            else datasets.WriteTo(writer);
        }
        packetStream.Stream.SetBytes(ms.ToArray(), compress: true);
    }

    /// <summary>Removes the XFA, so that viewers use the AcroForm fields (common when the XFA data is not maintained).</summary>
    public void Remove()
    {
        _form.Dictionary.Remove(PdfName.Get("XFA"));
        _form.Document.Catalog.Remove(PdfName.Get("NeedsRendering"));
    }

    private static XmlDocument Parse(byte[] xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), settings);
            document.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new PdfException("Invalid XFA XML: " + ex.Message, ex);
        }
        return document;
    }

    private static XmlElement? FindData(XmlElement datasets) =>
        datasets.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "data" && e.NamespaceURI == DataNamespace);

    private XmlElement? LoadDatasets(out XmlDocument document, out (string Name, PdfStream Stream) packet)
    {
        document = null!;
        packet = default;
        switch (Xfa)
        {
            case PdfStream single:
            {
                document = Parse(single.GetBytes());
                packet = ("xdp", single);
                return document.GetElementsByTagName("datasets", DataNamespace).OfType<XmlElement>().FirstOrDefault();
            }
            case PdfArray array:
                for (int i = 0; i + 1 < array.Count; i += 2)
                {
                    if (array[i].Resolve() is not PdfString { Text: "datasets" } || array[i + 1].Resolve() is not PdfStream stream) continue;
                    document = Parse(stream.GetBytes());
                    packet = ("datasets", stream);
                    return document.DocumentElement;
                }
                return null;
            default:
                return null;
        }
    }

    private XmlElement CreateDatasets(out XmlDocument document, out (string Name, PdfStream Stream) packet)
    {
        document = new XmlDocument { PreserveWhitespace = true };
        var datasets = document.CreateElement("xfa", "datasets", DataNamespace);
        document.AppendChild(datasets);
        if (Xfa is PdfArray array)
        {
            var stream = new PdfStream(Array.Empty<byte>());
            var reference = _form.Document.AddObject(stream);
            // insert before the template packet (or before the postamble)
            int index = array.Count;
            for (int i = 0; i + 1 < array.Count; i += 2)
                if (array[i].Resolve() is PdfString { Text: "template" or "postamble" or "</xdp:xdp>" }) { index = i; break; }
            array.Insert(index, reference);
            array.Insert(index, new PdfString("datasets"));
            packet = ("datasets", stream);
            return datasets;
        }
        throw new PdfException("The XFA stream contains no datasets packet.");
    }
}
