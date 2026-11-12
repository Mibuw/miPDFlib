using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MiPdf.Text;

/// <summary>Options for <see cref="PdfLayoutExport.ToMarkdown(IEnumerable{PageLayout}, MarkdownExportOptions?)"/>.</summary>
public sealed class MarkdownExportOptions
{
    /// <summary>
    /// Include running headers and footers (page numbers, document titles repeated on the pages) and text outside the
    /// flow of the page (a stamp in the margin). Default: false.
    /// </summary>
    public bool IncludeHeadersAndFooters { get; set; }

    /// <summary>
    /// Join words hyphenated at the end of a line ("docu-" + "ment" becomes "document"). A hyphen that belongs to the word
    /// is kept ("self-employed"): the document's own words decide, otherwise a hyphen after a lower-case letter is taken
    /// as a break. Default: true.
    /// </summary>
    public bool JoinHyphenatedWords { get; set; } = true;

    /// <summary>
    /// Join a paragraph that ends without a final punctuation mark with the next one when that begins in lower case – a
    /// paragraph broken by a page or column end. Default: true.
    /// </summary>
    public bool JoinBrokenParagraphs { get; set; } = true;

    /// <summary>A line written between pages (e.g. "---"), or null for none (default).</summary>
    public string? PageSeparator { get; set; }
}

/// <summary>
/// Writes the result of the <see cref="PdfLayoutAnalyzer"/> as Markdown (for language models, search indexes and
/// readers), as hOCR (the HTML format of OCR engines) or as ALTO 4 XML (the format of digitisation projects), with the
/// positions of blocks, lines and words.
/// </summary>
public static class PdfLayoutExport
{
    // ------------------------------------------------------------------ Markdown

    /// <summary>Analyses the document and writes it as Markdown.</summary>
    public static string ToMarkdown(PdfDocument document, MarkdownExportOptions? options = null, TextExtractionOptions? textOptions = null) =>
        ToMarkdown(PdfLayoutAnalyzer.Analyze(document, textOptions), options);

    /// <summary>
    /// Writes analysed pages as Markdown: headings as <c>#</c> to <c>######</c>, paragraphs with their lines joined,
    /// list items as <c>-</c> or with their numbers, tables as pipe tables, captions in italics.
    /// </summary>
    public static string ToMarkdown(IEnumerable<PageLayout> pages, MarkdownExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        options ??= new MarkdownExportOptions();
        var pageList = pages.ToList();
        var vocabulary = Vocabulary(pageList);
        string Join(IReadOnlyList<TextLineInfo> lines, MarkdownExportOptions _) => JoinLines(lines, options, vocabulary);
        var parts = new List<(LayoutBlockKind Kind, string Text)>();
        bool firstPage = true;
        foreach (var page in pageList)
        {
            if (!firstPage && options.PageSeparator != null) parts.Add((LayoutBlockKind.Header, options.PageSeparator));
            firstPage = false;
            foreach (var block in page.Blocks)
            {
                switch (block.Kind)
                {
                    case LayoutBlockKind.Header or LayoutBlockKind.Footer or LayoutBlockKind.Other:
                        if (options.IncludeHeadersAndFooters) parts.Add((block.Kind, Escape(Join(block.Lines, options))));
                        break;
                    case LayoutBlockKind.Heading:
                        parts.Add((block.Kind, new string('#', Math.Clamp(block.Level, 1, 6)) + " " + Escape(Join(block.Lines, options), inline: true)));
                        break;
                    case LayoutBlockKind.ListItem:
                        parts.Add((block.Kind, ListItem(Join(block.Lines, options))));
                        break;
                    case LayoutBlockKind.Table:
                        parts.Add((block.Kind, Table(block)));
                        break;
                    case LayoutBlockKind.Caption:
                        parts.Add((block.Kind, "*" + Escape(Join(block.Lines, options), inline: true) + "*"));
                        break;
                    default:
                    {
                        string text = Join(block.Lines, options);
                        // a paragraph broken by the end of a page or column goes on in the next one
                        if (options.JoinBrokenParagraphs && parts.Count > 0 && parts[^1].Kind == LayoutBlockKind.Paragraph
                            && !Regex.IsMatch(parts[^1].Text, @"[.!?:;)""”»]$") && text.Length > 0 && char.IsLower(text[0]))
                        {
                            parts[^1] = (LayoutBlockKind.Paragraph, JoinLine(parts[^1].Text, Escape(text), options, vocabulary));
                            continue;
                        }
                        parts.Add((LayoutBlockKind.Paragraph, Escape(text)));
                        break;
                    }
                }
            }
        }
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(parts[i].Kind == LayoutBlockKind.ListItem && parts[i - 1].Kind == LayoutBlockKind.ListItem ? "\n" : "\n\n");
            sb.Append(parts[i].Text);
        }
        return sb.Append('\n').ToString();
    }

    /// <summary>The words of the pages (lower case, without punctuation), to tell hyphenated words from compounds.</summary>
    private static HashSet<string> Vocabulary(IEnumerable<PageLayout> pages)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in pages.SelectMany(p => p.Blocks).SelectMany(b => b.Lines))
            foreach (var w in line.Words) words.Add(WordKey(w.Text));
        return words;
    }

    private static readonly char[] Punctuation = ".,;:!?()[]\"'“”‘’«»".ToCharArray();

    private static string WordKey(string word) => word.Trim(Punctuation).ToLowerInvariant();

    /// <summary>The lines of a block as one line of text.</summary>
    private static string JoinLines(IReadOnlyList<TextLineInfo> lines, MarkdownExportOptions options, HashSet<string> vocabulary)
    {
        string text = "";
        foreach (var line in lines) text = text.Length == 0 ? line.Text.Trim() : JoinLine(text, line.Text.Trim(), options, vocabulary);
        return Regex.Replace(text, @"\s+", " ");
    }

    /// <summary>
    /// Joins two lines. For a hyphen at the end of the first: if the document has the word with the hyphen elsewhere
    /// ("target-catalog") it is kept, if it has the word without ("document") the parts are joined; otherwise a hyphen
    /// after a lower-case letter before a lower-case one is taken as a break, any other ("LLM-", "Feature-") as part of
    /// the word. A soft hyphen is always a break.
    /// </summary>
    private static string JoinLine(string text, string next, MarkdownExportOptions options, HashSet<string> vocabulary)
    {
        if (!options.JoinHyphenatedWords || text.Length < 2 || next.Length == 0 || !char.IsLetterOrDigit(text[^2])) return text + " " + next;
        if (text[^1] == '­') return text[..^1] + next;
        if (text[^1] != '-') return text + " " + next;
        string before = text[(text.LastIndexOf(' ') + 1)..^1];
        string after = next.Split(' ', 2)[0];
        if (vocabulary.Contains(WordKey(before + "-" + after))) return text + next;
        if (vocabulary.Contains(WordKey(before + after))) return text[..^1] + next;
        return char.IsLower(text[^2]) && char.IsLower(next[0]) ? text[..^1] + next : text + next;
    }

    private static readonly Regex Bullet = new(@"^[•·▪◦‣∙●○■□–—\-\*]\s*", RegexOptions.CultureInvariant);
    private static readonly Regex Number = new(@"^(\(?(?:\d{1,3}|[a-z]|[ivxlc]{1,5})[.)])\s*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string ListItem(string text)
    {
        var bullet = Bullet.Match(text);
        if (bullet.Success) return "- " + Escape(text[bullet.Length..], inline: true);
        var number = Number.Match(text);
        if (number.Success && Regex.IsMatch(number.Groups[1].Value, @"^\d+\.$")) return number.Groups[1].Value + " " + Escape(text[number.Length..], inline: true);
        // other markers ("a)", "(iv)") are kept as text of a bulleted item
        return "- " + Escape(text, inline: true);
    }

    /// <summary>A pipe table from the block's cells; a cell spanning several rows or columns fills the first of them, the others stay empty.</summary>
    private static string Table(LayoutBlock block)
    {
        var table = block.Table ?? TableDetector.FromRows(block.Lines);
        var grid = new string[table.RowCount, table.ColumnCount];
        foreach (var cell in table.Cells)
        {
            ref var slot = ref grid[cell.Row, Math.Min(cell.Column, table.ColumnCount - 1)];
            var text = cell.Text.Replace('\n', ' ');
            slot = slot == null ? text : slot + " " + text;
        }
        var sb = new StringBuilder();
        for (int r = 0; r < table.RowCount; r++)
        {
            sb.Append('|');
            for (int c = 0; c < table.ColumnCount; c++)
                sb.Append(' ').Append(Escape(grid[r, c] ?? "", inline: true).Replace("|", "\\|")).Append(" |");
            sb.Append('\n');
            if (r == 0) sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", table.ColumnCount))).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Escapes what Markdown would read as markup; at the start of a block also what would begin a list, heading or quote.</summary>
    private static string Escape(string text, bool inline = false)
    {
        var s = Regex.Replace(text, @"([\\`*_\[\]<>])", @"\$1");
        if (!inline) s = Regex.Replace(s, @"^(\s*)([#>+\-]|\d+[.)])(\s)", @"$1\$2$3");
        return s;
    }

    // ------------------------------------------------------------------ hOCR

    /// <summary>Analyses the document and writes it as hOCR.</summary>
    public static string ToHocr(PdfDocument document, TextExtractionOptions? textOptions = null) =>
        ToHocr(PdfLayoutAnalyzer.Analyze(document, textOptions));

    /// <summary>
    /// Writes analysed pages as hOCR 1.2 (XHTML): an <c>ocr_page</c> per page, an <c>ocr_carea</c> with an
    /// <c>ocr_par</c> (or <c>ocr_header</c>, <c>ocr_footer</c>, <c>ocr_caption</c>) per block, <c>ocr_line</c>s and
    /// <c>ocrx_word</c>s. Coordinates are points from the top left corner of the page (<c>scan_res 72 72</c>).
    /// </summary>
    public static string ToHocr(IEnumerable<PageLayout> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
          .Append("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">\n")
          .Append("<html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"en\" lang=\"en\">\n<head>\n<title></title>\n")
          .Append("<meta http-equiv=\"Content-Type\" content=\"text/html;charset=utf-8\" />\n")
          .Append("<meta name=\"ocr-system\" content=\"miPDFlib\" />\n")
          .Append("<meta name=\"ocr-capabilities\" content=\"ocr_page ocr_carea ocr_par ocr_line ocrx_word ocr_header ocr_footer ocr_caption\" />\n")
          .Append("</head>\n<body>\n");
        int block = 0, line = 0, word = 0;
        foreach (var page in pages)
        {
            var box = page.PageBox;
            sb.Append($"<div class=\"ocr_page\" id=\"page_{page.PageNumber}\" title=\"bbox 0 0 {N(box.Width)} {N(box.Height)}; ppageno {page.PageNumber - 1}; scan_res 72 72\">\n");
            foreach (var b in page.Blocks)
            {
                block++;
                string kind = b.Kind switch
                {
                    LayoutBlockKind.Header => "ocr_header",
                    LayoutBlockKind.Footer => "ocr_footer",
                    LayoutBlockKind.Caption => "ocr_caption",
                    _ => "ocr_par",
                };
                sb.Append($" <div class=\"ocr_carea\" id=\"block_{block}\" title=\"{Box(b.Bounds, box)}\">\n");
                sb.Append($"  <p class=\"{kind}\" id=\"par_{block}\" title=\"{Box(b.Bounds, box)}\"");
                if (b.Kind is LayoutBlockKind.Heading or LayoutBlockKind.ListItem or LayoutBlockKind.Table)
                    sb.Append($" data-kind=\"{b.Kind.ToString().ToLowerInvariant()}{(b.Level > 0 ? b.Level.ToString(CultureInfo.InvariantCulture) : "")}\"");
                sb.Append(">\n");
                foreach (var l in b.Lines)
                {
                    line++;
                    double size = l.Words.Count > 0 ? l.Words.Max(w => w.FontSize) : b.FontSize;
                    sb.Append($"   <span class=\"ocr_line\" id=\"line_{line}\" title=\"{Box(l.Bounds, box)}; x_size {N(size)}\">");
                    foreach (var w in l.Words)
                    {
                        word++;
                        sb.Append($"<span class=\"ocrx_word\" id=\"word_{word}\" title=\"{Box(w.Bounds, box)}; x_wconf 100; x_fsize {N(w.FontSize)}\">")
                          .Append(WebUtility.HtmlEncode(w.Text)).Append("</span> ");
                    }
                    sb.Append("</span>\n");
                }
                sb.Append("  </p>\n </div>\n");
            }
            sb.Append("</div>\n");
        }
        return sb.Append("</body>\n</html>\n").ToString();
    }

    /// <summary>An hOCR bounding box: whole points from the top left corner of the page.</summary>
    private static string Box(PdfRectangle r, PdfRectangle page) =>
        $"bbox {N(Math.Floor(r.Left - page.Left))} {N(Math.Floor(page.Top - r.Top))} {N(Math.Ceiling(r.Right - page.Left))} {N(Math.Ceiling(page.Top - r.Bottom))}";

    private static string N(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ ALTO

    /// <summary>Analyses the document and writes it as ALTO 4 XML.</summary>
    public static string ToAlto(PdfDocument document, TextExtractionOptions? textOptions = null) =>
        ToAlto(PdfLayoutAnalyzer.Analyze(document, textOptions));

    /// <summary>
    /// Writes analysed pages as ALTO 4.4 XML: per page a <c>PrintSpace</c> with the blocks (<c>TextBlock</c>,
    /// <c>TextLine</c>, <c>String</c>, <c>SP</c>), headers and footers in <c>TopMargin</c> and <c>BottomMargin</c>, block
    /// kinds as layout tags and fonts as text styles. Positions are in 1/1200 inch (<c>inch1200</c>) from the top left
    /// corner of the page.
    /// </summary>
    public static string ToAlto(IEnumerable<PageLayout> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var list = pages.ToList();
        const string ns = "http://www.loc.gov/standards/alto/ns-v4#";
        var styles = new Dictionary<(string Font, double Size, bool Bold), string>();
        foreach (var b in list.SelectMany(p => p.Blocks))
            foreach (var w in b.Lines.SelectMany(l => l.Words))
                if (!styles.ContainsKey((w.FontName, w.FontSize, b.IsBold))) styles[(w.FontName, w.FontSize, b.IsBold)] = $"FONT{styles.Count + 1}";
        var tags = list.SelectMany(p => p.Blocks).Select(Tag).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();

        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var output = new MemoryStream();
        using (var x = XmlWriter.Create(output, settings))
        {
            x.WriteStartDocument();
            x.WriteStartElement("alto", ns);
            x.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            x.WriteAttributeString("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                ns + " http://www.loc.gov/standards/alto/v4/alto-4-4.xsd");
            x.WriteStartElement("Description", ns);
            x.WriteElementString("MeasurementUnit", ns, "inch1200");
            x.WriteStartElement("OCRProcessing", ns);
            x.WriteAttributeString("ID", "OCR_0");
            x.WriteStartElement("ocrProcessingStep", ns);
            x.WriteStartElement("processingSoftware", ns);
            x.WriteElementString("softwareName", ns, "miPDFlib");
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndElement();

            x.WriteStartElement("Styles", ns);
            foreach (var ((font, size, bold), id) in styles)
            {
                x.WriteStartElement("TextStyle", ns);
                x.WriteAttributeString("ID", id);
                x.WriteAttributeString("FONTFAMILY", font.Length > 0 ? font : "unknown");
                x.WriteAttributeString("FONTSIZE", N(size));
                if (bold) x.WriteAttributeString("FONTSTYLE", "bold");
                x.WriteEndElement();
            }
            x.WriteEndElement();
            if (tags.Count > 0)
            {
                x.WriteStartElement("Tags", ns);
                foreach (var tag in tags)
                {
                    x.WriteStartElement("LayoutTag", ns);
                    x.WriteAttributeString("ID", tag);
                    x.WriteAttributeString("LABEL", tag[4..]);
                    x.WriteEndElement();
                }
                x.WriteEndElement();
            }

            x.WriteStartElement("Layout", ns);
            int blockId = 0, lineId = 0, wordId = 0;
            foreach (var page in list)
            {
                var box = page.PageBox;
                x.WriteStartElement("Page", ns);
                x.WriteAttributeString("ID", $"PAGE{page.PageNumber}");
                x.WriteAttributeString("PHYSICAL_IMG_NR", page.PageNumber.ToString(CultureInfo.InvariantCulture));
                x.WriteAttributeString("HEIGHT", U(box.Height));
                x.WriteAttributeString("WIDTH", U(box.Width));
                var top = page.Blocks.Where(b => b.Kind == LayoutBlockKind.Header).ToList();
                var bottom = page.Blocks.Where(b => b.Kind == LayoutBlockKind.Footer).ToList();
                var body = page.Blocks.Where(b => b.Kind is not (LayoutBlockKind.Header or LayoutBlockKind.Footer)).ToList();
                // the schema's order: the margins first, then the print space
                Space("TopMargin", top);
                Space("BottomMargin", bottom);
                Space("PrintSpace", body);
                x.WriteEndElement();

                void Space(string name, List<LayoutBlock> blocks)
                {
                    if (blocks.Count == 0 && name != "PrintSpace") return;
                    var area = blocks.Count > 0 ? PdfTextExtractor.Union(blocks.Select(b => b.Bounds)) : box;
                    x.WriteStartElement(name, ns);
                    Position(area);
                    foreach (var b in blocks)
                    {
                        x.WriteStartElement("TextBlock", ns);
                        x.WriteAttributeString("ID", $"BLOCK{++blockId}");
                        Position(b.Bounds);
                        x.WriteAttributeString("TAGREFS", Tag(b));
                        foreach (var l in b.Lines)
                        {
                            x.WriteStartElement("TextLine", ns);
                            x.WriteAttributeString("ID", $"LINE{++lineId}");
                            Position(l.Bounds);
                            for (int i = 0; i < l.Words.Count; i++)
                            {
                                var w = l.Words[i];
                                if (i > 0)
                                {
                                    x.WriteStartElement("SP", ns);
                                    x.WriteAttributeString("HPOS", U(l.Words[i - 1].Bounds.Right - box.Left));
                                    x.WriteAttributeString("VPOS", U(box.Top - w.Bounds.Top));
                                    x.WriteAttributeString("WIDTH", U(Math.Max(0, w.Bounds.Left - l.Words[i - 1].Bounds.Right)));
                                    x.WriteEndElement();
                                }
                                x.WriteStartElement("String", ns);
                                x.WriteAttributeString("ID", $"STRING{++wordId}");
                                Position(w.Bounds);
                                x.WriteAttributeString("STYLEREFS", styles[(w.FontName, w.FontSize, b.IsBold)]);
                                x.WriteAttributeString("CONTENT", w.Text);
                                x.WriteEndElement();
                            }
                            x.WriteEndElement();
                        }
                        x.WriteEndElement();
                    }
                    x.WriteEndElement();
                }

                void Position(PdfRectangle r)
                {
                    x.WriteAttributeString("HEIGHT", U(r.Height));
                    x.WriteAttributeString("WIDTH", U(r.Width));
                    x.WriteAttributeString("VPOS", U(box.Top - r.Top));
                    x.WriteAttributeString("HPOS", U(r.Left - box.Left));
                }
            }
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndDocument();
        }
        return Encoding.UTF8.GetString(output.ToArray());

        // 1/1200 inch from points
        static string U(double points) => Math.Round(Math.Max(0, points) * 1200 / 72, 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The layout tag of a block kind (with the heading level).</summary>
    private static string Tag(LayoutBlock b) => "TAG_" + b.Kind switch
    {
        LayoutBlockKind.Heading => $"heading{b.Level}",
        LayoutBlockKind.ListItem => "listitem",
        _ => b.Kind.ToString().ToLowerInvariant(),
    };

    // ------------------------------------------------------------------ PAGE

    /// <summary>Analyses the document and writes each page as PAGE XML (one document per page).</summary>
    public static IReadOnlyList<string> ToPageXml(PdfDocument document, PageXmlExportOptions? options = null, TextExtractionOptions? textOptions = null) =>
        PdfLayoutAnalyzer.Analyze(document, textOptions).Select(p => ToPageXml(p, options)).ToList();

    /// <summary>
    /// Writes an analysed page as PAGE XML (2019-07-15, the format of Transkribus, OCR-D and eScriptorium): a
    /// <c>TextRegion</c> per block with its kind as region type, <c>TextLine</c>s with baselines, <c>Word</c>s and the
    /// text as <c>TextEquiv</c>; tables as <c>TableRegion</c>s with a <c>TextRegion</c> per cell and its row, column and
    /// spans; the reading order of the regions. Coordinates are pixels of a page image at
    /// <see cref="PageXmlExportOptions.Resolution"/> from its top left corner.
    /// </summary>
    public static string ToPageXml(PageLayout page, PageXmlExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        options ??= new PageXmlExportOptions();
        const string ns = "http://schema.primaresearch.org/PAGE/gts/pagecontent/2019-07-15";
        var box = page.PageBox;
        double scale = options.Resolution / 72;
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var output = new MemoryStream();
        using (var x = XmlWriter.Create(output, settings))
        {
            x.WriteStartDocument();
            x.WriteStartElement("PcGts", ns);
            x.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            x.WriteAttributeString("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                ns + " http://schema.primaresearch.org/PAGE/gts/pagecontent/2019-07-15/pagecontent.xsd");
            x.WriteStartElement("Metadata", ns);
            x.WriteElementString("Creator", ns, "miPDFlib");
            string now = (options.Created ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            x.WriteElementString("Created", ns, now);
            x.WriteElementString("LastChange", ns, now);
            x.WriteEndElement();

            x.WriteStartElement("Page", ns);
            x.WriteAttributeString("imageFilename", options.ImageFileName(page.PageNumber));
            x.WriteAttributeString("imageWidth", I(Math.Ceiling(box.Width * scale)));
            x.WriteAttributeString("imageHeight", I(Math.Ceiling(box.Height * scale)));
            x.WriteAttributeString("imageXResolution", N(options.Resolution));
            x.WriteAttributeString("imageYResolution", N(options.Resolution));
            x.WriteAttributeString("imageResolutionUnit", "PPI");
            var blocks = page.Blocks.ToList();
            if (blocks.Count > 0)
            {
                x.WriteStartElement("ReadingOrder", ns);
                x.WriteStartElement("OrderedGroup", ns);
                x.WriteAttributeString("id", $"p{page.PageNumber}_ro");
                for (int i = 0; i < blocks.Count; i++)
                {
                    x.WriteStartElement("RegionRefIndexed", ns);
                    x.WriteAttributeString("index", I(i));
                    x.WriteAttributeString("regionRef", RegionId(i));
                    x.WriteEndElement();
                }
                x.WriteEndElement();
                x.WriteEndElement();
            }
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (b.Kind == LayoutBlockKind.Table && b.Table is { } table) WriteTable(RegionId(i), b, table);
                else WriteText(RegionId(i), b);
            }
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndDocument();

            string RegionId(int i) => $"p{page.PageNumber}_r{i + 1}";

            void WriteText(string id, LayoutBlock b)
            {
                x.WriteStartElement("TextRegion", ns);
                x.WriteAttributeString("id", id);
                var (type, custom) = b.Kind switch
                {
                    LayoutBlockKind.Heading => ("heading", $"structure {{type:heading; level:{b.Level};}}"),
                    LayoutBlockKind.ListItem => ("paragraph", "structure {type:list_item;}"),
                    LayoutBlockKind.Caption => ("caption", null),
                    LayoutBlockKind.Header => ("header", null),
                    LayoutBlockKind.Footer => ("footer", null),
                    LayoutBlockKind.Paragraph => ("paragraph", null),
                    _ => ("other", null),
                };
                if (custom != null) x.WriteAttributeString("custom", custom);
                x.WriteAttributeString("type", type);
                Coords(b.Bounds);
                Lines(id, b.Lines.Select(l => (l, l.Words)));
                Equiv(b.Text);
                x.WriteStartElement("TextStyle", ns);
                x.WriteAttributeString("fontSize", N(b.FontSize));
                if (b.IsBold) x.WriteAttributeString("bold", "true");
                x.WriteEndElement();
                x.WriteEndElement();
            }

            void WriteTable(string id, LayoutBlock b, LayoutTable table)
            {
                x.WriteStartElement("TableRegion", ns);
                x.WriteAttributeString("id", id);
                x.WriteAttributeString("rows", I(table.RowCount));
                x.WriteAttributeString("columns", I(table.ColumnCount));
                x.WriteAttributeString("lineSeparators", table.HasRules ? "true" : "false");
                // the region holds its cells (those of a table with rules reach to the rules)
                Coords(PdfTextExtractor.Union(table.Cells.Select(c => c.Bounds).Append(b.Bounds)));
                foreach (var cell in table.Cells)
                {
                    x.WriteStartElement("TextRegion", ns);
                    x.WriteAttributeString("id", $"{id}_c{cell.Row + 1}_{cell.Column + 1}");
                    x.WriteAttributeString("type", "paragraph");
                    Coords(cell.Bounds);
                    x.WriteStartElement("Roles", ns);
                    x.WriteStartElement("TableCellRole", ns);
                    x.WriteAttributeString("rowIndex", I(cell.Row));
                    x.WriteAttributeString("columnIndex", I(cell.Column));
                    if (cell.RowSpan > 1) x.WriteAttributeString("rowSpan", I(cell.RowSpan));
                    if (cell.ColumnSpan > 1) x.WriteAttributeString("colSpan", I(cell.ColumnSpan));
                    x.WriteEndElement();
                    x.WriteEndElement();
                    // the words of the cell (by their middle), line by line
                    var parts = b.Lines
                        .Select(l => (l, (IReadOnlyList<TextWord>)l.Words.Where(w => Inside(w.Bounds, cell.Bounds)).ToList()))
                        .Where(p => p.Item2.Count > 0);
                    Lines($"{id}_c{cell.Row + 1}_{cell.Column + 1}", parts);
                    Equiv(cell.Text);
                    x.WriteEndElement();
                }
                x.WriteEndElement();
            }

            void Lines(string id, IEnumerable<(TextLineInfo Line, IReadOnlyList<TextWord> Words)> lines)
            {
                int n = 0;
                foreach (var (line, words) in lines)
                {
                    string lineId = $"{id}_l{++n}";
                    var bounds = PdfTextExtractor.Union(words.Select(w => w.Bounds));
                    x.WriteStartElement("TextLine", ns);
                    x.WriteAttributeString("id", lineId);
                    Coords(bounds);
                    // the baseline: word boxes reach 0.2 em below it
                    double size = words.Max(w => w.FontSize);
                    double baseline = bounds.Bottom + 0.2 * size;
                    x.WriteStartElement("Baseline", ns);
                    x.WriteAttributeString("points", $"{Px(bounds.Left)},{Py(baseline)} {Px(bounds.Right)},{Py(baseline)}");
                    x.WriteEndElement();
                    int k = 0;
                    foreach (var w in words)
                    {
                        x.WriteStartElement("Word", ns);
                        x.WriteAttributeString("id", $"{lineId}_w{++k}");
                        Coords(w.Bounds);
                        Equiv(w.Text);
                        x.WriteEndElement();
                    }
                    Equiv(string.Join(" ", words.Select(w => w.Text)));
                    x.WriteEndElement();
                }
            }

            void Coords(PdfRectangle r)
            {
                string l = Px(r.Left), t = Py(r.Top), right = Px(r.Right), bottom = Py(r.Bottom);
                x.WriteStartElement("Coords", ns);
                x.WriteAttributeString("points", $"{l},{t} {right},{t} {right},{bottom} {l},{bottom}");
                x.WriteEndElement();
            }

            void Equiv(string text)
            {
                x.WriteStartElement("TextEquiv", ns);
                x.WriteElementString("Unicode", ns, text);
                x.WriteEndElement();
            }

            // whole pixels from the top left corner, within the image
            string Px(double v) => I(Math.Clamp(Math.Round((v - box.Left) * scale), 0, Math.Ceiling(box.Width * scale)));
            string Py(double v) => I(Math.Clamp(Math.Round((box.Top - v) * scale), 0, Math.Ceiling(box.Height * scale)));
        }
        return Encoding.UTF8.GetString(output.ToArray());

        static string I(double v) => ((long)v).ToString(CultureInfo.InvariantCulture);
        static bool Inside(PdfRectangle word, PdfRectangle cell)
        {
            double cx = (word.Left + word.Right) / 2, cy = (word.Bottom + word.Top) / 2;
            return cx >= cell.Left && cx <= cell.Right && cy >= cell.Bottom && cy <= cell.Top;
        }
    }
}

/// <summary>Options for <see cref="PdfLayoutExport.ToPageXml(PageLayout, PageXmlExportOptions?)"/>.</summary>
public sealed class PageXmlExportOptions
{
    /// <summary>
    /// The resolution in pixels per inch of the page images the coordinates refer to (PAGE measures in pixels).
    /// Default: 300, e.g. for images rendered with miPDFlib.Rendering at <c>Dpi = 300</c>.
    /// </summary>
    public double Resolution { get; set; } = 300;

    /// <summary>The file name of a page's image (by page number). Default: <c>page-1.png</c>, <c>page-2.png</c>, ...</summary>
    public Func<int, string> ImageFileName { get; set; } = n => $"page-{n}.png";

    /// <summary>The creation time written to the metadata (UTC). Default: null, the time of the export.</summary>
    public DateTime? Created { get; set; }
}
